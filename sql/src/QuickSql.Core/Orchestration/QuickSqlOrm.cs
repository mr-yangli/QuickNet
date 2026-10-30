using QuickSql.Aop;
using QuickSql.Behaviors;
using QuickSql.Core.Schema;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Metadata.Fluent;
using QuickSql.Providers;
using QuickSql.Query;
using QuickSql.Query.Filters;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql.Ast;
using QuickSql.Tracking;
using QuickSql.Transactions;
using QuickSql.Translation;
using QuickSql.Write;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace QuickSql;

/// <summary>组装根的输入。<b>没有</b> <c>Type</c> 字段、没有 <c>Type.GetType(string)</c>：
/// provider 由调用方直接构造并交进来（§12.7 F），驱动的反射只发生在
/// <see cref="DriverLoading.DriverRegistry"/> 拿 <see cref="System.Data.Common.DbProviderFactory"/> 那一步。</summary>
public sealed class QuickSqlOrmOptions
{
    public required IDbProvider Provider { get; init; }

    /// <summary>连接串留在字符串形态：内核不认识任何驱动的连接串构建器（§1.2 原则 3）。
    /// <b>这一颗说的是缺省集群 <c>main</c> 的主库</b>，从库与其余具名集群走下面那两颗 ——
    /// 只写这一颗的调用方与今天逐字同形（一个实例、一条串、读不分离）。</summary>
    public required string ConnectionString { get; init; }

    /// <summary>缺省集群的从库清单，按给出的顺序轮询。为空即"这座库没有从库"，读落主库。</summary>
    public IReadOnlyList<string> Replicas { get; init; } = [];

    /// <summary>除 <c>main</c> 之外的具名集群。实体用 <c>[DataSource("名字")]</c> 点名自己住哪一座
    /// （规格 §16.4① 那颗"解析键按表还是按实体"的裁决：按实体）。</summary>
    public IReadOnlyList<ClusterConfig> Clusters { get; init; } = [];

    public QuickSqlAop Aop { get; init; } = new();

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

    /// <summary>§9.5 的<em>列级</em>同步那一档：库里已经在的表，要不要逐列比形状、补备注、补索引。
    /// <b>默认开</b>（用户 2026-10-06 当面定的那一句"改了表备注、小数长度、索引那些都要同步"）。
    /// 关掉它退到只有"缺表就建"的那一档，与这一格落地之前逐字同形。
    /// 它是一颗 opt-out 而不是 opt-in，因为会丢数据的那两档（删列、改主键与自增）不在这一格里——
    /// 它们在计划层就拒了，任何一颗开关都放行不了（见 <c>StructurePlan</c> 那几条拒）。
    /// <b>删索引不在这一颗下面</b>，它另开 <see cref="SchemaSyncDropIndexes"/>。</summary>
    public bool SchemaSyncColumns { get; init; } = true;

    /// <summary>§9.5 的<em>删索引</em>那一档：库里有一条契约上没有的索引时，要不要 <c>DROP</c> 掉它。
    /// <b>默认关</b>（用户 2026-10-06 定的是"补一列与删一条索引不是一个风险档"）：关掉时那一条只进同步结果的
    /// "要人工"清单并指名。开与关的分别只在<b>库里多出来</b>的那一条上——契约上有、库里没有的照建，
    /// 而同样的列序列换了名算同一条，不会因为它而删掉再重建。
    /// 唯一约束即便开了也不删：那一句 <c>DROP INDEX</c> 删不动约束，发出去只会以报错收尾。</summary>
    public bool SchemaSyncDropIndexes { get; init; }
}

/// <summary>一座具名集群：<b>一个名字、一颗主库、若干从库</b>。直给的记录形状，没有 <c>UseReplicaOn()</c>
/// 那一族方法——加一座库就是往 <see cref="QuickSqlOrmOptions.Clusters"/> 的清单里添一条。</summary>
/// <param name="Name">集群名，实体侧 <c>[DataSource(Name)]</c> 点的就是它。<c>main</c> 是保留名，
/// 由 <see cref="QuickSqlOrmOptions.ConnectionString"/> 占着。</param>
/// <param name="Primary">主库串。写、事务与三档强制主库都落它。</param>
/// <param name="Replicas">从库，按顺序轮询。null 或空即这座集群不分读写。</param>
public sealed record ClusterConfig(string Name, string Primary, IReadOnlyList<string>? Replicas = null);

/// <summary>组合根（§7 生命周期表里的单例）。只持有连接工厂、元数据缓存与方言，<b>不持有连接</b>：
/// 连接按语句从驱动的连接池取（§3 第 5 层）。可以跨线程共享，<see cref="DbContext"/> 才需要 Scoped。</summary>
public sealed class QuickSqlOrm
{
    ExpressionTranslator? _expressions;
    ProjectionTranslator? _projections;
    Func<Type, int, TableRef>? _tableRefFactory;

