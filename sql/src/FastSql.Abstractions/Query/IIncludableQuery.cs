namespace FastSql.Query;

/// <summary>急切加载（Include/ThenInclude）的链式接口（§5.6）。从 <c>Include</c> 或 <c>ThenInclude</c>
/// 返回，允许继续链式调用 <c>ThenInclude</c> 加载更深层的导航属性。继承 <see cref="IQuery{T}"/>，
/// 所以 <c>Where</c>/<c>OrderBy</c>/<c>ToListAsync</c> 等所有查询方法仍然可用。</summary>
/// <remarks>设计参考 EF Core 的 <c>IIncludableQueryable&lt;T, TProperty&gt;</c>，但适配 FastSql
/// 的同步链式模型。类型参数 <typeparamref name="TLastProp"/> 记录最后加载的导航属性类型，
/// 供 <c>ThenInclude</c> 的编译期类型检查使用。</remarks>
public interface IIncludableQuery<T, TLastProp> : IQuery<T>
    where T : class
{
}
