using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FastSql.Aop;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Metadata.Fluent;
using FastSql.Providers;
using FastSql.Query;
using FastSql.Query.Filters;
using FastSql.Transactions;
using FastSql.Query.Model;
using FastSql.Schema;
using FastSql.Core.Schema;
using FastSql.Translation;
using FastSql.Sql.Ast;
using FastSql.Tracking;
using FastSql.Write;

namespace FastSql;

/// <summary>组装根的输入。<b>没有</b> <c>Type</c> 字段、没有 <c>Type.GetType(string)</c>：
/// provider 由调用方直接构造并交进来（§12.7 F），驱动的反射只发生在
/// <see cref="DriverLoading.DriverRegistry"/> 拿 <see cref="System.Data.Common.DbProviderFactory"/> 那一步。</summary>
public sealed class FastSqlOrmOptions
{
    public required IDbProvider Provider { get; init; }

    /// <summary>连接串留在字符串形态：内核不认识任何驱动的连接串构建器（§1.2 原则 3）。</summary>
    public required string ConnectionString { get; init; }

    public FastSqlAop Aop { get; init; } = new();

    /// <summary>Fluent 配置（§10.2 优先级 1）。null 表示只用 DataAnnotations。</summary>
    public ModelConfiguration? Model { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>§9.4。默认 <see cref="PagingOrderingPolicy.Throw"/>：宁可少跑一条 SQL，也不要一个"数据库不保证的行序"被当成稳定的分页结果。</summary>
    public PagingOrderingPolicy PagingOrdering { get; init; } = PagingOrderingPolicy.Throw;

    /// <summary>§9.5 结构同步策略。默认 <see cref="SchemaSyncPolicy.Disabled"/>：生产库常禁 DDL 权限，不自动执行。
    /// Development 环境可设为 <see cref="SchemaSyncPolicy.Execute"/> 自动建表；非 Development 环境要求同时设 <see cref="ForceSchemaSync"/>，否则启动期抛错。</summary>
    public SchemaSyncPolicy SchemaSync { get; init; } = SchemaSyncPolicy.Disabled;

    /// <summary>§9.5。非 Development 环境下强制执行结构同步的开关。默认 false：光设 <see cref="SchemaSync"/> 不够，还要这一颗显式环境变量才放行。</summary>
    public bool ForceSchemaSync { get; init; } = false;
}

/// <summary>组合根（§7 生命周期表里的单例）。只持有连接工厂、元数据缓存与方言，<b>不持有连接</b>：
/// 连接按语句从驱动的连接池取（§3 第 5 层）。可以跨线程共享，<see cref="DbContext"/> 才需要 Scoped。</summary>
public sealed class FastSqlOrm
{
    ExpressionTranslator? _expressions;
    ProjectionTranslator? _projections;

    /// <summary>G2 兜底口：元数据注册表由内核反射构建（§12.7 A 的 G2 级）。
    /// 裁剪/AOT 场景请走 <see cref="FastSqlOrm(FastSqlOrmOptions, IEntitySchemaRegistry)"/>，把 G1 生成的注册表传进来。</summary>
    [RequiresUnreferencedCode("G2 兜底注册表按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("G2 兜底注册表编译读写委托。")]
    public FastSqlOrm(FastSqlOrmOptions options)
        : this(options, new EntitySchemaRegistry(new ContractResolver(options.Provider, options.Model)))
    {
    }

    /// <summary>干净口：注册表由外部提供（G1 生成、测试替身、或宿主自己的实现）。这条路径构造期零反射。</summary>
    public FastSqlOrm(FastSqlOrmOptions options, IEntitySchemaRegistry schemas)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(options.Provider);

        Options = options;
        Provider = options.Provider;
        Schemas = schemas;
        Aop = options.Aop;
        PagingOrdering = options.PagingOrdering;
        Compiler = new QueryCompiler(Provider);
        Ado = new AdoExecutor(Provider, options.ConnectionString, options.Aop, options.CommandTimeoutSeconds);
        Filters = new FilterRegistry(this);
    }

    /// <summary>§4 一行注册的 DI 侧：已知工厂、只要一行组装根。工厂由调用方递进来（宿主自己的容器、
    /// 或者 <see cref="FastSql.DriverLoading.DriverRegistry"/> 都行），provider 由生成器在编译期登记进
    /// <see cref="FastSql.Providers.ProviderRegistry"/> 的表里查出来。走 G2 兜底注册表，所以带 RUC/RDC；
    /// 要干净路径就照 §1.7 的 <c>new FastSqlOrm(options, schemas)</c> 姿势。</summary>
    [RequiresUnreferencedCode("Create 组装的是 G2 兜底构造器；AOT 场景请按 §1.7 用干净口。")]
    [RequiresDynamicCode("Create 组装的是 G2 兜底构造器；AOT 场景请按 §1.7 用干净口。")]
    public static FastSqlOrm Create(DbKind kind, string connectionString, DbProviderFactory driverFactory)
    {
        if (!ProviderRegistry.TryCreate(kind, driverFactory, out var provider))
            throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记。可能原因：① 生成器 FastSql.Generators 未作为 Analyzer 挂进编译；② 该 provider 程序集未被引用；③ 该 provider 类未带 [FastSqlProvider] 或缺 (DbProviderFactory) 公开构造器。已登记：{string.Join(", ", ProviderRegistry.Registered)}。");
        return new FastSqlOrm(new FastSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
        });
    }

