using QuickSql.Query.Model;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace QuickSql.Query;

/// <summary>急切加载的扩展方法（§5.6）。<c>Include</c> 与 <c>ThenInclude</c> 作为扩展方法实现，
/// 不在 <see cref="IQuery{T}"/> 接口上（返回类型需要是 <see cref="IIncludableQuery{T, TProp}"/>
/// 才能继续链式 <c>ThenInclude</c>，而接口方法不允许改变返回类型）。所有方法都转发到
/// <see cref="QueryEngine"/> 的内部实现。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
public static class IncludeExtensions
{
    /// <summary>急切加载引用导航属性（§5.6）。通过 LEFT JOIN 将关联实体与主实体一起加载，
    /// 避免 N+1 查询问题。返回 <see cref="IIncludableQuery{T, TProp}"/> 以继续链式调用
    /// <c>ThenInclude</c> 加载更深层导航。</summary>
    public static IIncludableQuery<T, TProp> Include<T, TProp>(
        this IQuery<T> query,
        Expression<Func<T, TProp>> navigation)
        where T : class
    {
        var engine = GetEngine<T>(query);
        var node = engine.Include(navigation);
        return new QuickSqlIncludableQuery<T, TProp>(engine.AddInclude(node), node);
    }

    /// <summary>在已有 Include 的基础上继续加载更深层的导航属性（§5.6）。只能在 <c>Include</c>
    /// 或另一个 <c>ThenInclude</c> 之后调用。链式的深度由 <see cref="IIncludableQuery{T, TLastProp}"/>
    /// 携带的最后加载节点决定，而不是模型中的顶级 Include 列表。</summary>
    public static IIncludableQuery<T, TProp> ThenInclude<T, TLast, TProp>(
        this IIncludableQuery<T, TLast> query,
        Expression<Func<TLast, TProp>> navigation)
        where T : class
    {
        if (query is not QuickSqlIncludableQuery<T, TLast> fq)
            throw new QuickSqlException(
                $"ThenInclude 只能用在 QuickSql 的 Include/ThenInclude 链式对象上。当前类型是 {query.GetType().Name}。");

        var engine = fq.Engine;
        var parentNode = fq.LastIncludeNode;
        var node = engine.ThenInclude(parentNode, navigation);
        return new QuickSqlIncludableQuery<T, TProp>(engine.AddIncludeTable(node), node);
    }

    static QueryEngine GetEngine<T>(IQuery<T> query) where T : class
        => query is QuickSqlQuery<T> fq
            ? fq.Engine
            : throw new QuickSqlException(
                $"Include/ThenInclude 只能用在 QuickSql 的链式查询上（orm.Select&lt;T&gt;() 返回的对象）。" +
                $"当前查询类型是 {query.GetType().Name}，不是 QuickSql 的内部实现。");

    /// <summary>在已有的集合加载（<c>IncludeMany</c>）上再往下一层挂集合导航（§5.7）。链式的深度由
    /// <see cref="IIncludableCollectionQuery{T, TItem}"/> 携带的最后一颗节点决定，与 <see cref="ThenInclude"/>
    /// 同一套读法。<b>下一层的父端是上一层刚缝出来的那一批子实体</b>，所以这一档不碰主查询：
    /// <c>IncludeMany(t =&gt; t.Groups).ThenIncludeMany(g =&gt; g.Details)</c> 在库端是三张表、主查询一条加子查询两条。
    /// 再往上一层的 <c>IncludeMany</c> 不接在这颗后面 —— 那颗永远从<em>根契约</em>起算，要挂回根就照 EF 那一句
    /// 重起一段链。</summary>
    public static IIncludableCollectionQuery<T, TProp> ThenIncludeMany<T, TItem, TProp>(
        this IIncludableCollectionQuery<T, TItem> query,
        Expression<Func<TItem, IEnumerable<TProp>>> navigation)
    {
        if (query is not QuickSqlIncludableManyQuery<T, TItem> fq)
            throw new QuickSqlException(
                $"ThenIncludeMany 只能用在 QuickSql 的 IncludeMany/ThenIncludeMany 链式对象上。当前类型是 {query.GetType().Name}。");

        var engine = fq.Engine;
        var node = engine.ThenIncludeMany(fq.LastIncludeManyNode, navigation);
        return new QuickSqlIncludableManyQuery<T, TProp>(engine, node);
    }

