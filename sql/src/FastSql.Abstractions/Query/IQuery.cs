using System.Linq.Expressions;
using FastSql.Sql;

namespace FastSql.Query;

/// <summary>分页总数算法（§5.12）。五值 × provider 能力矩阵决定实际用哪个。</summary>
public enum CountMethod
{
    Auto = 0,
    CountStar = 1,
    /// <summary><c>COUNT(*) OVER()</c> 一次往返拿全（需要 <see cref="DbCapabilities.Count_Over_Window"/>）。</summary>
    CountOverWindow = 2,
    /// <summary>只取 <c>size+1</c> 行判 <see cref="PagedResult{T}.HasNext"/>，不算总数。</summary>
    SkipTotal = 3,
    /// <summary>分离两条语句但在同一连接同一事务内，避免读写分离把 Count 打到延迟从库。</summary>
    SeparateTransaction = 4,
}

public sealed record PagedOptions(CountMethod CountMethod = CountMethod.Auto, bool StripOrderingForCount = true, string? CacheKey = null);

/// <summary>§9.4：分页语法本身要求 <c>ORDER BY</c> 的库（T-SQL 的 <c>OFFSET..FETCH</c>、DM 的 <c>ROWNUM</c>），
/// 在查询没给序时怎么办。默认 <see cref="Throw"/>，因为「补什么序」是语义决定（补主键？补创建时间？），
/// 归约层不知道业务想要哪个，静默补一个就是把不确定的行序伪装成确定的分页结果。
/// 放在模型落位处而不是方言里：方言只负责在策略已经放行时补占位序（<see cref="Sql.ISqlDialect.PagingRequiresOrdering"/>）。</summary>
public enum PagingOrderingPolicy
{
    /// <summary>抛 <c>FastSqlException</c>，消息里点名这一页需要的序。</summary>
    Throw = 0,

    /// <summary>用实体主键升序补上（无主键则退回 <see cref="Throw"/>）。给"就是要一个稳定页、序无所谓"的列表接口。</summary>
    AutoOrderPrimaryKey = 1,

    /// <summary>不补序，交给方言写 <c>ORDER BY (SELECT NULL)</c> 占位。数据库不保证行序，翻页可能重复或漏行。</summary>
    Allow = 2,
}

/// <summary><c>required</c> + <c>init</c> 是本仓库的语法基线（§12.7）：值语义，漏字段就是编译错误。</summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Page { get; init; }

    public required int Size { get; init; }

    /// <summary><see cref="CountMethod.SkipTotal"/> 下为 null，前端文案要降级成「下一页」（§16.11 待决）。</summary>
    public required int? Total { get; init; }

    public bool HasNext { get; init; }

    public int TotalPages => Total is { } t && Size > 0 ? (t + Size - 1) / Size : 0;
}

/// <summary><c>Explain()</c> 的产物，不依赖连接（§3）。改写类钩子的每一步都在这里可回放（§10.1 硬规则 1）。</summary>
public sealed record QueryPlan(
    Sql.Ast.SelectStatement Ast,
    Sql.DialectSql Sql,
    string ModelText,
    IReadOnlyList<Aop.HookTrace> HookReplay)
{
    /// <summary>内联字面量要问 <see cref="Values.IValueBinder"/>，所以计划自带 provider：
    /// AST 与 SQL 本来就是某一个方言的产物，带它不算跨层。</summary>
    public required Providers.IDbProvider Provider { get; init; }

    /// <summary>参数内联的可读 SQL（§3）。只读 <see cref="Sql.DialectSql.Text"/> 是不够的：
    /// 那里面有 <c>@p0</c>，DBA 拿到没法直接执行。</summary>
    public string ToLogSql() => Sql.ToLogSql(Provider);

    public override string ToString()
        => ModelText + Environment.NewLine + Sql.Text + Environment.NewLine
           + string.Join(Environment.NewLine, Sql.Parameters.Select(p => $"  {p.Name} = {p.Value ?? "NULL"}"));
}

/// <summary>读侧契约（§5.4）。<b>不实现 <c>IQueryable</c></b>（§16.16）：链式是唯一入口，
/// <c>from … join … let</c> 查询语法因此不可用，联表只走 <see cref="LeftJoin{T2}"/>/<see cref="InnerJoin{T2}"/>。
/// 留在契约里的只有 <c>Expression&lt;Func&lt;…&gt;&gt;</c> 形态的 lambda 与 <c>IAsyncEnumerable&lt;T&gt;</c>，
/// 两者都在共享框架内，所以本包零外部依赖、Domain 层单引这一包即可（§2）。没有 <c>Clone()</c>（§5.11）。</summary>
public interface IQuery<T> : IAsyncEnumerable<T>
{
    IOrderedQuery<T> OrderBy<TKey>(Expression<Func<T, TKey>> key);

