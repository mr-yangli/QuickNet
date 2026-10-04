using FastSql.Sql;
using FastSql.Sql.Ast;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;

namespace FastSql.Query;

/// <summary>标注文案集中一处：读侧只有链式一条入口，但标注要落在每个落地类型上（属性参数必须是常量表达式，
/// 故不能写成各类自己的 <c>const</c>），而它们付的是同一笔账 ——
/// 翻译 lambda 要反射读闭包成员，DTO/匿名投影要编译表达式树（§5.10 的 G2/G3）。
/// G1 预翻译接管后与本类一起摘掉。</summary>
static class TranslationCost
{
    public const string Message = "翻译 lambda 要反射读闭包成员，DTO/匿名投影要编译表达式树（§5.10 的 G2/G3）。G1 预翻译接管后与本类一起摘掉。";
}

/// <summary>「链式对象背后那台引擎」的口（§12.7 C 的同一条界线）。
/// 关联子查询要在表达式树里把捕获的链式对象求回 <see cref="QueryEngine"/>，
/// 原先那一步是按名字反射取 <c>Engine</c> 属性 —— 每次编译一次；有了这一格就是一型判断加一次属性读。
/// 公开形状不受影响：本接口是 <c>internal</c>，链式对象对外的成员一个都没多。</summary>
interface IEngineCarrier
{
    QueryEngine Engine { get; }
}

/// <summary>读侧的链式对象（§5.4）。<b>不可变</b>：每个算子返回新对象，所以同一个 <c>IQuery&lt;T&gt;</c>
/// 可以复用、可以传给别人、不需要 <c>Clone()</c>（§5.11），并发枚举天然安全。
/// 这里没有任何 SQL 文本与连接：<see cref="QueryEngine"/> 才是干活的地方，本类只做类型化的转发。</summary>
/// <remarks><b>为什么 <c>public</c>：</b>§8.1 写的就是 <c>DbSet&lt;T&gt; : ISet&lt;T&gt;</c>，而
/// <see cref="DbSet{T}"/> 要的正是「同一条链 + 一份会话」—— 继承比再抄三十个转发方法更近，
/// 因为它连"链式方法在跟踪集合上该返回什么类型"都一并复用了：<c>db.Orders.Where(...)</c> 得到
/// <c>IQuery&lt;Order&gt;</c> 而<em>仍然跟踪</em>（会话在引擎上，派生时带着走），
/// 那正是 §8.1 示例里 <c>IQuery&lt;Order&gt; open = db.Orders.Where(...); // 跟踪</c> 那一行的直读。
/// <b>构造口仍是 <c>internal</c></b>：链式的入口只有 <c>orm.Select&lt;T&gt;()</c> 与 <c>db.Set&lt;T&gt;()</c>，
/// 把类型公开不等于把"自己造一只链式对象"公开。</remarks>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
public class FastSqlQuery<T> : IQuery<T>, IEngineCarrier
{
    /// <summary>默认流式批大小。<see cref="ToAsyncEnumerable"/> 上调用方不给值时用这个 —— 对 SQL Server 它只是
    /// 一个通过校验的数，见 <see cref="QueryEngine.StreamAsync{TResult}"/>。</summary>
    const int DefaultStreamBatchSize = 50;

    internal FastSqlQuery(QueryEngine engine) => Engine = engine;

    internal QueryEngine Engine { get; }

    QueryEngine IEngineCarrier.Engine => Engine;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => Engine.StreamAsync<T>(DefaultStreamBatchSize, ct).GetAsyncEnumerator(ct);

    // ── 算子 ──────────────────────────────────────────────────────────

    public IQuery<T> Where(Expression<Func<T, bool>> predicate) => new FastSqlQuery<T>(Engine.Where(predicate));