    /// <summary>在已有的集合加载（<c>IncludeMany</c> / <c>ThenIncludeMany</c>）上，给<em>这一层的子实体</em>
    /// 再挂一根引用导航（§5.7）：<c>IncludeMany(o =&gt; o.Items).ThenInclude(i =&gt; i.Material)</c> ——
    /// 明细带出来之后，每一只明细手里那一份物资也一起带出来，整条链不需要再另起一句。
    ///
    /// <para><b>形状与 <c>ThenIncludeMany</c> 同一条纪律：主查询一个字符不变</b>，多出来的只是执行期的一条
    /// <c>SELECT ... WHERE 物资.主键 IN (这一批明细的外键值)</c>，然后逐只缝回明细的导航属性 —— 发出去的那条
    /// 与 <c>AsSplitQuery()</c> 那一档共用同一颗加载器，所以同一根边在两条路上长成同一个答案。
    /// 集合导航在这颗口上当场拒（那是 <see cref="ThenIncludeMany"/> 的射程）。</para>
    ///
    /// <para><b>返回的还是原来那一家</b>（<c>TItem</c> 不变），所以重复调用是在<em>同一层</em>并列多挂几根
    /// 引用边（<c>.ThenInclude(i =&gt; i.Material).ThenInclude(i =&gt; i.Warehouse)</c>），<c>ThenIncludeMany</c>
    /// 也还是往<em>下一层集合</em>走。往引用导航的第二层（物资的类别）今天没出口 —— 那一档只有
    /// <c>Include</c>/<c>ThenInclude</c> 那条 JOIN 链（或 <c>AsSplitQuery()</c>）走得到。</para></summary>
    public static IIncludableCollectionQuery<T, TItem> ThenInclude<T, TItem, TProp>(
        this IIncludableCollectionQuery<T, TItem> query,
        Expression<Func<TItem, TProp>> navigation)
    {
        if (query is not QuickSqlIncludableManyQuery<T, TItem> fq)
            throw new QuickSqlException(
                $"ThenInclude 只能用在 QuickSql 的 IncludeMany/ThenIncludeMany 链式对象上。当前类型是 {query.GetType().Name}。");

        fq.Engine.ThenInclude(fq.LastIncludeManyNode, navigation);

        return fq;
    }

