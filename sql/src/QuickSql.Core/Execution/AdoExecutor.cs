using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using QuickSql.Aop;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Transactions;

namespace QuickSql.Execution;

/// <summary>一次执行的标签：操作名 + 涉及的表 + 这条语句要落在哪座集群、链上有没有点名主/从，
/// 只喂观察类钩子、日志与路由（§10.1、§16.4）。
/// 由查询对象算一次并随对象复用，不在每行上重算。</summary>
/// <param name="Cluster">这条语句所属的集群名。缺省 <see cref="DataSourceRouting.DefaultCluster"/>，
/// 于是今天所有不声明集群的调用方在这里逐字不变。</param>
/// <param name="Choice">链上点的 <c>FromPrimary()</c> / <c>FromReplica()</c>；
/// <see cref="ChainChoice.Auto"/> 才是"交给路由判"。</param>
public sealed record ExecutionTag(string Operation, IReadOnlyList<string> Tables,
    string Cluster = DataSourceRouting.DefaultCluster, ChainChoice Choice = ChainChoice.Auto);

/// <summary>§3 的第 5 层：连接取还、参数落地、超时、观察类钩子。
/// 这一层只接 <see cref="DialectSql"/>，既不认识查询模型也不生成文本 ——
/// 所以「<c>ToSql()</c> 不连库」（§3）在这里是结构性的，不靠约定。</summary>
public sealed partial class AdoExecutor
{
    readonly ConnectionSources _sources;

    // ── 会话级连接复用（§8.4） ────────────────────────────────────────
    // DbContext 在 Set<T>() 时注册一只工厂（只是 delegate 引用，不建连接），
    // AdoExecutor.OpenAsync 在真正需要连接时调工厂取一条，之后同一会话的语句按 (集群, 角色) 各复用一条。
    // AsyncLocal 的传播语义与事务环境槽位一致：同一 async 执行流内的子调用都能看到，兄弟/父帧看不到。
    //
    // 槽位里放的是**可变对象**而不是连接本身：读写分离之后"会话的那一条连接"这句话变成了
    // "会话的那一张表"，而逐条换 AsyncLocal 值在异步被调方里写不回父帧（QuickSqlTransaction 的 remarks
    // 记过同一条规则），所以这里靠"引用不变、内容在长"的那个对象把两侧粘住。

    static readonly AsyncLocal<SessionConnections?> _sessionConnections = new();

    /// <summary>会话工厂槽位。<b>槽里存的是 (工厂, 主人) 而不是裸 delegate</b>（v0.39 第二笔）：
    /// 嵌套是常态而不是意外——里外两只 DbContext 同一条流上各有一只，先Set 的那只成为主人，
    /// 后到的只在位不改字。没有这一格的结局是：里层先 <c>Set&lt;T&gt;()</c> 时挡住外层的工厂
    /// （或反过来），里层先 <c>Dispose</c> 时把整条流的工厂清掉——外层之后每一句悄悄退回
    /// "每句取还池"，会话复用与写后读的保证无声失效。对照 <see cref="InstallFlowWrites"/> 同一块
    /// 已经用 owner 记账修过的形状：嵌套即共享，主人拆除，非主人不得清除。</summary>
    static readonly AsyncLocal<SessionFactorySlot?> _sessionFactory = new();

    /// <summary>"同一条流上这座集群写过"的戳子。写它的必须是同步帧（<see cref="InstallFlowWrites"/>），
    /// 异步的写语句只<em>改</em>那个对象、不<em>换</em>槽位。</summary>
    static readonly AsyncLocal<FlowWrites?> _flowWrites = new();

    /// <summary>注册会话工厂（<b>主人记账版</b>，见 <see cref="_sessionFactory"/>）：槽位空着就挂上并成为主人；
    /// 已被占用（同一条流上另一只 DbContext 先到）就当场不动字——嵌套即共享，后到的那只不发自己的工厂，
    /// 之后的连接照旧经主人的工厂落到主人的账本上，里层先结束也拆不了主人的槽。</summary>
    internal static void InstallSessionFactory(object owner, Func<string, ConnectionRole, DbConnection> factory)
        => _sessionFactory.Value ??= new SessionFactorySlot(owner, factory);

    /// <summary>拆会话工厂。<b>只有主人能拆</b>：非主人的 <c>Dispose</c> 里这一格不动
    /// （对照 <see cref="FlowWritesHandle"/> 的 <c>ownsSlot</c> 判法同一条纪律），
    /// 否则里层先释放就把外层还在用的工厂整条拔掉——那是 v0.39 第二笔修掉的静默退化。</summary>
    internal static void ClearSessionFactory(object owner)
    {
        if (_sessionFactory.Value is { } slot && ReferenceEquals(slot.Owner, owner))
            _sessionFactory.Value = null;
    }

    internal static void BindSessionConnection(string cluster, ConnectionRole role, DbConnection connection)
        => (_sessionConnections.Value ??= new SessionConnections()).Set(cluster, role, connection);

    internal static void UnbindSessionConnection(DbConnection connection) => _sessionConnections.Value?.Remove(connection);

    /// <summary>形态 (4)：<b>钉在这只 executor（也就是这一只 <see cref="QuickSql.QuickSqlOrm"/>）上的那场事务</b>。
    /// <para>环境槽位跟的是执行流，异步被调方写下的那一笔回不到调用方的帧（v0.12 实测，见
    /// <see cref="IQuickSqlTransaction"/> 抬头）；装配层——ABP 的工作单元——正好是这种形状：它在自己的
    /// async 帧里开事务，回到仓储那一帧时槽位是空的，于是「一场工作单元一次提交」悄悄退化成了每句自提。
    /// 这一格把归属从「流」扩成「流 <em>或</em> 实例」：<see cref="QuickSqlTransaction.For"/> 在帧里找不到
    /// 自己的环境事务时，回退读这里。</para>
    /// <para><b>槽位挂在实例上而不是类型上</b>，与 <see cref="QuickSqlOrm.CreateSharedContext{TContext}"/>
    /// 同一分工：两只 orm 共用一条流时各钉各的，互不串（跨 executor 想塞进对方的事务仍然当场拒）。
    /// 归属不是线程安全的——同一实例上的并发分支共用一条连接、一个 <c>DbTransaction</c>，
    /// 要并发照旧各自开流或各自建实例，这一档不设闸门。</para></summary>
    internal QuickSqlTransaction? SessionTransaction { get; private set; }

