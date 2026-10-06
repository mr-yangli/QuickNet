using System.Collections;
using FastSql.Metadata;

namespace FastSql.Tracking;

/// <summary>级联的三个方向。<b>只有这三档</b>：插入顺导航图把人带进会话，删除顺同一张图把子行标删，
/// 摘行只处理"上一次看见、这一次不在了"的那几只（裁决③：从集合里摘掉就是要把那一行删掉）。
/// 「父实体本轮还是新增、这一句 <c>Remove</c> 只是把它忘掉」那一档故意不在这里 —— 理由写在
/// <see cref="EntityTracker{T}.Remove"/>。</summary>
enum CascadeMode
{
    Insert,
    Delete,

    /// <summary>摘行档。<b>它不调 <see cref="CascadeWalker.Walk"/></b>：那一颗是"顺这一张实体的全部集合边走一遍"，
    /// 而这一档只处理一根边上的几只<em>已经不在那一串里</em>的实例 —— 现读的那一串里根本没有它们。
    /// 静态型仍然由同一把 <see cref="CollectionEdgeContract.CascadeEnter"/> 委托带进来，
    /// 委托带上来的那一份成员由摘行者换掉（见 <see cref="CascadeWalker.Detach"/>）。</summary>
    Detach,
}

/// <summary>一条欠着的外键：父实体手里那一串键值要在<em>父写出去之后、子的语句建出来之前</em>落进子实体的
/// 那几列（<see cref="ChangeTracker.ApplyCascadeLinks"/>）。
///
/// <para><b>为什么不复用临时主键那条改写路径</b>（<c>EntityTracker.ResolveTempKeys</c>）：它换的是本实体
/// <em>to-one 边</em>上的列，而级联的子端常常只有集合边（<c>OrderItem</c> 手里有 <c>OrderId</c> 列、
/// 却没有 <c>Order</c> 导航）—— 那条路径根本不知道这些列存在，于是一颗负数就会原样进库。
/// 这里换的是<b>父此刻的键</b>：要么已经有值，要么刚被 <c>INSERT</c> 回填回来，两种都走同一处判据。</para></summary>
readonly record struct CascadeLink(
    ITypeTracker Parent,
    object ParentEntity,
    ITypeTracker Child,
    object ChildEntity,
    int[] Columns);

/// <summary>级联的那一次遍历（§8.2「会话按外键依赖排」在写侧的前半段：<c>Add</c>/<c>Remove</c> 顺导航图带人）。
///
/// <para><b>每一次 <c>Add</c>/<c>Remove</c> 新建一份</b>，而不是挂在会话上：递归的形状是
/// <c>Enter</c> → 子端 <c>Add</c> → 子端自己的级联 → ……，共用一份游标会让内层把外层的边覆盖掉，
/// 于是「孙实体挂到错的父上」这种形状只有崩了才看得见。新建一份的代价是一次对象分配，
/// 而它只在真的带集合边的类型上发生（<see cref="EntityTracker{T}"/> 那边有一道 <c>_cascades</c> 闸门）。</para>
///
/// <para><b>静态型从哪来</b>：<see cref="ICascadeSink.Enter{TParent,TChild}"/> 由契约上那把
/// <see cref="CollectionEdgeContract.CascadeEnter"/> 委托调用 —— G1 是生成器写的 lambda，
/// G2 是启动期编译一次的表达式树。这一层因此<em>没有</em> <c>MakeGenericMethod</c>，
/// 公开的 <c>Add</c>/<c>Remove</c> 上也不必挂裁剪标注（§12.7 A 的「写侧 0 反射」）。</para></summary>
sealed class CascadeWalker(ChangeTracker session, CascadeMode mode) : ICascadeSink
{
    ITypeTracker _parent = null!;
    CollectionEdgeContract _edge = null!;

    /// <summary>摘行档要送进子端那一档的那几只<em>已经不在集合里</em>的实例。<b>只在
    /// <see cref="CascadeMode.Detach"/> 那一档有人读它</b>，其余两档它恒为 null，一次分配都不付。</summary>
    IReadOnlyList<object>? _victims;

    /// <summary>走完这一张实体手里的集合边。调用点是 <c>EntityTracker.Add</c>/<c>Remove</c> 里
    /// <b>「刚新登记/刚新标删」那两条分支</b>：已经跟踪过的实例再 <c>Add</c> 一次是幂等的，
    /// 顺它再走一遍图只会把同一批子实体反复送进 <c>Add</c>，而递归的终止靠的正是「新登记才往下走」——
    /// 图里有环（父子各有一根导航）时也是它收敛。</summary>
    public void Walk(ITypeTracker parent, object entity)
    {
        _parent = parent;

        foreach (var edge in parent.Contract.Collections)
        {
            if (!edge.Cascade)
                continue;

            var children = edge.NavigationReader(entity);

            if (children is null)
            {
                // 删除档：集合位是空的（没 Load、没 Include）就是"捞不到"，按裁决①拒 ——
                // 静默留下一串库里还挂着的孤儿行，比多一句报错难查得多。
                // 插入档相反：手里没有子实体就是没有要带的人，无话可说。
                if (mode == CascadeMode.Delete)
                    throw Unreachable(parent, edge);

                continue;
            }

            // 空集合在删除档也不响：那是"这一串我看过，确实没有"的一句声明。
            // 声明错了（库里有）由库里那条外键约束响，不替它在这里编一份账。
            if (!Any(children, edge))
                continue;

            _edge = edge;
            edge.CascadeEnter(this, entity);
        }
    }

