using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FastSql.Query.Model;

namespace FastSql.Query;

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
        return new FastSqlIncludableQuery<T, TProp>(engine.AddInclude(node), node);
    }

    /// <summary>在已有 Include 的基础上继续加载更深层的导航属性（§5.6）。只能在 <c>Include</c>
    /// 或另一个 <c>ThenInclude</c> 之后调用。链式的深度由 <see cref="IIncludableQuery{T, TLastProp}"/>
    /// 携带的最后加载节点决定，而不是模型中的顶级 Include 列表。</summary>
    public static IIncludableQuery<T, TProp> ThenInclude<T, TLast, TProp>(
        this IIncludableQuery<T, TLast> query,
        Expression<Func<TLast, TProp>> navigation)
        where T : class
    {
        if (query is not FastSqlIncludableQuery<T, TLast> fq)
            throw new FastSqlException(
                $"ThenInclude 只能用在 FastSql 的 Include/ThenInclude 链式对象上。当前类型是 {query.GetType().Name}。");

        var engine = fq.Engine;
        var parentNode = fq.LastIncludeNode;
        var node = engine.ThenInclude(parentNode, navigation);
        return new FastSqlIncludableQuery<T, TProp>(engine.AddIncludeTable(node), node);
    }

    static QueryEngine GetEngine<T>(IQuery<T> query) where T : class
        => query is FastSqlQuery<T> fq
            ? fq.Engine
            : throw new FastSqlException(
                $"Include/ThenInclude 只能用在 FastSql 的链式查询上（orm.Select&lt;T&gt;() 返回的对象）。" +
                $"当前查询类型是 {query.GetType().Name}，不是 FastSql 的内部实现。");
}

/// <summary><see cref="IIncludableQuery{T, TProp}"/> 的内部实现。携带最后加载的 <see cref="IncludeNode"/>，
/// 供 <c>ThenInclude</c> 继续向深层链式加载。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class FastSqlIncludableQuery<T, TProp>(QueryEngine engine, IncludeNode lastIncludeNode)
    : FastSqlQuery<T>(engine), IIncludableQuery<T, TProp>
    where T : class
{
    public IncludeNode LastIncludeNode { get; } = lastIncludeNode;
}
