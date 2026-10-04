using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using FastSql.Aop;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Transactions;

namespace FastSql.Execution;

/// <summary>一次执行的标签：操作名 + 涉及的表，只喂观察类钩子与日志（§10.1）。
/// 由查询对象算一次并随对象复用，不在每行上重算。</summary>
public sealed record ExecutionTag(string Operation, IReadOnlyList<string> Tables);

/// <summary>§3 的第 5 层：连接取还、参数落地、超时、观察类钩子。
/// 这一层只接 <see cref="DialectSql"/>，既不认识查询模型也不生成文本 ——
/// 所以「<c>ToSql()</c> 不连库」（§3）在这里是结构性的，不靠约定。</summary>
public sealed class AdoExecutor
{
    readonly string _connectionString;

    // ── 会话级连接复用（§8.4） ────────────────────────────────────────
    // DbContext 在 Set<T>() 时注册一只工厂（只是 delegate 引用，不建连接），
    // AdoExecutor.OpenAsync 在真正需要连接时调工厂取一条，之后同一会话的所有语句复用这条连接。
    // AsyncLocal 的传播语义与事务环境槽位一致：同一 async 执行流内的子调用都能看到，兄弟/父帧看不到。

    static readonly AsyncLocal<DbConnection?> _sessionConnection = new();
    static readonly AsyncLocal<Func<DbConnection>?> _sessionConnectionFactory = new();

    internal static void SetSessionFactory(Func<DbConnection> factory) => _sessionConnectionFactory.Value = factory;

    internal static void ClearSessionFactory() => _sessionConnectionFactory.Value = null;

    internal static void BindSessionConnection(DbConnection connection) => _sessionConnection.Value = connection;

    internal static void UnbindSessionConnection(DbConnection connection)
    {
        if (ReferenceEquals(_sessionConnection.Value, connection))
            _sessionConnection.Value = null;
    }

    public AdoExecutor(IDbProvider provider, string connectionString, FastSqlAop aop, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(aop);

        Provider = provider;
        _connectionString = connectionString;
        Aop = aop;
        CommandTimeoutSeconds = commandTimeoutSeconds;
    }

    public IDbProvider Provider { get; }

    public FastSqlAop Aop { get; }

    public int CommandTimeoutSeconds { get; }

    /// <summary><paramref name="expectedRows"/> 取自模型的 <c>Take(n)</c>：预容量是 §12.7 E 的硬要求，
    /// 传 null 表示行数确实未知（无 <c>Take</c> 的全量查询）。</summary>
    public async Task<List<T>> QueryAsync<T>(DialectSql sql, Func<DbDataReader, T> map, ExecutionTag tag, int? expectedRows, CancellationToken ct)
    {
        CheckNotCancelled(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, ct).ConfigureAwait(false);
        await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            var list = new List<T>(expectedRows ?? 0);

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

            Succeeded(sql, tag, started, list.Count);
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
        CheckNotCancelled(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, ct).ConfigureAwait(false);
        await using var reader = await session.Command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Succeeded(sql, tag, started, 0);
                return default;
            }

            var value = map(reader);
            Succeeded(sql, tag, started, 1);

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
        CheckNotCancelled(sql, tag);

        var started = Stopwatch.GetTimestamp();
        var rows = 0;

