using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FastSql.Execution;
using FastSql.Query;
using FastSql.Tracking;
using FastSql.Write;

namespace FastSql;

/// <summary>工作单元（§8）。<b>会话级连接复用（§8.4）</b>：首次执行时懒建一条连接并绑到当前 async 执行流，
/// 之后同一会话内的所有语句（读/写/事务）都走这条连接，而不是每条语句各取一条再还回池。
/// <see cref="DisposeAsync"/> 归还连接——关连接还池，AsyncLocal 槽位一并清理。
///
/// <b>生命周期：Scoped / Transient，绝不能单例</b>（§7 的表）。身份映射与脏标记都是它的状态，
/// 跨请求共享一个会话得到的不是缓存而是别人的未提交改动。
///
/// <b>派生类的写法</b>：§8.1 原稿那个 <c>public DbSet&lt;Order&gt; Orders { get; set; }</c> 的属性注入
/// 没有落地（它要反射写字节码里不存在的东西，与 §12.7「调用不行」相冲）。等价写法是表达式体属性：
/// <code>public DbSet&lt;Order&gt; Orders =&gt; Set&lt;Order&gt;();</code>
/// 这一句看着像每次取属性都造一只，实际上 <see cref="Set{T}"/> 按类型缓存，所以拿到的一直是同一只对象
/// （状态本来就全在 <see cref="ChangeTracker"/> 里，两只视图也共享同一份身份映射，但省掉一次构造）。
/// 规格原稿那种 <c>{ get; set; }</c> 的属性注入反而要多一层反射才能填满，而 §8.1 那三行示例并不要求它。</summary>
public class DbContext(FastSqlOrm orm) : IAsyncDisposable, IDisposable
{
    readonly ChangeTracker _tracker = new(orm);
    readonly Dictionary<Type, object> _sets = [];
    DbConnection? _sessionConnection;

    public FastSqlOrm Orm { get; } = orm;

    /// <summary>会话的跟踪视图（§8.2）。<c>DetectChanges()</c>、<c>Clear()</c>、<c>Entries()</c> 都从这里进。</summary>
    public DbChangeTracker ChangeTracker => _tracker;

    /// <summary>内核自己那一份（<see cref="DbSet{T}"/> 要的是它，而公开口只能看见抽象门面）。
    /// <b>两个名字指同一个对象</b>，所以 <c>db.Set&lt;Order&gt;().Local</c> 与 <c>db.ChangeTracker.Entries()</c>
    /// 说的必然是同一份会话状态 —— 要是各拿一份，§8.2 的身份映射就变成两份要互相同步的账。</summary>
    internal ChangeTracker Tracker => _tracker;

    /// <summary>§8.2 那个开关。<b>关掉的只有「物化之前」与 <see cref="DbChangeTracker.HasChanges"/> 里那两次自动侦测</b>；
    /// <see cref="SaveChanges"/> 恒定先侦测再写，否则关掉它就等于「只写你显式标过的行」，
    /// 而那是 <c>Attach</c> + <c>Entry(...).State</c> 那条显式路线的语义，不是一个开关该改的东西。</summary>
    public bool AutoDetectChangesEnabled
    {
        get => _tracker.AutoDetectChangesEnabled;
        set => _tracker.AutoDetectChangesEnabled = value;
    }

    /// <summary>某一类实体的会话入口（§8.1）。<b>同一个类型在同一会话里是同一只对象</b>，
    /// 于是 <c>db.Set&lt;Order&gt;()</c> 调两次共享的确实是同一份身份映射（那本来就是 <see cref="ChangeTracker"/> 的档），
    /// 而不是"共享但每次重新装一遍"。</summary>
    /// <remarks>这一格上的 <c>RUC/RDC</c> 是<b>读路径的入口税</b>（§5.10）：<see cref="FastSqlOrm.RootEngine(Type,FastSql.Tracking.ChangeTracker?)"/>
    /// 要按 <see cref="Type"/> 向注册表取契约。写侧那几个便利方法没有这两个标注，因为 <c>Set&lt;T&gt;()</c> 已经把契约取好了 ——
    /// 这个不对称是 §14 那条「G1 路径下写侧 0 反射」验收项的形状。</remarks>
    [RequiresUnreferencedCode("按 Type 向注册表取实体契约（G2）。")]
    [RequiresDynamicCode("G2 注册表编译读写委托。")]
    public DbSet<T> Set<T>() where T : class
    {
        if (_sets.TryGetValue(typeof(T), out var cached))
            return (DbSet<T>) cached;

        // 注册会话连接工厂（§8.4）：只是挂一只 delegate，不建连接。
        // 连接在首条语句执行时由 AdoExecutor.OpenAsync 调工厂懒建。
        EnsureSessionFactory();

        var set = new DbSet<T>(this);
        _sets[typeof(T)] = set;

        return set;
    }