    /// <summary>§4 一行注册的清单侧：只报 <see cref="DbKind"/> 与连接串，工厂由
    /// <see cref="FastSql.DriverLoading.DriverRegistry"/> 按 <c>FastSqlDrivers.json</c> 现场解析。
    /// 与上一颗同样依赖生成器登记；不同在这一颗会真去读清单文件，所以现场没驱动、清单没复制到输出目录
    /// 时抛的是 <c>DriverNotAvailableException</c>（消息带驱动名与清单路径）而不是这里的"未登记"。</summary>
    [RequiresUnreferencedCode("Create 组装的是 G2 兜底构造器，且工厂解析读 Instance 静态字段（§12.7 F）。")]
    [RequiresDynamicCode("Create 组装的是 G2 兜底构造器。")]
    public static FastSqlOrm Create(DbKind kind, string connectionString)
    {
        var driverKey = ProviderRegistry.DriverKeyOf(kind)
            ?? throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记（生成器未挂或 provider 未引用）。已登记：{string.Join(", ", ProviderRegistry.Registered)}。");
        var factory = FastSql.DriverLoading.DriverRegistry.Load().Factory(driverKey);
        return Create(kind, connectionString, factory);
    }

    public FastSqlOrmOptions Options { get; }

    public IDbProvider Provider { get; }

    public IEntitySchemaRegistry Schemas { get; }

    public FastSqlAop Aop { get; }

    public QueryCompiler Compiler { get; }

    public AdoExecutor Ado { get; }

    public PagingOrderingPolicy PagingOrdering { get; }

    /// <summary>全局过滤器（§10.7 的软删 / 多租户）。<b>一个 orm 实例一份，注册在启动期做完</b>，
    /// 之后读路径与 <see cref="Update{T}"/> / <see cref="Delete{T}"/> 两条链都会带上它。
    /// 它与 <see cref="Aop"/> 的分工要说清：<c>Aop</c> 上的 <c>CudBuilding</c> 是"整句文本改写的地方"
    /// （软删那句 <c>DELETE → UPDATE</c> 归它，约束 3），而这一份是"谓词从哪儿来"（约束 1、2、4）。
    /// <b>今天它管不到 <c>SaveChanges</c> 与批量</b>：那两条路上的 <c>WHERE</c> 长在 <c>EntityTracker</c> 上，
    /// 欠账与判据在 §10.7 的 v0.26 落地记录、§14 的 P1 余项那一格。</summary>
    public FilterRegistry Filters { get; }