    /// <summary>摘行档的入口：把 <paramref name="victims"/> 那几只送进子端那一档标删（连带它们自己手里的子串）。
    ///
    /// <para><b>为什么委托带上来的那一份成员要被换掉</b>：<see cref="CollectionEdgeContract.CascadeEnter"/> 读的是
    /// <em>此刻</em>那一串，而这一档要的正是"刚才在那一串里、现在没有了"的那几只 —— 现读读不到它们。
    /// 这一档要的还是同一个 <c>Enter&lt;TParent,TChild&gt;</c>：会话手里只有 <see cref="Type"/>，
    /// 而子端那一档的构造要编译期的 <c>TChild</c>（同 §12.7「写侧 0 反射」那条不变量）。
    /// 为这一档再开第三把委托，契约、生成器、表达式树三处各多一栏，换不到任何多出来的类型安全。</para></summary>
    public void Detach(ITypeTracker parent, object entity, CollectionEdgeContract edge, IReadOnlyList<object> victims)
    {
        _parent = parent;
        _edge = edge;
        _victims = victims;

        edge.CascadeEnter(this, entity);
    }

    public void Enter<TParent, TChild>(TParent parent, IEnumerable<TChild>? children, string memberName)
        where TParent : class
        where TChild : class
    {
        var tracker = session.For<TChild>();

        // 边的列解算放在这里而不是 Walk：到这一格才既有父契约又有子契约，
        // 而它只在"这一串确实有人、且要落键"时才付钱（推不出就响，见 ChangeTracker）。
        var ordinals = mode == CascadeMode.Insert
            ? session.CascadeForeignKeyOrdinals(_parent.Contract, tracker.Contract, _edge)
            : null;

        foreach (var child in Pending(children) ?? [])
        {
            if (child is null)
                continue;

            var node = mode == CascadeMode.Insert ? tracker.Add(child) : tracker.Remove(child);

            // 只有"这一行欠一句 INSERT"才需要父的键：已经在库里的子行，外键本来就对着那一条。
            if (ordinals is not null && node.State == EntityState.Added)
                session.AddCascadeLink(new CascadeLink(_parent, parent, tracker, child, ordinals));
        }
    }

    /// <summary>摘行档送的是<em>基线里那几只</em>，其余两档送的是委托现读的那一串。</summary>
    IEnumerable<TChild>? Pending<TChild>(IEnumerable<TChild>? children) where TChild : class
        => mode == CascadeMode.Detach ? [.. _victims!.Select(static victim => (TChild) victim)] : children;

    /// <summary>那一格集合位<em>此刻</em>装着的成员（按引用原样收，<c>null</c> 元素跳过）。
    ///
    /// <para><b>返回 <c>null</c> 只有一层意思：那一格是空的（没 Load、没 Include）</b>。调用方据此把
    /// "我从没见过这一串"与"我见过、而它是空的"分开 —— 前者判不出谁被摘掉（不替没看见的东西编一份缺席的账），
    /// 后者才判得出（裁决①与裁决③共用这一颗分岔）。</para></summary>
    internal static List<object>? LiveMembers(CollectionEdgeContract edge, object entity)
    {
        var navigation = edge.NavigationReader(entity);

        if (navigation is null)
            return null;

        var members = new List<object>();

        foreach (var member in RequireEnumerable(navigation, edge))
            if (member is not null)
                members.Add(member);

        return members;
    }

    /// <summary>"这一串里到底有没有人"只问一句：拿第一个 enumerator 挪一格就退。</summary>
    static bool Any(object children, CollectionEdgeContract edge)
    {
        var tick = RequireEnumerable(children, edge).GetEnumerator();

        try
        {
            return tick.MoveNext();
        }
        finally
        {
            (tick as IDisposable)?.Dispose();
        }
    }

    /// <summary><b>退给非泛型的 <see cref="IEnumerable"/> 而不是泛型那一支</b>：读口交回来的就是装箱的集合位，
    /// 静态型到下一格才由 <see cref="ICascadeSink.Enter{TParent,TChild}"/> 那把委托带回来。</summary>
    static IEnumerable RequireEnumerable(object navigation, CollectionEdgeContract edge)
        => navigation as IEnumerable ?? throw new FastSqlException(
            $"集合导航 {edge.MemberName} 读出来的不是一串集合（是 {navigation.GetType().Name}）：" +
            "级联按可枚举处理，这一格认不出该带谁进会话、也认不出谁被摘掉了。");

    FastSqlException Unreachable(ITypeTracker parent, CollectionEdgeContract edge)
        => new($"{parent.EntityType.Name} 声明了到 {edge.ChildType.Name} 的级联删除，而导航 {edge.MemberName} 是空的：" +
               "会话看不见子行，就不能替它判「没有要删的」还是「还没捞出来」。" +
               $"先把它捞进会话再删：Include 那根导航，或 db.Set<{edge.ChildType.Name}>().Load(父键)（裁决①：可达才删，捞不到就拒）。");
}
