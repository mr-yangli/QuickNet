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
