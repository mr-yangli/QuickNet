using System.Linq.Expressions;
using QuickSql.Sql;

namespace QuickSql.Query;

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
    /// <summary>抛 <c>QuickSqlException</c>，消息里点名这一页需要的序。</summary>
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

    /// <summary>按<b>成员名</b>排序 —— 给"排序键是运行期才知道的一个字符串"的调用方（页面表头点了哪一列、
    /// 导出任务挑的排序字段）。<b>它不是 <c>ORDER BY</c> 片段的入口</b>：那颗字符串只拿去<em>认列</em>
    /// （先属性名、再列名、最后大小写不敏感的唯一命中），认出来发的是契约里那一颗列加方言引号，
    /// 认不出当场抛。<b>没有"原样拼进去"的退路，所以这一格不需要黑名单</b>：黑名单会漏，而这一条路上
    /// "用户文本进 SQL"这件事没有对应的代码路径 —— 从外部来的排序键写错时症状是<em>页面顺序不对</em>，
    /// 没人会为此报修，所以它只能响，不能猜。
    /// 方向由 <paramref name="descending"/> 给，不写在字符串里：把 <c>"Name desc"</c> 拆成"名字 + 方向"
    /// 是调用方的口径，库不猜哪种方言的写法。多列排序在 <see cref="IOrderedQuery{T}"/> 上接
    /// <see cref="IOrderedQuery{T}.ThenBy(string, bool)"/>。<b>只落在 arity 1 这一档</b>，
    /// 与 <see cref="IgnoreQueryFilters"/>/<see cref="WithSql"/> 同一处已记名的缺口：联表链上
    /// "一个名字属于哪张表"要先有别名规则，那是另一格。</summary>
    IOrderedQuery<T> OrderBy(string memberName, bool descending = false);

    IQuery<T> Where(Expression<Func<T, bool>> predicate);

    /// <summary><b>条件成立才贴这一条谓词</b>：给"筛选条件是可选的"那一类调用方（检索表单里用户没填的那一栏）。
    /// <paramref name="condition"/> 为假时返回<em>自己</em>，一个字节的模型都不改，所以它与"根本没写过这一句"逐字同形
    /// （<see cref="Explain"/> 的模型文本、发出去的 SQL 与参数编号都不变）。<b>它不是开关而是省一段 <c>if</c></b>：
    /// 三个参数的入口与 <see cref="Where(Expression{Func{T, bool}})"/> 同一颗咽喉，翻译失败照样在编译期之前响。
    /// 多条可选条件就是连着写几句，<c>AND</c> 的顺序 = 书写的顺序。</summary>
    IQuery<T> Where(bool condition, Expression<Func<T, bool>> predicate);

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
    /// 那一条的模型文本与"这个 orm 实例从没注册过过滤器"逐字节相等。
    /// <b>它与 <c>IgnoreQueryFilter&lt;TFilter&gt;()</c> 差一个 <c>s</c>，那不是手误也不是漏改</b>：这一颗的字面
    /// 照抄 EF Core 的 <c>IgnoreQueryFilters()</c>，从 EF 迁过来的人第一眼认得的就是它，所以复数留着；按名那一颗
    /// 是本库自造的口、不对齐谁，单数读作"忽略某一条过滤器"更贴它的射程。两颗是同一件事的两种射程，不是两件事。</summary>
    IQuery<T> IgnoreQueryFilters();

    /// <summary>这一条链上只跳过<em>注册键正是 <typeparamref name="TFilter"/> 的那几条</em>全局过滤器，其余照旧贴。
    /// 参数吃的就是 <c>orm.Filters.Add&lt;TFilter&gt;(…)</c> 那一颗键（实体，或它实现的接口/基类，§10.7），
    /// 判据是<em>键的同一性</em>而不是继承关系：注册在 <c>ISoftDelete</c> 上的那一条要点 <c>ISoftDelete</c> 才跳得掉，
    /// 点名实体碰不到它 —— 反过来说，点名一个接口也<em>碰不到</em>注册在实现类上的那些条（比如同一张表上的租户过滤器）。
    /// <b>这一档窄在这里是有理由的</b>：一族式的点名会让"我只想关软删那一条"这句话静默地少掉一个租户条件，
    /// 而那正是下面那颗 <c>mandatory</c> 要拦的失败形态，拦不住的地方就不该提供口子。
    ///
    /// <para>它与 <see cref="IgnoreQueryFilters()"/> 是同一颗口的两种射程，<b>可以叠着写</b>
    /// （<c>.IgnoreQueryFilter&lt;ISoftDelete&gt;().IgnoreQueryFilter&lt;ITenant&gt;()</c> 两条都跳）。
    /// <see cref="IgnoreQueryFilters()"/> 那一句"没点名的也跳"在本方法里没有对应物：按名那一句只动点名的。
    /// 要说"全部"就写那一句；<c>IgnoreQueryFilter&lt;object&gt;()</c> 与它同一条路、同一个结果（<see cref="object"/>
    /// 是顶类型，那颗清单里它是唯一一颗匹配一切的键）。</para>
    ///
    /// <para><b>约束 2 照旧生效</b>：被点名的那一条注册时写了 <c>mandatory: true</c> 就在这里抛，
    /// 而<em>没被点名</em>的 mandatory 不受影响（那一条本来就要贴进句子，没什么可拦的）。
    /// 与上面那颗同样只落在 arity 1 这一档。</para></summary>
    IQuery<T> IgnoreQueryFilter<TFilter>() where TFilter : class;

    IQuery<T> AsSplitQuery(int navigateBatchSize = 20);

    IQuery<TResult> Select<TResult>(Expression<Func<T, TResult>> projector);

    /// <summary>急切加载<em>集合</em>导航属性（to-many，§5.7）。与 <see cref="IIncludableQuery{T, TLastProp}"/>
    /// 那一家（<c>Include</c>）分得很开，理由是形状不同：<c>Include</c> 是一条 LEFT JOIN 把整只<em>引用</em>实体
    /// 随主查询带回来，而把同一写法用在集合边上会<em>按明细行数复制每一行主表</em> —— 页码、行数与聚合一起改语义。
    /// 所以这一颗不动主查询 SQL：主查询执行完再发一条 <c>SELECT ... WHERE 外键 IN (父端主键集合)</c>，
    /// 在内存里缝回父实体的集合属性。父端为空时那条子查询整个跳过（不发空 IN）。
    /// 返回 <see cref="IIncludableCollectionQuery{T, TDetail}"/>，所以可以再往下一层挂
    /// <c>ThenIncludeMany</c>（<c>IncludeMany(t =&gt; t.Groups).ThenIncludeMany(g =&gt; g.Details)</c> 三张表两条子查询，
    /// 下一层的父端就是上一层刚缝出来的那一批）。外键发现按<em>约定</em>三档：子实体上指向父类型的引用导航、
    /// 集合边上点名的 <c>[ForeignKey]</c>、列名 <c>{父类型名}Id</c>。
    /// <b>子行的序</b>接链上的 <c>OrderByMany(d =&gt; d.Order)</c> 那颗扩展口（排在 <c>ThenIncludeMany</c>
    /// 之前就是给前一层点序）：发射在这一层子 SELECT 的 <c>ORDER BY</c> 上，主查询与不发序时逐字节同形。</summary>
    IIncludableCollectionQuery<T, TDetail> IncludeMany<TDetail>(Expression<Func<T, IEnumerable<TDetail>>> navigation);

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
    /// 送到两张表），口径见 <see cref="QuickSql.Metadata.SplitKind.Hash"/>。</summary>
    IQuery<T> SplitTables(params int[] buckets);

    /// <summary>这一条链点名落在<em>主库</em>上（§16.4 的读写分离）。<b>它与"三格自动强制主库"是同一颗开关的
    /// 两面</b>：事务内、同一条执行流上这座集群写过、这座集群一个从库都没配 —— 那三格不用点名也走主库，
    /// 这一颗是给"我知道我从库上此刻读不到我要的那一行"的调用方用的（刚切完复制、批处理尾巴上的核对读）。
    /// <b>它不改 SQL 文本</b>：点的只是连接，所以 <c>Explain()</c> 的模型文本与不点的那一条逐字相等，
    /// 而"这句实际走了哪条"在 <c>CommandLogEvent.Route</c> 那一格里看得见（那才是它的验收面）。
    /// <b>只落在 arity 1 这一档</b>，与 <see cref="WithSql"/> 同一处已记名的缺口。</summary>
    IQuery<T> FromPrimary();

    /// <summary>这一条链点名落在<em>从库</em>上（§16.4）。<b>它是三格强制主库的显式反命</b>，因此有两处
    /// <em>当场抛</em>而不是静默改道：在这条事务里点它（事务内的读必须落在被这条事务独占的那条连接上，
    /// 换一条去读说的就不是这个事务里的数据）、以及这座集群根本没配从库（"你要从库而这里没有"落不了地，
    /// 把它悄悄收成主库就是"看起来能用"那一类，而那条路的症状是容量与延迟，不是报错）。
    /// <b>没有权重、没有延迟探测</b>：从库按 <c>QuickSqlOrmOptions.Replicas</c> 给出的顺序轮询，
    /// 而复制延迟本家测不到（没有承接它的 SPI 槽位），所以这一颗要读"够新的"数据时请改用
    /// <see cref="FromPrimary"/>。</summary>
    IQuery<T> FromReplica();

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

    /// <summary><see cref="ToListAsync"/> 的数组形态：<b>同一颗咽喉</b> —— 同一条模型、同一句 SQL、
    /// 同一处跟踪与缓存落点、同样只发一条语句，换的只是交回来的 CLR 形状。<b>它不省一趟，反而多一次
    /// 整段拷贝</b>：物化那一路落的本来就是 <c>List&lt;T&gt;</c>，缓存里存的也是那一只
    /// （<c>cache.Set(key, rows, ttl)</c>），所以"少一次拷贝"要的是 <see cref="ToListAsync"/>。
    /// 这一颗值的是<em>交出去之后改不动</em> —— 长度定死，而且拿到的不是缓存里那一只，
    /// 调用方 <c>Add</c>/<c>Clear</c> 不会顺着改到下一次命中交回来的结果。
    /// 同步孪生口是 <see cref="ToArray"/>，与 <see cref="ToList"/> 同一档 <c>sync-over-async</c>。
    /// <b>只落在 arity 1 这一档</b>：联表链上那几颗带投影的 <c>ToListAsync&lt;TResult&gt;(projector)</c>
    /// 没有对应物，要数组就 <c>Select(projector)</c> 回到单表链再调这一颗。</summary>
    Task<T[]> ToArrayAsync(CancellationToken ct = default);

    Task<T?> FirstOrDefaultAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);

    /// <summary>存在性出口，计数一族的第三颗。<b>今天发的是 <c>COUNT(*) &gt; 0</c></b>，语义对但没到最优：
    /// <c>WHERE EXISTS(…)</c> 的反形要改投影计划，记在 P1 而不是不说 —— 因为"大表上 <c>Any</c> 慢"是可预期的成本，
    /// 而"<c>Any</c> 返回错结果"不是。<b>它与 <see cref="CountAsync"/> 走同一条咽喉</b>，所以分组、
    /// <c>Distinct</c>、带 <c>Skip/Take</c> 的链上判的是同一批行；那条链带页时"有没有"问的是
    /// <em>那一页</em>有没有，不是整表（与 <c>Skip(…).Take(…).Any()</c> 同一语义）。</summary>
    Task<bool> AnyAsync(CancellationToken ct = default);

    /// <summary>计数一族的第四颗，与 <see cref="CountAsync"/> 同一个咽喉、同一档派生表口径。
    /// <b>它不改变发出去的句子</b>：库里那一列本来就是整数，所以这一颗与 <c>Count</c> 发同一句
    /// <c>COUNT(*)</c>，换的只是读回来的 CLR 形状（<see cref="long"/>）。存在的理由只有这一条 ——
    /// 行数过了 <c>int</c> 的范围时，<see cref="CountAsync"/> 那一颗读不回来。</summary>
    Task<long> LongCountAsync(CancellationToken ct = default);

    Task<PagedResult<T>> ToPagedAsync(int page, int size, PagedOptions? options = null, CancellationToken ct = default);

    IAsyncEnumerable<T> ToAsyncEnumerable(int batchSize);

    List<T> ToList();

    /// <summary><see cref="ToArrayAsync"/> 的同步口，与 <see cref="ToList"/> 同一档 <c>sync-over-async</c>
    /// （异步可用时优先 <c>ToArrayAsync</c>）。<b>它不是"少付一次拷贝"的那条路</b>：走的仍是同一条
    /// <c>Rows</c> 加末尾那一次 <c>ToArray()</c>，比 <see cref="ToList"/> 多的那一趟整段拷贝与异步那颗逐字相同。</summary>
    T[] ToArray();

    T? FirstOrDefault();

    int Count();

    /// <summary><see cref="AnyAsync"/> 的同步口，与 <see cref="Count"/> 同一档 <c>sync-over-async</c>
    /// （异步可用时优先 <c>AnyAsync</c>）。</summary>
    bool Any();

    /// <summary><see cref="LongCountAsync"/> 的同步口，同一档 <c>sync-over-async</c>。</summary>
    long LongCount();

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

    /// <summary>字符串形态的第二颗，与 <see cref="IQuery{T}.OrderBy(string, bool)"/> 共用同一处认列
    /// （同一份三档、同一处抛），差别只在它是<em>追加</em>到已有的序后面。列序 = 调用序，
    /// 这一点与 lambda 那一条同一个口径。</summary>
    IOrderedQuery<T> ThenBy(string memberName, bool descending = false);
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

    /// <summary>条件成立才贴这一条谓词，为假时返回自己；口径同 <see cref="IQuery{T}.Where(bool, Expression{Func{T, bool}})"/>。</summary>
    IQuery<T1, T2> Where(bool condition, Expression<Func<T1, T2, bool>> predicate);

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

    /// <summary>条件成立才贴这一条谓词，为假时返回自己；口径同 <see cref="IQuery{T}.Where(bool, Expression{Func{T, bool}})"/>。</summary>
    IQuery<T1, T2, T3> Where(bool condition, Expression<Func<T1, T2, T3, bool>> predicate);

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