    public IQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> projector)
        => new FastSqlQuery<TResult>(Engine.Project(projector));

    public IOrderedQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> key)
        => new FastSqlOrderedQuery<T>(Engine.Order(key, descending: false, append: false));

    public IOrderedQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key)
        => new FastSqlOrderedQuery<T>(Engine.Order(key, descending: true, append: false));

    public IQuery<T> Skip(int count) => new FastSqlQuery<T>(Engine.Skip(count));

    public IQuery<T> Take(int count) => new FastSqlQuery<T>(Engine.Take(count));

    public IQuery<T> Distinct() => new FastSqlQuery<T>(Engine.Distinct());

    public IQuery<T> AsNoTracking() => new FastSqlQuery<T>(Engine.NoTracking());

    /// <summary>与 <see cref="AsNoTracking"/> 同一形状：<b>只松开手里的一样东西，模型一个字都不改</b>
    /// （那里松的是会话，这里松的是全局过滤器），所以派生出来的那一条链仍然带着同一个跟踪器 ——
    /// <c>db.Orders.IgnoreQueryFilters()</c> 读回来的行照样进身份映射。</summary>
    public IQuery<T> IgnoreQueryFilters() => new FastSqlQuery<T>(Engine.IgnoreFilters());

    public IQuery<T> WithCache(string cacheKey, TimeSpan ttl) => new FastSqlQuery<T>(Engine.Cache(cacheKey, ttl));

    public IQuery<T> As(string alias) => new FastSqlQuery<T>(Engine.Alias(alias));

    public IQuery<T> WithSql(string sql, object? args = null) => new FastSqlQuery<T>(Engine.WithSql(sql, args));

    public IQuery<T, T2> LeftJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class
        => new FastSqlJoinQuery<T, T2>(Engine.Join(JoinKind.Left, on));

    public IQuery<T, T2> InnerJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class
        => new FastSqlJoinQuery<T, T2>(Engine.Join(JoinKind.Inner, on));

    public ISelectGrouping<TKey, T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector)
        => new FastSqlSelectGrouping<TKey, T>(Engine.GroupBy(keySelector));

    // ── P1：接口里有、实现还没有，所以直接抛，不给"看起来能用"的假象 ──────────

    public IQuery<T> IncludeMany<TDetail>(Expression<Func<T, IEnumerable<TDetail>>> navigation)
        => new FastSqlQuery<T>(Engine.IncludeMany(navigation));

    public IQuery<T> AsSplitQuery(int navigateBatchSize = 20)
        => new FastSqlQuery<T>(Engine.SplitQuery());

    /// <summary>「接口里有、实现还没有」的统一说法。<b>不写成返回源对象</b>：那样调用方看不出它没生效，
    /// 而 §5.6/§5.7 那几条路径的产出恰恰是"导航属性该有值了"。<see cref="DbSet{T}.Load{TProp}"/> 用同一个口，
    /// 因为那是同一种"没实现"。</summary>
    internal static FastSqlException Deferred(string member, string why)
        => new($"{member}(...) 未实现：{why} 现在静默返回源查询会让调用方以为导航属性已经加载，读出来全是 null。");

    // ── 出口 ──────────────────────────────────────────────────────────

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public string ToSql(out IReadOnlyList<AdoParameter> parameters) => Engine.ToSql(out parameters);

    public string ToLogSql() => Engine.ToLogSql();

    public Task<List<T>> ToListAsync(CancellationToken ct = default) => Engine.ListAsync<T>(ct);

    public Task<T?> FirstOrDefaultAsync(CancellationToken ct = default) => Engine.FirstAsync<T>(ct);

    public Task<int> CountAsync(CancellationToken ct = default) => Engine.CountAsync(ct);

    public Task<PagedResult<T>> ToPagedAsync(int page, int size, PagedOptions? options = null, CancellationToken ct = default)
        => Engine.PagedAsync<T>(page, size, options, ct);

    public IAsyncEnumerable<T> ToAsyncEnumerable(int batchSize) => Engine.StreamAsync<T>(batchSize, default);

    /// <summary>同步出口是 <c>sync-over-async</c>：内核没有第二套同步执行路径（那会变成两份要各自维护的
    /// 连接与钩子逻辑）。所有 await 都走 <c>ConfigureAwait(false)</c>，所以在没有同步上下文宿主里不会死锁；
    /// 但它在 WinForms/ASP.NET 经典模型下会占住一个线程，异步可用时请优先用 <c>*Async</c>。</summary>
    public List<T> ToList() => Blocking(() => Engine.ListAsync<T>(default));

    public T? FirstOrDefault() => Blocking(() => Engine.FirstAsync<T>(default));

    public int Count() => Blocking(() => Engine.CountAsync(default));

    public bool Contains(T value)
    {
        // 运行期退化路径：表达式树内由 ExpressionTranslator 拦截翻译成 IN 子查询，不到这里。
        // 这里只处理直接调用（如 query.Contains(42)），物化后判等是正确但非最优的实现。
        return ToList().Contains(value);
    }

    // ── 聚合 ──────────────────────────────────────────────────────────

    public TResult Sum<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.SumAsync<TResult>(selector, default));

    public TResult Max<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.MaxAsync<TResult>(selector, default));

    public TResult Min<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.MinAsync<TResult>(selector, default));

    public TResult Average<TResult>(Expression<Func<T, TResult>> selector) => Blocking(() => Engine.AverageAsync<TResult>(selector, default));

    public Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.SumAsync<TResult>(selector, ct);

    public Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.MaxAsync<TResult>(selector, ct);

    public Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.MinAsync<TResult>(selector, ct);

    public Task<TResult> AverageAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default) => Engine.AverageAsync<TResult>(selector, ct);

    static TRes Blocking<TRes>(Func<Task<TRes>> run) => run().GetAwaiter().GetResult();
}