        await using var session = await OpenAsync(sql, ct).ConfigureAwait(false);
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
            Succeeded(sql, tag, started, rows);
        }
    }

    // ── 写出口（§6 示例里的 ExecuteAffrowsAsync 走的就是这里） ──────────

    /// <summary>非查询出口，返回驱动报回的受影响行数。多值 INSERT 时这个数就是行数（§12.4 第 2 项）。</summary>
    public async Task<int> NonQueryAsync(DialectSql sql, ExecutionTag tag, CancellationToken ct)
    {
        CheckNotCancelled(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, ct).ConfigureAwait(false);

        try
        {
            var rows = await session.Command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            Succeeded(sql, tag, started, rows);
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
        CheckNotCancelled(sql, tag);

        var started = Stopwatch.GetTimestamp();

        await using var session = await OpenAsync(sql, ct).ConfigureAwait(false);
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

            Succeeded(sql, tag, started, rows);
            return rows;
        }
        catch (Exception ex)
        {
            Failed(ex, sql, started);
            throw;
        }
    }

    // ── 事务（§6 (1)(3)） ────────────────────────────────────────────

    /// <summary>开一条真事务并<b>就地</b>写进环境槽位。整个方法必须是同步的，两个理由见
    /// <see cref="IFastSqlTransaction"/>：一是 ADO.NET 的 begin/commit/rollback 没有异步重载，
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
    /// 于是 <see cref="FastSqlTransaction.Commit"/> 与 <see cref="FastSqlTransaction.Rollback"/>
    /// 拆的是同一条路。上面那段"嵌套 = SAVEPOINT"的语义两条路共用，没有第二套。</para>
    ///
    /// <para><b>传播</b>（v0.38）：<paramref name="propagation"/> 为 <see cref="TransactionPropagation.RequiresNew"/>
    /// 时挂起当前环境事务并新开一条独立事务，结束后恢复被挂起的那条。</para></summary>
    public FastSqlTransaction BeginTransaction(IsolationLevel level, TransactionPropagation propagation = TransactionPropagation.Requires)
    {
        var ambient = FastSqlTransaction.Ambient;

        // Requires：外层事务在位时，创建保存点而不是新开事务。
        // 隔离级别在保存点路径上被忽略（SAVEPOINT 继承外层事务的隔离级别，这是 SQL 标准行为）。
        if (propagation == TransactionPropagation.Requires && ambient is not null)
        {
            if (!ReferenceEquals(ambient.Executor, this))
                throw new FastSqlException(
                    $"当前执行流上已经有一条属于另一个 orm 实例的环境事务：不能把{Provider.Kind}的语句塞进另一家库的事务里。" +
                    "如果要同时操作两个库，请在不同的执行流（不同的 async 方法或 Task.Run）里分别开事务。");

            var savepointName = $"fastsql_sp_{ambient._nextSavepointId++}";

            // 两条路做的是同一件事，差别只在"这句话由驱动发还是由我们发"（§4 那颗 SupportsDriverSavepoints）。
            // 走哪条路在这里定一次，并把答案带进那只保存点事务 —— 创建它的那条路决定拆除它的那条路，
            // 不允许出现"驱动发的保存点由文本去释放"这种跨路拆卸。
            var byDriver = Provider.Dialect.SupportsDriverSavepoints;

            if (byDriver)
                ambient.DbTransaction.Save(savepointName);
            else
                ExecuteSavepoint(ambient, SavepointStep.Create, savepointName);

            var nested = new FastSqlTransaction(ambient, savepointName, textSavepoints: !byDriver);

            nested.Install();

            return nested;
        }

        // RequiresNew：挂起当前环境事务（如果有），新开一条独立事务。
        // 跨 executor 的 RequiresNew 允许：新事务用新连接，与被挂起的那条互不干扰。
        FastSqlTransaction? suspended = null;
        if (ambient is not null)
        {
            suspended = ambient;
            FastSqlTransaction.ClearAmbient();
        }

        var connectors = Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Provider.Kind.ToString().ToLowerInvariant(), failure);

        // 会话级连接复用（§8.4）：DbContext 绑的连接在位时直接用它开事务，不再另取一条。
        // 工厂在位但连接还没建时，先调工厂取连接再开事务。
        // 事务结束后连接不归池——它还在 AsyncLocal 里，后续语句继续复用。
        var sessionConn = _sessionConnection.Value;

        if (sessionConn is null && _sessionConnectionFactory.Value is { } factory)
        {
            sessionConn = factory();
            _sessionConnection.Value = sessionConn;
        }

        var ownsConnection = sessionConn is null;
        var connection = sessionConn ?? connectors.CreateConnection(_connectionString);

        try
        {
            if (ownsConnection)
                connection.Open();

            var transaction = new FastSqlTransaction(this, connection, connection.BeginTransaction(level), ownsConnection, suspended);

            transaction.Install();

            return transaction;
        }
        catch
        {
            if (ownsConnection)
                connection.Dispose();
            throw;
        }
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
    internal void ExecuteSavepoint(FastSqlTransaction transaction, SavepointStep step, string savepointName)
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
            default: throw new FastSqlException($"不存在的保存点步骤：{step}（新增了 SavepointStep 的成员而没在这里给出对应的文本出口）");
        }

        using var command = Prepare(transaction.Connection, transaction.DbTransaction, new DialectSql(writer.ToSqlString(), writer.Parameters));

        command.ExecuteNonQuery();
    }

    async ValueTask<Session> OpenAsync(DialectSql sql, CancellationToken ct)
    {
        // 环境事务在位时不取新连接：语句必须走那条被事务独占的连接，否则"自动 enlist"是假的（§6 (3)）。
        if (FastSqlTransaction.For(this) is { } ambient)
            return new Session(ambient.Connection, Prepare(ambient.Connection, ambient.DbTransaction, sql), ownsConnection: false);

        // 会话级连接复用（§8.4）：DbContext 绑的连接在位时直接走它，不再每句取还。
        if (_sessionConnection.Value is { } sessionConn)
            return new Session(sessionConn, Prepare(sessionConn, transaction: null, sql), ownsConnection: false);

        // 工厂在位但连接还没建——首次执行时调工厂取连接，之后复用。
        if (_sessionConnectionFactory.Value is { } factory)
        {
            var conn = factory();
            _sessionConnection.Value = conn;
            return new Session(conn, Prepare(conn, transaction: null, sql), ownsConnection: false);
        }

        var connectors = Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Provider.Kind.ToString().ToLowerInvariant(), failure);

        var connection = connectors.CreateConnection(_connectionString);

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new Session(connection, Prepare(connection, transaction: null, sql), ownsConnection: true);
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

    void Succeeded(DialectSql sql, ExecutionTag tag, long started, int rows)
    {
        var elapsed = Elapsed(started);

        if (Aop.Executed is { } executed)
            Aop.Observe(() => executed(new CommandLogEvent(sql, tag.Operation, tag.Tables, elapsed, rows, FirstRowSummary: null)));

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

    /// <summary>§10.1 决策类钩子的发射点：执行前最后一道闸门。未订阅时零成本（一次分支预测），
    /// 订阅后返回 <c>true</c> = 取消执行、抛 <see cref="FastSqlCommandCancelledException"/>。
    /// 典型用法：拦没有 WHERE 的 UPDATE/DELETE（生产禁整表写硬闸）。</summary>
    void CheckNotCancelled(DialectSql sql, ExecutionTag tag)
    {
        if (Aop.CommandTerminating is not { } hook)
            return;

        var ev = new CommandTerminatingEvent(sql.Text, tag.Operation, tag.Tables, sql.Parameters);

        if (hook(ev))
            throw new FastSqlCommandCancelledException(
                $"CommandTerminating 钩子取消了这条 {tag.Operation} 语句（涉及表：{string.Join("、", tag.Tables)}）。" +
                "如果要放行，请检查钩子的注册条件。");
    }

    /// <remarks>参数只能进属性初始化器、不能在方法体里再引用一次：主构造函数参数一旦"既被捕获进状态、
    /// 又被用来初始化成员"就是 CS9124，所以 <see cref="DisposeAsync"/> 读的是属性而不是参数。</remarks>
    sealed class Session(DbConnection connection, DbCommand command, bool ownsConnection) : IAsyncDisposable
    {
        public DbCommand Command { get; } = command;

        public async ValueTask DisposeAsync()
        {
            await Command.DisposeAsync().ConfigureAwait(false);

            // 环境事务那条连接归事务，语句结束不能把它还给池 —— 连接上还有一个活着的事务，
            // 归还等于把别人的事务交给下一个借用者。
            if (ownsConnection)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