    /// <summary>把根事务钉到本实例上。<see cref="QuickSqlOrm.BeginSessionTransaction"/> 唯一会调它的地方，
    /// 所以「谁钉的」与「钉了几场」都只有一条路。</summary>
    internal void BindSessionTransaction(QuickSqlTransaction transaction)
    {
        SessionTransaction = transaction;
        transaction.SessionScoped = true;
    }

    /// <summary>摘钩。只摘自己那一场：<c>RequiresNew</c> 那一族里内层结束时外层仍在位，
    /// 按引用比一次才不会把别人钉的那场抹掉。</summary>
    internal void UnbindSessionTransaction(QuickSqlTransaction transaction)
    {
        if (ReferenceEquals(SessionTransaction, transaction))
            SessionTransaction = null;
    }

    /// <summary>在<b>调用方的同步帧</b>里挂上这流的写后读戳子，返回一个"关掉它"的把手（会话释放用）。
    /// 已经挂着就复用那一枚 —— 两只 DbContext 嵌套时共用同一份"写过哪些集群"的账，
    /// 因为要防的那件事（读完自己刚写的旧值）跨会话也一样成立。</summary>
    internal static IDisposable InstallFlowWrites()
    {
        var ownsSlot = _flowWrites.Value is null;

        if (ownsSlot)
            _flowWrites.Value = new FlowWrites();

        return new FlowWritesHandle(ownsSlot);
    }

    /// <summary>本条流上这座集群是否写过。戳子不在位即 false（链式侧不显式开帧时就是这个默认，
    /// 于是那条路上"写后读"不生效，而它生效的地方是会话与显式开了帧的流）。</summary>
    internal static bool FlowWroteCluster(string cluster) => _flowWrites.Value?.Contains(cluster) == true;

    internal static void MarkFlowWrite(string cluster) => _flowWrites.Value?.Mark(cluster);

    sealed class FlowWritesHandle(bool ownsSlot) : IDisposable
    {
        public void Dispose()
        {
            if (ownsSlot)
                _flowWrites.Value = null;
        }
    }

    public AdoExecutor(IDbProvider provider, ConnectionSources sources, QuickSqlAop aop, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(aop);

        Provider = provider;
        _sources = sources;
        Aop = aop;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public IDbProvider Provider { get; }

    public QuickSqlAop Aop { get; }

    public int CommandTimeoutSeconds { get; }

    /// <summary>预容量的天花板（行）。读多少行是库给的答案，而<em>开多大数组</em>是这里自己的事：
    /// 上限取 8192 是因为 §12.8 那几枚钉都在 1 000 行那一档（一次扩容到 8192 只需要 13 次翻倍），
    /// 而"取全部"那一档（<c>Take(int.MaxValue)</c>）照抄会把进程在读第一行之前就按死。</summary>
    const int MaxPreallocatedRows = 8192;

    /// <summary><paramref name="expectedRows"/> 取自模型的 <c>Take(n)</c>：预容量是 §12.7 E 的硬要求，
    /// 传 null 表示行数确实未知（无 <c>Take</c> 的全量查询）。
    /// <b>而这一颗预容量是按上限收着的</b>：<c>Take(n)</c> 说的是"最多读 n 行"，不是"给我准备 n 行的数组"——
    /// 消费侧那颗 <c>pageSize = int.MaxValue</c> 的"取全部"照抄过来会在读第一行之前就
    /// <c>OutOfMemoryException</c>（Array dimensions exceeded supported range），
    /// 而那一发本来只该回几行。超过这一格仍按 §12.7 E 预容量，只是不再按库外的数字开数组。</summary>
    public async Task<List<T>> QueryAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, int? expectedRows, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, ct).ConfigureAwait(false);
        await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            var list = new List<T>(expectedRows is > 0 and var wanted ? Math.Min(wanted, MaxPreallocatedRows) : 0);