    IOrderedQuery<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> key);

    IQuery<T> Where(Expression<Func<T, bool>> predicate);

    IQuery<T> AsNoTracking();

    /// <summary>这一条链上不套用 <c>orm.Filters</c> 上注册的全局过滤器（§10.7 约束 2）。
    /// <b>它是读侧唯一的旁路口，而且不是万能的</b>：注册时写了 <c>mandatory: true</c> 的那一条
    /// 在这里直接抛 —— 多租户隔离不能靠"约定大家都记得加条件"，而一个能被随手关掉的隔离条件
    /// 就是那种约定。<b>只落在 arity 1 这一档</b>：两表/三表链（<see cref="IQuery{T1,T2}"/>）上今天
    /// 没有这个口子，那是一处已记名的缺口（§10.7 的 v0.26 落地记录里点名了它的后果：联表之后
    /// 想跳过过滤器只能回到 <c>orm.Select&lt;T&gt;()</c> 重起一条链）。
    /// 它自己在 <c>QueryModel</c> 里<em>不留痕迹</em> —— 反过来说，戴过滤器那一条链的
    /// <see cref="Explain"/> 模型文本里会多出那几颗谓词（§10.7 要的正是这件事能被看见：
    /// 一条"看起来没有租户条件"的语句要在 <c>Explain()</c> 里就判得出来），而 <c>IgnoreQueryFilters()</c>
    /// 那一条的模型文本与"这个 orm 实例从没注册过过滤器"逐字节相等。</summary>
    IQuery<T> IgnoreQueryFilters();

    IQuery<T> AsSplitQuery(int navigateBatchSize = 20);

    IQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> projector);

    IQuery<T> IncludeMany<TDetail>(Expression<Func<T, IEnumerable<TDetail>>> navigation);

    /// <summary>显式别名，取代按形参名找表（§5.1）。</summary>
    IQuery<T> As(string alias);

    /// <summary>根表位的 FROM 换成<b>一整段自己写的 SELECT</b>（规格 §11 的表源那一格）：
    /// 外面这一层仍然由库端 —— <c>Where</c>/<c>OrderBy</c>/<c>Select</c> 照契约出列，内层那一段一个字不动，
    /// 两边合成同一个 <c>DialectSql</c>，所以日志、AOP、连接复用、超时与链式同一条链。
    /// <c>args</c> 的口径与 <c>orm.Ado</c> 的字符串口同一份实现（<c>RawSql</c>）：参数名 = 本家前缀 + 属性名，
    /// 文本里的占位符要写成同形的那一颗；位置式那一家（GBase 8s）当场拒，参数名占了生成号段（<c>@p0</c> 那一形）也当场拒。
    /// <b>内层必须交回契约里那些列名</b>（<c>SELECT *</c> 那形天然成立）。
    /// <b>只落在 arity 1 这一档</b>，与 <see cref="IgnoreQueryFilters"/> 同一处已记名的缺口：联表链上没有这一颗口。</summary>
    IQuery<T> WithSql(string sql, object? args = null);

    /// <summary>分表实体的<b>按时间范围选片</b>（§16.4）：起与止各自下整到本档的周期（月档就是月初），
    /// 中间逐周期展开成这一次要读的片清单。链上后面的 <c>Where/OrderBy/Skip/Take</c> 都在<b>合并之后</b>才落，
    /// 所以序与分页是整段区间的事，不是每片各页。
    /// <b>只有一片时不包 <c>UNION ALL</c> 那一层</b>：FROM 直接换成那张物理表，句子与不分表的实体逐字同形。
    /// <b>没有"不给范围就读全部片"的退路</b>：现存的分片有几张只有库知道，而一句静默的 <c>UNION</c> 三百张日表
    /// 是要压在页面上的事故，不是优化。跨片上的聚合/分组/连接一律当场拒（<c>CountAsync</c> 也是聚合），
    /// 分页只要"有没有下一页"时传 <c>CountMethod.SkipTotal</c>。
    /// <b>在不分表的实体上调它直接抛</b>：那不该是一条能写出来的话。与 <see cref="WithSql"/> 同一档，
    /// 只落在 arity 1 这一链上。</summary>
    IQuery<T> SplitTables(DateTime from, DateTime to);

    /// <summary>分表实体的<b>按桶号选片</b>（§16.4 的 <c>SplitKind.Hash</c> 档）：给哪几个桶就读哪几张表，
    /// 重复桶号去掉，越界的桶号抛。与时间档同一句纪律——不给"全部桶"的顺口感，桶数在实体上写着，
    /// 真要全量就自己按桶批过去。哈希取模用的不是 <c>GetHashCode()</c>（那个随进程变，会把同一行在两次运行里
    /// 送到两张表），口径见 <see cref="FastSql.Metadata.SplitKind.Hash"/>。</summary>
    IQuery<T> SplitTables(params int[] buckets);

    IQuery<T> WithCache(string cacheKey, TimeSpan ttl);

    IQuery<T> Distinct();

    IQuery<T> Skip(int count);

    IQuery<T> Take(int count);

    /// <summary>§5.2 的 arity 递增形态。<paramref name="on"/> 的形参按 FROM 序列位置解析，允许截断尾部形参。</summary>
    IQuery<T, T2> LeftJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class;

    IQuery<T, T2> InnerJoin<T2>(Expression<Func<T, T2, bool>> on) where T2 : class;

    QueryPlan Explain();

    string ToSql();

    string ToSql(out IReadOnlyList<Sql.AdoParameter> parameters);

    string ToLogSql();

    Task<List<T>> ToListAsync(CancellationToken ct = default);

    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    Task<PagedResult<T>> ToPagedAsync(int page, int size, PagedOptions? options = null, CancellationToken ct = default);

    IAsyncEnumerable<T> ToAsyncEnumerable(int batchSize);

    List<T> ToList();

    T? FirstOrDefault();

    int Count();

    /// <summary>判断子查询结果集是否包含指定值。在表达式树内翻译为 <c>target IN (SELECT ...)</c>；
    /// 在表达式树外（运行期直接调用）退化为 <c>FirstOrDefault</c> 判空。</summary>
    bool Contains(T value);

    // ── 分组 ──────────────────────────────────────────────────────────

    /// <summary>按 <paramref name="keySelector"/> 那一列（或那几列）分组，交出分组链（§5.3）。
    /// <b>返回的不是 <c>IQuery&lt;…&gt;</c></b>：分组之后能选的只有分组键与聚合值，而这个限制由类型系统免费代劳 ——
    /// 形参是 <see cref="IGrouping{TKey, TElement}"/>，它除了 <c>Key</c> 与「元素序列上的聚合方法」之外
    /// 一个实体成员都没有，所以「分组后 Select 里偷偷放非聚合列」那一类事故在这里根本写不出来，
    /// 不必再开一颗 <c>ONLY_FULL_GROUP_BY</c> 风格的运行期校验开关（§5.3 原稿那颗开关的用途已被类型参数吃掉）。</summary>
    ISelectGrouping<TKey, T> GroupBy<TKey>(Expression<Func<T, TKey>> keySelector);

    // ── 聚合 ──────────────────────────────────────────────────────────

    /// <summary>对指定列求和。表达式树内翻译为 <c>(SELECT SUM(...) FROM ...)</c> 标量子查询。</summary>
    TResult Sum<TResult>(Expression<Func<T, TResult>> selector);

    /// <summary>对指定列求最大值。</summary>
    TResult Max<TResult>(Expression<Func<T, TResult>> selector);

    /// <summary>对指定列求最小值。</summary>
    TResult Min<TResult>(Expression<Func<T, TResult>> selector);

    /// <summary>对指定列求平均值。</summary>
    TResult Average<TResult>(Expression<Func<T, TResult>> selector);

    Task<TResult> SumAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);

    Task<TResult> MaxAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);

    Task<TResult> MinAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);

    Task<TResult> AverageAsync<TResult>(Expression<Func<T, TResult>> selector, CancellationToken ct = default);
}