    /// <summary>lambda → AST 的归约器。按需构造：只有真正走到链式翻译的调用方才为它付 AOT 代价（§5.10）。
    /// 标注只能落在 <c>get</c> 上 —— <c>RequiresUnreferencedCode</c> 的目标是方法，属性本身不是合法目标（CS0592）。</summary>
    internal ExpressionTranslator Expressions
    {
        [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
        [RequiresDynamicCode("不可约子表达式的退路是 LambdaExpression.Compile()。")]
        get => _expressions ??= new ExpressionTranslator(Provider, Schemas, NewTableRef);
    }

    internal ProjectionTranslator Projections
    {
        [RequiresUnreferencedCode("转调 ExpressionTranslator。")]
        [RequiresDynamicCode("转调 ExpressionTranslator。")]
        get => _projections ??= new ProjectionTranslator(Expressions);
    }

    /// <summary>读侧入口（§5.4）。返回的对象是不可变的值，可以复用、可以传出去、不需要 Clone（§5.11）。
    /// <b>没有非泛型口</b>：按运行期 <see cref="Type"/> 造 <c>FastSqlQuery&lt;T&gt;</c> 只能靠
    /// <c>Activator.CreateInstance</c> + <c>MakeGenericMethod</c>，那正是 §12.7 F 点名要避开的形态；
    /// 动态表名/影子表请走 <c>ModelConfiguration</c> 的表名映射，而不是把 T 退化成 Type。</summary>
    /// <remarks>标注在这里是<b>整条读路径的入口税</b>：G1 预翻译没落地之前，<c>Select&lt;T&gt;()</c> 往下每一步都要
    /// 反射与表达式树编译（§15 的"标注传染面"）。等 G1 接管后应把这一串标注从入口开始逐个摘掉 ——
    /// 摘不干净就说明 §14 的"G1 路径全链路 0 反射"闸门没达成。</remarks>
    [RequiresUnreferencedCode("G2/G3 读路径：闭包求值与投影组装按运行期 Type 反射。")]
    [RequiresDynamicCode("G2/G3 读路径：投影与比较委托要编译表达式树，AOT 下不可用。")]
    public IQuery<T> Select<T>() where T : class => new FastSqlQuery<T>(RootEngine(typeof(T)));

    /// <summary>根模型：一个表位 + 实体投影。<b>取契约这一步就是 G2</b>（按 Type 反射解析实体），
    /// 所以标注跟着 <see cref="Select{T}"/>；换成 G1 生成的 <see cref="IEntitySchemaRegistry"/> 之后，
    /// 反射从注册表里消失，这一串标注才能整体摘掉 —— 只摘一处会让"0 反射"的闸门变成假的。</summary>
    /// <param name="tracker">会话的跟踪器，null 就是这一条链不跟踪（§8.1「跟踪与否不是两套 API」的落点）。
    /// 它<em>不</em>进模型：模型文本里没有跟不跟踪这一行，所以 <c>orm.Select&lt;T&gt;()</c> 与
    /// <c>db.Set&lt;T&gt;().AsNoTracking()</c> 的 <c>Explain().ModelText</c> 逐字节相等（§13.2 ②）。</param>
    [RequiresUnreferencedCode("按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    internal QueryEngine RootEngine(Type clrType, ChangeTracker? tracker = null)
    {
        var table = NewTableRef(clrType, 0);
        var model = new QueryModel { Tables = [table], Plan = new EntityProjection(0) };

        return new QueryEngine(this, model, tracker);
    }

    /// <summary>造一个会话（§7 那行 <c>orm.CreateDbContext&lt;AppDbContext&gt;()</c>）。
    /// <b>这条路上有反射</b>：<c>Activator.CreateInstance</c> 按 <see cref="Type"/> 找构造函数，所以它带
    /// <c>RUC/RDC</c>，也正是 §7 那句「AOT 场景请自己 new」的原因 ——
    /// <c>new AppDbContext(orm)</c> 走的是普通构造调用，一行反射都没有，功能完全等价。
    /// <b>每次调用都是新会话</b>（新的身份映射），这里没有任何共享或池化：要共享有一份显式的口子，
    /// 而它把范围限在一条执行流上（<see cref="CreateSharedContext{TContext}"/>，§7）。</summary>
    /// <remarks>要求 <c>TContext</c> 有一个 <c>public TContext(FastSqlOrm orm)</c> 构造函数；没有的话
    /// <c>MissingMethodException</c> 会从这里抛出来，消息里点名类型而不点名签名，所以写错构造函数时先看这一句。</remarks>
    [RequiresUnreferencedCode("CreateDbContext 按 Type 反射构造 TContext（§12.7 F）；AOT 下请自己 new。")]
    [RequiresDynamicCode("CreateDbContext 按 Type 反射构造 TContext；AOT 下请自己 new。")]
    public TContext CreateDbContext<TContext>() where TContext : DbContext
        => (TContext) Activator.CreateInstance(typeof(TContext), this)!;

    /// <summary>§7 那行 <c>orm.CreateSharedContext&lt;AppDbContext&gt;()</c>：<b>在当前执行流上</b>取一只会话 ——
    /// 该流上第一次取时按 <see cref="CreateDbContext{TContext}"/> 建一只，之后每次取到的都是那一只，
    /// 于是同一一流里前后两个方法不必经 DI 注册也共享同一份身份映射（控制台、后台服务、一条链上的分层写法）。
    /// 它的共享范围不是"进程"也不是"线程"，而是<b>一条 async 执行流</b>，这正是 §7 那句「不会跨请求泄漏」的技术读法：
    /// <see cref="AsyncLocal{T}"/> 的写入<em>只在同步那一段回到调用方的帧</em>（v0.12 探针，见
    /// <see cref="FastSqlTransaction"/> 的 remarks 与 §6），所以 <c>await</c> 之后的子任务里建的那只留在它自己的分支里，
    /// 父帧和兄弟分支都取不到 —— 反过来，父帧先建、子任务后取，子任务拿到的就是父帧那一只（这是同一枚币的另一面）。</summary>
    /// <remarks>三处边界要跟 §7 的表对齐着读：① <b>槽位挂在实例上而不是类型上</b>，所以两只 <see cref="FastSqlOrm"/>
    /// 共用一条流时各拿各的会话，与 <see cref="FastSqlTransaction.For"/> 那句「两个 orm 实例（两套库）不能互相塞进对方的事务」同一条理由；
    /// ② <b>共享的是同一只 <see cref="ChangeTracker"/>，而它不是线程安全的</b> —— 父帧先建之后再 <c>Task.WhenAll</c> 扇出的并发分支
    /// 会同时改同一本账，这一档今天不设闸门（加了就是新公开语义，归 §14 P1 余项那一行），要并发就各自开流或各自 <see cref="CreateDbContext{TContext}"/>；
    /// ③ 会话<em>没有</em> <see cref="IDisposable"/>（§8 的理由：它不持有连接），所以"结束一条共享会话"= 让那条流出作用域，
    /// 或者在流末端复用它的场合自己按 <see cref="DbChangeTracker.Clear"/> 清账。</remarks>
    [RequiresUnreferencedCode("走 CreateDbContext 的反射构造（§12.7 F）；AOT 下请自己 new 并把实例传下去。")]
    [RequiresDynamicCode("走 CreateDbContext 的反射构造；AOT 下请自己 new。")]
    public TContext CreateSharedContext<TContext>() where TContext : DbContext
    {
        var type = typeof(TContext);

        for (var at = _shared.Value; at is not null; at = at.Next)
        {
            if (at.Type == type)
                return (TContext) at.Context;
        }

        // 先建、再把新节点挂到这条流自己的那份链表头上：赋值 AsyncLocal 是"换引用"而不是"改表"，
        // 所以父帧的链头永远不被子任务改动 —— 上面那句「不回写父帧」全靠这一行是赋值而不是 Add。
        var created = CreateDbContext<TContext>();
        _shared.Value = new SharedFrame(type, created, _shared.Value);

        return created;
    }

    /// <summary>本实例在当前执行流上的共享会话链表。节点不可变，头指针按流复制（<see cref="AsyncLocal{T}"/> 的语义）。</summary>
    readonly AsyncLocal<SharedFrame?> _shared = new();

    sealed record SharedFrame(Type Type, DbContext Context, SharedFrame? Next);


    /// <summary>FROM 序列里第 <paramref name="index"/> 个表位的别名。<b>由位置唯一决定</b>（§5.1），
    /// 所以形参名、重命名重构、<c>nameof</c> 都不影响产出的 SQL。</summary>
    internal static string AliasAt(int index) => "t" + index.ToString();

    /// <summary>FROM 序列里的一个表位。别名由位置生成（§5.1：<c>t0</c>/<c>t1</c>…），
    /// 所以形参名、重命名重构、<c>nameof</c> 都不影响产出的 SQL。</summary>
    internal TableRef NewTableRef(Type clrType, int index)
    {
        var contract = Schemas.Contract(clrType);

        return new TableRef(index, AliasAt(index), contract.Table, contract);
    }

    // ── 写侧入口（§5.4） ──────────────────────────────────────────────

    /// <summary>单行插入。<b>与 <see cref="Select{T}"/> 不同，这条路径上没有 RUC/RDC 入口税</b>：
    /// 写侧只向注册表要列契约与已编译的读写委托，不编译表达式树、不求值闭包 ——
    /// G1 注册表接进来之后整条写路径 0 反射（§14 的验收项），而读路径还要等预翻译。</summary>
    public IInsert<T> Insert<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new InsertEngine<T>(this, [entity]);
    }

    /// <summary>多行插入（§12.3 的 <c>L3</c> 多值语句 + 客户端分块）。<b>不叫 <c>Insert</c></b>，
    /// 因为 <c>Insert(T)</c> 与 <c>Insert(IEnumerable&lt;T&gt;)</c> 同族重载传集合时必然选中前者
    /// （identity 转换胜过隐式引用转换），于是 <c>orm.Insert(list)</c> 会把 <c>List&lt;Customer&gt;</c>
    /// 当成实体去建契约 —— 规格 §12.3 原稿的那句已按 <c>InsertRange</c> 改写（v0.12 回写）。</summary>
    public IInsert<T> InsertRange<T>(IEnumerable<T> rows) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        // 立刻物化：延迟到执行才枚举的话，AuditValue 填充与"看到的那份数据"就可能不是同一份。
        return new InsertEngine<T>(this, rows as T[] ?? [.. rows]);
    }

    /// <summary>UPDATE 链（§6 示例里那句 <c>orm.Update&lt;Item&gt;().Set(...).ExecuteAffrowsAsync(ct)</c>）。
    /// <b>这一条链与 <see cref="Select{T}"/> 一样要付 RUC/RDC 入口税</b>，而 <see cref="Insert{T}"/> 不付：
    /// 条件与表达式值要过 <see cref="ExpressionTranslator"/>（闭包求值 + 退路编译），
    /// 插入只向注册表要列契约与委托。这是"写侧两条链的 AOT 成本不同"这件事的唯一出处，
    /// G1 预翻译接管读侧时要一并看着它（§14 的 0 反射闸门）。
    /// <see cref="Filters"/> 上在册的过滤器会叠进这一句的 <c>WHERE</c>，口径与 <see cref="Delete{T}"/> 相同。</summary>
    [RequiresUnreferencedCode("Where/Set 的 lambda 归约走闭包常量求值。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    public IUpdate<T> Update<T>() where T : class => new UpdateEngine<T>(this);

    /// <summary>DELETE 链（§6 示例里那句 <c>orm.Delete&lt;Cart&gt;().Where(...)</c>）。
    /// <b>这一句改的是行本身还是 <c>deleted</c> 那一格，取决于有没有人订阅 <c>CudBuilding</c></b>：
    /// §10.7 约束 3 那个改写点（<see cref="StatementEmitter.ApplyCud"/>）v0.18 就接上了，而库里预置的
    /// <c>Behaviors.SoftDelete</c> 那一口子子还没有，所以今天<em>没人订阅时</em>它是物理删除 ——
    /// <c>Explain().Reason</c> 每次按<em>最终文本</em>自陈落在哪一支，不靠注册状态猜。
    /// <see cref="Filters"/> 上在册的过滤器会叠进这一句的 <c>WHERE</c>（§10.7 约束 1 的写侧那一半）。</summary>
    [RequiresUnreferencedCode("Where 的 lambda 归约走闭包常量求值。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    public IDelete<T> Delete<T>() where T : class => new DeleteEngine<T>(this);

    // ── 事务（§6 (1)(2)(3)） ──────────────────────────────────────────
    /// <summary>形态 (1)：开一条事务、把它放进当前执行流的环境槽位，之后<b>本实例</b>的每一条语句
    /// （读侧也算）都走那条被它独占的连接。不调 <see cref="IFastSqlTransaction.Commit"/>
    /// 而直接出 <c>using</c> 块 = 回滚。<b>同步方法是有原因的</b>，见 <see cref="IFastSqlTransaction"/>。</summary>
    public IFastSqlTransaction BeginTransaction(IsolationLevel level = IsolationLevel.ReadCommitted, TransactionPropagation propagation = TransactionPropagation.Requires) => Ado.BeginTransaction(level, propagation);

    /// <summary>形态 (3)：环境事务。与 <see cref="BeginTransaction"/> 是同一个机制（AsyncLocal 槽位），
    /// 只是调用方想说的是"后面所有调用自动进来"，而不是"我要亲手 commit 这一笔"。</summary>
    public IFastSqlTransaction BeginAmbientTransaction() => Ado.BeginTransaction(IsolationLevel.ReadCommitted);

    /// <summary>形态 (2)：不抛异常、返回结果对象（对齐 SqlSugar 的 <c>UseTran</c>）。
    /// <b>回滚失败不在这里吞</b>：<c>Dispose</c> 兜底回滚，那一步炸了就让它抛出去 ——
    /// "事务已回滚"这个结论必须由事务自己证明，不能由一个 <c>Success = false</c> 代替。</summary>
    public async Task<TransactionResult> ExecuteInTransactionAsync(
        Func<CancellationToken, Task> body,
        CancellationToken ct = default,
        IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        var tran = BeginTransaction(level);

        try
        {
            await body(ct).ConfigureAwait(false);
            tran.Commit();

            return TransactionResult.Ok();
        }
        catch (Exception exception)
        {
            return TransactionResult.Failed(exception);
        }
        finally
        {
            tran.Dispose();
        }
    }

    public async Task<TransactionResult<T>> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> body,
        CancellationToken ct = default,
        IsolationLevel level = IsolationLevel.ReadCommitted)
    {
        var tran = BeginTransaction(level);

        try
        {
            var value = await body(ct).ConfigureAwait(false);
            tran.Commit();

            return TransactionResult.Ok(value);
        }
        catch (Exception exception)
        {
            return TransactionResult.Failed<T>(exception);
        }
        finally
        {
            tran.Dispose();
        }
    }

    // ── 结构同步（§9.5） ──────────────────────────────────────────────

    /// <summary>§9.5 同步已注册实体的表结构。按 <see cref="FastSqlOrmOptions.SchemaSync"/> 策略决定行为：
    /// <see cref="SchemaSyncPolicy.Disabled"/> 返回空结果；<see cref="SchemaSyncPolicy.Execute"/> 直接建缺失的表；
    /// <see cref="SchemaSyncPolicy.Script"/> 只生成 DDL 不执行。今天只做"表是否缺失"这一档 diff（§14 P2）。</summary>
    /// <remarks>非 Development 环境下 <see cref="SchemaSyncPolicy.Execute"/> 要求 <see cref="FastSqlOrmOptions.ForceSchemaSync"/> 为 true，
    /// 否则抛 <see cref="InvalidOperationException"/>。生产库常禁 DDL 权限，这一道闸门防止误操作。</remarks>
    public async Task<SchemaSyncResult> SyncStructureAsync(CancellationToken ct = default)
    {
        var policy = Options.SchemaSync;

        if (policy == SchemaSyncPolicy.Execute)
            GuardSchemaSyncAllowed();

        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Options.ConnectionString);

        return await syncer.SyncAsync(policy, ct).ConfigureAwait(false);
    }

    /// <summary><see cref="SyncStructureAsync"/> 的同步壳。与 <c>SaveChanges()</c> 同样的 sync-over-async 形态（§7）。</summary>
    public SchemaSyncResult SyncStructure()
        => SyncStructureAsync().GetAwaiter().GetResult();

    // ── 分表建片（§16.4） ─────────────────────────────────────────────

    /// <summary>按时间范围给分表实体建缺失的那几张片（§16.4）。<b>范围是必填的</b>：<see cref="SyncStructureAsync"/>
    /// 那一格遇到分表实体是<em>跳过并记名</em>的，因为"现存的分片该有哪几段"只有业务知道，而默认建全部
    /// 在一颗日档实体上是凭空造几千张表。起与止各自下整到本档的周期，中间逐周期展开。
    /// 逐张先问存在再发 DDL，所以同一个范围跑两次第二次一张也不建。</summary>
    /// <remarks>走的是 <see cref="SchemaSyncPolicy.Execute"/> 那一道闸门（非 Development 环境要
    /// <see cref="FastSqlOrmOptions.ForceSchemaSync"/>），理由与整库同步同一句：建表是 DDL。
    /// <b>要的是模板上声明的那一档，档与范围不匹配（哈希实体传日期）当场抛</b>，不会退成"建一张实名表"。</remarks>
    public async Task<SplitTablesResult> CreateSplitTablesAsync<T>(DateTime from, DateTime to, CancellationToken ct = default)
        where T : class
    {
        GuardSchemaSyncAllowed();

        var (template, shards) = SplitShards<T>(from, to);
        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Options.ConnectionString);

        return await syncer.CreateShardsAsync(template, shards, ct).ConfigureAwait(false);
    }

    /// <summary><see cref="CreateSplitTablesAsync{T}(DateTime,DateTime,CancellationToken)"/> 的同步壳（启动期建片用）。</summary>
    public SplitTablesResult CreateSplitTables<T>(DateTime from, DateTime to) where T : class
        => CreateSplitTablesAsync<T>(from, to).GetAwaiter().GetResult();

    /// <summary>按桶号给哈希档的分表实体建缺失的那几张片。桶号越界抛，重复去掉。</summary>
    public async Task<SplitTablesResult> CreateSplitTablesAsync<T>(int[] buckets, CancellationToken ct = default)
        where T : class
    {
        GuardSchemaSyncAllowed();

        var (template, shards) = SplitShards<T>(buckets);
        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Options.ConnectionString);

        return await syncer.CreateShardsAsync(template, shards, ct).ConfigureAwait(false);
    }

    /// <summary><see cref="CreateSplitTablesAsync{T}(int[],CancellationToken)"/> 的同步壳。</summary>
    public SplitTablesResult CreateSplitTables<T>(params int[] buckets) where T : class
        => CreateSplitTablesAsync<T>(buckets).GetAwaiter().GetResult();

    /// <summary>那一份建片脚本，<b>不连库</b>（§9.5 的 Script 模式同一条路）：给 DBA 审批，也让"一个范围算出哪几张表、
    /// 每张表的 DDL 长什么样"在离线处就钉得住。<see cref="SyncStructureAsync"/> 的 Script 档跳过分表实体，
    /// 这一格是它们唯一的出脚本口。</summary>
    public string SplitTablesScript<T>(DateTime from, DateTime to) where T : class
    {
        var (template, shards) = SplitShards<T>(from, to);

        return new Core.Schema.SchemaSyncer(Provider, Schemas, Options.ConnectionString).ShardsScript(template, shards);
    }

    /// <summary><see cref="SplitTablesScript{T}(DateTime,DateTime)"/> 的哈希档。</summary>
    public string SplitTablesScript<T>(params int[] buckets) where T : class
    {
        var (template, shards) = SplitShards<T>(buckets);

        return new Core.Schema.SchemaSyncer(Provider, Schemas, Options.ConnectionString).ShardsScript(template, shards);
    }

    /// <summary>契约 + 换算好的片清单。<b>档不匹配的那一句由 <see cref="SplitRouting"/> 说</b>
    /// （时间范围打在哈希档上、桶号打在时间档上都是它那句），这里不加第二套判据。</summary>
    (EntityContract Template, IReadOnlyList<TableInfo> Shards) SplitShards<T>(DateTime from, DateTime to) where T : class
    {
        var contract = Schemas.Contract<T>();

        return (contract, SplitRouting.EnumerateTime(contract.Table, NeedSplit(contract), from, to));
    }

    (EntityContract Template, IReadOnlyList<TableInfo> Shards) SplitShards<T>(int[] buckets) where T : class
    {
        var contract = Schemas.Contract<T>();

        return (contract, SplitRouting.EnumerateBuckets(contract.Table, NeedSplit(contract), buckets));
    }

    static SplitFacts NeedSplit(EntityContract contract)
        => contract.Table.Split
           ?? throw new FastSqlException(
                $"{contract.Table.Name} 没有声明分表（类上没有 [SplitTable]），没有片可建。整库建表请用 orm.SyncStructure。");

    void GuardSchemaSyncAllowed()
    {
        if (Options.ForceSchemaSync)
            return;

        var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                  ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

        if (string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            $"非 Development 环境下执行结构同步需要 ForceSchemaSync=true（当前环境：{env ?? "(未设置)"}）。" +
            "生产库常禁 DDL 权限，请先用 SchemaSync=Script 生成脚本、交 DBA 审批后手动执行（§9.5）。");
    }
}