    // ── 写侧便利方法（§8.3） ────────────────────────────────────────

    /// <summary>标为新增。<b>自增整数主键由会话先发一个负的临时主键</b>（§8.2 的 <c>Tp</c>）：
    /// 同一批里的依赖行因此有个值可以捏着，而真实的键由库在 <see cref="SaveChanges"/> 那一句里给回来、
    /// 并在依赖行建语句之前把临时值换掉 —— 临时值任何时候都不会进库（换不出去就是拒绝）。
    /// 客户端生成的键（<c>Guid</c> 那一派）<b>要自己填</b>：那一档不代填，空着是填错了而不是"还没生成"。</summary>
    public T Add<T>(T entity) where T : class
    {
        _tracker.For<T>().Add(entity);

        return entity;
    }

    /// <summary>整批标为新增。逐只走 <see cref="Add{T}"/>，所以"同键两只实例"会在第一只冲突的那一只上抛，
    /// 而之前那几只已经进了会话 —— 抛出时看到的批次是<em>半成品</em>。这不是 §12.2 的无痕降级
    /// （它响了），但调用方要清楚这里没有回滚：没写库，可也没把已经收进去的那几只退回去。</summary>
    public void AddRange<T>(IEnumerable<T> entities) where T : class
    {
        var tracker = _tracker.For<T>();

        foreach (var entity in entities)
            tracker.Add(entity);
    }

    /// <summary>承认为「库里已有、当前值就是库值」，之后逐字段比对决定写不写。</summary>
    public T Attach<T>(T entity) where T : class
    {
        _tracker.For<T>().Attach(entity);

        return entity;
    }

    /// <summary>纳入会话并且每列都算脏 —— §8.3 那两行 <c>db.Attach(order); db.Entry(order).State = Modified;</c> 的合成形态。</summary>
    public T Update<T>(T entity) where T : class
    {
        _tracker.For<T>().Update(entity);

        return entity;
    }

    /// <summary>标为删除（物理删除：§10.7 的软删改写点没接线）。<b>被标过的实例仍留在会话里</b>，
    /// 直到那一句 <c>DELETE</c> 真的执行完 —— 否则 <see cref="SaveChanges"/> 之后就没有任何东西能生成那条语句了。</summary>
    public T Remove<T>(T entity) where T : class
    {
        _tracker.For<T>().Remove(entity);

        return entity;
    }

    public void RemoveRange<T>(IEnumerable<T> entities) where T : class
    {
        var tracker = _tracker.For<T>();

        foreach (var entity in entities)
            tracker.Remove(entity);
    }

    /// <summary>某个实例的状态入口（§8.2 的 <c>Entry(o).State</c> 与 <c>Entry(o).Property(...).IsModified</c>）。
    /// <b>它不顺手把实体纳入会话</b>：没跟踪过的实例在这里抛，因为"查一个入口"与"我有一个新意图"是两件事。
    /// 交的是泛型那一档（<see cref="EntityEntry{T}"/>），否则 §8.2 那个收 lambda 的 <c>Property</c> 重载就够不着。
    /// 开头那一次跨档问话只为了把拒绝说准：<paramref name="entity"/> 是派生实例、而它是按基类纳入会话的，
    /// 泛型参数推出的那一档根本不存在，"先 Set&lt;派生&gt;().Attach"于是成了一句错的建议。</summary>
    public EntityEntry<T> Entry<T>(T entity) where T : class
    {
        if (_tracker.OwnerOf(entity) is { } owner && owner.EntityType != typeof(T))
            throw new FastSqlException($"这个实例是按 {owner.EntityType.Name} 纳入会话的，而 {typeof(T).Name} 那一档里没有它。" +
                $"派生实例目前共享基类那张表的契约（各占一档要等继承映射，§8.2、P2/P3），所以入口也要按基类取：Set<{owner.EntityType.Name}>().Entry(...)。");

        return _tracker.For<T>().Entry(entity);
    }

    /// <summary>§8.3 最后那行 <c>db.InsertRaw(order)</c>：绕过会话，直接走 <see cref="FastSqlOrm.Insert{T}"/> 那条链。
    /// 存在的理由是"我不想让这一行进身份映射"（导数、修补数据、写日志表），而它确实会改库里的行 ——
    /// <b>会话里已经读过同一行的话，这里写完它不知道，快照就此是旧的</b>。要么别混用，要么写完 <c>Clear()</c>。</summary>
    public IInsert<T> InsertRaw<T>(T entity) where T : class => Orm.Insert(entity);

    // ── SaveChanges（§8.2 的编排） ───────────────────────────────────

    /// <summary>把会话里的意图写出去。<b>同步出口是 <c>sync-over-async</c></b>（内核没有第二套同步执行路径，
    /// 理由同 <c>FastSqlQuery.ToList()</c> 那段）：异步可用时优先 <see cref="SaveChangesAsync"/>。</summary>
    public SaveChangesReport SaveChanges() => SaveChangesAsync(default).GetAwaiter().GetResult();