public interface IOrderedQuery<T> : IQuery<T>
{
    IOrderedQuery<T> ThenBy<TKey>(Expression<Func<T, TKey>> key);

    IOrderedQuery<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> key);
}

/// <summary>分组链（§5.3 的 <c>ISelectGrouping&lt;TKey,TValue&gt;</c>，草案名在此落地）。
/// <b>它刻意没有 <c>Where</c>、也没有任何物化出口</b>：分组之前的条件属于 <see cref="IQuery{T}"/>，
/// 分组之后的条件属于 <see cref="Having"/>；而「分组了却还没选」这条链上的形状（整行 + <c>GROUP BY</c>）
/// 在任何一家都是错句，所以这里连 <c>Explain()</c> 都不给 —— 出口一律在 <see cref="Select"/> 之后，
/// 拿到 <c>IQuery&lt;TResult&gt;</c> 就是普通的 <c>ToListAsync/ToPagedAsync/Explain</c>。</summary>
public interface ISelectGrouping<TKey, TValue>
{
    /// <summary>筛分组。lambda 的形参是 <see cref="IGrouping{TKey, TElement}"/>，
    /// 于是能写的只有 <c>g.Key</c> 与 <c>g.Count()/g.Sum(…)</c> 这一族 —— 谓词落进 <c>HAVING</c> 而不是 <c>WHERE</c>，
    /// 这一点由「它在 <c>GroupBy</c> 之后」决定，不由书写顺序决定。多条 <c>Having</c> 以 <c>AND</c> 叠加。</summary>
    ISelectGrouping<TKey, TValue> Having(Expression<Func<IGrouping<TKey, TValue>, bool>> predicate);

