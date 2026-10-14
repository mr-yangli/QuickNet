namespace QuickSql.Query;

/// <summary>急切加载（Include/ThenInclude）的链式接口（§5.6）。从 <c>Include</c> 或 <c>ThenInclude</c>
/// 返回，允许继续链式调用 <c>ThenInclude</c> 加载更深层的导航属性。继承 <see cref="IQuery{T}"/>，
/// 所以 <c>Where</c>/<c>OrderBy</c>/<c>ToListAsync</c> 等所有查询方法仍然可用。</summary>
/// <remarks>设计参考 EF Core 的 <c>IIncludableQueryable&lt;T, TProperty&gt;</c>，但适配 QuickSql
/// 的同步链式模型。类型参数 <typeparamref name="TLastProp"/> 记录最后加载的导航属性类型，
/// 供 <c>ThenInclude</c> 的编译期类型检查使用。</remarks>
public interface IIncludableQuery<T, TLastProp> : IQuery<T>
    where T : class
{
}

/// <summary>集合导航（to-many）急切加载的链式接口（§5.7）。从 <c>IncludeMany</c> 或 <c>ThenIncludeMany</c>
/// 返回，允许继续链式调用 <c>ThenIncludeMany</c> 往下一层挂集合导航、或 <c>ThenInclude</c> 给这一层的子实体
/// 挂一根引用导航。继承 <see cref="IQuery{T}"/>，
/// 所以 <c>Where</c>/<c>OrderBy</c>/<c>ToListAsync</c> 等所有查询方法仍然可用。
///
/// <para><b>它与 <see cref="IIncludableQuery{T, TLastProp}"/> 是两家，不是一家的两个名字</b>：那一家是
/// <em>一条 LEFT JOIN 把整只实体带回来</em>（引用导航，一层多一张表、多一列扩展列），这一家的每一层是
/// <em>另发一条 SELECT 再内存缝合</em>（集合导航，主查询 SQL 一个字符不变）。
/// <c>ThenInclude</c> 这一颗今天两边都收，收的是各自那一档：<b>接在那一家后面是往引用链的更深层加一张
/// JOIN，接在这一家后面是给这一层刚缝出来的那一批子实体另发一条拆分加载</b>（与
/// <c>AsSplitQuery()</c> 同一颗加载器）—— 见 §5.7。两边的类型参数都记<em>最后一层加载到的元素类型</em>，
/// 供下一颗 lambda 的形参在编译期定下来 —— 集合那一档记的是<em>元素</em>型（<c>TItem</c>）而不是
/// <c>List&lt;TItem&gt;</c>，所以下一层写的是 <c>g =&gt; g.Details</c>（形参 = 一只分组）、旁边那一根写的是
/// <c>g =&gt; g.Warehouse</c>（同一只分组手里的另一只实体）。</para>
///
/// <para><b>子行的序长在链上</b>：这一家继承 <see cref="IQuery{T}"/>，链级 <c>OrderBy</c> 排的永远是
/// <em>父行</em>；要给<em>最后一层</em>子行点序，接 <c>OrderByMany(d =&gt; d.Order)</c> 那颗扩展口
/// （内核 §5.7 的集合加载一族）—— 它把序落进这一层那条另发的 <c>SELECT … WHERE 外键 IN (…)</c> 的
/// <c>ORDER BY</c>，主查询一个字符不变。点在哪一层后面就排哪一层，没点的层不排。</para></summary>
/// <typeparam name="T">根实体。</typeparam>
/// <typeparam name="TItem">链上最后一层集合导航的元素类型。</typeparam>
public interface IIncludableCollectionQuery<T, TItem> : IQuery<T>
{
}