    /// <summary>给链上<em>最后一层</em>集合加载的子行点序（§5.7 的子行排序口）：
    /// <c>IncludeMany(r =&gt; r.Details).OrderByMany(d =&gt; d.Order)</c> —— 这一层另发的那条子 SELECT
    /// 带上 <c>ORDER BY</c> 出场，缝回父实体的顺序就是库里的顺序。
    ///
    /// <para><b>为什么是这一颗形状，而不是 <c>IncludeMany(navigation, orderBy, descending)</c> 的可选形参</b>：
    /// 这一家的链式语法（<see cref="ThenIncludeMany"/> / <see cref="ThenInclude"/>）全都长在
    /// <see cref="IIncludableCollectionQuery{T, TItem}"/> 上、各收各自那一档的 lambda（v0.39 契约注释里
    /// 写明的"类型参数记最后一层元素型，供下一颗 lambda 的形参在编译期定下来"就是这套语法的地基）；
    /// 把序塞进 <c>IncludeMany</c> 的形参表，只盖得住<em>第一层</em> —— <c>ThenIncludeMany</c> 是另一颗口，
    /// 要盖嵌套就得在它身上再抄一遍可选形参，一颗能力分裂成两颗口、两套默认值。长在链上则天然逐层可达：
    /// 点在哪一层后面就排哪一层（<c>.IncludeMany(a).OrderByMany(x).ThenIncludeMany(b).OrderByMany(y)</c>
    /// 两层各排各的），没点的层不排 —— 目标由<em>书写位置</em>显式给出，不存在"隔层生效"或"静默吞掉下一层"
    /// 这两种失败形态。方向与键同理：<b>两颗都由形参给，不藏在表达式里</b>（链级按名字那一颗今天也收
    /// 串里的 <c>asc</c> / <c>desc</c>，那是给前端整串让的路，与本口无关 —— 本口收的是表达式，没有串可拆）。</para>
    ///
    /// <para><b>发射在库里</b>：那颗序进的是这一层子 SELECT 的 <c>ORDER BY</c>（目标方言的引号由发射器加），
    /// 不是物化之后在内存里排 —— 后者既表达不了方言侧的序语义，也白付一次全量拷贝。
    /// <b>主查询一个字符不变</b>：与整条集合链同一条纪律；没点这颗口时子 SELECT 里连
    /// <c>ORDER BY</c> 三个字都不出现，与本口存在之前逐字节同形。
    /// 排序键认列与链级 <c>OrderBy(string)</c> 共用同一颗三档咽喉（属性名→列名→大小写不敏感唯一命中），
    /// <b>认不出当场抛</b>在接链的那一刻；只收子实体上的直接属性访问，嵌套路径当场拒（那颗口上写了为什么）。
    /// 再点一次是整串替换（<c>OrderBy</c> 之后 <c>OrderBy</c> 的 LINQ 口径）；多列子行序今天没有出口。</para>
    ///
    /// <para><b>链级 <c>OrderBy</c> 不归它管</b>：<see cref="IIncludableCollectionQuery{T, TItem}"/> 继承
    /// <see cref="IQuery{T}"/>，那一句排的还是父行 —— 两颗口各排各的行集，互不遮蔽。</para></summary>
    /// <typeparam name="T">根实体。</typeparam>
    /// <typeparam name="TItem">链上最后一层集合导航的元素类型 —— 排序键就从它上认列。</typeparam>
    /// <typeparam name="TKey">排序键的类型。泛型而不是 <c>IComparable</c>：免掉值类型每一次的装箱。</typeparam>
    public static IIncludableCollectionQuery<T, TItem> OrderByMany<T, TItem, TKey>(
        this IIncludableCollectionQuery<T, TItem> query,
        Expression<Func<TItem, TKey>> key,
        bool descending = false)
    {
        if (query is not QuickSqlIncludableManyQuery<T, TItem> fq)
            throw new QuickSqlException(
                $"OrderByMany 只能用在 QuickSql 的 IncludeMany/ThenIncludeMany 链式对象上。当前类型是 {query.GetType().Name}。");

        fq.Engine.OrderByMany(fq.LastIncludeManyNode, key, descending);

        return fq;
    }
}

/// <summary><see cref="IIncludableQuery{T, TProp}"/> 的内部实现。携带最后加载的 <see cref="IncludeNode"/>，
/// 供 <c>ThenInclude</c> 继续向深层链式加载。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlIncludableQuery<T, TProp>(QueryEngine engine, IncludeNode lastIncludeNode)
    : QuickSqlQuery<T>(engine), IIncludableQuery<T, TProp>
    where T : class
{
    public IncludeNode LastIncludeNode { get; } = lastIncludeNode;
}

/// <summary><see cref="IIncludableCollectionQuery{T, TItem}"/> 的内部实现。携带最后一颗
/// <see cref="IncludeManyNode"/>，供 <c>ThenIncludeMany</c> 往下一层挂集合、<c>ThenInclude</c> 往旁边挂引用。与
/// <see cref="QuickSqlIncludableQuery{T, TProp}"/> 的差别只在一颗：这一家<em>不派生新引擎</em>，
/// 因为两档节点都挂在本颗集合节点的 <see cref="IncludeManyNode.Children"/> 与
/// <see cref="IncludeManyNode.References"/> 上，模型的清单里不添条目。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class QuickSqlIncludableManyQuery<T, TItem>(QueryEngine engine, IncludeManyNode lastIncludeManyNode)
    : QuickSqlQuery<T>(engine), IIncludableCollectionQuery<T, TItem>
{
    public IncludeManyNode LastIncludeManyNode { get; } = lastIncludeManyNode;
}
