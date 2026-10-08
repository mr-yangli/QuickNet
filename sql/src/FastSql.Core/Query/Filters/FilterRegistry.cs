using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FastSql.Query.Model;
using FastSql.Sql.Ast;
using FastSql.Translation;

namespace FastSql.Query.Filters;

/// <summary>全局过滤器（§10.7）：注册在 <c>orm.Filters</c> 上的那些谓词，以及「贴进哪一张表、贴进那一句的哪一段」
/// 这件事的唯一落点。组装根上的 <see cref="FastSqlOrm.Filters"/> 就是这一只，一个 orm 实例一份。
///
/// <para><b>为什么注入点在出口而不是在链式算子上</b>：过滤器要作用于这条查询<em>最终</em>的 FROM 序列，
/// 而 FROM 序列在链上还在长（<c>Where</c> 之后还能 <c>LeftJoin</c>，<c>As()</c> 会换掉根表位的实例）。
/// 注册时就翻成文本的话，列前缀指向的是一个还没进 FROM 的表位。所以这里存的是<b>lambda 本身</b>，
/// 每一次出口 —— <c>ToList</c>/<c>Count</c>/<c>ToPaged</c>/<c>Stream</c>/<c>Explain</c>/<c>ToSql</c> 全部经
/// <c>QueryEngine.Effective()</c> 那一个咽喉 —— 拿<em>当下</em>的表位重新翻译一遍。</para>
///
/// <para><b>"每出口重翻译"不是偷懒，是规格里那句注释直接要求的</b>：
/// <c>orm.Filters.Add&lt;Order&gt;(o =&gt; o.TenantId == Tenant.Current)</c> 后面写着「闭包捕获 AsyncLocal，
/// 每次执行重新求值」。缓存翻译结果等于把第一次执行时那颗租户 id 冻进后面所有语句，而换租户的那一次
/// 不会报错、只会读到<em>上一个租户</em>的行 —— 那是比根本没装过滤器更糟的形态（它会让人以为隔离生效了）。</para>
///
/// <para><b>代价写在明处</b>：每条语句多付一次谓词翻译，且这一档今天<em>没有</em>任何形式的缓存
/// （按 (注册, 表位) 记一份翻译结果会重新引入上面那个竞态，除非把 AsyncLocal 的值请进缓存键 —— 那等于没有缓存）。
/// 读侧本来就在付 §5.10 那笔 RUC/RDC 的税，G1 预翻译落地时过滤器要跟着一起进预翻译，届时这一格再结。
/// 无缓存是这一版的设计现状，不是漏了。</para>
///
/// <para><b>§10.7 那四条约束在这一只上的兑现度是不均匀的，逐条说清</b>：约束 1（作用于 SELECT / UPDATE / DELETE）
/// 兑现了这三条链，<b>没有</b>兑现 <c>SaveChanges</c> 与批量那两档 —— 那两档的 <c>WHERE</c> 长在
/// <c>EntityTracker</c> 上，接过去要先把"被过滤器挡住的行算不算一次并发冲突"这件事定下来（公开语义，见 §14 的 P1 余项那一格）。
/// 约束 2（<c>IgnoreQueryFilters()</c> 与 <c>mandatory</c>）见 <see cref="AssertIgnorable"/>。
/// 约束 3（软删改写）不在这一只上，它在 v0.38 第十七笔有了内容（<c>orm.Behaviors.SoftDelete</c>），而落点是 AST 形状而不是
/// <c>CudBuilding</c> 的文本 —— 判据与两处各管哪一半写在 <c>FastSql.Behaviors.BehaviorRegistry</c> 的类型注释里。
/// 约束 4（过滤器与用户 lambda 同一个翻译器、启动期失败）见 <see cref="Add{TEntity}"/> 里那次试翻译。</para>
/// </summary>
public sealed class FilterRegistry
{
    readonly FastSqlOrm _orm;