    /// <summary>一次 <c>SaveChanges</c> = 一条事务里的「侦测 → Insert 档 → Update 档 → Delete 档 → 结算」。
    ///
    /// <b>没有任何一行欠着语句时，这里一条语句都不发、事务也不开</b>（交一份空报告）。
    /// 空会话上的 <c>SaveChanges</c> 是调用方常写的收尾，而事务一开就把这一批钉在一条连接上（§6）：
    /// 为一个什么都不做的收尾去库里留一条（哪怕只有一瞬的）事务，是要付连接与锁的代价的。
    ///
    /// <b>事务归属</b>（§6 (3) 那句「DbContext 那半边」的落点）：当前执行流上已经有<em>本 orm</em> 的环境事务时，
    /// 这一批就属于它 —— 不另开一条，也<em>不</em>提交它（提交归开它的那个人）。没有时才自己开一条、成功即提交。
    /// 判据用 <see cref="FastSqlTransaction.For"/> 而不是「有没有环境事务」：两个 orm 实例（两套库）共用一个执行流时，
    /// 把语句塞进另一家库的事务里不报错，只是数据少了一半。</summary>
    public async Task<SaveChangesReport> SaveChangesAsync(CancellationToken ct = default)
    {
        if (!_tracker.WritesAreDue())
            return new SaveChangesReport([], []);

        // 先判"这一批排不排得出写入先后"，再碰驱动：成环是元数据的事，而 BeginTransaction 已经去要连接了，
        // 让它抢在前面只会把 FastSqlException 盖成"连不上库"—— 而且一次注定失败、本该一条语句都不发的保存，
        // 不该在库里留下一个开过又回滚的事务（§8.2）。
        _tracker.EnsureWriteOrderResolvable();

        // 同一格位置上的第二道元数据闸门：分表实体的写要走"按行的分片键值定表"，跟踪器给不出那句
        // （§16.4，理由见 EnsureNoSplitWrites 那一段）。放在开事务之前是同一条理由：注定失败的保存
        // 不该在库里留一个开过又回滚的事务。
        _tracker.EnsureNoSplitWrites();

        // 确保会话连接工厂在位：SaveChanges 的事务会复用它（§8.4），而不是另取一条。
        EnsureSessionFactory();

        if (FastSqlTransaction.For(Orm.Ado) is not null)
            return await _tracker.FlushDueAsync(ct).ConfigureAwait(false);

        using var tran = Orm.BeginTransaction();
        var report = await _tracker.FlushDueAsync(ct).ConfigureAwait(false);
        tran.Commit();

        return report;
    }

    // ── 会话级连接复用（§8.4） ───────────────────────────────────────

    /// <summary>注册连接工厂——只挂一只 delegate，不建连接。连接在首条语句执行时由
    /// <c>AdoExecutor.OpenAsync</c> 调工厂懒建，之后同一会话的所有语句复用。</summary>
    internal void EnsureSessionFactory()
    {
        if (_sessionConnection is not null)
            return;

        AdoExecutor.SetSessionFactory(CreateSessionConnection);
    }

    /// <summary>连接工厂本体：<c>AdoExecutor.OpenAsync</c> 在首条语句执行时调它。
    /// 创建、打开、绑定，一步到位。后续语句走已绑定的连接，不再调工厂。</summary>
    DbConnection CreateSessionConnection()
    {
        var connectors = Orm.Provider.Connectors;

        if (!connectors.TryEnsureLoaded(out var failure))
            throw new DriverNotAvailableException(Orm.Provider.Kind.ToString().ToLowerInvariant(), failure);

        var connection = connectors.CreateConnection(Orm.Options.ConnectionString);
        connection.Open();

        _sessionConnection = connection;
        AdoExecutor.BindSessionConnection(connection);

        return connection;
    }

    /// <summary>归还会话连接——关连接还池，AsyncLocal 槽位一并清理。
    /// 同步 <c>Dispose</c> 与异步 <c>DisposeAsync</c> 都走同一条路径：<c>DbConnection.Close</c>
    /// 本身就是同步的，<c>DisposeAsync</c> 只是多一层 <c>await</c> 壳。</summary>
    public async ValueTask DisposeAsync()
    {
        AdoExecutor.ClearSessionFactory();

        if (_sessionConnection is { } conn)
        {
            _sessionConnection = null;
            AdoExecutor.UnbindSessionConnection(conn);
            await conn.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>同步壳，给不用 <c>await using</c> 的调用方。DI 容器的 scoped 释放通常走 <c>DisposeAsync</c>。</summary>
    public void Dispose()
    {
        AdoExecutor.ClearSessionFactory();

        if (_sessionConnection is { } conn)
        {
            _sessionConnection = null;
            AdoExecutor.UnbindSessionConnection(conn);
            conn.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