/// <summary><see cref="IOrderedQuery{T}"/> 的落地。<b>有序与无序是同一个引擎</b>，类型区别只为让
/// <c>ThenBy</c> 出现在能接它的对象上，不额外携带状态。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class FastSqlOrderedQuery<T>(QueryEngine engine) : FastSqlQuery<T>(engine), IOrderedQuery<T>
{
    public IOrderedQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> key)
        => new FastSqlOrderedQuery<T>(Engine.Order(key, descending: false, append: true));

    public IOrderedQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key)
        => new FastSqlOrderedQuery<T>(Engine.Order(key, descending: true, append: true));
}

/// <summary>分组链（§5.3）。<b>这一层没有任何物化出口</b>，这是刻意的：分组而未选的形状（整行列 +
/// <c>GROUP BY</c>）在任何一家都是错句，所以它连 <c>Explain()</c> 都不给，只给"继续说下去"的那几颗算子，
/// 出口在 <see cref="Select"/> 交回的 <see cref="IQuery{TResult}"/> 上。
/// 与 <see cref="FastSqlJoinQuery{T1,T2}"/> 同一个分工：只做类型化转发，模型派生在 <see cref="QueryEngine"/>。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
class FastSqlSelectGrouping<TKey, TValue>(QueryEngine engine) : ISelectGrouping<TKey, TValue>
{
    internal QueryEngine Engine { get; } = engine;

    public ISelectGrouping<TKey, TValue> Having(Expression<Func<IGrouping<TKey, TValue>, bool>> predicate)
        => new FastSqlSelectGrouping<TKey, TValue>(Engine.Having(predicate));

    public IOrderedSelectGrouping<TKey, TValue> OrderBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new FastSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: false, append: false));

    public IOrderedSelectGrouping<TKey, TValue> OrderByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new FastSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: true, append: false));

    public ISelectGrouping<TKey, TValue> Skip(int count) => new FastSqlSelectGrouping<TKey, TValue>(Engine.Skip(count));

    public ISelectGrouping<TKey, TValue> Take(int count) => new FastSqlSelectGrouping<TKey, TValue>(Engine.Take(count));

    public IQuery<TResult> Select<TResult>(Expression<Func<IGrouping<TKey, TValue>, TResult>> projector)
        => new FastSqlQuery<TResult>(Engine.Project(projector));
}

/// <summary>分组链的有序形态，理由与 <see cref="FastSqlOrderedQuery{T}"/> 同一条：类型区别只为让
/// <c>ThenBy</c> 出现在能接它的对象上（<c>OrderBy</c> 之后再 <c>OrderBy</c> 是替换而不是追加，那一颗口不在这层暴露）。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class FastSqlOrderedSelectGrouping<TKey, TValue>(QueryEngine engine)
    : FastSqlSelectGrouping<TKey, TValue>(engine), IOrderedSelectGrouping<TKey, TValue>
{
    public IOrderedSelectGrouping<TKey, TValue> ThenBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new FastSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: false, append: true));

    public IOrderedSelectGrouping<TKey, TValue> ThenByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key)
        => new FastSqlOrderedSelectGrouping<TKey, TValue>(Engine.Order(key, descending: true, append: true));
}

/// <summary>联表之后的两表查询（§5.2）。类型参数集合就是「已进 FROM 的表」，所以引用没联过的表在
/// <b>编译期</b>就过不去，而不是等到数据库报"无法绑定多部分标识符"。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
class FastSqlJoinQuery<T1, T2> : IQuery<T1, T2>
{
    public FastSqlJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate)
        => new FastSqlJoinQuery<T1, T2>(Engine.Where(predicate));

    public IQuery<T1, T2> Skip(int count) => new FastSqlJoinQuery<T1, T2>(Engine.Skip(count));

    public IQuery<T1, T2> Take(int count) => new FastSqlJoinQuery<T1, T2>(Engine.Take(count));

    public IOrderedJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> key)
        => new FastSqlOrderedJoinQuery<T1, T2>(Engine.Order(key, descending: false, append: false));

    public IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, TResult>> projector)
        => new FastSqlQuery<TResult>(Engine.Project(projector));

    public IQuery<T1, T2, T3> LeftJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class
        => new FastSqlJoinQuery<T1, T2, T3>(Engine.Join(JoinKind.Left, on));

    public IQuery<T1, T2, T3> InnerJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class
        => new FastSqlJoinQuery<T1, T2, T3>(Engine.Join(JoinKind.Inner, on));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public string ToLogSql() => Engine.ToLogSql();

    public Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default)
        => new FastSqlQuery<TResult>(Engine.Project(projector)).ToListAsync(ct);

    public Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default)
        => new FastSqlQuery<TResult>(Engine.Project(projector)).ToPagedAsync(page, size, options, ct);
}