    /// <summary>G2 兜底口：元数据注册表由内核反射构建（§12.7 A 的 G2 级）。
    /// 裁剪/AOT 场景请走 <see cref="QuickSqlOrm(QuickSqlOrmOptions, IEntitySchemaRegistry)"/>，把 G1 生成的注册表传进来。</summary>
    [RequiresUnreferencedCode("G2 兜底注册表按 Type 反射解析实体与注解。")]
    [RequiresDynamicCode("G2 兜底注册表编译读写委托。")]
    public QuickSqlOrm(QuickSqlOrmOptions options)
        : this(options, new EntitySchemaRegistry(new ContractResolver(options.Provider, options.Model)))
    {
    }

    /// <summary>干净口：注册表由外部提供（G1 生成、测试替身、或宿主自己的实现）。这条路径构造期零反射。</summary>
    public QuickSqlOrm(QuickSqlOrmOptions options, IEntitySchemaRegistry schemas)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(options.Provider);

        Options = options;
        Provider = options.Provider;
        Schemas = schemas;
        Aop = options.Aop;
        PagingOrdering = options.PagingOrdering;
        Sources = new ConnectionSources(options.ConnectionString, options.Replicas, options.Clusters);
        Compiler = new QueryCompiler(Provider);
        Ado = new AdoExecutor(Provider, Sources, options.Aop, options.CommandTimeoutSeconds);
        Filters = new FilterRegistry(this);
        Behaviors = new BehaviorRegistry(this);
    }

    /// <summary>§4 一行注册的 DI 侧：已知工厂、只要一行组装根。工厂由调用方递进来（宿主自己的容器、
    /// 或者 <see cref="QuickSql.DriverLoading.DriverRegistry"/> 都行），provider 由生成器在编译期登记进
    /// <see cref="QuickSql.Providers.ProviderRegistry"/> 的表里查出来。走 G2 兜底注册表，所以带 RUC/RDC；
    /// 要干净路径就照 §1.7 的 <c>new QuickSqlOrm(options, schemas)</c> 姿势。</summary>
    [RequiresUnreferencedCode("Create 组装的是 G2 兜底构造器；AOT 场景请按 §1.7 用干净口。")]
    [RequiresDynamicCode("Create 组装的是 G2 兜底构造器；AOT 场景请按 §1.7 用干净口。")]
    public static QuickSqlOrm Create(DbKind kind, string connectionString, DbProviderFactory driverFactory)
    {
        if (!ProviderRegistry.TryCreate(kind, driverFactory, out var provider))
            throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记。可能原因：① 生成器 QuickSql.Generators 未作为 Analyzer 挂进编译；② 该 provider 程序集未被引用；③ 该 provider 类未带 [QuickSqlProvider] 或缺 (DbProviderFactory) 公开构造器。已登记：{string.Join(", ", ProviderRegistry.Registered)}。");
        return new QuickSqlOrm(new QuickSqlOrmOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
        });
    }

    /// <summary>§4 一行注册的驱动侧：只报 <see cref="DbKind"/> 与连接串，工厂由
    /// <see cref="QuickSql.DriverLoading.DriverRegistry"/> 按内建表 <c>BuiltinDrivers</c> 现场解析。
    /// 与上一颗同样依赖生成器登记；不同在这一颗会真去按程序集名装载驱动，所以现场没驱动时抛的是
    /// <c>DriverNotAvailableException</c>（消息带驱动名与 <c>drivers/</c> 探测位）而不是这里的"未登记"。</summary>
    [RequiresUnreferencedCode("Create 组装的是 G2 兜底构造器，且工厂解析读 Instance 静态字段（§12.7 F）。")]
    [RequiresDynamicCode("Create 组装的是 G2 兜底构造器。")]
    public static QuickSqlOrm Create(DbKind kind, string connectionString)
    {
        var driverKey = ProviderRegistry.DriverKeyOf(kind)
            ?? throw new InvalidOperationException(
                $"DbKind.{kind} 未在 ProviderRegistry 中登记（生成器未挂或 provider 未引用）。已登记：{string.Join(", ", ProviderRegistry.Registered)}。");
        var factory = new QuickSql.DriverLoading.DriverRegistry().Factory(driverKey);
        return Create(kind, connectionString, factory);
    }

    public QuickSqlOrmOptions Options { get; }

    public IDbProvider Provider { get; }

    public IEntitySchemaRegistry Schemas { get; }

    public QuickSqlAop Aop { get; }

    public QueryCompiler Compiler { get; }

    public AdoExecutor Ado { get; }

    /// <summary>在册的集群与各自的串。<b>它与 <see cref="Options"/> 一样是"配置"的一侧</b>：
    /// 实体上只有集群名，串只在这一颗与 <see cref="Options"/> 上看得见，因此也只在它们该被读的地方被读。</summary>
    public ConnectionSources Sources { get; }

    public PagingOrderingPolicy PagingOrdering { get; }

    /// <summary>全局过滤器（§10.7 的软删 / 多租户）。<b>一个 orm 实例一份，注册在启动期做完</b>，
    /// 之后读路径与 <see cref="Update{T}"/> / <see cref="Delete{T}"/> 两条链都会带上它。
    /// 它与 <see cref="Aop"/> 的分工要说清：<c>Aop</c> 上的 <c>CudBuilding</c> 是"整句文本改写的地方"
    /// （软删那句 <c>DELETE → UPDATE</c> 归它，约束 3），而这一份是"谓词从哪儿来"（约束 1、2、4）。
    /// <b>今天它管不到 <c>SaveChanges</c> 与批量</b>：那两条路上的 <c>WHERE</c> 长在 <c>EntityTracker</c> 上，
    /// 欠账与判据在 §10.7 的 v0.26 落地记录、§14 的 P1 余项那一格。</summary>
    public FilterRegistry Filters { get; }

    /// <summary>库里预置的写侧行为（§10.7 约束 3 的入口子，今天只有 <c>SoftDelete</c> 一件）。
    /// <b>它与 <see cref="Filters"/> 是两只对象、各管一半</b>：这一份把 <c>DELETE</c> 换成「置位那一格」的
    /// <c>UPDATE</c>，<see cref="Filters"/> 把已置位的行从读侧与写侧的 <c>WHERE</c> 里筛掉 —— 只装其中一只的项目
    /// 要么删得动而读得见，要么读得干净而删的还是行本身。三处删除出口（<see cref="Delete{T}"/>、
    /// <see cref="DeleteRange{T}"/>、<c>SaveChanges</c> 里被摘掉的行）都读这一份，判据与分工写在
    /// <see cref="BehaviorRegistry"/> 的类型注释里。</summary>
    public BehaviorRegistry Behaviors { get; }

    /// <summary>lambda → AST 的归约器。按需构造：只有真正走到链式翻译的调用方才为它付 AOT 代价（§5.10）。
    /// 标注只能落在 <c>get</c> 上 —— <c>RequiresUnreferencedCode</c> 的目标是方法，属性本身不是合法目标（CS0592）。</summary>
    internal ExpressionTranslator Expressions
    {
        [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
        [RequiresDynamicCode("不可约子表达式的退路是 LambdaExpression.Compile()。")]
        get => _expressions ??= new ExpressionTranslator(Provider, Schemas, TableRefFactory);
    }

    internal ProjectionTranslator Projections
    {
        [RequiresUnreferencedCode("转调 ExpressionTranslator。")]
        [RequiresDynamicCode("转调 ExpressionTranslator。")]
        get => _projections ??= new ProjectionTranslator(Expressions);
    }

    /// <summary>读侧入口（§5.4）。返回的对象是不可变的值，可以复用、可以传出去、不需要 Clone（§5.11）。
    /// <b>没有非泛型口</b>：按运行期 <see cref="Type"/> 造 <c>QuickSqlQuery&lt;T&gt;</c> 只能靠
    /// <c>Activator.CreateInstance</c> + <c>MakeGenericMethod</c>，那正是 §12.7 F 点名要避开的形态；
    /// 动态表名/影子表请走 <c>ModelConfiguration</c> 的表名映射，而不是把 T 退化成 Type。</summary>
    /// <remarks>标注在这里是<b>整条读路径的入口税</b>：G1 预翻译没落地之前，<c>Select&lt;T&gt;()</c> 往下每一步都要
    /// 反射与表达式树编译（§15 的"标注传染面"）。等 G1 接管后应把这一串标注从入口开始逐个摘掉 ——
    /// 摘不干净就说明 §14 的"G1 路径全链路 0 反射"闸门没达成。</remarks>
    [RequiresUnreferencedCode("G2/G3 读路径：闭包求值与投影组装按运行期 Type 反射。")]
    [RequiresDynamicCode("G2/G3 读路径：投影与比较委托要编译表达式树，AOT 下不可用。")]
    public IQuery<T> Select<T>() where T : class => new QuickSqlQuery<T>(RootEngine(typeof(T)));

    /// <summary>根模型：一个表位 + 实体投影。<b>取契约这一步就是 G2</b>（按 Type 反射解析实体），
    /// 所以标注跟着 <see cref="Select{T}"/>；换成 G1 生成的 <see cref="IEntitySchemaRegistry"/> 之后，
    /// 反射从注册表里消失，这一串标注才能整体摘掉 —— 只摘一处会让"0 反射"的闸门变成假的。</summary>
    /// <param name="tracker">会话的跟踪器，null 就是这一条链不跟踪（§8.1「跟踪与否不是两套 API」的落点）。
    /// 它<em>不</em>进模型：模型文本里没有跟不跟踪这一行，所以 <c>orm.Select&lt;T&gt;()</c> 与
    /// <c>db.Set&lt;T&gt;().AsNoTracking()</c> 的 <c>Explain().ModelText</c> 逐字节相等（§13.2 ②）。</param>
    /// <param name="gate">会话的那颗并发闸（<see cref="SessionGate"/>），null 就是根链 —— 根链没有会话，
    /// 每条链各是各的操作，不进闸。</param>
    [RequiresUnreferencedCode("按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    internal QueryEngine RootEngine(Type clrType, ChangeTracker? tracker = null, SessionGate? gate = null)
    {
        var table = NewTableRef(clrType, 0);
        var model = new QueryModel { Tables = [table], Plan = new EntityProjection(0) };

        return new QueryEngine(this, model, tracker, gate: gate);
    }

    /// <summary>造一个会话（§7 那行 <c>orm.CreateDbContext&lt;AppDbContext&gt;()</c>）。
    /// <b>这条路上有反射</b>：<c>Activator.CreateInstance</c> 按 <see cref="Type"/> 找构造函数，所以它带
    /// <c>RUC/RDC</c>，也正是 §7 那句「AOT 场景请自己 new」的原因 ——
    /// <c>new AppDbContext(orm)</c> 走的是普通构造调用，一行反射都没有，功能完全等价。
    /// <b>每次调用都是新会话</b>（新的身份映射），这里没有任何共享或池化：要共享有一份显式的口子，
    /// 而它把范围限在一条执行流上（<see cref="CreateSharedContext{TContext}"/>，§7）。</summary>
    /// <remarks>要求 <c>TContext</c> 有一个 <c>public TContext(QuickSqlOrm orm)</c> 构造函数；没有的话
    /// <c>MissingMethodException</c> 会从这里抛出来，消息里点名类型而不点名签名，所以写错构造函数时先看这一句。</remarks>
    [RequiresUnreferencedCode("CreateDbContext 按 Type 反射构造 TContext（§12.7 F）；AOT 下请自己 new。")]
    [RequiresDynamicCode("CreateDbContext 按 Type 反射构造 TContext；AOT 下请自己 new。")]
    public TContext CreateDbContext<TContext>() where TContext : DbContext
        => (TContext)Activator.CreateInstance(typeof(TContext), this)!;

    /// <summary>§7 那行 <c>orm.CreateSharedContext&lt;AppDbContext&gt;()</c>：<b>在当前执行流上</b>取一只会话 ——
    /// 该流上第一次取时按 <see cref="CreateDbContext{TContext}"/> 建一只，之后每次取到的都是那一只，
    /// 于是同一一流里前后两个方法不必经 DI 注册也共享同一份身份映射（控制台、后台服务、一条链上的分层写法）。
    /// 它的共享范围不是"进程"也不是"线程"，而是<b>一条 async 执行流</b>，这正是 §7 那句「不会跨请求泄漏」的技术读法：
    /// <see cref="AsyncLocal{T}"/> 的写入<em>只在同步那一段回到调用方的帧</em>（v0.12 探针，见
    /// <see cref="QuickSqlTransaction"/> 的 remarks 与 §6），所以 <c>await</c> 之后的子任务里建的那只留在它自己的分支里，
    /// 父帧和兄弟分支都取不到 —— 反过来，父帧先建、子任务后取，子任务拿到的就是父帧那一只（这是同一枚币的另一面）。</summary>
    /// <remarks>三处边界要跟 §7 的表对齐着读：① <b>槽位挂在实例上而不是类型上</b>，所以两只 <see cref="QuickSqlOrm"/>
    /// 共用一条流时各拿各的会话，与 <see cref="QuickSqlTransaction.For"/> 那句「两个 orm 实例（两套库）不能互相塞进对方的事务」同一条理由；
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
                return (TContext)at.Context;
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

    /// <summary><see cref="NewTableRef"/> 的那颗委托，<b>按实例缓存一次</b>。
    /// 递给翻译现场（<c>NavigationJoinScout</c>、<see cref="ExpressionTranslator"/>）的是方法组，
    /// 而方法组每次转换都新建一只委托 —— 现场是「每颗 lambda 一份」的，所以那笔钱乘在读路径的算子次数上
    /// （§12.8 第 4 行「每谓词字节」钉的就是这种每算子常数项）。缓存之后一只 Orm 只付一次。</summary>
    internal Func<Type, int, TableRef> TableRefFactory => _tableRefFactory ??= NewTableRef;

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

    /// <summary>一句 upsert（§5.4 写侧、§12.1 写侧扩展那句"含 MERGE"那一档的一等公民入口）：
    /// <b>按主键决定"没有就插、有就改"</b>——每一行把除主键、库里给值的列与并发列以外的全部列写进去，
    /// 主键既是插入清单的一部分、又是冲突判定的键。发射形态由这一家自己定（MySQL ∶ PG 血统走句尾、
    /// SqlServer ∶ Oracle 血统 ∶ 虚谷 ∶ Gbase8s 走整语句 <c>MERGE</c>），调用方不选。
    /// <para>它是 <see cref="UpdateRange{T}"/> 的 <see cref="Providers.BulkWriteMethod.Upsert"/> 档同一句文本的
    /// 另一扇门（同一把列尺子、同两道当场拒），独立出来只为让"我要 upsert"写在方法名上。<b>入口零反射税</b>，
    /// 与 <see cref="InsertRange{T}"/>、<see cref="UpdateRange{T}"/> 同档：列来自契约、值来自编译好的委托。</para>
    /// <para>数据走 <see cref="IInsertOrUpdate{T}.SetSource(T)"/> 而不是这颗的形参（FreeSql 式独立链的口径），
    /// 于是同一只 <see cref="IInsertOrUpdate{T}"/> 可以先 <c>.SetSource(一只)</c> 再换一批。<b>主键由库给值而
    /// 调用方没带键时当场拒</b>——那种键的冲突判定出不来，逐家形态与"哪几家只到文本层"记 §9.1 与使用册那一节。</para></summary>
    public IInsertOrUpdate<T> InsertOrUpdate<T>() where T : class => new InsertOrUpdateEngine<T>(this);

    /// <summary>集合同步链（SqlSugar 的 <c>Storageable</c> 同一语义，2026-10-30 落地）：给一个集合、
    /// 给一把键（不给就用实体主键，自增键"没填"的行直接进新增桶），与库对账出<b>新增 / 修改 / 删除</b>三个桶；
    /// 对账只发读（<c>ToStorage</c>），执行与否、执行哪几桶由调用方挑
    /// （<c>ExecuteInsertAsync</c> / <c>ExecuteUpdateAsync</c> / <c>ExecuteDeleteAsync</c> /
    /// <c>ExecuteCommandAsync</c>，最后那颗一口气做，顺序固定删 → 改 → 增）。键点名、删除桶、
    /// 分块对账的形状与如实记账全在 <see cref="IStorageable{T}"/> 抬头。</summary>
    [RequiresUnreferencedCode("对账要按 Type 解析实体契约（G2）并编译键读口。")]
    [RequiresDynamicCode("键读口编译成委托。")]
    public IStorageable<T> Storageable<T>(IEnumerable<T> rows) where T : class => new StorageableChain<T>(this, rows);

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
    /// <b>这一句改的是行本身还是 <c>deleted</c> 那一格，取决于这张表上有没有在册的软删行为</b>：
    /// <see cref="Behaviors"/> 上注册过 <c>SoftDelete</c> 的实体，这一句在构建时就换成"置位 + 记时间"的 UPDATE
    /// （§10.7 约束 3，v0.38 第十七笔；改形状那一步在 <see cref="StatementEmitter.EmitDelete"/> 之前，
    /// 而文本仍走 <see cref="StatementEmitter.ApplyCud"/> 与 <see cref="StatementEmitter.ApplyBinding"/>，
    /// 所以日志与审计看到的永远是真正发出去的那一句）。没注册的实体仍发物理 DELETE，
    /// 而调用方自己那颗 <c>CudBuilding</c> 订阅者仍能在文本层再改一次 —— 三种情形 <c>Explain().Reason</c>
    /// 各说一句，按<em>最终</em>状态自陈，不靠注册状态猜。
    /// <see cref="Filters"/> 上在册的过滤器会叠进这一句的 <c>WHERE</c>（§10.7 约束 1 的写侧那一半）。</summary>
    [RequiresUnreferencedCode("Where 的 lambda 归约走闭包常量求值。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    public IDelete<T> Delete<T>() where T : class => new DeleteEngine<T>(this);

    /// <summary>按实体定位的单行 UPDATE（<c>orm.Update(order).ExecuteAffrowsAsync()</c>）：
    /// <b>列清单来自这只实体，<c>WHERE</c> 来自它的主键</b>，于是这句话读作"把这只实体写回它那一行"。
    /// <para><b>它写的是带来值的那些列</b>：除主键、库里给值的列与并发列以外，<em>值为 null 的属性不进 <c>SET</c></em>。
    /// 这一条与 <see cref="UpdateRange{T}"/> 的「整行交出去，那一格说的是 NULL 就是 NULL」<em>正对面</em>，
    /// 是同一批实体的两种意图而不是可以互换的两种写法——要把某一格真写成 NULL，只有链式那一条做得到
    /// （<c>orm.Update&lt;T&gt;().Set(o =&gt; o.列, null)</c>），因为在这一档里 null 说的是"这一列我没说"。
    /// 值类型列永远带来值（<c>int Amount = 0</c> 不是"没填"），所以它每趟都进 <c>SET</c>。</para>
    /// <para>取的是 <see cref="QuickSqlAop.AuditValue"/> 之后的值、种类是 <c>"update"</c>，与批量链同一趟钩子：
    /// 订阅"补修改时间"的人不该因为调用方选了哪扇门而少填一格。逐列的契约尺子（§10.6 长度与必填）也照链式那份过。
    /// 主键为空、或主键由库给值而交来的是没给的形态，<b>当场拒</b>——那两句都只会安静地报告 0 行。
    /// 分表实体按它<em>自己的分片键</em>换算那一张片（与插入同一颗 <c>SplitRouting.Resolve</c>），
    /// 全局过滤器与软删那两道闸门在这条路上与链式同一条。</para>
    /// <para><b>交回的仍是 <see cref="IUpdate{T}"/></b>，所以可以再叠 <c>.Where(...)</c>（条件排在主键之后、
    /// 过滤器最后）。<c>.Set(...)</c> 只补得动<em>实体没带的就是 null 的那些列</em>：实体带着值的那一列再点一次
    /// 会被链式那道"设置了两次"守卫拦下，本家答案不是让后写的覆盖先写的，而是让调用方说清哪个值算。
    /// 因此"整只实体写回 + 某一列在库里算"这种混合现场在这一档<em>不成立</em>，那是链式的形状（§14 记一笔）。</para></summary>
    /// <remarks><b>取值段跑在这一颗入口的调用时刻，不跑在出口</b>：这只实体的列值与钩子改写在那一刻定格，
    /// 之后改实体不会再改出一份文本——与 <see cref="InsertRange{T}"/> 那句"立刻物化，看到的那份与发出去的那份
    /// 必须是同一份"是同一条纪律，只是这里的"一段"短到一只实体。
    /// <para><b>这一颗正是 <see cref="InsertRange{T}"/> 抬头记过的那个重载形状</b>：把整批实体递进来会命中它，
    /// 而 <c>T</c> 被推成 <c>List&lt;Order&gt;</c>、去建一张根本没映射的表的契约。下面那颗 <c>RefuseCollection</c>
    /// 把它拦在运行期并指路——本家没有能看调用点的分析器，编译期就红这件事欠着（§14）。
    /// 成本那一格要说准：入口一次 lambda 都不建（列来自契约、值来自编译好的读写委托，与
    /// <see cref="InsertRange{T}"/> 同档），但交回的那条链<em>在有全局过滤器在册时</em>出口仍要翻那几句谓词，
    /// 所以 <c>RequiresUnreferencedCode</c> 与 <c>RequiresDynamicCode</c> 照链式挂着，不因"这一格不写条件"而摘掉。</para></remarks>
    [RequiresUnreferencedCode("全局过滤器的叠加走闭包常量求值。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public IUpdate<T> Update<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RefuseCollection(entity, nameof(Update));

        return UpdateEngine<T>.FromEntity(this, entity);
    }

    /// <summary>按实体定位的单行 DELETE（<c>orm.Delete(order).ExecuteAffrowsAsync()</c>）：
    /// <b>这一格只借主键，不借值</b>——实体上其余列一列都不读，所以"哪几列是 null"在这条路上没有含义。
    /// 发出去的是 <c>DELETE</c> 还是置位的 <c>UPDATE</c>，与 <see cref="Delete{T}"/> 同一条规则：
    /// <see cref="Behaviors"/> 上在册的软删改写接管，<see cref="Filters"/> 叠进 <c>WHERE</c>，
    /// <c>Explain().Reason</c> 按最终发出去的那一句自陈。
    /// <b>复合主键在这里支持</b>（逐列 <c>=</c> 再 <c>AND</c>）：<see cref="DeleteRange{T}"/> 拒它靠的是
    /// "IN 清单装不下元组"，那一句在单行上根本不成立。分表实体同样按自己的分片键换算那一片。</summary>
    [RequiresUnreferencedCode("全局过滤器的叠加走闭包常量求值。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public IDelete<T> Delete<T>(T entity) where T : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RefuseCollection(entity, nameof(Delete));

        return DeleteEngine<T>.FromEntity(this, entity);
    }

    /// <summary>「把一批实体递给了一只实体的门」那道拦截。<b>判据是<em>实例</em>的形状而不是 <c>typeof(T)</c> 的接口表</b>：
    /// 后者要 <c>Type.GetInterface</c>，那一条会在裁剪产物上要求把 <c>DynamicallyAccessedMembers</c> 一路标到公开方法的
    /// 泛型参数上——为一道守卫把注解摊进公共 API 是不值的。
    /// <c>orm.Update(orders)</c> 会把 <c>T</c> 推成 <c>List&lt;Order&gt;</c>，那一档撞上的是一张根本没映射的表，
    /// 而报错点离写它的人隔着一整条契约解析。
    /// <b>能走到这一格的必然是引用类型的序列</b>（<c>IEnumerable&lt;out T&gt;</c> 的协方差只在引用类型之间成立），
    /// 所以 <c>string</c>、<c>byte[]</c> 与值类型元素的集合天然落不进来；真把 <c>IEnumerable&lt;…&gt;</c>
    /// 实现成自身形状的聚合根确实会被拦——那一档本家的答案是<em>响</em>，因为它宁可让调用方改走链式，
    /// 也不肯安静地按一张不存在的表建契约。</summary>
    static void RefuseCollection(object entity, string entrance)
    {
        if (entity is not IEnumerable<object>)
            return;

        throw new QuickSqlException($"{entrance} 收的是一只实体，而 {entity.GetType().Name} 是一批可以枚举的对象：" +
            $"把它当实体会去建一张不存在的表的契约。整批按主键写走 {entrance}Range(rows)，" +
            $"按条件写走 {entrance.ToLowerInvariant()}<实体>().Where(...)。");
    }

    /// <summary>批量整行更新（§12.1 写侧扩展）：每行把除主键、库里给值的列与并发列以外的全部列写进
    /// <c>SET</c>，主键进 <c>WHERE</c>；Merged 档把多条 UPDATE 并成一条命令文本一次递交，只有声明了
    /// <see cref="Providers.IBulkStrategy.SupportsMergedStatements"/> 的家可达，其余家 Auto 落逐条。
    /// <b>入口零反射税</b>（列来自契约、值来自编译好的委托，一个表达式树都不建），与 <see cref="InsertRange{T}"/> 同档。
    /// 它不与 <c>SaveChanges</c> 合流：那条要跟踪器的脏列判定，这一条的契约就是"整行交出去"。</summary>
    public IUpdateBulk<T> UpdateRange<T>(IEnumerable<T> rows) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        // 立刻物化，理由与 InsertRange 逐字相同：看到的那份数据与发出去的那份必须是同一份。
        return new UpdateBulkEngine<T>(this, rows as T[] ?? [.. rows]);
    }

    /// <summary>批量按主键删（§12.1 写侧扩展）：Merged 档一句 <c>WHERE 键 IN (…)</c> 是<b>单条语句</b>，
    /// 不欠多语句能力，所以这一条在所有 provider 上 Auto 都直接取 Merged；复合主键当场拒并指路。
    /// 入口同 <see cref="UpdateRange{T}"/>：零反射税。</summary>
    public IDeleteBulk<T> DeleteRange<T>(IEnumerable<T> rows) where T : class
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new DeleteBulkEngine<T>(this, rows as T[] ?? [.. rows]);
    }

    // ── 事务（§6 (1)(2)(3)） ──────────────────────────────────────────
    /// <summary>形态 (1)：开一条事务、把它放进当前执行流的环境槽位，之后<b>本实例</b>的每一条语句
    /// （读侧也算）都走那条被它独占的连接。不调 <see cref="IQuickSqlTransaction.Commit"/>
    /// 而直接出 <c>using</c> 块 = 回滚。<b>同步方法是有原因的</b>，见 <see cref="IQuickSqlTransaction"/>。</summary>
    public IQuickSqlTransaction BeginTransaction(IsolationLevel level = IsolationLevel.ReadCommitted, TransactionPropagation propagation = TransactionPropagation.Requires) => Ado.BeginTransaction(level, propagation);

    /// <summary>钉住一座集群开事务（§16.4 的"事务有了归属"）。<b>之后这条事务里的每一条语句都必须说同一座
    /// 集群</b>：要另一座的那一句当场拒（判据在 <c>AdoExecutor.RentAsync</c> 的 ambient 那一支，与
    /// <c>BeginTransaction</c> 里跨集群嵌套那一句共用同一条）。这不是懒：一场事务底下只有一条连接，
    /// 而"跨集群的一场事务"要的是两阶段提交那一族协议（规格 §16.4① 没有裁它），本家不给它一个能写出来的形状。
    /// 上面那一颗缺省的重载说的就是 <see cref="DataSourceRouting.DefaultCluster"/>，所以不点名集群的调用方
    /// 与这一格落地之前逐字同形。</summary>
    public IQuickSqlTransaction BeginTransaction(string cluster, IsolationLevel level = IsolationLevel.ReadCommitted, TransactionPropagation propagation = TransactionPropagation.Requires)
        => Ado.BeginTransaction(cluster, level, propagation);

    /// <summary>形态 (3)：环境事务。与 <see cref="BeginTransaction"/> 是同一个机制（AsyncLocal 槽位），
    /// 只是调用方想说的是"后面所有调用自动进来"，而不是"我要亲手 commit 这一笔"。</summary>
    public IQuickSqlTransaction BeginAmbientTransaction() => Ado.BeginTransaction(IsolationLevel.ReadCommitted);

    /// <summary>形态 (4)：开一场事务并<b>钉在这只 orm 上</b>（归属跟实例，不跟执行流）。
    /// <para>什么时候用这一颗：调用方<b>不是</b>语句的父帧，而是隔着一层 async 装配把它装上——
    /// ABP 的工作单元就是这一形：provider 在自己的帧里 <c>Begin</c>，回到仓储那一帧时
    /// <see cref="System.Threading.AsyncLocal"/> 那一笔已经看不见了（<see cref="IQuickSqlTransaction"/>
    /// 抬头 v0.12 那条实测），于是每句各拿各的池连接自动提交。钉在实例上之后，
    /// 凡是从这只 orm 发出的语句都进同一场，跨不跨帧都一样。</para>
    /// <para>与形态 (1)(3) 的分工没变：一条 <c>DbConnection</c> + 一个 <c>DbTransaction</c>、
    /// 保存点嵌套与集群闸门同一条判据；orm 实例本来就按会话活，收口仍是<em>唯一那一场</em>
    /// （已经钉过再钉当场拒），结束（提交/回滚/<c>Dispose</c>）时自动摘钩。
    /// 同步方法、<b>同一只实例上不要并发使</b>，两条纪律与
    /// <see cref="CreateSharedContext{TContext}"/> 抬头那条一样。</para></summary>
    public IQuickSqlTransaction BeginSessionTransaction(IsolationLevel level = IsolationLevel.ReadCommitted)
        => Ado.BeginSessionTransaction(DataSourceRouting.DefaultCluster, level);

    /// <summary>钉住一座集群开会话事务（§16.4 与形态 (4) 的合体）：语句要落别座集群的，仍然当场拒。</summary>
    public IQuickSqlTransaction BeginSessionTransaction(string cluster, IsolationLevel level = IsolationLevel.ReadCommitted)
        => Ado.BeginSessionTransaction(cluster, level);

    /// <summary>形态 (2)：不抛异常、返回结果对象（对齐 SqlSugar 的 <c>UseTran</c>）。
    /// <b>回滚失败不在这里吞</b>：<c>Dispose</c> 兜底回滚，那一步炸了就让它抛出去 ——
    /// "事务已回滚"这个结论必须由事务自己证明，不能由一个 <c>Success = false</c> 代替。
    /// <para><b>v0.39 第二笔对"炸了就抛"加了一条边界</b>：业务异常在先、回滚失败在后时，
    /// 抛出去的必须是<em>原先那一句</em>——回滚失败（锁冲突/连接断了）从旁路记进
    /// <see cref="TransactionResult.RollbackFailure"/>，不再从收口冒出来把业务异常整个换掉。
    /// 业务原因在先的那场事故才是调用方要分支的那一场；收口失败两条都在时两个都看得到。</para></summary>
    public async Task<TransactionResult> ExecuteInTransactionAsync(
        Func<CancellationToken, Task> body,
        IsolationLevel level = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
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
            // 兜底回滚在这里完成（不留给 finally）：失败记进旁路而不是从收口冒出来盖掉业务异常。
            // RollbackQuiet 只住具体类上（接口是公开面）；BeginTransaction 造的恒是具体类本尊。
            return TransactionResult.Failed(exception, ((QuickSqlTransaction)tran).RollbackQuiet());
        }
        finally
        {
            tran.Dispose();
        }
    }

    public async Task<TransactionResult<T>> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> body,
        IsolationLevel level = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
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
            // 同上：回滚失败走旁路，业务异常保住。
            return TransactionResult.Failed<T>(exception, ((QuickSqlTransaction)tran).RollbackQuiet());
        }
        finally
        {
            tran.Dispose();
        }
    }

    // ── 结构同步（§9.5） ──────────────────────────────────────────────

    /// <summary>§9.5 同步已注册实体的表结构。按 <see cref="QuickSqlOrmOptions.SchemaSync"/> 策略决定行为：
    /// <see cref="SchemaSyncPolicy.Disabled"/> 返回空结果；<see cref="SchemaSyncPolicy.Execute"/> 建缺失的表，
    /// 并按 <see cref="QuickSqlOrmOptions.SchemaSyncColumns"/> 决定要不要逐列比形状（补列、改长度与标度、
    /// 改可空、重发备注、补索引）；<see cref="SchemaSyncPolicy.Script"/> 只生成建表 DDL 不执行，
    /// 而列级那一档在它身上判不了（不连库就没有"库里现形状"那半份判据）——结果里的 NotJudged 会指名这一句。
    /// 库里<em>多出来</em>的那条索引默认只进"要人工"清单，要它真 <c>DROP</c> 得开
    /// <see cref="QuickSqlOrmOptions.SchemaSyncDropIndexes"/>。</summary>
    /// <remarks>非 Development 环境下 <see cref="SchemaSyncPolicy.Execute"/> 要求 <see cref="QuickSqlOrmOptions.ForceSchemaSync"/> 为 true，
    /// 否则抛 <see cref="InvalidOperationException"/>。生产库常禁 DDL 权限，这一道闸门防止误操作。</remarks>
    public async Task<SchemaSyncResult> SyncStructureAsync(CancellationToken ct = default)
    {
        var policy = Options.SchemaSync;

        if (policy == SchemaSyncPolicy.Execute)
            GuardSchemaSyncAllowed();

        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Sources, Options.SchemaSyncColumns, Options.SchemaSyncDropIndexes);

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
    /// <see cref="QuickSqlOrmOptions.ForceSchemaSync"/>），理由与整库同步同一句：建表是 DDL。
    /// <b>要的是模板上声明的那一档，档与范围不匹配（哈希实体传日期）当场抛</b>，不会退成"建一张实名表"。</remarks>
    public async Task<SplitTablesResult> CreateSplitTablesAsync<T>(DateTime from, DateTime to, CancellationToken ct = default)
        where T : class
    {
        GuardSchemaSyncAllowed();

        var (template, shards) = SplitShards<T>(from, to);
        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Sources);

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
        var syncer = new Core.Schema.SchemaSyncer(Provider, Schemas, Sources);

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

        return new Core.Schema.SchemaSyncer(Provider, Schemas, Sources).ShardsScript(template, shards);
    }

    /// <summary><see cref="SplitTablesScript{T}(DateTime,DateTime)"/> 的哈希档。</summary>
    public string SplitTablesScript<T>(params int[] buckets) where T : class
    {
        var (template, shards) = SplitShards<T>(buckets);

        return new Core.Schema.SchemaSyncer(Provider, Schemas, Sources).ShardsScript(template, shards);
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
           ?? throw new QuickSqlException(
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