            if (Aop.Materialized is { } materialized)
            {
                for (var ordinal = 0; await reader.ReadAsync(ct).ConfigureAwait(false); ordinal++)
                {
                    var value = map(reader);
                    list.Add(value);

                    if (value is not null)
                        materialized(new MaterializedEvent(value, typeof(T).Name, ordinal));
                }
            }
            else
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    list.Add(map(reader));
            }

            Succeeded(sql, tag, started, list.Count, session.Route);
            return list;
        }
        catch (Exception ex)
        {
            Failed(ex, sql, started);
            throw;
        }
    }

    public async Task<T?> FirstAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, ct).ConfigureAwait(false);
        await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Succeeded(sql, tag, started, 0, session.Route);
                return default;
            }

            var value = map(reader);
            Succeeded(sql, tag, started, 1, session.Route);

            return value;
        }
        catch (Exception ex)
        {
            Failed(ex, sql, started);
            throw;
        }
    }

    /// <summary>流式读取：连接活在整个枚举期间，调用方中途 <c>break</c> 时由 <c>DisposeAsync</c> 归还。</summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, [EnumeratorCancellation] CancellationToken ct = default)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();
        var rows = 0;

        await using var session = await OpenAsync(sql, tag, ct).ConfigureAwait(false);
        await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows++;
                yield return map(reader);
            }
        }
        finally
        {
            // 提前 break 不算错误，但行数必须是"实际读到的行数"，不能报成完整结果集。
            Succeeded(sql, tag, started, rows, session.Route);
        }
    }

    // ── 写出口（§6 示例里的 ExecuteAffrowsAsync 走的就是这里） ──────────

    /// <summary>非查询出口，返回驱动报回的受影响行数。多值 INSERT 时这个数就是行数（§12.4 第 2 项）。</summary>
    public async Task<int> NonQueryAsync(DialectSql sql, ExecutionTag tag, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, RouteIntent.Write, ct).ConfigureAwait(false);

        try
        {
            var rows = await session.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            // 写过就记账：这条流上这座集群之后的读一律落主库（复制延迟测不到，所以这一档只肯保守）。
            MarkFlowWrite(session.Route.Cluster);

            Succeeded(sql, tag, started, rows, session.Route);
            return rows;
        }
        catch (Exception ex)
        {
            Failed(ex, sql, started);
            throw;
        }
    }

    /// <summary>带结果集的写出口：一条 <c>OUTPUT</c>/<c>RETURNING</c> 语句执行完就地读回它产出的行，
    /// <paramref name="consumeRow"/> 在第 i 行上被调一次（i 从 0 起，<b>就是回填时要对上的源行序</b>）。
    /// 返回值按<b>回包行数</b>计而不是驱动的行数：这条路径的存在意义就是把行和实体对上，
    /// 而触发器多塞进行数不在 §12.4 第 2 项的账里（那是第 6 项的范围）。
    /// <b>v0.34 第一笔补的那一停</b>：同一句命令尾巴上接"语句后回读"那一档（<c>InsertEngine.Build</c> 的
    /// postRead 分支）是多语句批，MySQL 血统的驱动把批的<em>第一个</em>结果交回的是 INSERT 自己那颗
    /// （0 列），键在下一个结果集里 —— 而发 <c>OUTPUT</c>/<c>RETURNING</c> 的两家第一个结果就带列，
    /// 下面那一句 while 对它们一次都不迭代：为回读而加，不为任何一家改路。</summary>
    public async Task<int> WriteWithResultAsync(DialectSql sql, ExecutionTag tag, Action<DbDataReader, int> consumeRow, CancellationToken ct)
    {
        BeforeExecute(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, tag, RouteIntent.Write, ct).ConfigureAwait(false);
        await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            // 跳到第一个"带列"的结果集：见上一段 doc 的那一停。空回包（虚谷旧 RETURNING 档的 FIELDS=0）
            // 在这里同样是"没有下一结果集"，落回 0 行由调用方的 0/N 守卫接住 —— 不在此处另立判据。
            while (reader.FieldCount == 0 && await reader.NextResultAsync(ct).ConfigureAwait(false))
            { }

            var rows = 0;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                consumeRow(reader, rows);
                rows++;
            }

            MarkFlowWrite(session.Route.Cluster);

            Succeeded(sql, tag, started, rows, session.Route);
            return rows;
        }
        catch (Exception ex)
        {
            Failed(ex, sql, started);
            throw;
        }
    }

    // ── 装载通道（§12.1 的 L1） ──────────────────────────────────────

    /// <summary>§12.1 第三行「批量装载通道」在这一层的形状：<b>不发命令，只把一条已经开好的连接借出去</b>
    /// （连同它上面可能活着的事务与超时），由 provider 的 <see cref="IBulkCopyChannel"/> 自家的高速入口接管那一批行。
    /// 这正是这一层必须存在而不是让 provider 自己去拿连接的理由：<see cref="RentAsync"/> 那四支来路
    /// （环境事务 → 会话连接 → 工厂 → 新建）是执行层的私有知识，provider 伸手去拿就会在环境事务在位时
    /// 拿到一条<em>旁支</em>连接 —— 那句话 §6 (3) 已经写过一次，读者是文本路径，本家是同一个坑的第二条路。
    ///
    /// <para><b>观察钩子收到的是一句合成描述符，不是 SQL 文本</b>：这一档压根不产文本，
    /// 而 <see cref="QuickSqlAop.Executed"/> 与 <see cref="QuickSqlAop.Error"/> 的形状里都带一颗
    /// <see cref="DialectSql"/>。填 <c>null</c> 会把"这一句没有文本"变成"这两个钩子可能收到 null"，
    /// 让每个订阅方都去补一次空判；于是给一句人能读懂的 <c>BULK COPY INTO &lt;表&gt;</c>，
    /// 参数表是空的（<b>装载通道没有参数</b>，§12.1 那一句"不走文本、不走参数"就是这条空表的出处）。
    /// <see cref="BeforeExecute"/> 照常放这句进去：<c>CommandTerminating</c> 是决策类闸门，
    /// 新增一条写出口不能从它旁边绕过去 —— 拦整表写的现场至少能在 <paramref name="tag"/> 的表名上看住这一档。
    /// 同一道门上的 <see cref="QuickSqlAop.Executing"/> 因此也收到这句合成描述符：装载通道没有文本可报，
    /// 但"这一批要发出去了"这一行日志不该只有 <see cref="QuickSqlAop.Executed"/> 一处才看得见。</para>
    ///
    /// <para><b>修剪税在这里交，不在 provider 里藏</b>：这一句要往上冒到 <c>IInsert&lt;T&gt;.ExecuteBulkAsync</c>
    /// 的公开签名上（IL2046 要求接口与实现两边标注一致），所以写路径上第一次出现一个带
    /// <see cref="RequiresUnreferencedCodeAttribute"/> 的出口。它不是疏漏：装载类不在
    /// <see cref="System.Data.Common.DbProviderFactory"/> 的抽象面上，只能按类型名找。</para></summary>
    [RequiresUnreferencedCode("转调装载通道：驱动自家的批量类不在 DbProviderFactory 的抽象面上，只能按类型名在驱动程序集里找。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public async Task<int> CopyAsync(IBulkCopyChannel channel, BulkCopyRequest request, ExecutionTag tag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = new DialectSql($"BULK COPY INTO {request.DestinationTable}", []);

        BeforeExecute(descriptor, tag);

        var started = Stopwatch.GetTimestamp();

        await using var rented = await RentAsync(tag, RouteIntent.Write, ct).ConfigureAwait(false);

        try
        {
            var rows = await channel.WriteAsync(
                new BulkConnection(rented.Connection, rented.Transaction, CommandTimeoutSeconds), request, ct)
                .ConfigureAwait(false);

            // 装载通道是一片/一批一次递交，但它同样是写：账要记，否则这座集群之后的读会落到还没装完的从库上。
            MarkFlowWrite(rented.Route.Cluster);

            Succeeded(descriptor, tag, started, rows, rented.Route);
            return rows;
        }
        catch (Exception ex)
        {
            Failed(ex, descriptor, started);
            throw;
        }
    }

    // ── 事务（§6 (1)(3)） ────────────────────────────────────────────

    /// <summary>开一条真事务并<b>就地</b>写进环境槽位。整个方法必须是同步的，两个理由见
    /// <see cref="IQuickSqlTransaction"/>：一是 ADO.NET 的 begin/commit/rollback 没有异步重载，
    /// 二是 <c>AsyncLocal</c> 只能由同步被调方写回调用方的帧 —— 而形态 (1) 之后的语句靠的就是那个槽位。
    /// 连接因此被这条事务独占到结束，<c>Open()</c> 也走同步重载（await 一个同步 open 只会多一层状态机）。
    ///
    /// <para><b>嵌套 = SAVEPOINT</b>（v0.38）：如果当前帧已有一条环境事务，不再新开一条 DbTransaction，
    /// 而是在外层事务上创建一个 SAVEPOINT 并返回一只保存点事务。保存点事务 Commit 释放保存点、
    /// Rollback 回退到保存点，外层事务不受影响。保存点名称按外层事务上的计数器递增生成。</para>
    ///
    /// <para><b>保存点有两条落地路</b>（v0.38 第一笔）：驱动自己发（<c>DbTransaction.Save</c>）与
    /// 我们发文本（<see cref="ExecuteSavepoint"/>），分岔点是方言那颗
    /// <see cref="ISqlDialect.SupportsDriverSavepoints"/>；走哪一条在这里定一次并记在那只保存点事务上，
    /// 于是 <see cref="QuickSqlTransaction.Commit"/> 与 <see cref="QuickSqlTransaction.Rollback"/>
    /// 拆的是同一条路。上面那段"嵌套 = SAVEPOINT"的语义两条路共用，没有第二套。</para>
    ///
    /// <para><b>传播</b>（v0.38）：<paramref name="propagation"/> 为 <see cref="TransactionPropagation.RequiresNew"/>
    /// 时挂起当前环境事务并新开一条独立事务，结束后恢复被挂起的那条。</para></summary>
    public QuickSqlTransaction BeginTransaction(IsolationLevel level, TransactionPropagation propagation = TransactionPropagation.Requires)
        => BeginTransaction(DataSourceRouting.DefaultCluster, level, propagation);

    /// <summary>钉住一座集群开事务（§16.4）。<b>事务从此有了归属</b>：这只事务里的每一条语句都要落在
    /// 同一座集群的<em>主库</em>那条连接上，别座集群的语句<em>当场拒</em>（判据在
    /// <see cref="RentAsync(ExecutionTag, RouteIntent, CancellationToken)"/> 那一支，两处共用一条）。
    /// 跨集群的提交协议（两阶段、XA、或按 SAVEPOINT 拼一场"看着像一场"的事务）规格没裁，
    /// 所以这里不给它一个能表达出来的形状。</summary>
    public QuickSqlTransaction BeginTransaction(string cluster, IsolationLevel level, TransactionPropagation propagation = TransactionPropagation.Requires)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        // 事务可选那道口（【本笔（ClickHouse 重开·笔一）新增】：公开的 BeginTransaction 第一次要认
        // DbCapabilities.Transactions 那颗位 —— provider 没声明它，这一格当场抛，照
        // EnsureDatabaseExistsAsync 那颗 CanCreateDatabase=false 先例的形状与纪律：NotSupportedException
        // 带明话。严禁静默假事务：没有位的 provider 上"开一条事务再照常发语句"会把 autocommit 引擎
        // （ClickHouse）读成"每句各自递交"，一次提交的承诺一句都对不上 —— 与其开着一条自己撤不掉的
        // 空壳（驱动 BeginDbTransaction 会抛、或者撤早撤晚都有人替它圆谎），不如把"没有事务"说成
        // "没有事务"。写路径不经过这一格：SaveChanges / InsertOrUpdate / DeleteRange 三处先判位再定
        // 包不包，没有位的那三家走逐句 autocommit 直发（不抛、不开），判据同一颗位分两半读。
        // 保存点那一支也不用另设铁闸：环境事务只能由这一格开出来，本格先抛之后它在本家不可达——
        // 也就是说"Requires + 环境事务在场"的嵌套形状在这一家根本到不了保存点那一步。
        if (!TransactionCapability.Declared(Provider))
            throw new NotSupportedException(
                $"Provider {GetType().Name} 不支持事务（{nameof(DbCapabilities.Transactions)} 位未声明）。" +
                "这一家的事务要么驱动不理解、要么服务端默认不开（如 ClickHouse 的多语句事务是实验档且驱动 BeginDbTransaction 本地抛）：" +
                "不要在这里开事务——内核写路径已经按这一位改走逐句 autocommit 直发；要一次整批落库，请在库侧用单条语句表达（§6）。");

        var ambient = QuickSqlTransaction.Ambient;

        // Requires：外层事务在位时，创建保存点而不是新开事务。
        // 隔离级别在保存点路径上被忽略（SAVEPOINT 继承外层事务的隔离级别，这是 SQL 标准行为）。
        if (propagation == TransactionPropagation.Requires && ambient is not null)
        {
            if (!ReferenceEquals(ambient.Executor, this))
                throw new QuickSqlException(
                    $"当前执行流上已经有一条属于另一个 orm 实例的环境事务：不能把{Provider.Kind}的语句塞进另一家库的事务里。" +
                    "如果要同时操作两个库，请在不同的执行流（不同的 async 方法或 Task.Run）里分别开事务。");

            if (!string.Equals(ambient.Cluster, cluster, StringComparison.Ordinal))
                throw new QuickSqlException($"当前环境事务钉在集群 {ambient.Cluster} 上，又要为集群 {cluster} 开事务：同一座集群里嵌套才是保存点，跨集群的嵌套不是一场事务。请按集群分别开事务、各自提交。");

            var savepointName = $"quicksql_sp_{ambient._nextSavepointId++}";

            // 两条路做的是同一件事，差别只在"这句话由驱动发还是由我们发"（§4 那颗 SupportsDriverSavepoints）。
            // 走哪条路在这里定一次，并把答案带进那只保存点事务 —— 创建它的那条路决定拆除它的那条路，
            // 不允许出现"驱动发的保存点由文本去释放"这种跨路拆卸。
            var byDriver = Provider.Dialect.SupportsDriverSavepoints;

            if (byDriver)
                ambient.DbTransaction.Save(savepointName);
            else
                ExecuteSavepoint(ambient, SavepointStep.Create, savepointName);

            var nested = new QuickSqlTransaction(ambient, savepointName, textSavepoints: !byDriver);

            nested.Install();

            return nested;
        }

        // RequiresNew：挂起当前环境事务（如果有），新开一条独立事务。
        // 跨 executor 的 RequiresNew 允许：新事务用新连接，与被挂起的那条互不干扰。
        QuickSqlTransaction? suspended = null;
        if (ambient is not null)
        {
            suspended = ambient;
            QuickSqlTransaction.ClearAmbient();
        }

        var connectors = Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Provider.Kind.ToString().ToLowerInvariant(), failure);

        // 会话级连接复用（§8.4）：DbContext 在 (这座集群, 主库) 那一格已有连接时直接用它开事务，不再另取一条。
        // 事务钉的是主库那一格 —— 从库那一格即使在位也不参与（写不进从库，而"事务里的读"必须看见自己的写）。
        // 工厂在位但那一格还没建时，先调工厂取连接再开事务。
        // 事务结束后连接不归池——它还在会话的格子里，后续语句继续复用。
        DbConnection? sessionConn = null;
        var sessions = _sessionConnections.Value;

        if (sessions is not null && sessions.TryGet(cluster, ConnectionRole.Primary, out var bound))
            sessionConn = bound;
        else if (_sessionFactory.Value is { } slot)
        {
            sessionConn = slot.Factory(cluster, ConnectionRole.Primary);
            BindSessionConnection(cluster, ConnectionRole.Primary, sessionConn);
        }

        var ownsConnection = sessionConn is null;
        var connection = sessionConn ?? connectors.CreateConnection(_sources.PrimaryConnectionString(cluster));

        try
        {
            if (ownsConnection)
                connection.Open();

            var transaction = new QuickSqlTransaction(this, cluster, connection, connection.BeginTransaction(level), ownsConnection, suspended);

            transaction.Install();

            return transaction;
        }
        catch
        {
            if (ownsConnection)
                ConnectionRelease.Release(connection);
            throw;
        }
    }

    /// <summary>形态 (4)：开一场事务并<b>钉在本 executor 上</b>——归属跟实例走，不跟执行流走。
    /// 帧里那一份照旧也装上（同一个机制），所以开它的那一帧与它的所有下游都进这场事务，
    /// 而<em>父帧与兄弟帧</em>靠 <see cref="SessionTransaction"/> 那一格回来。
    /// <para>与 <see cref="BeginTransaction(string,IsolationLevel,TransactionPropagation)"/> 的分工：那颗说的是
    /// 「我在这段代码里亲手收口」，这颗说的是「这段工作单元/这只会话底下就是一场事务」。
    /// 已经钉过就当场拒——同一实例上钉两场，「一次提交」那句话就又是假的。</para></summary>
    public QuickSqlTransaction BeginSessionTransaction(string cluster, IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (QuickSqlTransaction.For(this) is { } existing)
            throw new QuickSqlException(
                $"这只 orm 已经在事务里（集群 {existing.Cluster}），不能再钉一场会话事务：同一只实例上钉两场，"
                + "「一场工作单元一次提交」就不成立了。要先把手里那场收掉，或者按集群各开一只 orm。");

        var transaction = BeginTransaction(cluster, level, TransactionPropagation.Requires);

        // 根事务才钉实例；保存点事务（这一颗不会走到，因为上面已经拒了嵌套）共用外层那一条归属。
        BindSessionTransaction(transaction);

        return transaction;
    }

    /// <summary>在<em>已经被这条事务占有</em>的连接上发一句文本保存点（三口文本由方言给，见
    /// <see cref="ISqlDialect.EmitSavepoint"/> 那三口）。它<em>不</em>走 <see cref="OpenAsync"/> 那条取还连接的路：
    /// 保存点必须落在外层事务那条连接与那个 <c>DbTransaction</c> 上，落到别的连接上说的是另一件事。
    ///
    /// <para><b>命令构造复用 <see cref="Prepare"/>，一个字的特殊处理都不给它</b>：超时、事务绑定、
    /// 参数落地（这里传的是空表）因此与业务语句同源。<b>而 AOP 那一套钩子一个都不过</b>：
    /// <c>CudBuilding</c> 改的是业务写语句，把 <c>SAVEPOINT</c> 送进 <c>Rewritten</c> 只会让改写钩子
    /// 看到一句它不该动、也认不出的文本（§10.1 硬规则 1 的反面 —— 钩子看到的必须是它能改的东西）。
    /// 同理不走 <c>StatementEmitter</c>：那是写侧三条链的共用段，事务控制文本不属于它们。</para>
    ///
    /// <para><b>同步、<see cref="DbCommand.ExecuteNonQuery"/>（不是 reader）</b>：前一半与 begin/commit/rollback
    /// 同一条理由（这个方法整个都在调用方的同步帧里，见 <see cref="BeginTransaction"/> 抬头）；
    /// 后一半是量出来的 —— 用 reader 跑这一句会把进程撞在虚谷驱动 <c>XGDataReader.get_nextrow</c> 的原生
    /// 0xC0000005 上【实测·<c>obj/logs/v37-xg-recon-savepoint-1.log</c> 该探针文件抬头：第一趟走 reader，
    /// 第二趟全改 <c>ExecuteNonQuery</c> 之后 Q-08 到 Q-15 才一次跑完】。
    /// 三口都没有结果集，<c>affected</c> 在这一家也不是这件事的判据（探针里 <c>OK affected=0</c> 那一句
    /// 与库里真的建出了保存点是两件事，真判据是 §13.4 那一场的语义断言）。</para></summary>
    internal void ExecuteSavepoint(QuickSqlTransaction transaction, SavepointStep step, string savepointName)
    {
        var dialect = Provider.Dialect;

        // 容量：最长那句是 `ROLLBACK TO SAVEPOINT `（22 字符）加名字，三口都用它预容量，
        // 不为三句各算一遍 —— 这一句每只嵌套事务各付一次，不在任何热路径上。
        var writer = new SqlWriter(new StringBuilder(24 + savepointName.Length), [], dialect.ParameterPrefix);

        switch (step)
        {
            case SavepointStep.Create: dialect.EmitSavepoint(writer, savepointName); break;
            case SavepointStep.Rollback: dialect.EmitRollbackToSavepoint(writer, savepointName); break;
            case SavepointStep.Release: dialect.EmitReleaseSavepoint(writer, savepointName); break;
            default: throw new QuickSqlException($"不存在的保存点步骤：{step}（新增了 SavepointStep 的成员而没在这里给出对应的文本出口）");
        }

        using var command = Prepare(transaction.Connection, transaction.DbTransaction, new DialectSql(writer.ToSqlString(), writer.Parameters));

        command.ExecuteNonQuery();
    }

    ValueTask<Session> OpenAsync(DialectSql sql, ExecutionTag tag, CancellationToken ct) => OpenAsync(sql, tag, RouteIntent.Read, ct);

    async ValueTask<Session> OpenAsync(DialectSql sql, ExecutionTag tag, RouteIntent intent, CancellationToken ct)
    {
        var rented = await RentAsync(tag, intent, ct).ConfigureAwait(false);

        // 借来的那一条不在这里 dispose：所有权随 Session 走，命令与连接同一个借用周期。
        return new Session(rented, Prepare(rented.Connection, rented.Transaction, sql));
    }

    /// <summary>连接的四支来路，按"谁已经占着它"的优先级排：<b>环境事务 → 会话连接 → 会话工厂 → 新建并打开</b>。
    /// 抽出来只多了一颗 <see cref="RentedConnection"/>，四支的判据与副作用一个字节都没动 —— 抽它的理由是装载通道
    /// 那一条路要的是<em>连接本身</em>而不是命令（§12.1 第三行：那一家的高速入口不接 <c>DbCommand</c>），
    /// 而"环境事务在位时必须落在被事务独占的那条连接上"这句话（§6 (3)）在两条路上必须是同一份实现，
    /// 不许各写一遍再各自漂移。
    ///
    /// <para><b>读写分离与分库都长在这一颗上，因为它是全仓库唯一决定"用哪条连接"的地方</b>：四支来路逐支问
    /// 同一句"这一座集群、这一个角色占着的是哪条连接"。集群与角色由 <see cref="DataSourceRouting"/> 判一次
    /// （顺序即裁决，见 <see cref="DataSourceRouting.Resolve"/>），结论随借用一起交给
    /// <see cref="RentedConnection.Route"/>，好让观察钩子那一路读得出"这句走了哪条"。环境事务那一支多一条
    /// 硬闸：事务钉着一座集群，要另一座集群的语句<em>当场拒</em> —— 两条连接各开一个事务不是一场事务，
    /// 而"补一条 SAVEPOINT 拼起来"那一格规格没裁（§16.4①）。</para></summary>
    /// <remarks><b>返回值的连接归调用方 dispose</b>（<see cref="RentedConnection.DisposeAsync"/>，只在
    /// "这一支是我们自己新建的"时才真的还）。两条路各自怎么交接连接写在那两条路上：<see cref="OpenAsync"/>
    /// 把所有权转给 <see cref="Session"/>，<see cref="CopyAsync"/> 自己 <c>await using</c>。</remarks>
    async ValueTask<RentedConnection> RentAsync(ExecutionTag tag, RouteIntent intent, CancellationToken ct)
    {
        var cluster = tag.Cluster;
        var ambient = QuickSqlTransaction.For(this);
        var route = DataSourceRouting.Resolve(cluster, intent, tag.Choice, ambient is not null,
            FlowWroteCluster(cluster), _sources.ReplicaCount(cluster));

        // 环境事务在位时不取新连接：语句必须走那条被事务独占的连接，否则"自动 enlist"是假的（§6 (3)）。
        if (ambient is not null)
        {
            if (!string.Equals(ambient.Cluster, cluster, StringComparison.Ordinal))
                throw new QuickSqlException($"当前环境事务钉在集群 {ambient.Cluster} 上，而这条 {tag.Operation} 语句要落在集群 {cluster}：一场事务只有一条连接。要分两座库写就分别开事务、各自提交。");

            return new RentedConnection(ambient.Connection, ambient.DbTransaction, ownsConnection: false, route);
        }

        // 会话级连接复用（§8.4）：DbContext 绑的连接在位时直接走它，不再每句取还。
        // 键是 (集群, 角色)：会话因此可以持有两条连接，而"同一会话里读到刚写的那一份"由那把写后读
        // 戳子保证（它把会话内的读送回主库那一条），不是靠"只留一条连接"留出来的。
        if (_sessionConnections.Value?.TryGet(cluster, route.Role, out var sessionConn) == true)
            return new RentedConnection(sessionConn, transaction: null, ownsConnection: false, route);

        // 工厂在位但这一格还没建——首次用到时调工厂取连接，之后同一会话复用那一条。
        // 从库因此是"会话钉住一颗"而不是"每句换一颗"：一轮询落在会话上，会话内的两次读才互相看得见对方的顺序。
        if (_sessionFactory.Value is { } factory)
        {
            var conn = factory.Factory(cluster, route.Role);
            BindSessionConnection(cluster, route.Role, conn);

            return new RentedConnection(conn, transaction: null, ownsConnection: false, route);
        }

        var connectors = Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Provider.Kind.ToString().ToLowerInvariant(), failure);

        var connection = connectors.CreateConnection(_sources.ConnectionStringFor(cluster, route.Role));

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await ConnectionRelease.ReleaseAsync(connection).ConfigureAwait(false);
            throw;
        }

        return new RentedConnection(connection, transaction: null, ownsConnection: true, route);
    }

    DbCommand Prepare(DbConnection connection, DbTransaction? transaction, DialectSql sql)
    {
        var command = Provider.Connectors.CreateCommand(connection);

        command.Transaction = transaction;
        command.CommandText = sql.Text;
        command.CommandTimeout = CommandTimeoutSeconds;

        foreach (var parameter in sql.Parameters)
            Bind(command, parameter);

        return command;
    }

    /// <summary><see cref="AdoParameter"/> 是 provider 已经算好的描述，这里只做「搬进驱动参数对象」这一件事。
    /// <see cref="DbType.Object"/> 表示 binder 有意不表态，硬设会让部分驱动连非 DBNull 的值也拒掉。
    ///
    /// <para><b>名字在搬进去之前摘掉方言前缀</b>（v0.32 第二格，一台真库逼出来的；v0.34 第二笔起这句只对
    /// <em>没有声明收带前缀名</em>的四家成立，SQL Server 走 <see cref="ISqlDialect.DriverAcceptsPrefixedParameterNames"/>
    /// 那一格原样递 —— 摘法本身一字未动，动的只是"谁来付那份 <c>Substring</c>"）。
    /// <see cref="SqlWriter.NextParameterName"/> 造的是一颗<em>两用</em>名字：它既是 SQL 文本里的那颗占位符，
    /// 又是交给驱动的参数名。前四家的驱动两种写法都认，所以这一格从来量不到；虚谷那颗<em>只认不带前缀的</em> ——
    /// 交 <c>":p0"</c> 得到 E10036「未绑定变量 P0」，交 <c>"p0"</c> 才绑得上。
    /// 出处：<c>obj/logs/v32-recon-orm-1.log</c> 的 O-10…O-14（本仓库自己产出的读/写/改/分页/删五条，
    /// 在改之前<em>五条全红</em>），与 <c>obj/logs/v32-recon-bind2-1.log</c>（同五条文本报进去、只把名字的前缀摘掉，
    /// 九问全绿）。也就是说这一格不是"某家偏好"，是"第五家的每一条参数化语句今天都跑不通"。</para>
    ///
    /// <para><b>文本一颗没动</b>：<see cref="AdoParameter.Name"/> 的对外口径与六份黄金样本、
    /// 与 <c>SqlLogging</c> 按名字回填值那条链都还是带前缀的原样 —— 摘前缀只发生在跨进驱动对象的那一步。
    /// 对前四家这是同义改写而不是行为改动，而"应当如此"不算证据，要用 §13.4 那 95 条在授权实例上复跑钉住。</para>
    ///
    /// <para><b>这一颗的价格已经进 §12.8 的账，而且它是每颗参数一次的</b>：<c>Substring</c> 每绑一颗就多一条
    /// 2–5 字符的名字串。门禁当场把它量出来了 —— 第 6 行 ①（1 000 行 × 29 列 = 29 000 颗）+928 168 B、
    /// 第 11 行两腿（各 26 颗）各 +832 B、第 12 行（1 颗 / 3 颗）+32 B 与 +96 B，五枚钉一起重钉；
    /// 而没绑进驱动的那几腿（第 6 行 ②、第 10 行两格、第 11 行的地板）逐字未动，把 <c>Substring</c> 按回原样
    /// 之后 15 处钉全部复齐（<c>obj/logs/v32-gate-15/16/17.log</c>，归属写在 <c>Gates.Baselines.BulkInsertBytes</c>）。
    /// 想省它有三条路，前两条要动契约：<c>AdoParameter.Name</c> 拆成"文本名 + 驱动名"两颗、或在
    /// <see cref="ISqlDialect"/> 上声明驱动侧要不要前缀 —— "两格都记 §14，裁决归用户"这一句到 v0.34 为止；
    /// <b>第二笔按「全部完善」的整批授权把前两条一起落了</b>：
    /// <see cref="AdoParameter.DriverName"/> 是第 ① 条的载体（provider 点名给驱动的名字，给就不动），
    /// <see cref="ISqlDialect.DriverAcceptsPrefixedParameterNames"/> 是第 ② 条的声明位（本家驱动收带前缀名
    /// 且有活库见证才许 true）—— 今天只有 SQL Server 为 true，摘法留给必须摘的虚谷与没有复点的另三家。
    /// 第三条（在执行器里按 <see cref="SqlWriter"/> 的命名形状缓存摘好的名字）<b>维持不做</b>：
    /// 它不用动契约，但把执行器与那颗名字的拼法焊死，而前两条落地之后它已经没有存在理由。</para></summary>
    void Bind(DbCommand command, AdoParameter parameter)
    {
        var ado = command.CreateParameter();

        var prefix = Provider.Dialect.ParameterPrefix;
        var name = parameter.Name;

        ado.ParameterName = parameter.DriverName
            ?? (Provider.Dialect.DriverAcceptsPrefixedParameterNames
                ? name
                : name.Length > 0 && name[0] == prefix ? name.Substring(1) : name);
        ado.Value = parameter.Value ?? DBNull.Value;

        if (parameter.DbType != DbType.Object)
            ado.DbType = parameter.DbType;

        if (parameter.Size > 0)
            ado.Size = parameter.Size;

        command.Parameters.Add(ado);
    }

    void Succeeded(DialectSql sql, ExecutionTag tag, long started, int rows, DataSourceRoute route)
    {
        var elapsed = Elapsed(started);

        if (Aop.Executed is { } executed)
            Aop.Observe(() => executed(new CommandLogEvent(sql, tag.Operation, tag.Tables, elapsed, rows, FirstRowSummary: null, route)));

        if (elapsed >= Aop.SlowQueryThresholdMilliseconds && Aop.SlowQuery is { } slow)
            Aop.Observe(() => slow(new SlowCommandEvent(sql, elapsed, Aop.SlowQueryThresholdMilliseconds)));
    }

    void Failed(Exception exception, DialectSql sql, long started)
    {
        // 硬规则 3：钩子自己炸了不许盖掉业务异常，所以走 Observe 的吞-and-转投边界。
        if (Aop.Error is { } error)
            Aop.Observe(() => error(new CommandErrorEvent(exception, sql.Text, Elapsed(started))));
    }

    static long Elapsed(long started) => Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>§10.1 执行前的那道关口，两道按<em>固定顺序</em>走：<b>先决策类闸门</b>（<see cref="CheckNotCancelled"/>，
    /// 它可以把这句叫停），<b>后观察类播报</b>（<see cref="QuickSqlAop.Executing"/>，"这一句要发出去了"）。
    /// 顺序是这一格的全部裁决，所以两条合在一处、不留在六个出口上各自排：被闸门取消的句子不该在日志里
    /// 留下一行"正在执行"，而那一句也确实一个字节都没交给驱动。
    /// <para>位置在 <c>Stopwatch.GetTimestamp()</c> 之前，于是播报这一刻手上只有文本、操作名与表名 ——
    /// 耗时、行数与实际路由都还没被决定，那三格在 <see cref="Succeeded"/> 那一发上。未订阅时两条各是一次分支预测，
    /// §12.8 的账不动。</para>
    /// <para><b>六个出口都在这一道门上</b>：reader、scalar、stream、non-query、带结果集的写、装载通道。
    /// 新增一条写出口从这一行旁边绕过去，就是同时绕过了禁整表写的硬闸与"这句话发过"的那一行日志。</para></summary>
    void BeforeExecute(DialectSql sql, ExecutionTag tag)
    {
        CheckNotCancelled(sql, tag);

        if (Aop.Executing is { } executing)
            Aop.Observe(() => executing(new CommandExecutingEvent(sql, tag.Operation, tag.Tables)));
    }

    /// <summary>§10.1 决策类钩子的发射点：执行前最后一道闸门。未订阅时零成本（一次分支预测），
    /// 订阅后返回 <c>true</c> = 取消执行、抛 <see cref="QuickSqlCommandCancelledException"/>。
    /// 典型用法：拦没有 WHERE 的 UPDATE/DELETE（生产禁整表写硬闸）。</summary>
    void CheckNotCancelled(DialectSql sql, ExecutionTag tag)
    {
        if (Aop.CommandTerminating is not { } hook)
            return;

        var ev = new CommandTerminatingEvent(sql.Text, tag.Operation, tag.Tables, sql.Parameters);

        if (hook(ev))
            throw new QuickSqlCommandCancelledException(
                $"CommandTerminating 钩子取消了这条 {tag.Operation} 语句（涉及表：{string.Join("、", tag.Tables)}）。" +
                "如果要放行，请检查钩子的注册条件。");
    }

    /// <remarks>参数只能进属性初始化器、不能在方法体里再引用一次：主构造函数参数一旦"既被捕获进状态、
    /// 又被用来初始化成员"就是 CS9124，所以 <see cref="DisposeAsync"/> 读的是属性而不是参数。</remarks>
    sealed class RentedConnection(DbConnection connection, DbTransaction? transaction, bool ownsConnection, DataSourceRoute route) : IAsyncDisposable
    {
        public DbConnection Connection { get; } = connection;

        public DbTransaction? Transaction { get; } = transaction;

        /// <summary>这次借用是怎么决定用这条连接的（集群、角色、理由）。</summary>
        public DataSourceRoute Route { get; } = route;

        public async ValueTask DisposeAsync()
        {
            // 环境事务那条连接归事务，会话那条连接归会话，语句结束都不能把它还给池 ——
            // 连接上还有一个活着的事务（或还有后面的语句要用它），归还等于把别人的事务交给下一个借用者。
            if (ownsConnection)
                await ConnectionRelease.ReleaseAsync(Connection).ConfigureAwait(false);
        }
    }

    /// <remarks>同 <see cref="RentedConnection"/>：主构造函数参数不能既捕获又初始化（CS9124）。
    /// 连接那一半的处置整个交给 <paramref name="rental"/>，这里只负责命令 ——
    /// "谁自有的连接谁还"这句话因此只有 <see cref="RentedConnection.DisposeAsync"/> 一处。</remarks>
    sealed class Session(RentedConnection rental, DbCommand command) : IAsyncDisposable
    {
        public DbCommand Command { get; } = command;

        /// <summary>这句落在哪座集群、哪个角色、为什么 —— 归借用它的那次取连接判出来，观察钩子读它。</summary>
        public DataSourceRoute Route => rental.Route;

        public async ValueTask DisposeAsync()
        {
            await Command.DisposeAsync().ConfigureAwait(false);
            await rental.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>一次会话持有的连接：<b>按 (集群, 角色) 一格一条</b>。
    /// 它是会话级连接复用在读写分离之后唯一还成立的说法 —— 说"会话有一条连接"从这一版起不成立了，
    /// 而"会话对同一格只有一条"仍然成立，事务、装载通道与写后读那一档都靠这一条。</summary>
    internal sealed class SessionConnections
    {
        readonly Dictionary<(string, ConnectionRole), DbConnection> _bySlot = [];

        public bool TryGet(string cluster, ConnectionRole role, out DbConnection connection)
        {
            if (_bySlot.TryGetValue((cluster, role), out var found))
            {
                connection = found;
                return true;
            }

            connection = null!;
            return false;
        }

        public void Set(string cluster, ConnectionRole role, DbConnection connection) => _bySlot[(cluster, role)] = connection;

        /// <summary>按引用摘掉一格。会话释放时逐条还池，槽位里不留悬空的引用。</summary>
        public void Remove(DbConnection connection)
        {
            foreach (var slot in _bySlot.Where(kv => ReferenceEquals(kv.Value, connection)).Select(kv => kv.Key).ToArray())
                _bySlot.Remove(slot);
        }

        public IReadOnlyList<DbConnection> Connections => [.. _bySlot.Values];
    }

    /// <summary>会话工厂槽位的本体：(工厂, 主人) 一对。主人在 <see cref="ClearSessionFactory"/> 里按引用比一次，
    /// 与 <see cref="UnbindSessionTransaction"/> 那颗"只摘自己那一场"的判法同一形状。</summary>
    sealed class SessionFactorySlot(object owner, Func<string, ConnectionRole, DbConnection> factory)
    {
        internal object Owner { get; } = owner;

        internal Func<string, ConnectionRole, DbConnection> Factory { get; } = factory;
    }

    /// <summary>"这条流上哪几座集群写过"的一本账。<b>槽位里放的是这本账而不是一个布尔</b>：
    /// 异步被调方改不动父帧的槽位（<see cref="QuickSqlTransaction"/> 的 remarks 记过同一条规则），
    /// 但父帧与子帧<em>共用同一枚引用</em>，所以写侧在深处 <see cref="Mark"/> 一笔、外层的下一次读看得见。
    ///
    /// <para>账只增不减：一座集群被写过，这条流上它之后的读一律落主库。复制延迟测不到（没有 SPI 槽位），
    /// 所以这里唯一的保守方向是"写过就当从库还旧着"。</para></summary>
    internal sealed class FlowWrites
    {
        readonly List<string> _clusters = [];

        public bool Contains(string cluster)
        {
            lock (_clusters)
                return _clusters.Contains(cluster, StringComparer.Ordinal);
        }

        public void Mark(string cluster)
        {
            lock (_clusters)
                if (!_clusters.Contains(cluster, StringComparer.Ordinal))
                    _clusters.Add(cluster);
        }
    }
}

/// <summary>把自己自有的连接交回那一步的<em>唯一写法</em>：<b>先 <see cref="DbConnection.Close"/>，
/// 再 Dispose</b>（同步或异步那两支都一样）。这不是防御性冗余，是第五族逼出来的一格：
///
/// <para><b>虚谷那颗驱动的 <c>Dispose()</c> 与 <c>DisposeAsync()</c> 不把会话还回去，只有 <c>Close()</c> 还</b>。
/// 四支形状在同一颗进程里用同一条连接串逐支量过（探针 <c>obj/probe-xugu/Program.cs</c>，
/// 每支都是 OpenAsync 之后只换"还"的写法）：
/// <c>Close()</c> 单用 ∶ <c>Close()</c> + <c>DisposeAsync()</c> 两支各跑满 3000 轮不红；
/// <c>Dispose()</c> 单用 ∶ <c>DisposeAsync()</c> 单用两支都<b>正好在第 1000 次 Open 红</b>，
/// 与第五支"开完根本不还、只交给终结器"红在同一颗数上【实测·obj/logs/v40-xugu-release-shapes.log，
/// 五支逐支的读数与重跑法都在那份日志里】。
/// 也就是说这一族的 <c>Dispose</c> 覆写<em>不</em>等价于 <c>Close</c>，会话要等终结器跑过才交回 ——
/// 于是"有没有 Dispose"这一问在这一家根本不成立，成立的是"有没有 Close"。
/// 另两道底：同一串累计 7500 次 Open→Close→Dispose 不红，300 条<em>同时</em>在场也不红，
/// 所以那 1000 限的是"没还回去的会话条数"，既不是登录次数也不是并发条数。</para>
///
/// <para><b>为什么这一句写给全部五族而不是开一个第五族的洞</b>：另四族的 <c>Dispose()</c> 里做的本来就是
/// <c>Close()</c> 那一件事（把连接还给池），先 Close 再 Dispose 对它们是同义反复、不新增语义；
/// 而"谁自有的连接谁还"这句话因此仍然只有 <see cref="RentedConnection.DisposeAsync"/> 一处，
/// 只是那一处从此走本类的两个入口。</para></summary>
internal static class ConnectionRelease
{
    public static void Release(DbConnection connection)
    {
        connection.Close();
        connection.Dispose();
    }

    public static async ValueTask ReleaseAsync(DbConnection connection)
    {
        connection.Close();

        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