/// <summary>三表查询。<b>链式在这里封顶</b>：再往上是 4 个类型参数的同构复制，收益递减；§5.2 的目标态是
/// <c>IQuery&lt;T1..T6&gt;</c>，补齐属公开签名变更，按 §16.14 一并待决。所以四表以上的联表本版本没有入口。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
class FastSqlJoinQuery<T1, T2, T3> : IQuery<T1, T2, T3>
{
    public FastSqlJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate)
        => new FastSqlJoinQuery<T1, T2, T3>(Engine.Where(predicate));

    public IQuery<T1, T2, T3> Skip(int count) => new FastSqlJoinQuery<T1, T2, T3>(Engine.Skip(count));

    public IQuery<T1, T2, T3> Take(int count) => new FastSqlJoinQuery<T1, T2, T3>(Engine.Take(count));

    public IOrderedJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key)
        => new FastSqlOrderedJoinQuery<T1, T2, T3>(Engine.Order(key, descending: false, append: false));

    public IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, T3, TResult>> projector)
        => new FastSqlQuery<TResult>(Engine.Project(projector));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public string ToLogSql() => Engine.ToLogSql();

    public Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default)
        => new FastSqlQuery<TResult>(Engine.Project(projector)).ToPagedAsync(page, size, options, ct);
}

/// <summary>两表查询的有序形态。<see cref="IOrderedJoinQuery{T1,T2}"/> 不继承 <c>IQuery&lt;T1,T2&gt;</c>，
/// 所以联表之后 <c>OrderBy</c> 就只剩排序与物化：想再筛条件要在 <c>OrderBy</c> 之前。<b>留着这层窄化，但别把它记成正确性闸门</b> ——
/// 实测谓词写在 <c>OrderBy</c> 前后产出的是同一条 SQL（<c>GuardrailTests.Writing_a_condition_after_paging_emits_the_sql_clause_order_not_the_chain_order</c>）：
/// 谓词恒进 <c>WHERE</c>、序恒进 <c>ORDER BY</c>，书写顺序进不了模型，所以它既挡不住错语义也放不出错语义，挡住的只是"写法顺序与读法不一致"。
/// 单表侧的 <c>IOrderedQuery&lt;T&gt;</c> 没有这层窄化，产出的 SQL 同样与书写顺序无关（§16.15 已决：不给"排序/分页之后再筛条件"加闸门）。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class FastSqlOrderedJoinQuery<T1, T2> : IOrderedJoinQuery<T1, T2>
{
    public FastSqlOrderedJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2> ThenBy<TKey>(Expression<Func<T1, T2, TKey>> key)
        => new FastSqlJoinQuery<T1, T2>(Engine.Order(key, descending: false, append: true));

    public IQuery<T1, T2> ThenByDescending<TKey>(Expression<Func<T1, T2, TKey>> key)
        => new FastSqlJoinQuery<T1, T2>(Engine.Order(key, descending: true, append: true));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default)
        => new FastSqlQuery<TResult>(Engine.Project(projector)).ToListAsync(ct);
}

/// <summary>三表查询的有序形态，理由同 <see cref="FastSqlOrderedJoinQuery{T1,T2}"/>。</summary>
[RequiresUnreferencedCode(TranslationCost.Message)]
[RequiresDynamicCode(TranslationCost.Message)]
sealed class FastSqlOrderedJoinQuery<T1, T2, T3> : IOrderedJoinQuery<T1, T2, T3>
{
    public FastSqlOrderedJoinQuery(QueryEngine engine) => Engine = engine;

    public QueryEngine Engine { get; }

    public IQuery<T1, T2, T3> ThenBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key)
        => new FastSqlJoinQuery<T1, T2, T3>(Engine.Order(key, descending: false, append: true));

    public IQuery<T1, T2, T3> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> key)
        => new FastSqlJoinQuery<T1, T2, T3>(Engine.Order(key, descending: true, append: true));

    public QueryPlan Explain() => Engine.Explain();

    public string ToSql() => Engine.ToSql();

    public Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, CancellationToken ct = default)
        => new FastSqlQuery<TResult>(Engine.Project(projector)).ToListAsync(ct);
}