    /// <summary>登记是启动期的事（一次），读取是每条语句的事（无数次），所以写侧加锁换整表、读侧不加锁。
    /// <c>volatile</c> 落在这里而不是靠 <c>lock</c> 的栅栏：<see cref="HasAny"/> 要在读路径的热身位上被问一次，
    /// 它<em>不</em>进锁 —— 进了锁就等于每条查询都要为"可能一个人也没注册"付一次monitor 成本。</summary>
    readonly Lock _gate = new();

    volatile Registration[] _items = [];

    internal FilterRegistry(FastSqlOrm orm) => _orm = orm;

    /// <summary>有没有任何一个过滤器在册。<b>读侧与两条写链都靠它在没人用过滤器时整段短路</b>：
    /// 这件事不设短路的话，§12.8 那几格分配基线会为一条没有过滤器的查询多付一次列表与一次翻译，
    /// 而"未订阅连事件对象都不构造"（§10.1 硬规则 2）对过滤器同样成立。</summary>
    internal bool HasAny => _items.Length > 0;

    /// <summary>注册一个全局过滤器。<b>键可以是实体本身，也可以是它实现的接口或基类</b>
    /// （规格示例第一句就是 <c>Add&lt;ISoftDelete&gt;(e =&gt; !e.Deleted)</c>，意图是"按接口注册一次、
    /// 所有实现类都带上这一条"），匹配规则只有一句：<see cref="Type.IsAssignableFrom(Type)"/> 反过来读 ——
    /// 表位的实体类型能被键接住，这个过滤器就对那张表生效。</summary>
    /// <param name="predicate">谓词。<b>存下来的是 lambda，不是文本</b>，理由见类型注释那句"每次执行重新求值"。
    /// 注册这一趟会把它<em>试翻译一次</em>（见下），所以谓词里的闭包在启动期会被求值一遍 —— 过滤器的谓词必须无副作用，
    /// 这条纪律与读侧 <c>Where</c> 本来就有的一样，只是这里多了一层"求值两次（注册一次、执行一次）"。</param>
    /// <param name="mandatory"><c>true</c> ⇒ 这条链上没有任何口子能把它关掉：<c>IgnoreQueryFilters()</c> 当场抛
    /// （§10.7 约束 2 那句「多租户隔离不能靠'约定大家都记得加条件'」）。默认 <c>false</c>。</param>
    /// <remarks><b>规格示例第三句那一站到这里还没有</b>：原稿写的是 <c>orm.Filters.Add&lt;TenantFilter&gt;(mandatory: true)</c>，
    /// <em>没有谓词</em> —— 那要求 <c>TenantFilter</c> 自己能交出谓词来，于是"谁能交"要变成一处公开契约
    /// （要么一个 <c>IFastSqlFilter</c> 之类的接口，要么 <c>Add(Type, LambdaExpression, bool)</c> 那个非泛型口）。
    /// 本版按 <c>Add&lt;TEntity&gt;(predicate, mandatory)</c> 落地，把没有谓词的那一档留给那一处决定，
    /// 而不是先造一个只有自己的用例在用的接口。<b>能兑现的是这一句的实质</b>：
    /// <c>mandatory: true</c> 对带谓词的注册同样成立，所以"不可忽略的租户过滤器"今天是写得出来的
    /// （<c>Add&lt;Order&gt;(o =&gt; o.TenantId == Tenant.Current, mandatory: true)</c>），缺的只是"标记类自己带谓词"那一档语法糖。</remarks>
    /// <remarks><b>启动期那次试翻译（约束 4）对接口与抽象基类键<em>落不到</em></b>：<c>ISoftDelete</c> 没有列契约
    /// （它不是实体，注册表不给它建契约），而那一份谓词要贴的是<em>实现类</em>的契约 —— 于是"这一句翻不翻得动"
    /// 只有到第一次真贴上某张表时才知道。<b>这是约束 4 在这一版只兑现了一半的那一半</b>，写在
    /// <see cref="Probe"/> 与 §10.7 的落地记录里，而不是靠"注册时也去翻一遍所有已知实体"糊过去
    /// （注册表今天没有"列出全部实体"那个口子，为了做一次检查去开它，是给约束 4 补了一条没验过的公开形状）。</remarks>
    [RequiresUnreferencedCode("试翻译过滤器谓词要反射读闭包成员（与读侧 Where 同一套）。")]
    [RequiresDynamicCode("试翻译过滤器谓词的退路是编译表达式树。")]
    public FilterRegistry Add<TEntity>(Expression<Func<TEntity, bool>> predicate, bool mandatory = false) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);

        // 先试翻译、后登记：反过来的话，一条翻不动的谓词会留在册子上，而抛出去的那一句会让人以为没注册上。
        Probe(typeof(TEntity), predicate);

        lock (_gate)
            _items = [.. _items, new Registration(typeof(TEntity), predicate, mandatory)];

        return this;
    }

    /// <summary>约束 4 的落点：注册时按"这张表"翻一遍，翻不动就在这里抛，不等第一次查询。
    /// 结果丢掉不是浪费 —— 要的是那一次翻译<em>走得通</em>这个结论。</summary>
    [RequiresUnreferencedCode("转调 ExpressionTranslator。")]
    [RequiresDynamicCode("转调 ExpressionTranslator。")]
    void Probe(Type key, LambdaExpression predicate)
    {
        if (key.IsInterface || key.IsAbstract)
            return;

        Translate(new Registration(key, predicate, false), _orm.NewTableRef(key, 0));
    }

    /// <summary>表位上在册的过滤器。<b>顺序 = 注册序</b>，与读侧多次 <c>Where</c> 的折叠方向同一条纪律
    /// （两侧都是往末尾追加），所以 <c>((用户条件 AND 过滤器1) AND 过滤器2)</c> 这个形状在读写两条链上一致。</summary>
    internal IReadOnlyList<Registration> For(Type? entity)
    {
        if (entity is null || _items.Length == 0)
            return [];

        List<Registration>? matched = null;

        foreach (var item in _items)
            if (item.Key.IsAssignableFrom(entity))
                (matched ??= []).Add(item);

        return matched ?? [];
    }

    internal IReadOnlyList<Registration> For(TableRef table) => For(table.Contract?.ClrType);

    /// <summary>写侧两条链（<c>IUpdate&lt;T&gt;</c> / <c>IDelete&lt;T&gt;</c>）的叠加口。
    /// <b>它接在 <c>Guard.Whereless</c> 之后</b>：全局过滤器不是「你说的条件」，一条一个 <c>Where</c> 都没写的
    /// UPDATE 不能因为册子上有过滤器就获得免死 —— 那道闸门判的是调用方有没有说过要对哪些行做这件事。
    /// 传进来的 <see cref="TableRef"/> 是那条语句<em>自己的</em>表位（自引用名由方言的
    /// <c>WriteTargetAlias</c> 定），所以 Oracle 血统那一家不会拿到一个带 <c>[t0].</c> 前缀的谓词。</summary>
    [RequiresUnreferencedCode("谓词翻译走闭包常量求值。")]
    [RequiresDynamicCode("谓词翻译的退路是编译表达式树。")]
    internal SqlExpression ApplyTo(TableRef table, SqlExpression where)
    {
        var current = where;

        foreach (var registration in For(table))
            current = And(current, Translate(registration, table));

        return current;
    }

    /// <summary>这一张表上贴得进去几条过滤器。<b>只给 <c>Explain().Reason</c> 那句话用</b>：
    /// 一条链式 UPDATE 的自述如果不说自己的 <c>WHERE</c> 里混进了别的东西，读日志的人就会照着调用方那两句
    /// 去理解那条语句的命中范围 —— 而 §10.7 约束 1 存在的理由恰恰是"这两件事不一样"。</summary>
    internal int CountFor(Type entity) => For(entity).Count;

    /// <summary>读侧的注入口：把在册的过滤器贴进这份模型的 <c>WHERE</c> 或那一个 <c>ON</c>。
    /// <b>返回同一个实例表示什么都没贴</b>（这张表上没过滤器），于是调用方的 <c>with</c> 也不会发生。
    /// <paramref name="ignored"/> 是这条链点名要跳过的那些注册键（<c>IgnoreQueryFilter&lt;TFilter&gt;()</c>，§10.7 约束 2）：
    /// 它在 <see cref="For"/> 之后、<see cref="Translate"/> 之前问，所以被点名的那一条<em>连翻译都不付</em> ——
    /// 而不是"翻出来再丢掉"。null 与空集都不跳任何一条，那条快路是每查询的常态（§12.8 第 1/4 行钉的每算子常数项）。</summary>
    [RequiresUnreferencedCode("谓词翻译走闭包常量求值。")]
    [RequiresDynamicCode("谓词翻译的退路是编译表达式树。")]
    internal QueryModel Apply(QueryModel model, IReadOnlyList<Type>? ignored = null)
    {
        List<SqlExpression>? where = null;
        List<JoinClause>? joins = null;
        Dictionary<int, SqlExpression>? includeOn = null;

        for (var i = 0; i < model.Tables.Count; i++)
        {
            var table = model.Tables[i];

            foreach (var registration in For(table))
            {
                if (ignored is not null && IsIgnored(registration.Key, ignored))
                    continue;

                var node = Translate(registration, table);

                // LEFT 那一档进 ON，其余进 WHERE。这条分岔只有一处理由：把右表的过滤器写进 WHERE，
                // 未匹配的补 NULL 行会被它一起筛掉（NULL 上任何比较都是 UNKNOWN），左联就静默退化成内联 ——
                // 抹掉的正是 §13.6 那条「左联补 NULL 行」用例存在的全部意义。根表与 INNER 两侧等价
                //（INNER 的补 NULL 行不存在），所以放 WHERE 与用户的条件同一个形状。
                // RIGHT / FULL / CROSS 今天从链式走不到（只有 LeftJoin / InnerJoin 两个口），
                // 所以这里不为它们开闸门，只在这句话里留名 —— 补齐那三种联表形态时这一条判据要一起重看。
                if (i > 0 && i <= model.Joins.Count && model.Joins[i - 1].Kind == JoinKind.Left)
                {
                    // 从 joins 里读而不是从 model.Joins：同一张右表上两条过滤器要叠在同一份已有 ON 上，
                    // 读原文会丢掉前一条。
                    joins ??= [.. model.Joins];

                    var at = i - 1;
                    var clause = joins[at];

                    joins[at] = clause with { On = clause.On is null ? node : And(clause.On, node) };

                    continue;
                }

                // Include 的那一档走同一条判据，但要在另一个地方落脚：<b>Include 的 LEFT JOIN 不在
                // <see cref="QueryModel.Joins"/> 里</b>，它是 <c>QueryCompiler.CollectIncludeColumns</c> 在编译期
                // 照着 <see cref="IncludeNode.OnCondition"/> 现造的。于是上面那一岔对表位 tN 永远判不中，
                // 这条过滤器就会落进 WHERE，而它引用的是那一张左联表 —— 两个后果各是一类事故：
                // 未匹配的补 NULL 行被它一起筛掉（左联静默退化成内联，行在页面上凭空消失而不报错），
                // 以及 <c>Count</c> 那一档把 Include 的联表省掉之后（聚合计划不是 <c>EntityProjection</c>，
                // <c>UsesIncludeJoins</c> 就不响），WHERE 里剩下一句引用不到任何表位的 <c>t1.IsDeleted</c>，
                // 库直接报"列不存在"。所以这里折进那颗节点自己的 ON。
                // <b>折的是副本，不是那颗节点</b>：节点实例挂在链上、被同一份链的下一次执行共用，
                // 就地改写会让第二次执行把同一条过滤器再叠一遍（ON 越长越离谱而结果不变，是最难查的那一类）。
                if (model.Includes.Count > 0 && Covers(model.Includes, i))
                {
                    if (includeOn is null)
                        includeOn = new Dictionary<int, SqlExpression>();

                    includeOn[i] = includeOn.TryGetValue(i, out var stacked) ? And(stacked, node) : node;

                    continue;
                }

                (where ??= []).Add(node);
            }
        }

        if (where is null && joins is null && includeOn is null)
            return model;

        var result = model;

        if (where is not null)
            result = result with { Predicates = [.. result.Predicates, .. where] };

        if (joins is not null)
            result = result with { Joins = joins };

        if (includeOn is not null)
            result = result with { Includes = Fold(result.Includes, includeOn) };

        return result;
    }

    /// <summary>这一颗表位是不是 Include 树里某个节点的目标表（<see cref="TableRef.Index"/> 就是它进 FROM 的位置，
    /// 而 Include 的表位正是按 <c>Model.Tables.Count</c> 取的这一个号）。</summary>
    static bool Covers(IReadOnlyList<IncludeNode> includes, int index)
    {
        foreach (var include in includes)
            if (include.TargetTable.Index == index || Covers(include.Children, index))
                return true;

        return false;
    }

    /// <summary>把 <paramref name="on"/> 里那些过滤器折进对应节点的 ON，返回一份新树。
    /// <b>只有真的被折到的那一颗连同它的每一层祖先才换实例</b>：祖先不换的话，改写的就不是副本而是原树
    /// （子节点列表在父节点身上），下一次执行就又踩回就地改写那一格。没被点到的子树原样交回，不复制。</summary>
    static IReadOnlyList<IncludeNode> Fold(IReadOnlyList<IncludeNode> includes, Dictionary<int, SqlExpression> on)
    {
        List<IncludeNode>? result = null;

        for (var i = 0; i < includes.Count; i++)
        {
            var node = includes[i];
            var children = Fold(node.Children, on);
            var extra = on.TryGetValue(node.TargetTable.Index, out var expression) ? expression : null;

            if (extra is null && ReferenceEquals(children, node.Children))
                continue;

            var rewritten = new IncludeNode
            {
                NavigationMemberName = node.NavigationMemberName,
                Relationship = node.Relationship,
                TargetTable = node.TargetTable,
                OnCondition = extra is null ? node.OnCondition : And(node.OnCondition, extra),
            };

            foreach (var child in children)
                rewritten.Children.Add(child);

            (result ??= [.. includes])[i] = rewritten;
        }

        return result ?? includes;
    }

    /// <summary>约束 2 的另一半：<c>IgnoreQueryFilters()</c> 撞上 mandatory 就抛。<b>调用点有两处，
    /// 两处都要</b>：<c>IgnoreFilters()</c> 当场问一次（报错落在写那句 Ignore 的人手里，而不是落在
    /// 一次<em>执行</em>里 —— 同一句 lambda 在 <c>ToList()</c> 才响的话，链式复用时会让人找不到出处）；
    /// 每一次出口再问一次（否则「先 <c>IgnoreQueryFilters()</c> 再 <c>LeftJoin</c> 一张带 mandatory 过滤器的表」
    /// 这一种绕法就是那条约束上留下的洞 —— 标记位已经生效，那条表位的过滤器会被整段跳过）。
    /// 第二处只在 <c>Ignore</c> 过的链上付，未 Ignore 的链一次都不进这个方法。
    ///
    /// <para><paramref name="ignored"/> 非空时<b>只判被点名要跳的那些</b>：约束 2 拦的是"关掉一条不可忽略的过滤器"
    /// 这句话，而点名 <c>IgnoreQueryFilter&lt;ISoftDelete&gt;()</c> 并没有关租户那一条，拦它就是把约束写过头。
    /// 判据与真正跳过的那一处共用 <see cref="IsIgnored"/> —— 一处问、一处跳，两次实现迟早对不上。</para></summary>
    internal void AssertIgnorable(IReadOnlyList<TableRef> tables, IReadOnlyList<Type>? ignored = null)
    {
        if (!HasAny)
            return;

        foreach (var table in tables)
            foreach (var registration in For(table))
            {
                if (ignored is not null && !IsIgnored(registration.Key, ignored))
                    continue;

                if (registration.Mandatory)
                    throw new FastSqlException(
                        $"{table.Table.Name}（表位 {table.Alias}）上有一条 mandatory 的全局过滤器（注册自 {registration.Key.Name}），" +
                        "它不能被 IgnoreQueryFilters() 关掉 —— 规格 §10.7 约束 2 那句「多租户隔离不能靠约定大家都记得加条件」" +
                        "说的就是这一档：能关掉的那一条不叫隔离。要跳过它只有两条路：注册时别写 mandatory，" +
                        "或者不走这条链、直接把自己的条件写全（那一句是你负责的，不是库默认帮你省的）。");
            }
    }

    /// <summary>这一条注册是不是被 <paramref name="ignored"/> 里某一颗点名跳过的。
    /// <b>判据是注册键的同一性，不是继承关系</b>：点名点的是<em>那一格注册</em>，不是"某个类型的所有过滤器"。
    /// 一族式的匹配（<c>type.IsAssignableFrom(key)</c>）看着更省事，但它在一个要命的方向上漏：
    /// <c>Ticket</c> 既实现 <c>ISoftDelete</c>、又可能带一条注册在 <c>Ticket</c> 上的租户过滤器，
    /// 于是点名 <c>ISoftDelete</c> 会连租户那一条一起跳掉 —— 而约束 2 存在的理由恰恰是"隔离条件不能被
    /// 一句看起来只关了软删的话关掉"。静默少一个租户条件是这句话唯一不能被原谅的失败形态。
    ///
    /// <para><see cref="object"/> 是这一句唯一的例外，而它不是特例而是顶类型：不带参数那颗
    /// （<c>IgnoreQueryFilters()</c>）递进来的就是它，"接得住一切"由它在类型系统里的位置承担。
    /// 所以 <c>IgnoreQueryFilter&lt;object&gt;()</c> 与不带参数那一句同一条路、同一个结果，
    /// 判据仍然只有一条 —— "少跳一条"与"全跳"没有两套实现。</para></summary>
    static bool IsIgnored(Type key, IReadOnlyList<Type> ignored)
    {
        foreach (var type in ignored)
            if (type == key || type == typeof(object))
                return true;

        return false;
    }

    /// <summary>过滤器谓词 → AST，并且把翻译失败换成一句点名"是谁贴不进谁"的话。
    /// <b>为什么必须包这一层</b>：翻译器只知道 lambda 与表位，不知道这个 lambda 是用户当场写的还是
    /// 启动期从另一只实体上注册来的。少了这一层，一次接口键的错配会报成"无法翻译表达式 o.Deleted"，
    /// 而调用方要看的是"哪个注册、贴在哪张表上"。</summary>
    [RequiresUnreferencedCode("闭包常量求值按 MemberInfo 读字段/属性。")]
    [RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
    internal SqlExpression Translate(Registration registration, TableRef table)
    {
        // 形参按位置绑表，而过滤器的 lambda 只有一个形参，所以这里造的是"只有这一张表"的模型：
        // 表位实例是外面递进来的那一个，翻出来的列引用因此带的就是最终语句里的那个别名。
        var model = new QueryModel { Tables = [table], Plan = new EntityProjection(0) };

        try
        {
            return _orm.Expressions.Predicate(registration.Predicate, Scope.ForFilter(model, registration.Predicate));
        }
        catch (FastSqlException exception)
        {
            throw new FastSqlException(
                $"{registration.Key.Name} 上注册的全局过滤器贴不进 {table.Table.Name}（表位 {table.Alias}）。" +
                (registration.Key.IsInterface || registration.Key.IsAbstract
                    ? $"键是接口/基类，而这一档要成立得让每一个实现类的契约里都有谓词点到的那个成员 —— 注册期查不出来（没有契约可查），只有贴上某张表时才知道。"
                    : string.Empty) + $"翻译器原话：{exception.Message}",
                exception);
        }
    }

    static SqlExpression And(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.And, left, right);

    /// <summary>一条注册。<b>内部类型</b>：它带着一整份 <see cref="LambdaExpression"/>，
    /// 公开它就等于公开"过滤器可以按运行期 Type 塞进来"这条路，而那条口要的是非泛型签名 —— 那是另一处决定（见 <see cref="Add{TEntity}"/> 的 remarks）。</summary>
    internal sealed record Registration(Type Key, LambdaExpression Predicate, bool Mandatory);
}