    IOrderedSelectGrouping<TKey, TValue> OrderBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);

    IOrderedSelectGrouping<TKey, TValue> OrderByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);

    /// <summary>分组链上的分页：页与组同一单位（<c>ORDER BY … OFFSET/FETCH</c> 落在分组之后的结果集上）。
    /// 序的缺省策略照 §9.4；但「无序时补主键」在分组上没有对应物（主键不在分组键里），会当场报错而不是补一条错句。</summary>
    ISelectGrouping<TKey, TValue> Skip(int count);

    ISelectGrouping<TKey, TValue> Take(int count);

    /// <summary>选出来就是最终形状：DTO / 匿名类型 / 一颗标量（<c>g =&gt; g.Count()</c>）。
    /// 投影列只能读 <c>g.Key</c>（复合键读 <c>g.Key.成员</c>）与聚合，其余在 <see cref="IGrouping{TKey, TElement}"/>
    /// 上根本不存在，所以编译期就过不去。</summary>
    IQuery<TResult> Select<TResult>(Expression<Func<IGrouping<TKey, TValue>, TResult>> projector);
}

/// <summary>分组链的有序形态。与 <see cref="IOrderedQuery{T}"/> 同一个理由：类型区别只为让 <c>ThenBy</c>
/// 出现在能接它的对象上，<c>OrderBy</c> 之后再 <c>OrderBy</c> 会<em>替换</em>前一串，所以那一颗口不在这层暴露。</summary>
public interface IOrderedSelectGrouping<TKey, TValue> : ISelectGrouping<TKey, TValue>
{
    IOrderedSelectGrouping<TKey, TValue> ThenBy<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);

    IOrderedSelectGrouping<TKey, TValue> ThenByDescending<TKeyOrder>(Expression<Func<IGrouping<TKey, TValue>, TKeyOrder>> key);
}

/// <summary>联表后的多表查询。类型参数集合就是「已进 FROM 的表」，编译期挡住引用未 join 的表（§5.2）。</summary>
public interface IQuery<T1, T2>
{
    IQuery<T1, T2> Where(Expression<Func<T1, T2, bool>> predicate);

    IQuery<T1, T2, T3> LeftJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class;

    IQuery<T1, T2, T3> InnerJoin<T3>(Expression<Func<T1, T2, T3, bool>> on) where T3 : class;

    IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, TResult>> projector);

    IOrderedJoinQuery<T1, T2> OrderBy<TKey>(Expression<Func<T1, T2, TKey>> key);

    IQuery<T1, T2> Skip(int count);

    IQuery<T1, T2> Take(int count);

    QueryPlan Explain();

    string ToSql();

    string ToLogSql();

    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default);

    Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default);
}

public interface IQuery<T1, T2, T3>
{
    IQuery<T1, T2, T3> Where(Expression<Func<T1, T2, T3, bool>> predicate);

    IQuery<TResult> Select<TResult>(Expression<Func<T1, T2, T3, TResult>> projector);

    IOrderedJoinQuery<T1, T2, T3> OrderBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key);

    IQuery<T1, T2, T3> Skip(int count);

    IQuery<T1, T2, T3> Take(int count);

    QueryPlan Explain();

    string ToSql();

    string ToLogSql();

    Task<PagedResult<TResult>> ToPagedAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, int page, int size, PagedOptions? options = null, CancellationToken ct = default);
}

public interface IOrderedJoinQuery<T1, T2>
{
    IQuery<T1, T2> ThenBy<TKey>(Expression<Func<T1, T2, TKey>> key);

    IQuery<T1, T2> ThenByDescending<TKey>(Expression<Func<T1, T2, TKey>> key);

    QueryPlan Explain();

    string ToSql();

    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, TResult>> projector, CancellationToken ct = default);
}

public interface IOrderedJoinQuery<T1, T2, T3>
{
    IQuery<T1, T2, T3> ThenBy<TKey>(Expression<Func<T1, T2, T3, TKey>> key);

    IQuery<T1, T2, T3> ThenByDescending<TKey>(Expression<Func<T1, T2, T3, TKey>> key);

    QueryPlan Explain();

    string ToSql();

    Task<List<TResult>> ToListAsync<TResult>(Expression<Func<T1, T2, T3, TResult>> projector, CancellationToken ct = default);
}
