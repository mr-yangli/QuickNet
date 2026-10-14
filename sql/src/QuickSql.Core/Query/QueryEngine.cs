using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Materialization;
using QuickSql.Metadata;
using QuickSql.Query.Filters;
using QuickSql.Query.Model;
using QuickSql.Sql;
using QuickSql.Tracking;
using QuickSql.Translation;
using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using QuickSql.Sql.Ast;

namespace QuickSql.Query;

/// <summary>读侧唯一的算子落点（§5.5）。<b>算子只在这里实现一次</b>：链式对象每个方法都只是类型化的转发，
/// 于是「同一条查询在不同 arity 上产出的模型一致」不是两边各写一遍再测相等，而是只有一个实现。
/// 这里也不产生任何 SQL 文本：出口在下面 <see cref="Build"/>，它只读 <see cref="Model"/>。</summary>
/// <remarks>标注的理由见 §5.10：闭包常量求值要按 <see cref="System.Reflection.FieldInfo"/> 读值，
/// 不可约算术形态的退路是 <c>LambdaExpression.Compile()</c>。G1 预翻译接管后这两个标注一起摘掉。</remarks>
[RequiresUnreferencedCode("lambda 归约与闭包常量求值要反射读成员。")]
[RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
sealed class QueryEngine
{
    readonly string[] _tables;

    /// <summary>这条链要去的那座集群 = 根表位的声明（§16.4）。<b>读"根"而不是读"全部表位"是有配套的</b>：
    /// 跨集群的连接与急切加载在编译口就拒掉了（<c>QueryCompiler</c> 里那一颗），所以能走到执行的句子
    /// 只可能说一座集群的名字，而那一座就是根表位那一张表住的那一座。
    /// 集合导航的第二条查询<em>不</em>受这一格约束：它是一条独立的 SELECT，根表位是子实体自己，
    /// 于是"父在 A 库、子在 B 库、内存里缝起来"天然成立 —— 那正是分库要的东西，不是漏洞。</summary>
    readonly string _cluster;

    /// <summary>会话的跟踪器，<b>null 表示这一条链不跟踪</b>（<c>orm.Select&lt;T&gt;()</c> 与
    /// <c>AsNoTracking()</c> 之后都是 null）。v0.16 之前这个位落在 <c>QueryModel.NoTracking</c> 上，
    /// 挪到这里才让 §8.1 那句「跟踪与否不是两套 API」成为事实：模型里没有跟不跟踪这件事，
    /// 于是 <c>orm.Select&lt;T&gt;()</c> 与 <c>db.Set&lt;T&gt;().AsNoTracking()</c> 的模型文本逐字节相等
    /// （§13.2 第 2 项 ② 的判据）。它跟着 <see cref="Derive"/> 走，所以 <c>Where/OrderBy/Skip</c>
    /// 派生出来的每一段都还是同一个会话的那份身份映射。</summary>
    readonly ChangeTracker? _tracker;

    /// <summary><c>IgnoreQueryFilters()</c> 的落点（§10.7 约束 2）。<b>它跟着 <see cref="Derive"/> 走</b>，
    /// 所以"这一条链不戴全局过滤器"是一句说一次就全程有效的话，而不是每次出口重新判断。
    /// 它<em>不</em>进 <see cref="QueryModel"/>：模型是查询的形状，而戴不戴过滤器是这一次执行的决定 ——
    /// 进了模型就会让 §13.2 那句「链式与 LINQ 两侧模型文本逐字节相等」的判据多一个自由度。
    /// 代价是这个位只有读侧用得到（<c>WithCache</c> 的键不含它，见 <see cref="Cache"/> 那句提醒）。
    ///
    /// <para><b>形状是一颗可空清单而不是一颗 bool</b>：清单里是<em>被点名跳过的那些注册键</em>
    /// （<c>IgnoreQueryFilter&lt;TFilter&gt;()</c>），"全部"那一句递进来的是唯一一颗 <see cref="object"/>，
    /// 而 null 是"没点过名"。选清单而不是 <c>bool + Type</c> 两格，是因为多次点名（既跳软删又跳租户）
    /// 本来就要往末尾追加 —— 那正是 <see cref="_navigation"/> 与 <c>Predicates</c> 用的同一个形状。
    /// null 那一档是热路径：没点过名的链一次都不进 <c>AssertIgnorable</c>，也不为这一格多付一次分配。
    /// 清单怎么对上一颗注册（按键的同一性，不按继承）判在 <c>FilterRegistry.IsIgnored</c> 一处，
    /// 那里也写了"为什么不按一族匹配"那句代价更高的理由。</para></summary>
    readonly IReadOnlyList<Type>? _ignoredFilters;

    /// <summary>分组链的翻译侧形状（§5.3）。<b>它不进 <see cref="QueryModel"/>：SQL 侧的
    /// <c>GROUP BY</c> 表达式已经在模型里</b>，这里只留"<c>g.Key</c> 落回哪颗表达式、<c>g.Sum(o =&gt; …)</c> 的
    /// <c>o</c> 绑到哪张表"这一件翻译时才要用的事。它跟着 <see cref="Derive"/> 走，理由与 <c>_tracker</c> 同一条：
    /// 分组之后的 <c>Having/OrderBy/Select</c> 都是派生出来的新引擎，而它们的 lambda 形参是 <c>IGrouping</c>，
    /// 没有这一格就绑不出作用域。</summary>
    readonly GroupingShape? _grouping;

    /// <summary>这条链上<em>已经按引用导航自动补进 FROM</em> 的那几张表（§5.2）。它与
    /// <see cref="_grouping"/>、<see cref="_tracker"/> 同一档：跟着 <see cref="Derive"/> 走，而不进
    /// <see cref="QueryModel"/> —— 补出来的那条 <c>LEFT JOIN</c> 本身<em>已经</em>在模型的
    /// <see cref="QueryModel.Joins"/> 里（那是查询形状，该进规范文本），这一份留的只是"宿主那一格的这根边
    /// 已经联过"这条翻译时才要用的对照，为的是下一颗 lambda 再读 <c>o.Customer.City</c> 时复用同一个表位，
    /// 而不是联出第二张 <c>Customer</c>。
    ///
    /// <para><b>它是只读清单，不是可变登记表</b>：链上的对象可复用（§5.11），而 <c>q.Where(导航1)</c> 与
    /// <c>q.Where(导航2)</c> 这两个分叉各自的模型里只比自己那一条。共享一只可变字典的话，第二个分叉会
    /// 看见第一颗 lambda 补的那张表位并复用之，而它自己那份 FROM 里没有那张表 —— 得到的是一句
    /// 引用了不存在表位的错句。所以每颗 lambda 一份现场（<see cref="Scout"/>），落地时才延长这一份。</para></summary>
    readonly IReadOnlyList<NavigationJoin> _navigation;

    public QueryEngine(QuickSqlOrm orm, QueryModel model, ChangeTracker? tracker = null,
        IReadOnlyList<Type>? ignoredFilters = null, GroupingShape? grouping = null,
        IReadOnlyList<NavigationJoin>? navigation = null)
    {
        Orm = orm;
        Model = model;
        _tracker = tracker;
        _ignoredFilters = ignoredFilters;
        _grouping = grouping;
        _navigation = navigation ?? [];
        _tables = [.. model.Tables.Select(t => t.Table.Name)];
        _cluster = DataSourceRouting.ClusterOf(model.Root.Table);
    }

    public QuickSqlOrm Orm { get; }

    public QueryModel Model { get; }

    public QueryEngine Derive(QueryModel next) => new(Orm, next, _tracker, _ignoredFilters, _grouping, _navigation);

    /// <summary>分组那一步专用：模型与形状同时换。<b>不写成 <c>Derive(next) with grouping</c></b>——
    /// 形状在引擎上而不在模型上，派生一次只会把刚立起来的分组丢掉。</summary>
    QueryEngine Derive(QueryModel next, GroupingShape grouping) => new(Orm, next, _tracker, _ignoredFilters, grouping, _navigation);

    /// <summary>这一颗 lambda 的翻译现场：一份只读的已联清单 + 从进来的表位数往后长的位置。
    /// <see cref="QueryModel"/> 是不可变的，所以补出来的联表只能<em>欠着</em>，由算子决定它落进哪一份新模型
    /// （<see cref="Advanced"/>）—— 这就是它每颗 lambda 新建一颗、而不是挂在引擎上的理由。
    ///
    /// <para><b>没有 to-one 边的那一张表不建现场（返回 null）</b>：单表链上唯一的宿主就是那张表，
    /// 它的契约里没有引用导航，就没有任何一次 <c>Resolve</c> 问得出结果 —— 而这一颗是每算子一份的，
    /// §12.8 第 4 行「每谓词字节」钉的正是这种每算子常数项，所以「用不用自动联表」这笔钱只由真用得着的人付。
    /// 两张表以上照旧建：联进来的任何一张都可能是宿主，而 <c>Tables[0]</c> 那一张没有边不代表其余的没有。</para></summary>
    NavigationJoinScout? Scout(QueryModel model)
        => model.Tables is [var only] && only.Contract?.Relationships is { Count: 0 }
            ? null
            : new NavigationJoinScout(_navigation, model.Tables.Count, Orm.TableRefFactory, model);

    /// <summary>把这一颗 lambda 欠下的自动联表落地，并把它记进链级那份清单。
    /// <b>两处要一起改</b>：只落模型则下一颗 lambda 认不出"已经联过"而联第二张；只记清单则这一颗的 ON
    /// 根本没进 FROM，发出去的句子引用一张不存在的表。没有欠账时走 <see cref="Derive(QueryModel)"/>，
    /// 一条数组拷贝都不付（§12.8 那几格被门禁钉住的分配数字里就有"每个算子都新建一份"这一形）。
    /// <paramref name="grouping"/> 是给 <see cref="GroupBy"/> 那一步递新形状用的：那一次派生本来就要同时换
    /// 模型与形状，为它另开一条出口只会把"欠账落地"这件事写成两份实现。</summary>
    QueryEngine Advanced(NavigationJoinScout? scout, QueryModel model, GroupingShape? grouping = null)
        => scout is { HasPending: true }
            ? new(Orm, scout.Flush(model), _tracker, _ignoredFilters, grouping ?? _grouping, [.. _navigation, .. scout.Pending])
            : grouping is { } shape ? Derive(model, shape) : Derive(model);

    // ── 算子 ──────────────────────────────────────────────────────────

    public QueryEngine Where(LambdaExpression predicate)
    {
        Filterable();

        var scout = Scout(Model);
        var node = Orm.Expressions.Predicate(predicate, Scope.Create(Model, predicate, scout));

        return Advanced(scout, Model.AddPredicate(node));
    }

    public QueryEngine Project(LambdaExpression projector)
    {
        var scout = Scout(Model);
        var plan = Orm.Projections.Translate(projector, Binding(projector, scout));

        return Advanced(scout, Model with { Plan = plan });
    }

    /// <summary>分组（§5.3）。键的表达式落进 <see cref="QueryModel.GroupBy"/>（那是 SQL 侧的真相，
    /// <c>Explain()</c> 的文本里有它），而「<c>g.Key</c> 怎么落回去」这份 CLR 侧的对照表留在引擎的
    /// <see cref="_grouping"/> 上。<b>投影在这里刻意不动</b>：分组链的出口只有 <c>Select</c>，
    /// 而 <c>GroupBy</c> 到 <c>Select</c> 之间还要接 <c>Having</c> 与分组序，那两步的 lambda 都要求作用域里
    /// 已经有这一格。</summary>
    public QueryEngine GroupBy(LambdaExpression keySelector)
    {
        if (Model.GroupBy.Count > 0)
            throw new QuickSqlException("同一条链上只能分组一次：再 GroupBy 一次要么替换掉前一次、要么套成派生表，两者本版本都没有对应物。要按更多列分组请写成 GroupBy(o => new { o.A, o.B })。");

        if (Model.Plan is not EntityProjection entity)
            throw new QuickSqlException("投影之后不能再分组：投影列已经离开列契约，分组键无处可落。把 GroupBy 挪到 Select 之前。");

        if (Model.Offset is > 0 || Model.Limit is not null)
            throw new QuickSqlException("带 Skip/Take 的查询要先包成派生表再分组（每组是一行，页与组的单位在这一版接不上），本版本未接入（§5.12）。请在未分页的查询上分组。");

        if (Model.Ordering.Count > 0)
            throw new QuickSqlException(
                $"分组之前排的序（{Model.Ordering.Count} 颗）到分组之后没有对应物：组里的行不再有先后。请把 OrderBy 挪到 GroupBy 之后（那时它排的是组，键与聚合都能用）。");

        var table = Model.Tables[entity.TableIndex];
        var scout = Scout(Model);
        var scope = Scope.Create(Model, keySelector, scout);
        var body = UnwrapConvert(keySelector.Body);

        // 复合键 = "把几颗键构造成一个形状"（new { o.A, o.B } 或 new Key { A = o.A }）。
        // 匿名类型的成员名由编译器按实参命名，所以 `new { o.State, o.CustomerId }` 的成员就是 State/CustomerId，
        // 而 `new { S = o.State }` 的成员是 S —— 用户写什么名，g.Key 后面就只能读什么名。
        var members = KeyMembers(body);

        if (members is null)
        {
            var key = Orm.Expressions.Visit(body, scope);

            return Advanced(scout, Model with { GroupBy = [key] }, new GroupingShape(table, key, null));
        }

        var keys = new List<SqlExpression>(members.Count);
        var map = new Dictionary<string, SqlExpression>(StringComparer.Ordinal);

        foreach (var (name, expression) in members)
        {
            var node = Orm.Expressions.Visit(expression, scope);

            keys.Add(node);
            map[name] = node;
        }

        // 同一个成员名出现两次（`new { o.A, A = o.B }` 编不出来，但 MemberInit 那形写得出）：
        // 键的<em>顺序</em>照原样进 GROUP BY，而 `g.Key.A` 只能落一个，取后写的那一颗会静默改掉用户的意思，
        // 所以这里直接报错，不猜。
        if (map.Count != members.Count)
            throw new QuickSqlException("复合分组键里有重名的成员：g.Key.<名字> 无法决定落哪一颗。请给它们不同的名字。");

        return Advanced(scout, Model with { GroupBy = [.. keys] }, new GroupingShape(table, null, map));
    }

    /// <summary>分组之后的筛条件。落 <see cref="QueryModel.Having"/> 而不是 <c>WHERE</c>：这件事由
    /// 「它在 <see cref="GroupBy"/> 之后」决定，不由书写顺序决定；多条以 <c>AND</c> 叠加，与谓词那一族同样。</summary>
    public QueryEngine Having(LambdaExpression predicate)
    {
        if (_grouping is null)
            throw new QuickSqlException("Having 要在 GroupBy 之后：没有分组键就没有可筛的聚合值。请把这一条放到 GroupBy 之前的 Where。");

        var scout = Scout(Model);
        var node = Orm.Expressions.Predicate(predicate, Scope.ForGrouping(Model, predicate, _grouping.Value, scout));
        var existing = Model.Having;

        return Advanced(scout, Model with { Having = existing is null ? node : new SqlBinaryExpression(BinaryOperator.And, existing, node) });
    }

    /// <summary>键选择器 lambda 的体是不是"由几颗键构造出来的形状"。是则给出<em>成员名 → 表达式</em>
    /// （顺序即 GROUP BY 的列序），否则 null（单列键）。</summary>
    static IReadOnlyList<(string Name, Expression Value)>? KeyMembers(Expression body) => body switch
    {
        // 匿名类型与具名 DTO 的构造器形状：实参与成员名一一对应（编译器为匿名类型生成的构造器就是这个形状）。
        NewExpression { Constructor: not null, Members: { Count: > 0 } members } @new
            => [.. members.Select((m, i) => (m.Name, UnwrapConvert(@new.Arguments[i])))],

        MemberInitExpression init
            => [.. init.Bindings.OfType<MemberAssignment>().Select(b => (b.Member.Name, UnwrapConvert(b.Expression)))],

        _ => null,
    };

    static Expression UnwrapConvert(Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? UnwrapConvert(unary.Operand)
            : node;

    /// <summary>这一条 lambda 的作用域：分组链上形参是 <c>IGrouping</c>，它不是表位，
    /// 所以走 <see cref="Scope.ForGrouping"/>；其余一律按位置绑表（§5.1）。
    /// <paramref name="scout"/> 是这一颗 lambda 的翻译现场（§5.2 的自动联表欠账）：绑作用域这一步
    /// 自己一个都不补，它只是把"欠着了往哪儿记"递进去 —— 补与落地分在两层，是因为落点只有算子知道。</summary>
    Scope Binding(LambdaExpression lambda, NavigationJoinScout? scout)
        => _grouping is { } grouping
            ? Scope.ForGrouping(Model, lambda, grouping, scout)
            : Scope.Create(Model, lambda, scout);

    public QueryEngine Order(LambdaExpression key, bool descending, bool append)
    {
        if (Model.Plan is not EntityProjection)
            throw new QuickSqlException("投影之后不能再排序：投影列没有列契约，补出来的序会指向错列。把 OrderBy 挪到 Select 之前，或在源查询上排好序再投影。");

        var scout = Scout(Model);
        var order = new OrderExpression(Orm.Expressions.Visit(key.Body, Binding(key, scout)), descending);

        return Append(scout, Model, order, append);
    }

    /// <summary>按成员名排序（v0.38 第二十笔）。<b>这一条不经过表达式翻译</b>：字符串只用来在契约上认列，
    /// 认出来的是 <see cref="ColumnContract"/>，发出去的是那一颗列加方言引号 —— 所以"传进来的文本进了 SQL"
    /// 这一形在这里没有对应的代码路径，不是一个要靠过滤堵住的风险面。闸门与 lambda 那一条共用
    /// <see cref="Append"/>，两条口对同一个投影形状给同一句话。</summary>
    public QueryEngine Order(string memberName, bool descending, bool append)
    {
        if (Model.Plan is not EntityProjection)
            throw new QuickSqlException("投影之后不能再排序：投影列没有列契约，补出来的序会指向错列。把 OrderBy 挪到 Select 之前，或在源查询上排好序再投影。");

        var contract = Model.Root.Contract
            ?? throw new QuickSqlException($"根表位 {Model.Root.Table.Name} 没有实体契约，按成员名排不出序：认列靠的就是那份契约。用 lambda 形态，或把这条链的表源换成一张实体表。");

        return Append(null, Model, new OrderExpression(new ColumnExpression(Model.Root, Column(contract, memberName)), descending), append);
    }

    /// <summary>把一颗序落到模型上。<b>追加还是替换由这一处决定</b>：契约上 <c>OrderBy</c> 之后再
    /// <c>OrderBy</c> 是替换前一串（那是 LINQ 的口径），而链式对象上那两颗口各自把 <paramref name="append"/>
    /// 写死，所以两种形态（lambda 与成员名）不会各写一套叠加规则。
    /// <paramref name="scout"/> 与 <paramref name="model"/> 成对进来：前者是 lambda 那一形欠下的自动联表
    /// （§5.2），成员名那一形不经过表达式翻译，没有欠账可落，所以那一口递 null 进来 —— 于是两条口在
    /// <see cref="Advanced"/> 那里合流，叠加规则仍然只有一份。</summary>
    QueryEngine Append(NavigationJoinScout? scout, QueryModel model, OrderExpression order, bool append)
        => Advanced(scout, append ? model.AddOrdering(order) : model with { Ordering = [order] });

    /// <summary>成员名 → 列。<b>三档，逐字节优先</b>：先属性名、再列名、最后大小写不敏感的<em>唯一</em>命中
    /// （HTTP 查询串上过来的是 <c>createTime</c> 那一形，实体上写的是 <c>Create_time</c> 或 <c>CreateTime</c>，
    /// 大小写这一档是给它的，不是给通配的）。认不出与认得多颗都当场抛，<b>没有"原样发出去"的退路</b>：
    /// 那一路的产物是一条看起来合理的错句，而排序键错了在页面上读得出来的只有顺序，没人会为此报修。</summary>
    static ColumnContract Column(EntityContract contract, string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            throw new QuickSqlException($"{contract.Table.Name} 的排序成员名是空的：按名字排只认这一张表上的列，请把要排的那一列的属性名或列名给出来。");

        if (contract.ByMember.TryGetValue(memberName, out var byMember)) return byMember;
        if (contract.ByColumn.TryGetValue(memberName, out var byColumn)) return byColumn;

        var matches = contract.Columns
            .Where(c => string.Equals(c.MemberName, memberName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.ColumnName, memberName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有名为 {memberName} 的列：按名字排只认这张表上映射过的列（属性名或列名），" +
                "导航属性、投影列与 SQL 片段都不认。要排别的表那一侧的列请改用 lambda 形态（读引用导航会自动联表，§5.2）。");

        if (matches.Length > 1)
            throw new QuickSqlException($"{contract.Table.Name} 上能对上 {memberName} 的列不止一颗（{string.Join(" / ", matches.Select(m => m.MemberName))}）：" +
                "大小写不敏感这一档撞在了两张列上，而本家不替调用方挑一颗。按逐字节的写法命中（属性名或列名写全），或改用 lambda 形态。");

        return matches[0];
    }

    /// <summary><c>Skip</c> 只落偏移位、<c>Take</c> 只落行数位，都不在这一步检查序（§9.4）：
    /// <c>Skip().Take().OrderBy()</c> 与 <c>OrderBy().Skip().Take()</c> 必须产出同一个模型，
    /// 所以"有没有序"只能在出口判定，见 <see cref="Effective"/>。</summary>
    public QueryEngine Skip(int count) => Derive(Model with { Offset = count });

    public QueryEngine Take(int count) => Derive(Model with { Limit = count });

    public QueryEngine Distinct() => Derive(Model with { Distinct = true });

    /// <summary><c>AsNoTracking()</c> 的落点：<b>模型一个字都不改，只把手里那份会话松开</b>。
    /// 所以它产出的是「同一条查询的无跟踪视图」而不是「另一条查询」—— §8.3 那句「前两行只差一个标记位」
    /// 说的就是这个，而 §13.2 ② 的逐字节相等也由这里直接成立（不再需要把标记位从文本里剥掉）。</summary>
    public QueryEngine NoTracking() => new(Orm, Model, tracker: null, _ignoredFilters, _grouping, _navigation);

    /// <summary>「这一条链上没有任何全局过滤器」那一句的落点：递进来的清单里只有 <see cref="object"/> 一颗，
    /// 于是 <c>FilterRegistry.IsIgnored</c> 对每条注册都判真。<b>先问 mandatory 再置位</b>：那条约束要的是
    /// "关掉不可忽略的过滤器"这句话<em>当场</em>响，而不是安静地生成一条少了一个条件的语句 ——
    /// 少一个租户条件这件事在结果集上看不出来，等到有人发现数据串了才知道，那是 §10.7 点名的最坏形状。
    /// 判据本身在 <c>FilterRegistry.AssertIgnorable</c>，出口处还会再判一次（那里说了为什么要判两次）。</summary>
    public QueryEngine IgnoreFilters() => IgnoreFilters(typeof(object));

    /// <summary><c>IgnoreQueryFilter&lt;TFilter&gt;()</c> 的落点（§10.7 约束 2 的按条那一档）：
    /// 只把 <paramref name="key"/> 递进清单里，<b>其余在册的一条都不动</b>。
    /// 判 mandatory 时递的是<em>叠过这一颗之后的</em>清单而不是这一颗：一条链先 <c>IgnoreQueryFilter&lt;ISoftDelete&gt;()</c>
    /// 再 <c>IgnoreQueryFilter&lt;ITenant&gt;()</c>，第二次要把第一次那份账一起问一遍 —— 只问新来的那一颗的话，
    /// 一条链上多颗 mandatory 只有第一颗响，而这两句在 SQL 上造成的缺口是一样的。</summary>
    public QueryEngine IgnoreFilters(Type key)
    {
        IReadOnlyList<Type> ignored = _ignoredFilters is { } existing ? [.. existing, key] : [key];

        Orm.Filters.AssertIgnorable(Model.Tables, ignored);

        return new QueryEngine(Orm, Model, _tracker, ignored, _grouping, _navigation);
    }

    public QueryEngine Cache(string cacheKey, TimeSpan ttl) => Derive(Model with { CacheKey = cacheKey, CacheTtl = ttl });

    /// <summary><c>FromPrimary()</c> / <c>FromReplica()</c> 的落点（§16.4 的链上显式表态）。
    /// <b>与 <see cref="NoTracking"/> 同一形状：改的是这一次执行的去向，模型里的查询形状一个字不动</b>
    /// （那一格进的是 <see cref="QueryModel.Role"/>，它不参与规范文本，理由写在那里）。
    /// 它<em>不</em>在这里判"能不能"：事务里点从库、没配从库还点从库，那两件事的判据在
    /// <see cref="DataSourceRouting.Resolve"/> 一处，由执行时的那次取连接兑现 —— 在这里判等于两处各写一套，
    /// 而"这一句到底走了哪条"从此有了两个答案。</summary>
    public QueryEngine Role(ChainChoice choice) => Derive(Model with { Role = choice });

    public QueryEngine Alias(string alias)
    {
        // 别名改的是 FROM 表位，而已翻译的谓词持有旧 TableRef 实例：先加别名再叠条件是安全的，反过来会写出
        // FROM [Orders] AS [o] 配 WHERE [t0].[State] —— 报"无法绑定多部分标识符"，比静默错结果好，但仍要说清楚。
        if (Model.Predicates.Count > 0 || Model.Joins.Count > 0 || Model.Ordering.Count > 0)
            throw new QuickSqlException($"As(\"{alias}\") 要放在链式最前面：表上已叠加 {Model.Predicates.Count} 个谓词/{Model.Joins.Count} 个联表/{Model.Ordering.Count} 个排序，它们的列引用仍指向旧别名。");

        var root = Model.Root with { Alias = alias };

        // 覆盖源里的 TableRef 是 WithSql 那一刻抓住的旧别名实例：不改写它，FROM 挂 "t0" 而谓词引用 "o"，
        // 得到的是一句"无法绑定多部分标识符"。与上面那颗守卫同一个理由，只是这一格的引用藏在表源里。
        var source = Model.Source is SourceRawText raw ? raw with { Ref = root } : Model.Source;

        return Derive(Model with { Tables = [root, .. Model.Tables.Skip(1)], Source = source });
    }

    /// <summary><c>WithSql</c>（规格 §11 的表源那一格）：把根表位的 FROM 换成<b>一整段用户自己写的 SELECT</b>，
    /// 链上后面的 <c>Where</c>/<c>OrderBy</c>/<c>Select</c> 仍然照契约端出去 —— 于是外面这一层是库写的、
    /// 里面那一段是你写的，两边在同一个 <c>DialectSql</c> 里，AOP 与日志仍是同一条链。
    ///
    /// <para>三条口径与 <see cref="RawSql"/> 那一段完全一致（那是唯一的一份折叠实现）：文本一颗不动；
    /// 参数名 = 本家前缀 + 属性名；位置式占位符那一家（GBase 8s）当场拒。多一条在这里：<b>参数名不许长成
    /// <c>前缀 + p + 数字</c> 的形状</b> —— 那是 <c>SqlWriter</c> 给生成参数留的号段（编号长在项目参数集合的
    /// 长度上），撞上去的症状是两处绑定抢同一个名字、值串到隔壁列。</para>
    ///
    /// <para><b>外面那一层按契约选列</b>，所以内层必须交回契约里那些列名（<c>Select *</c> 那形天然成立）。
    /// 想要内层的列名与契约不同的形状，就用 <c>Select</c> 投影或干脆走 §11 的原生 SQL 口。</para></summary>
    public QueryEngine WithSql(string sql, object? args)
    {
        var prefix = Orm.Provider.Dialect.ParameterPrefix;
        var folded = RawSql.ToDialectSql(sql, args, prefix);

        foreach (var parameter in folded.Parameters)
            if (IsGeneratedName(parameter.Name, prefix))
                throw new QuickSqlException(
                    $"WithSql 的参数名 {parameter.Name} 占了生成 SQL 的号段（前缀 {prefix} + p + 数字，那里按项目参数集合的长度编号）：" +
                    "同名的两颗会让外层生成的参数与原生文本里的这颗抢同一个绑定。请把它改成不叫 p<数字> 的名字。");

        return Derive(Model with { Source = new SourceRawText(Model.Root, folded.Text, folded.Parameters) });
    }

    static bool IsGeneratedName(string name, char prefix)
    {
        if (name.Length < 3 || name[0] != prefix || name[1] != 'p')
            return false;

        for (var i = 2; i < name.Length; i++)
            if (!char.IsAsciiDigit(name[i]))
                return false;

        return true;
    }

    /// <summary>分表实体的<b>按时间范围选片</b>（§16.4）：起与止各下整到本档的周期，中间逐周期展开成一份片清单，
    /// 落进 <see cref="QueryModel.Shards"/>。时间档就是"这段区间里可能有哪几张表"，所以剪枝不是优化而是定义 ——
    /// 三月查二月那张表里没有的行，与"没查"不是一回事。
    /// <b>说一次算一次，后一次覆盖前一次</b>：片清单是这一份模型的事实，叠加两份会得到第三种没人要的区间。
    /// 单片不包 <c>UNION ALL</c> 外层（见 <c>QueryCompiler.CompileSplit</c>），所以"这个月"的句子与不分表时逐字同形。</summary>
    public QueryEngine SplitTables(DateTime from, DateTime to)
    {
        var split = SplitOrThrow();

        return Derive(Model with { Shards = SplitRouting.EnumerateTime(Model.Root.Table, split, from, to) });
    }

    /// <summary>分表实体的<b>按桶号选片</b>（§16.4 的哈希档）：给哪几个桶就读哪几张，顺序就是 <c>UNION ALL</c>
    /// 的分支顺序，重复桶号去掉。<b>没有"全部桶"这一颗口</b>，理由与时间档同一句：桶数写在实体上，
    /// 而"把全部桶 UNION 起来"在几百个桶上不是一句可执行的查询，给它一个顺手的写法就是给它一条通往线上的路。
    /// 真要全量统计，就按桶分批或在库侧做（§16.4 已记名：跨片聚合本家不接）。</summary>
    public QueryEngine SplitTables(params int[] buckets)
    {
        var split = SplitOrThrow();

        return Derive(Model with { Shards = SplitRouting.EnumerateBuckets(Model.Root.Table, split, buckets) });
    }

    /// <summary>选片口只给分表实体用：在不分表的实体上调它，得到的不是"整张表"而是一个错——
    /// 那句话会让人以为自己的实体分了片。判据取契约上那一格，不做二次缓存。</summary>
    SplitFacts SplitOrThrow()
        => Model.Root.Table.Split
           ?? throw new QuickSqlException(
                $"实体 {Model.Root.Contract?.ClrType.Name} 没有声明分表（类上没有 [SplitTable]），没有片可选。");

    /// <summary>联表的落点。ON 的<b>最后一个</b>形参就是新表（§5.2 的 arity 递增），所以新表的 CLR 类型从它取，
    /// 不需要调用方再报一遍。形参名不参与解析，所以 <c>(o, i) =></c> 与 <c>(x, y) =></c> 产出同一个模型。</summary>
    public QueryEngine Join(JoinKind kind, LambdaExpression on)
    {
        Filterable();

        var detail = on.Parameters[^1].Type;

        if (detail.IsGenericType && detail.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            throw new QuickSqlException("联表的右表是集合导航（IEnumerable<T>）：集合导航的加载是 IncludeMany/子查询的职责，P1（§5.7）。");

        var table = Orm.NewTableRef(detail, Model.Tables.Count);
        var scoped = Model with { Tables = [.. Model.Tables, table] };
        var clause = new JoinClause(kind, new SourceTable(table), Orm.Expressions.Predicate(on, Scope.Create(scoped, on)));

        return Derive(Model.AddJoin(clause, table));
    }

    void Filterable()
    {
        // §5.8：投影之后再 Where 只能引用投影列，而投影列不在列契约里 —— 引用不到就报，不猜。
        if (Model.Plan is not EntityProjection)
            throw new QuickSqlException("投影之后不能再加 WHERE：投影列已经离开列契约，继续翻译会静默丢列或认错列。把条件放到 Select 之前。");
    }

    // ── Include（§5.6） ─────────────────────────────────────────────────

    /// <summary>解析引用导航属性并构造 IncludeNode。从根实体的契约中查找关系，创建目标表位，
    /// 构建 LEFT JOIN 条件（FK = PK）。</summary>
    public IncludeNode Include(LambdaExpression navigation)
    {
        if (Model.Plan is not EntityProjection rootProjection)
            throw new QuickSqlException("Include 只能在实体查询上使用，投影之后不能再加载导航属性。把 Include 放到 Select 之前。");

        var rootTable = Model.Tables[rootProjection.TableIndex];
        var rootContract = rootTable.Contract ?? throw new QuickSqlException($"表位 {rootTable.Alias} 没有实体契约，无法解析导航属性。");

        var memberName = ExtractMemberName(navigation.Body);
        var relationship = FindRelationship(rootContract, memberName);

        // 先读导航、后 Include 那一序：这一根边已经作为模型里的真 JOIN 进 FROM 了（§5.2 的自动联表），
        // Include 就收养那一颗而不是再新造一张 —— 与 NavigationJoinScout.AdoptedInclude 同一条裁决的两个方向，
        // 合起来才是"一根边一张 JOIN"。
        var targetTable = Joined(rootTable, memberName)
                          ?? Orm.NewTableRef(relationship.TargetType, Model.Tables.Count);
        var onCondition = BuildJoinCondition(rootTable, relationship, targetTable);

        return new IncludeNode
        {
            NavigationMemberName = memberName,
            Relationship = relationship,
            TargetTable = targetTable,
            OnCondition = onCondition,
        };
    }

    /// <summary>在已有 Include 的基础上继续加载更深层导航。从指定父节点的实体契约中查找关系，
    /// 新节点作为父节点的子节点加入树中。</summary>
    public IncludeNode ThenInclude(IncludeNode parent, LambdaExpression navigation)
    {
        var parentContract = parent.TargetTable.Contract
            ?? throw new QuickSqlException($"Include 的目标表 {parent.TargetTable.Alias} 没有实体契约。");

        var memberName = ExtractMemberName(navigation.Body);
        var relationship = FindRelationship(parentContract, memberName);
        var targetTable = Joined(parent.TargetTable, memberName)
                          ?? Orm.NewTableRef(relationship.TargetType, Model.Tables.Count);
        var onCondition = BuildJoinCondition(parent.TargetTable, relationship, targetTable);

        var node = new IncludeNode
        {
            NavigationMemberName = memberName,
            Relationship = relationship,
            TargetTable = targetTable,
            OnCondition = onCondition,
        };

        parent.Children.Add(node);

        return node;
    }

    /// <summary>将顶级 IncludeNode 加入模型（追加到 Includes 列表 + 追加目标表位）。
    /// 表位<em>可能已经在场</em>：这一根边先被自动联表补进 FROM、<see cref="Include"/> 收养它的那一序
    /// （<see cref="Joined"/>），那时候不能再添第二份，否则 <c>Tables</c> 里同一种表出现两次，
    /// 而按位置绑形参的那道（<c>Scope.Bound</c>）会把 arity≥2 的 lambda 错配一格。</summary>
    public QueryEngine AddInclude(IncludeNode node)
        => Derive(Model with
        {
            Includes = [.. Model.Includes, node],
            Tables = WithTable(Model.Tables, node.TargetTable),
        });

    /// <summary>将 ThenInclude 的子节点的目标表位加入模型。节点已经在父节点的 Children 里，
    /// 不需要再追加到 Includes 列表。</summary>
    public QueryEngine AddIncludeTable(IncludeNode node)
        => Derive(Model with { Tables = WithTable(Model.Tables, node.TargetTable) });

    static IReadOnlyList<TableRef> WithTable(IReadOnlyList<TableRef> tables, TableRef table)
    {
        foreach (var existing in tables)
            if (existing.Index == table.Index)
                return tables;

        return [.. tables, table];
    }

    /// <summary>这条链上那一根边（宿主表位的这一格成员）是不是已经联过，是就交回那一张表位。
    /// 判据与 <see cref="NavigationJoin"/> 同一把尺：按宿主表位的<em>位置</em>加成员名，不按实例。</summary>
    TableRef? Joined(TableRef host, string member)
    {
        foreach (var join in _navigation)
            if (join.HostIndex == host.Index && join.Member == member)
                return join.Table;

        return null;
    }

    /// <summary><c>AsSplitQuery()</c> 的落点：引用导航不走 LEFT JOIN，改在主查询之后单独发查询再内存缝合。
    /// 模型里只置一个位，真正改写法的是编译器（跳过 Include 列收集）与执行层（<c>ListWithSplitIncludesAsync</c>）。</summary>
    public QueryEngine SplitQuery() => Derive(Model with { SplitQuery = true });

    /// <summary>从表达式体提取成员名。只支持简单的属性访问（<c>o =&gt; o.Customer</c>），
    /// 不支持方法调用或复杂表达式。</summary>
    static string ExtractMemberName(Expression body)
    {
        if (body is MemberExpression member)
            return member.Member.Name;

        throw new QuickSqlException(
            $"Include/ThenInclude 的 lambda 必须是简单的属性访问（如 o => o.Customer），当前表达式形态是 {body.NodeType}。");
    }

    /// <summary>从实体契约中查找指定名称的关系（引用导航）。找不到时抛出明确的错误消息。</summary>
    static RelationshipContract FindRelationship(EntityContract contract, string memberName)
    {
        foreach (var rel in contract.Relationships)
        {
            if (rel.MemberName == memberName)
                return rel;
        }

        throw new QuickSqlException(
            $"实体 {contract.ClrType.Name} 上没有名为 {memberName} 的导航属性。" +
            $"已注册的关系：{string.Join(", ", contract.Relationships.Select(r => r.MemberName).DefaultIfEmpty("(无)"))}。" +
            "请确认属性名拼写正确，且已在契约中配置（Fluent 或注解）。");
    }

    /// <summary>构建 LEFT JOIN 的 ON 条件：依赖侧的 FK 列 = 主端实体的 PK 列。
    /// 复合外键时生成多个等值条件用 AND 连接。<b>全仓库只有这一处会按一条引用导航造 JOIN 条件</b>：
    /// <c>Include</c> 的急切加载与 §5.2 的自动联表（<see cref="NavigationJoinScout"/>）交出来的必须是同一句 ON，
    /// 两条边在两种来路上长成两种形状的话，"这根导航怎么联"就有了第二套真相。</summary>
    internal static SqlExpression BuildJoinCondition(TableRef sourceTable, RelationshipContract relationship, TableRef targetTable)
    {
        var targetContract = targetTable.Contract
            ?? throw new QuickSqlException($"目标表 {targetTable.Alias} 没有实体契约，无法构建 JOIN 条件。");

        var targetKeys = targetContract.PrimaryKey;

        if (targetKeys.Length == 0)
            throw new QuickSqlException(
                $"目标实体 {targetContract.ClrType.Name} 没有主键，无法构建 Include 的 JOIN 条件。" +
                "请为该实体配置主键（[Key] 注解或 Fluent API）。");

        if (relationship.ColumnNames.Count != targetKeys.Length)
            throw new QuickSqlException(
                $"关系 {relationship.MemberName} 的外键列数（{relationship.ColumnNames.Count}）" +
                $"与目标实体主键列数（{targetKeys.Length}）不匹配。复合外键的列序必须与主键一一对应。");

        SqlExpression? condition = null;

        for (var i = 0; i < relationship.ColumnNames.Count; i++)
        {
            var fkColumnName = relationship.ColumnNames[i];
            var fkColumn = sourceTable.Contract!.ByColumn[fkColumnName];
            var pkColumn = targetKeys[i];

            var fkExpr = new ColumnExpression(sourceTable, fkColumn);
            var pkExpr = new ColumnExpression(targetTable, pkColumn);
            var equal = new SqlBinaryExpression(BinaryOperator.Equal, fkExpr, pkExpr);

            condition = condition is null ? equal : new SqlBinaryExpression(BinaryOperator.And, condition, equal);
        }

        return condition!;
    }

    // ── IncludeMany（§5.7） ─────────────────────────────────────────────

    /// <summary>解析集合导航属性并构造 IncludeManyNode。不改写主查询 SQL，执行层发两条查询再缝合。
    /// FK 发现：先在子实体契约的 Relationships 里找指向父类型的边，找不到时按约定匹配
    /// （<c>{父类型名}Id</c> / <c>{父类型名}{PK列名}</c>）。</summary>
    public IncludeManyNode IncludeMany(LambdaExpression navigation)
    {
        if (Model.Plan is not EntityProjection rootProjection)
            throw new QuickSqlException("IncludeMany 只能在实体查询上使用，投影之后不能再加载集合导航。把 IncludeMany 放到 Select 之前。");

        var memberName = ExtractMemberName(navigation.Body);
        var rootTable = Model.Tables[rootProjection.TableIndex];
        var parentContract = rootTable.Contract
            ?? throw new QuickSqlException($"表位 {rootTable.Alias} 没有实体契约，无法解析集合导航。");

        return NewIncludeManyNode(parentContract, memberName);
    }

    /// <summary>把根上的那一颗集合节点加进模型（追加到 IncludeManys 列表）。子节点走
    /// <see cref="ThenIncludeMany"/>，不入这张清单 —— 它挂在本节点的 <see cref="IncludeManyNode.Children"/> 上。</summary>
    public QueryEngine AddIncludeMany(IncludeManyNode node)
        => Derive(Model with { IncludeManys = [.. Model.IncludeManys, node] });

    /// <summary>在已有集合节点上再挂一层集合导航（<c>ThenIncludeMany</c>，§5.7）。父端是<em>上一层刚缝出来的
    /// 那一批子实体</em>，所以这一档不需要表位、也不动 <c>QueryModel</c> —— 主查询 SQL 一个字符不变，
    /// 多出来的只是执行层的一条 SELECT。判"这一层能不能挂"的是父节点子型的契约上有没有那根集合边。</summary>
    public IncludeManyNode ThenIncludeMany(IncludeManyNode parent, LambdaExpression navigation)
    {
        var parentContract = Orm.Schemas.Contract(parent.ChildType);
        var node = NewIncludeManyNode(parentContract, ExtractMemberName(navigation.Body));

        parent.Children.Add(node);

        return node;
    }

    /// <summary>在已有集合节点上再挂一层<em>引用</em>导航（<c>ThenInclude</c>，§5.7）。父端还是上一层刚缝出来的
    /// 那一批子实体，所以这一档与 <see cref="ThenIncludeMany"/> 同一条纪律：<b>不动 <c>QueryModel</c>、不发主查询
    /// 的任何东西</b>，节点记在本节点的 <see cref="IncludeManyNode.References"/> 上，执行期另发一条
    /// <c>SELECT ... WHERE 目标.PK IN (那一批的 FK 值)</c> 再逐只缝回 —— 走的是 <c>AsSplitQuery</c> 那一档
    /// 已有的那颗加载器，一条引用边在两条路上必须长成同一个答案。
    /// 集合导航在这一档当场拒：它有自己的那颗口（<see cref="ThenIncludeMany"/>），按引用那一档发出去只缝得上
    /// 集合里的最后一只，而"只少一只"这种错在结果里看不出形状。</summary>
    public IncludeNode ThenInclude(IncludeManyNode parent, LambdaExpression navigation)
    {
        var memberName = ExtractMemberName(navigation.Body);
        var parentContract = Orm.Schemas.Contract(parent.ChildType);

        if (parentContract.Collections.Any(c => c.MemberName == memberName))
            throw new QuickSqlException(
                $"实体 {parentContract.ClrType.Name} 上的 {memberName} 是集合导航：这一档只走引用导航，" +
                $"集合换 ThenIncludeMany（它按外键把那一批全挂上）。用 ThenInclude 挂集合只会剩下最后一只。");

        var relationship = FindRelationship(parentContract, memberName);

        // 两侧表位的索引都取 0：这一颗不进 FROM，执行期那条独立 SELECT 自己现取别名（与拆分加载同一处理）。
        // ON 仍按全仓库共用的那一颗现造 —— 它今天不被发射，但它是"这根边的键对不对得上"唯一的判据，
        // 让它在链上就响，比在执行期缝出一只空导航、没人知道为什么好。
        var sourceTable = Orm.NewTableRef(parentContract.ClrType, 0);
        var targetTable = Orm.NewTableRef(relationship.TargetType, 0);

        var node = new IncludeNode
        {
            NavigationMemberName = memberName,
            Relationship = relationship,
            TargetTable = targetTable,
            OnCondition = BuildJoinCondition(sourceTable, relationship, targetTable),
        };

        parent.References.Add(node);

        return node;
    }

    /// <summary>给链上最后一层集合节点的<em>子行</em>点序（<c>OrderByMany</c>，§5.7 的子行排序口）。
    /// <b>动的是节点，不是模型</b>：序记在这一层的 <see cref="IncludeManyNode.Orderings"/> 上，
    /// 主查询与 <c>QueryModel</c> 的清单一个字符不变 —— 与 <see cref="ThenIncludeMany"/>、
    /// <see cref="ThenInclude(IncludeManyNode, LambdaExpression)"/> 同一条纪律。
    /// 认列在<em>接上链的这一步</em>就做完（共用 <see cref="Column"/> 那颗三档咽喉）：排错列的代价是
    /// 页面上"看着合理的错顺序"，没人会为此报修，所以它只能在这里响，不许拖到执行期。
    /// 再点一次是整串替换 —— 与契约上 <c>OrderBy</c> 之后再 <c>OrderBy</c> 同一个 LINQ 口径，
    /// 那颗口没有 <c>ThenBy</c> 的替身，多列子行序是这格今天没给的射程。</summary>
    public void OrderByMany(IncludeManyNode node, LambdaExpression key, bool descending)
    {
        var contract = Orm.Schemas.Contract(node.ChildType);
        var column = Column(contract, ManyOrderMember(key));

        node.Orderings.Clear();
        node.Orderings.Add(new IncludeManyOrdering(column, descending));
    }

    /// <summary>子行序 key 的形态闸：<b>只收子实体上的直接属性访问</b>（<c>d =&gt; d.Order</c>，允许值类型
    /// 装箱那一层 <c>Convert</c> 壳）。嵌套路径（<c>d =&gt; d.Warehouse.Name</c>）当场拒而不是悄悄剥成
    /// <c>Name</c>：那一剥会拿"物资的 Name"去撞子实体自己的列名，撞中了就是排错列、撞不中就是排空气 ——
    /// 两条都比"接链时响一句、让用户自己换列"坏。这一档不发 JOIN（子 SELECT 的形状由外键那一条边定死），
    /// 所以排在别的表那一侧的列<em>根本发不出来</em>，拒它是射程声明，不是偷懒。</summary>
    static string ManyOrderMember(LambdaExpression key)
    {
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } box
            ? box.Operand
            : key.Body;

        if (body is MemberExpression { Expression: ParameterExpression } member)
            return member.Member.Name;

        throw new QuickSqlException(
            $"OrderByMany 的排序键必须是子实体上的直接属性访问（如 d => d.Order）：这颗口只把序落进子行那一条另发的 SELECT 的 ORDER BY，" +
            $"这一档不发 JOIN，嵌套路径（如 d => d.Warehouse.Name）与算式都排不出去。当前表达式形态是 {key.Body.NodeType}。");
    }

    /// <summary>两层共用的一颗节点构造：从父契约上按属性名取集合元素类型，再发现外键。</summary>
    IncludeManyNode NewIncludeManyNode(EntityContract parentContract, string memberName)
    {
        var navProperty = parentContract.ClrType.GetProperty(memberName)
            ?? throw new QuickSqlException(
                $"实体 {parentContract.ClrType.Name} 上没有名为 {memberName} 的属性。");

        var childType = GetCollectionElementType(navProperty.PropertyType)
            ?? throw new QuickSqlException(
                $"属性 {parentContract.ClrType.Name}.{memberName} 不是集合类型（IEnumerable<T>）。IncludeMany 只用于集合导航。");

        var childContract = Orm.Schemas.Contract(childType);
        var fkColumns = DiscoverFkColumns(parentContract, childContract, memberName);

        return new IncludeManyNode
        {
            NavigationMemberName = memberName,
            ChildType = childType,
            ChildFkColumnNames = fkColumns,
        };
    }

    /// <summary>从子实体契约上发现指向父实体 PK 的外键列。先查显式关系（子端有引用导航回父），
    /// 再看那根集合导航上点名的外键成员，最后按约定匹配列名 —— 三档与级联落键同序（见
    /// <see cref="ContractResolver.ResolveDeclaredFkColumns"/>）。返回的列顺序与父实体 PK 列对齐。</summary>
    static IReadOnlyList<string> DiscoverFkColumns(EntityContract parentContract, EntityContract childContract, string memberName)
    {
        var parentKeys = parentContract.PrimaryKey;

        if (parentKeys.Length == 0)
            throw new QuickSqlException(
                $"父实体 {parentContract.ClrType.Name} 没有主键，无法发现子实体 {childContract.ClrType.Name} 的外键。");

        foreach (var rel in childContract.Relationships)
        {
            if (rel.TargetType == parentContract.ClrType)
            {
                if (rel.ColumnNames.Count != parentKeys.Length)
                    throw new QuickSqlException(
                        $"子实体 {childContract.ClrType.Name} 到 {parentContract.ClrType.Name} 的关系有 {rel.ColumnNames.Count} 个外键列，" +
                        $"但父实体有 {parentKeys.Length} 个主键列，数量不匹配。");

                return rel.ColumnNames;
            }
        }

        if (ContractResolver.ResolveDeclaredFkColumns(parentContract, childContract, memberName) is { } declaredColumns)
            return declaredColumns;

        var parentTypeName = parentContract.ClrType.Name;

        if (parentKeys.Length == 1)
        {
            var conventionNames = new[] { $"{parentTypeName}Id", $"{parentTypeName}ID" };

            foreach (var conventionName in conventionNames)
            {
                if (childContract.ByColumn.ContainsKey(conventionName))
                    return [conventionName];
            }

            var byColumnName = $"{parentTypeName}{parentKeys[0].ColumnName}";

            if (childContract.ByColumn.ContainsKey(byColumnName))
                return [byColumnName];
        }

        var compositeNames = new List<string>(parentKeys.Length);

        foreach (var pk in parentKeys)
        {
            var name = $"{parentTypeName}{pk.ColumnName}";

            if (!childContract.ByColumn.ContainsKey(name))
                throw new QuickSqlException(
                    $"无法发现子实体 {childContract.ClrType.Name} 指向父实体 {parentContract.ClrType.Name} 的外键列。" +
                    $"约定匹配失败：在子实体上找不到 {(parentKeys.Length == 1 ? $"\"{parentTypeName}Id\" 或 \"{parentTypeName}{parentKeys[0].ColumnName}\"" : $"\"{name}\"")}。" +
                    "请在子实体上配置引用导航或在 Fluent API 中显式指定外键。");

            compositeNames.Add(name);
        }

        return compositeNames;
    }

    static Type? GetCollectionElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return type.GetGenericArguments()[0];

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>可执行模型：先贴全局过滤器，再按 §9.4 补分页的序。全仓库只有这一处读 <see cref="QuickSqlOrm.PagingOrdering"/>。
    /// <b>过滤器也落在这里，是因为这是读侧唯一的一个咽喉</b>：<c>ToList</c>/<c>Count</c>/<c>Any</c>/<c>ToPaged</c>/
    /// <c>Stream</c>/<c>Explain</c>/<c>ToSql</c> 全走这一格，写在算子上的话每加一个出口就要重记一遍"过滤器会不会漏"
    /// （而 §10.7 约束 1 那句"批量绕过过滤器 = 跨租户改数据"判的正是漏不漏）。
    /// 序补在过滤器<em>之后</em>是有意的：补出来的那一个主键序可能与被筛掉的行无关，两件事本来就该各说各的。</summary>
    public QueryModel Effective()
    {
        var model = Filtered();

        if ((model.Offset is null or 0 && model.Limit is null) || model.Ordering.Count > 0)
            return model;

        switch (Orm.PagingOrdering)
        {
            case PagingOrderingPolicy.Allow:
                return model;

            case PagingOrderingPolicy.AutoOrderPrimaryKey:
                // 分组的那一条不能补主键序：主键不在分组键里，`ORDER BY t0.Id` 配 `GROUP BY t0.State`
                // 在每一家都是错句，而这一版的模型层发不出"按组内主键排序"这种东西。
                if (model.GroupBy.Count > 0)
                    throw new QuickSqlException(
                        "分页（未给序）落在分组查询上，而 AutoOrderPrimaryKey 补不出序：主键不是分组键，补出来的那一句在库里不合法。" +
                        "请在 GroupBy 之后显式 OrderBy(g => g.Key) 或 OrderByDescending(g => g.Count())（§9.4）。");

                return model with { Ordering = [.. PrimaryKeyOrdering(model)] };

            default:
                // 那句话不能写"两家的 OFFSET..FETCH 都语法错"：前半对 T-SQL，后半对 PG 系不成立
                //（它只需要 OFFSET 存在即可，见 <c>PostgreSqlDialect.EmitPaging</c>）。两家的共同点只有一个：
                // 没有 ORDER BY 就没有可复现的行序。
                throw new QuickSqlException(
                    $"分页（OFFSET {model.Offset ?? 0} ROWS，FETCH {model.Limit?.ToString() ?? "全部"}）没有 ORDER BY：这一家的 OFFSET 不保证行序，" +
                    "有的方言族甚至要求先有 ORDER BY 才合法。放开策略只会得到一个数据库不保证的结果。补一个 OrderBy(...)，或按需把 PagingOrdering 设成 AutoOrderPrimaryKey / Allow（§9.4）。");
        }
    }

    /// <summary>全局过滤器（§10.7）贴进这份模型的那一步。<b>没注册过任何东西时这里一个对象都不造</b>
    /// （<see cref="FilterRegistry.HasAny"/> 一次数组长度比较就返回），所以那几格被门禁钉住的分配数字
    /// 不会为"有人可能用过滤器"这件事付钱 —— 与 §10.1 硬规则 2 对钩子的那句要求同一条。
    /// <see cref="_ignoredFilters"/> 那一支仍然要问一次 mandatory：标记位可能是在表位长齐之前置的
    /// （<c>IgnoreQueryFilters()</c> 之后再 <c>InnerJoin</c> 一张带 mandatory 过滤器的表），漏掉那一次问
    /// 就等于给约束 2 留一条绕法。</summary>
    QueryModel Filtered()
    {
        if (!Orm.Filters.HasAny)
            return Model;

        if (_ignoredFilters is { } ignored)
        {
            Orm.Filters.AssertIgnorable(Model.Tables, ignored);

            return Orm.Filters.Apply(Model, ignored);
        }

        return Orm.Filters.Apply(Model);
    }

    IEnumerable<OrderExpression> PrimaryKeyOrdering(QueryModel model)
    {
        var contract = model.Root.Contract ?? throw new QuickSqlException("根表位没有实体契约，补不出主键序。");
        var keys = contract.PrimaryKey;

        if (keys.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有主键，AutoOrderPrimaryKey 补不出序。请显式 OrderBy，或改用 PagingOrdering.Allow（§9.4）。");

        return keys.Select(key => new OrderExpression(new ColumnExpression(model.Root, key), false));
    }

    public SelectStatement Compile(QueryModel model) => Orm.Compiler.Compile(model);

    public DialectSql BuildSql(out SelectStatement ast) => Build(Effective(), out ast);

    DialectSql Build(QueryModel model, out SelectStatement ast)
    {
        ast = Compile(model);

        var dialect = Orm.Provider.Dialect;
        var writer = new SqlWriter(new(QueryCompiler.EstimateSqlLength(ast)), new(), dialect.ParameterPrefix);
        dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), writer.Parameters);
    }

    public string ToSql(out SelectStatement ast) => Build(Effective(), out ast).Text;

    /// <summary>不写成 <c>ToSql(out _)</c>：那里有两个 <c>out</c> 重载，丢弃参数写不成类型，编译器报二义。</summary>
    public string ToSql() => Build(Effective(), out _).Text;

    public string ToSql(out IReadOnlyList<AdoParameter> parameters)
    {
        var sql = Build(Effective(), out _);
        parameters = sql.Parameters;

        return sql.Text;
    }

    public string ToLogSql() => Build(Effective(), out _).ToLogSql(Orm.Provider);

    public QueryPlan Explain()
    {
        var model = Effective();
        var sql = Build(model, out var ast);

        // §10.1 硬规则 1：已注册的改写类钩子要在这里逐个回放。P0 的改写钩子只作用在写路径的值上，
        // 读路径此刻确实没有可记的步骤 —— 空集合是真的空，不是漏了。
        return new QueryPlan(ast, sql, model.StableKey, []) { Provider = Orm.Provider };
    }

    // ── 执行 ──────────────────────────────────────────────────────────

    public Task<List<TResult>> ListAsync<TResult>(CancellationToken ct) => Rows<TResult>(Effective(), ct);

    /// <summary>内部行数帽（<c>First</c> 取 1、<c>Single</c> 取 2 判重复，§5.7）。帽不走 §9.4 的序策略：
    /// "任意一行"本来就是 LINQ 语义，为此要求用户补序只会变成噪音（方言侧的占位序见
    /// <c>DialectBase.EmitPagingOrdering</c>）。</summary>
    public Task<List<TResult>> CappedAsync<TResult>(int rows, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        return Rows<TResult>(Effective() with { Limit = rows }, ct);
    }

    async Task<List<TResult>> Rows<TResult>(QueryModel model, CancellationToken ct)
    {
        Detect(model);

        if (Cached<TResult>(model) is { } hit)
        {
            Track(model, hit);

            return hit;
        }

        List<TResult> rows;

        if (model.SplitQuery && model.Includes.Count > 0 && model.Plan is EntityProjection)
            rows = await ListWithSplitIncludesAsync<TResult>(model, ct).ConfigureAwait(false);
        else if (model.IncludeManys.Count > 0 && model.Plan is EntityProjection)
            rows = await ListWithIncludeManyAsync<TResult>(model, ct).ConfigureAwait(false);
        else
            rows = await Orm.Ado.QueryAsync(Build(model, out _), Reader<TResult>(model), Tag("select"), model.Limit, ct).ConfigureAwait(false);

        Track(model, rows);
        Store(model, rows);

        return rows;
    }

    /// <summary>IncludeMany 的两条查询执行（§5.7）。先执行主查询拿父实体，再发第二条查询拿子实体
    /// （WHERE FK IN (@parentPks)），最后在内存里把子实体缝回父实体的集合属性。
    /// 父实体为空时跳过子查询（避免生成空 IN 子句）。链上挂了 <c>ThenIncludeMany</c> 就往下一层递归，
    /// 挂了 <c>ThenInclude</c> 就在这一层的子实体上另发一条拆分加载（§5.7 的引用那一档），
    /// 一条链发一串 SELECT：根一条、每一层各一条、每一层旁边每根引用导航再一条。</summary>
    [RequiresUnreferencedCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    [RequiresDynamicCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    async Task<List<TResult>> ListWithIncludeManyAsync<TResult>(QueryModel model, CancellationToken ct)
    {
        var parents = await Orm.Ado.QueryAsync(
            Build(model, out _), Reader<TResult>(model), Tag("select"), model.Limit, ct).ConfigureAwait(false);

        if (parents.Count == 0)
            return parents;

        var entity = (EntityProjection)model.Plan;
        var parentContract = entity.TableIndex >= 0 ? model.Tables[entity.TableIndex].Contract! : throw new QuickSqlException("IncludeMany 需要实体投影。");

        foreach (var includeMany in model.IncludeManys)
        {
            var parentObjects = new List<object>(parents.Count);

            foreach (var p in parents)
                parentObjects.Add(p!);

            await LoadChainAsync(parentObjects, parentContract, includeMany, ct).ConfigureAwait(false);
        }

        return parents;
    }

    /// <summary>逐层往下缝：本层刚捞出的子实体就是下一层的父端。上一层没行就直接停 —— 下一颗节点没有
    /// 可当 IN 值的键，而这一档不发空 IN（旁边那一档引用导航同理，它收的也是这一批子实体）。</summary>
    [RequiresUnreferencedCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    [RequiresDynamicCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    async Task LoadChainAsync(List<object> parents, EntityContract parentContract, IncludeManyNode node, CancellationToken ct)
    {
        var children = await LoadChildrenAsync(parents, parentContract, node, ct).ConfigureAwait(false);

        if (children.Count == 0)
            return;

        var childContract = Orm.Schemas.Contract(node.ChildType);

        // 旁边那一档：这一批子实体各自手里的引用导航。父端就是刚缝出来的这一批，所以直接把拆分加载器
        // 递过去 —— 它收的本来就是"一批任意实体 + 它们身上的引用边"，与根实体没有半句约定。
        if (node.References.Count > 0)
            await LoadSplitIncludeNodesAsync(children, childContract, node.References, ct).ConfigureAwait(false);

        foreach (var child in node.Children)
            await LoadChainAsync(children, childContract, child, ct).ConfigureAwait(false);
    }

    [RequiresUnreferencedCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    [RequiresDynamicCode("IncludeMany 按运行期 Type 反射读取主键值与设置集合导航属性。")]
    async Task<List<object>> LoadChildrenAsync(
        List<object> parents, EntityContract parentContract,
        IncludeManyNode node, CancellationToken ct)
    {
        var childContract = Orm.Schemas.Contract(node.ChildType);
        var childRead = Orm.Schemas.ReadDelegate(node.ChildType);
        var parentKeys = parentContract.PrimaryKey;

        var parentKeyGetters = BuildKeyGetters(parentContract.ClrType, parentKeys);
        var parentKeyValues = new List<object>(parents.Count);
        var parentMap = new Dictionary<object, List<object>>(parents.Count,
            parentKeys.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

        foreach (var parent in parents)
        {
            object key;

            if (parentKeys.Length == 1)
                key = parentKeyGetters[0](parent);
            else
            {
                var values = new object[parentKeys.Length];

                for (var j = 0; j < parentKeys.Length; j++)
                    values[j] = parentKeyGetters[j](parent);

                key = values;
            }

            parentKeyValues.Add(key);

            if (!parentMap.TryGetValue(key, out var list))
            {
                list = [];
                parentMap[key] = list;
            }

            list.Add(parent);
        }

        var childTable = Orm.NewTableRef(node.ChildType, 0);
        var childColumns = new List<Projection>(childContract.Columns.Length);

        for (var i = 0; i < childContract.Columns.Length; i++)
            childColumns.Add(new Projection(new ColumnExpression(childTable, childContract.Columns[i]), null));

        var inValues = new List<SqlExpression>(parentKeyValues.Count);

        foreach (var pkValue in parentKeyValues)
            inValues.Add(new ParameterValue(pkValue, null));

        var conditions = new List<SqlExpression>(node.ChildFkColumnNames.Count);

        for (var i = 0; i < node.ChildFkColumnNames.Count; i++)
        {
            var fkColumn = childContract.ByColumn[node.ChildFkColumnNames[i]];
            conditions.Add(new InExpression(new ColumnExpression(childTable, fkColumn), inValues));
        }

        SqlExpression? where = null;

        foreach (var condition in conditions)
            where = where is null ? condition : new SqlBinaryExpression(BinaryOperator.And, where, condition);

        var childAst = new SelectStatement
        {
            Columns = childColumns,
            From = new SourceTable(childTable),
            Where = where,
            // 子行序（OrderByMany，§5.7）：点过就把序灌进这一层子 SELECT 的 ORDER BY 槽，发射在方言里；
            // 没点过这一格就是空清单，方言不发 ORDER BY，那条子句与本口存在之前逐字节同形。
            OrderBy = [.. node.Orderings.Select(o => new OrderExpression(new ColumnExpression(childTable, o.Column), o.Descending))],
        };

        var dialect = Orm.Provider.Dialect;
        var writer = new SqlWriter(
            new StringBuilder(QueryCompiler.EstimateSqlLength(childAst)),
            new List<AdoParameter>(),
            dialect.ParameterPrefix);
        dialect.Emit(in childAst, writer);
        var childSql = new DialectSql(writer.ToSqlString(), writer.Parameters);

        var children = await Orm.Ado.QueryAsync(
            childSql,
            Materializer.EntityRows<object>(childRead, childContract),
            Tag("include-many"),
            null,
            ct).ConfigureAwait(false);

        var childKeyGetters = BuildKeyGettersByColumnName(node.ChildType, childContract, node.ChildFkColumnNames);
        var nav = MemberAccessors.BindCollection(
            parentContract.ClrType, node.NavigationMemberName, node.ChildType,
            $"实体 {parentContract.ClrType.Name} 上没有名为 {node.NavigationMemberName} 的集合导航属性，无法挂上 IncludeMany 加载的子实体。");

        foreach (var child in children)
        {
            object key;

            if (childKeyGetters.Length == 1)
                key = childKeyGetters[0](child);
            else
            {
                var values = new object[childKeyGetters.Length];

                for (var j = 0; j < childKeyGetters.Length; j++)
                    values[j] = childKeyGetters[j](child);

                key = values;
            }

            if (parentMap.TryGetValue(key, out var matchedParents))
            {
                // 缝的是<em>会话里的那一只</em>：这一行可能早就在身份映射里（先查过明细、再补载父的那一串），
                // 而按刚物化那只实例缝会让会话里同一行留两只 —— 级联删除撞上第二只时只能说"同键两只没有定义"。
                var stitched = _tracker?.CanonicalOf(child, node.ChildType) ?? child;

                foreach (var parent in matchedParents)
                    nav.Attach(parent, stitched);
            }
        }

        if (_tracker is not null)
            _tracker.TrackAll(children, node.ChildType);

        return children;
    }

    /// <summary>拆分查询执行（§5.7）。引用导航不走 LEFT JOIN：先执行主查询拿根实体，再为每个 Include 节点
    /// 单独发一条 SELECT ... WHERE PK IN (父端 FK 值集合)，最后在内存里把子实体缝回父实体的引用导航属性。
    /// 嵌套的 ThenInclude 递归处理：子实体加载完后继续为它的子节点发查询。</summary>
    [RequiresUnreferencedCode("AsSplitQuery 按运行期 Type 反射读取主键值与设置引用导航属性。")]
    [RequiresDynamicCode("AsSplitQuery 按运行期 Type 反射读取主键值与设置引用导航属性。")]
    async Task<List<TResult>> ListWithSplitIncludesAsync<TResult>(QueryModel model, CancellationToken ct)
    {
        var roots = await Orm.Ado.QueryAsync(
            Build(model, out _), Reader<TResult>(model), Tag("select"), model.Limit, ct).ConfigureAwait(false);

        if (roots.Count == 0)
            return roots;

        var entity = (EntityProjection)model.Plan;
        var rootContract = model.Tables[entity.TableIndex].Contract!;
        var rootObjects = new List<object>(roots.Count);

        foreach (var r in roots)
            rootObjects.Add(r!);

        await LoadSplitIncludeNodesAsync(rootObjects, rootContract, model.Includes, ct).ConfigureAwait(false);

        return roots;
    }

    /// <summary>递归加载一层拆分 Include 节点。对每个节点：收集父端 FK 值 → 查子表 WHERE PK IN (...) →
    /// 按 PK 建索引 → 缝回父实体的引用导航属性 → 递归处理子节点的 Children。</summary>
    [RequiresUnreferencedCode("拆分 Include 按运行期 Type 反射读取 FK 值与设置引用导航属性。")]
    [RequiresDynamicCode("拆分 Include 按运行期 Type 反射读取 FK 值与设置引用导航属性。")]
    async Task LoadSplitIncludeNodesAsync(
        List<object> parents, EntityContract parentContract,
        IReadOnlyList<IncludeNode> includes, CancellationToken ct)
    {
        foreach (var node in includes)
        {
            var childContract = node.TargetTable.Contract!;
            var childRead = Orm.Schemas.ReadDelegate(childContract.ClrType);
            var childPkColumns = childContract.PrimaryKey;

            // 缝回父实体：与 DbSet.Load 同一处理 —— 属性名与父型在节点上就定了，
            // 没道理每配到一颗子行都按运行期类型找一次属性再反射写一次。
            var setNavigation = MemberAccessors.Setter(
                parentContract.ClrType, node.NavigationMemberName,
                $"实体 {parentContract.ClrType.Name} 上没有名为 {node.NavigationMemberName} 的引用导航属性，无法挂上拆分 Include 加载的实体。",
                $"实体 {parentContract.ClrType.Name}.{node.NavigationMemberName} 没有公共写口：Include 要求导航属性可写。");

            var parentFkGetters = BuildKeyGettersByColumnName(parentContract.ClrType, parentContract, node.Relationship.ColumnNames);

            var fkValues = new HashSet<object>(
                parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);
            var parentByFk = new Dictionary<object, List<object>>(
                parentFkGetters.Length == 1 ? EqualityComparer<object>.Default : StructuralComparer.Instance);

            foreach (var parent in parents)
            {
                object fkKey;

                if (parentFkGetters.Length == 1)
                    fkKey = parentFkGetters[0](parent);
                else
                {
                    var vals = new object[parentFkGetters.Length];

                    for (var j = 0; j < parentFkGetters.Length; j++)
                        vals[j] = parentFkGetters[j](parent);

                    fkKey = vals;
                }

                fkValues.Add(fkKey);

                if (!parentByFk.TryGetValue(fkKey, out var list))
                {
                    list = [];
                    parentByFk[fkKey] = list;
                }

                list.Add(parent);
            }

            var childTable = Orm.NewTableRef(childContract.ClrType, 0);
            var childColumnProjections = new List<Projection>(childContract.Columns.Length);

            for (var i = 0; i < childContract.Columns.Length; i++)
                childColumnProjections.Add(new Projection(new ColumnExpression(childTable, childContract.Columns[i]), null));

            var inValues = new List<SqlExpression>(fkValues.Count);

            foreach (var fkValue in fkValues)
                inValues.Add(new ParameterValue(fkValue, null));

            var conditions = new List<SqlExpression>(childPkColumns.Length);

            for (var i = 0; i < childPkColumns.Length; i++)
                conditions.Add(new InExpression(new ColumnExpression(childTable, childPkColumns[i]), inValues));

            SqlExpression? where = null;

            foreach (var condition in conditions)
                where = where is null ? condition : new SqlBinaryExpression(BinaryOperator.And, where, condition);

            var childAst = new SelectStatement
            {
                Columns = childColumnProjections,
                From = new SourceTable(childTable),
                Where = where,
            };

            var dialect = Orm.Provider.Dialect;
            var writer = new SqlWriter(
                new StringBuilder(QueryCompiler.EstimateSqlLength(childAst)),
                new List<AdoParameter>(),
                dialect.ParameterPrefix);
            dialect.Emit(in childAst, writer);
            var childSql = new DialectSql(writer.ToSqlString(), writer.Parameters);

            var children = await Orm.Ado.QueryAsync(
                childSql,
                Materializer.EntityRows<object>(childRead, childContract),
                Tag("split-include"),
                null,
                ct).ConfigureAwait(false);

            // 先登记再缝：TrackAll 就地写回"会话里的那一只"（§8.2），于是导航上挂的、往下递归加载的都是那一只。
            // 这里可以登记在配之前，因为配的是子端<em>主键</em>对父端外键 —— 身份映射就是按主键建的，
            // 那一只与这一只在这个判据上恒等；而 IncludeMany 那一档配的是外键，改成登记之后缝就会把
            // "会话里改过外键、还没写出去"的那一行配到错的父上，所以那一档缝的时候才现问身份映射。
            if (_tracker is not null)
                _tracker.TrackAll(children, childContract.ClrType);

            var childPkGetters = BuildKeyGetters(childContract.ClrType, childPkColumns);

            foreach (var child in children)
            {
                object pkKey;

                if (childPkGetters.Length == 1)
                    pkKey = childPkGetters[0](child);
                else
                {
                    var vals = new object[childPkGetters.Length];

                    for (var j = 0; j < childPkGetters.Length; j++)
                        vals[j] = childPkGetters[j](child);

                    pkKey = vals;
                }

                if (parentByFk.TryGetValue(pkKey, out var matchedParents))
                {
                    foreach (var parent in matchedParents)
                        setNavigation(parent, child);
                }
            }

            if (node.Children.Count > 0)
            {
                var childObjects = new List<object>(children.Count);

                foreach (var c in children)
                    childObjects.Add(c);

                await LoadSplitIncludeNodesAsync(childObjects, childContract, node.Children, ct).ConfigureAwait(false);
            }
        }
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取主键属性读口。")]
    [RequiresDynamicCode("把主键属性读口编译成委托。")]
    static Func<object, object>[] BuildKeyGetters(Type entityType, ColumnContract[] keyColumns)
    {
        var getters = new Func<object, object>[keyColumns.Length];

        for (var i = 0; i < keyColumns.Length; i++)
            getters[i] = MemberAccessors.Getter(
                entityType, keyColumns[i].MemberName,
                $"实体 {entityType.Name} 上没有主键属性 {keyColumns[i].MemberName}",
                $"实体 {entityType.Name} 的主键属性 {keyColumns[i].MemberName} 没有公共 getter");

        return getters;
    }

    [RequiresUnreferencedCode("按运行期 Type 反射取外键列对应属性的读口。")]
    [RequiresDynamicCode("把外键列对应属性的读口编译成委托。")]
    static Func<object, object>[] BuildKeyGettersByColumnName(Type entityType, EntityContract contract, IReadOnlyList<string> columnNames)
    {
        var getters = new Func<object, object>[columnNames.Count];

        for (var i = 0; i < columnNames.Count; i++)
        {
            var memberName = contract.ByColumn[columnNames[i]].MemberName;

            getters[i] = MemberAccessors.Getter(
                entityType, memberName,
                $"实体 {entityType.Name} 上没有属性 {memberName}",
                $"实体 {entityType.Name} 的属性 {memberName} 没有公共 getter");
        }

        return getters;
    }

    /// <summary>复合外键的字典比较器：按元素比较 <c>object[]</c> 键值。</summary>
    sealed class StructuralComparer : IEqualityComparer<object>
    {
        public static readonly StructuralComparer Instance = new();

        public new bool Equals(object? x, object? y)
        {
            if (x is object[] ax && y is object[] ay)
                return ax.SequenceEqual(ay);

            return object.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is object[] arr)
            {
                var hash = new HashCode();

                foreach (var item in arr)
                    hash.Add(item);

                return hash.ToHashCode();
            }

            return obj.GetHashCode();
        }
    }

    public async Task<TResult?> FirstAsync<TResult>(CancellationToken ct)
        => await CappedAsync<TResult>(1, ct).ConfigureAwait(false) is [var only, ..] ? only : default;

    /// <summary>流式出口：一次只有一行在内存里（§12.7 E）。
    /// <paramref name="batchSize"/> 在别的库上是驱动侧的 <c>FetchSize</c>，而 TDS 没有逐批取回的游标协议、
    /// <see cref="DbDataReader"/> 本来就是从网络缓冲里逐行读 —— 所以这个值在这里只用于校验，
    /// 不影响结果集，也不假装调了什么服务端开关。SQL Server 逐行流本来就是它的语义。</summary>
    public IAsyncEnumerable<TResult> StreamAsync<TResult>(int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        return Streamed<TResult>(Effective(), ct);
    }

    async IAsyncEnumerable<TResult> Streamed<TResult>(QueryModel model, [EnumeratorCancellation] CancellationToken ct)
    {
        Detect(model);

        var tracker = _tracker;
        var rows = Orm.Ado.StreamAsync(Build(model, out _), Reader<TResult>(model), Tag("stream"), ct);

        // 流式与整批走同一份登记规则：一次查询在两种出口下"进没进身份映射"必须一致，
        // 否则 foreach (var o in db.Set<T>()) 之后 Entry(o) 会问它不在会话里，而 ToListAsync 之后又在。
        if (tracker is not null && EntityClrType(model) is { } streamedType)
        {
            await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
                yield return (TResult) tracker.TrackEntity(row!, streamedType);

            yield break;
        }

        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
            yield return row;
    }

    // ── 跟踪接缝（§8.2） ───────────────────────────────────────────

    /// <summary>这一份模型投影出来的是<em>实体本身</em>时的 CLR 类型，否则 null。
    /// <b>DTO / 标量 / 聚合投影一律落空</b>：它们不是「库里那一行」，没有身份映射可言 ——
    /// 把 <c>Orders</c> 投影成的 <c>OrderRow</c> 登记进去，得到的是一个既不能写也不该写的假实例。</summary>
    static Type? EntityClrType(QueryModel model)
        => model.Plan is EntityProjection projection && model.Tables[projection.TableIndex].Contract is { } contract
            ? contract.ClrType
            : null;

    /// <summary>§8.2 那句「物化之前先 DetectChanges」的落点。判据与 <see cref="Track"/> 同一条：
    /// 只有要往身份映射里登记东西，才需要先把已有的意图算清楚 —— 无跟踪的读、DTO 投影的读，
    /// 这条路上一次比较都不付（§8.2 末尾那句「零额外分配」要靠这个才成立）。</summary>
    void Detect(QueryModel model)
    {
        if (_tracker is { AutoDetectChangesEnabled: true } && EntityClrType(model) is not null)
            _tracker.DetectChanges();
    }

    void Track<T>(QueryModel model, List<T> rows)
    {
        if (_tracker is null)
            return;

        if (EntityClrType(model) is { } clrType)
            _tracker.TrackAll(rows, clrType);
    }

    /// <summary>聚合出口（§5.12 表的第一列）。序由 <see cref="QueryModel.StripsOrdering"/> 自动剥掉
    /// （聚合计划的 <c>UsesOrdering</c> 为 false），所以 §5.12 那条"复用带 OrderBy 的 iq 先 Count 再取页必炸"
    /// 的高频事故在这里不可能复现。</summary>
    public async Task<T> AggregateAsync<T>(AggregateKind kind, LambdaExpression? key, CancellationToken ct)
    {
        var model = AggregateModel(Effective(), kind, key);

        // 聚合查询恒定返回一行（COUNT/SUM/MIN 都是如此），所以"零行"不是空结果而是实现或方言错误。
        // 报出来比给 default 好：给 0 会让一个漏了 GROUP BY 的聚合看起来像"确实没有数据"。
        return await Orm.Ado.FirstAsync(Build(model, out _), Reader<T>(model), Tag(kind.ToString().ToLowerInvariant()), ct).ConfigureAwait(false)
               ?? throw new QuickSqlException($"{kind} 的聚合查询没有返回任何行：聚合恒定一行，这是方言发射或模型派生的缺陷，不是空结果集。");
    }

    public Task<int> CountAsync(CancellationToken ct) => AggregateAsync<int>(AggregateKind.Count, null, ct);

    public Task<long> LongCountAsync(CancellationToken ct) => AggregateAsync<long>(AggregateKind.LongCount, null, ct);

    /// <summary><c>Any()</c> 现在就是 <c>COUNT(*) &gt; 0</c>，语义对但没到最优：<c>WHERE EXISTS(...)</c> 的
    /// 反形要改投影计划，记在 P1。留在这里而不是不说，是因为"大表上 Any 很慢"是可预期的成本，
    /// 而"Any 返回错结果"不是。</summary>
    public async Task<bool> AnyAsync(CancellationToken ct) => await AggregateAsync<int>(AggregateKind.Any, null, ct).ConfigureAwait(false) > 0;

    public Task<TResult> SumAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Sum, key, ct);

    public Task<TResult> MaxAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Max, key, ct);

    public Task<TResult> MinAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Min, key, ct);

    public Task<TResult> AverageAsync<TResult>(LambdaExpression key, CancellationToken ct) => AggregateAsync<TResult>(AggregateKind.Average, key, ct);

    internal QueryModel AggregateModel(QueryModel model, AggregateKind kind, LambdaExpression? key)
    {
        var countLike = kind is AggregateKind.Count or AggregateKind.LongCount or AggregateKind.Any;

        // 派生表那一档（§5.12）：这三种形状下"在原模型上换一个 COUNT 出来"得到的是一个看起来完全合理的错数字 ——
        // 分组之后按行数是"每组的行数"，DISTINCT 之后按行数是去重前的行数，而带页的整句换掉分页就数了全表。
        // 现在它们各发各的句子：整份模型原样进内层，外层只剩一个 COUNT(*)。
        if (NeedsDerivedTable(model))
        {
            // 只有 COUNT 一族发得出这一档：外层要引用内层的列（SELECT SUM(t0.Total) FROM (…) t0）
            // 就得先给派生行造一份列契约，而那一步今天没有形状（派生行的列名与类型只在内层的投影计划里）。
            if (!countLike)
                throw new QuickSqlException(
                    $"分组之后、或带 Skip/Take、DISTINCT 之后再 {kind} 要的是外层引用内层的列（SELECT {kind}(t0.那一列) FROM (…) t0），" +
                    "而派生行的列契约今天没有形状 —— 这一档只接 COUNT 一族。请把这一列在未分组、未分页的查询上聚合，" +
                    "或者走 §11 的原生 SQL 口（orm.Ado.QueryAsync / ExecuteScalarAsync）自己写外层。");

            return Derived(model, kind);
        }

        var scout = Scout(model);
        var plan = countLike
            ? new AggregateProjection(kind, new StarExpression(null), kind == AggregateKind.LongCount ? typeof(long) : typeof(int), Distinct: false)
            : key is null
                ? throw new QuickSqlException($"{kind} 需要值选择器，例如 q.Sum(o => o.Amount)。")
                : new AggregateProjection(kind, Orm.Expressions.Visit(key.Body, Scope.Create(model, key, scout)), key.Body.Type, Distinct: false);

        // 这一份是聚合出口就地换投影造出来的临时模型，它不进链（链上后面没有算子会再读它），
        // 所以这一颗 lambda 欠下的联表落进这一句为止：<b>不延长 <see cref="_navigation"/></b>，那是链上的账。
        var aggregate = model with { Plan = plan, Ordering = [], Offset = null, Limit = null };

        return scout is null ? aggregate : scout.Flush(aggregate);
    }

    /// <summary>这一份模型能不能就地补一个聚合、还是必须先包成内层。</summary>
    static bool NeedsDerivedTable(QueryModel model)
        => model.GroupBy.Count > 0 || model.Distinct || model.Offset is > 0 || model.Limit is not null;

    /// <summary>包内层：外层清掉一切子句（它们都活在内层那一份里，重复端出去就是"同一条 WHERE 写两遍而第二遍引用不到列"），
    /// 只留表位与聚合计划。<b>内层的序按一句库的事实处理</b>：T-SQL 拒绝带 <c>ORDER BY</c> 的派生表
    /// （除非内层自己有分页，那时那个序是 <c>OFFSET..FETCH</c> 的一部分，摘掉就改了页的内容），
    /// 而 count 不关心序，所以没有分页时把它剥掉是既合法又省的一刀。</summary>
    static QueryModel Derived(QueryModel model, AggregateKind kind)
    {
        var inner = model.Offset is null && model.Limit is null ? model with { Ordering = [] } : model;

        var plan = new AggregateProjection(kind, new StarExpression(null), kind == AggregateKind.LongCount ? typeof(long) : typeof(int), Distinct: false);

        return model with
        {
            Plan = plan,
            Ordering = [],
            GroupBy = [],
            Having = null,
            Offset = null,
            Limit = null,
            Distinct = false,
            Predicates = [],
            Joins = [],
            Includes = [],
            IncludeManys = [],
            DerivedFrom = inner,
        };
    }

    public async Task<PagedResult<TResult>> PagedAsync<TResult>(int page, int size, PagedOptions? options, CancellationToken ct)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page), "页码从 1 开始。");

        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "页大小必须为正：0 会写成 FETCH NEXT 0 ROWS，读回来是空页而不是全量。");

        var source = Effective();
        var method = Resolve(page, options?.CountMethod ?? CountMethod.Auto);
        var offset = (page - 1) * size;

        // Q4：多取一行判有无下一页，省掉整条 count —— 大表上那条 count 就是每次翻页一次全表扫描（§5.12）。
        var items = source with { Offset = offset, Limit = method == CountMethod.SkipTotal ? size + 1 : size };
        var rows = await Rows<TResult>(items, ct).ConfigureAwait(false);

        if (method == CountMethod.SkipTotal)
            return new PagedResult<TResult>
            {
                Items = [.. rows.Take(size)],
                Page = page,
                Size = size,
                Total = null,
                HasNext = rows.Count > size,
            };

        // 页码与页大小覆盖查询上已叠的 Skip/Take —— "第 N 页"本来就是绝对位置，叠出来的偏移没人想要。
        var total = await TotalCount(source, options, ct).ConfigureAwait(false);

        return new PagedResult<TResult>
        {
            Items = [.. rows],
            Page = page,
            Size = size,
            Total = total,
            HasNext = offset + size < total,
        };
    }

    async Task<int> TotalCount(QueryModel source, PagedOptions? options, CancellationToken ct)
    {
        if (options is { StripOrderingForCount: false })
            throw new QuickSqlException("StripOrderingForCount = false 会让 count 带上 ORDER BY，而 SQL Server 与 PG 血统的库对此直接报错（§5.12 的表）。没有一种库需要这个形态。");

        var model = AggregateModel(source with { Offset = null, Limit = null }, AggregateKind.Count, key: null);

        return await Orm.Ado.FirstAsync(Build(model, out _), Reader<int>(model), Tag("count"), ct).ConfigureAwait(false);
    }

    /// <summary><c>CountMethod.Auto</c> 的落位：首页只判断有没有下一页，第二页起才付 count（§5.12 明写"应该是默认行为"）。
    /// <see cref="CountMethod.CountOverWindow"/>（Q1）与 <see cref="CountMethod.SeparateTransaction"/>（同连接同事务）
    /// 一条都没实现 —— 没实现就别让它看起来能用。</summary>
    static CountMethod Resolve(int page, CountMethod requested) => requested switch
    {
        CountMethod.Auto => page == 1 ? CountMethod.SkipTotal : CountMethod.CountStar,
        CountMethod.SkipTotal or CountMethod.CountStar => requested,
        CountMethod.CountOverWindow => throw new QuickSqlException("COUNT(*) OVER() 单往返（§5.12 Q1）要改投影计划（结果集多出一列总数），未接入。"),
        CountMethod.SeparateTransaction => throw new QuickSqlException("同连接同事务的两条语句（§5.12 Q3 的一致性形态）要等 §6 的事务层。"),
        _ => throw new ArgumentOutOfRangeException(nameof(requested), requested, "未知 CountMethod"),
    };

    Func<DbDataReader, TResult> Reader<TResult>(QueryModel model)
        => model.Includes.Count > 0 && model.Plan is EntityProjection && !model.SplitQuery
            ? Materializer.CreateWithIncludes<TResult>(model, Orm.Schemas)
            : Materializer.Create<TResult>(model, Orm.Schemas);

    ExecutionTag Tag(string operation) => new(operation, _tables, _cluster, Model.Role);

    /// <summary>缓存只在 <c>WithCache</c> 与 <see cref="ICacheAdapter"/> 同时就位时生效。宿主没装缓存适配器时
    /// 这一层就是没有缓存 —— 结果集不受影响，所以这不是 §12.2 意义上的降级，不需要留痕。</summary>
    List<TResult>? Cached<TResult>(QueryModel model)
    {
        if (model.CacheKey is not { Length: > 0 } key || Orm.Aop.Cache is not { } cache)
            return null;

        return cache.TryGet<List<TResult>>(key, out var hit) ? hit : null;
    }

    void Store<TResult>(QueryModel model, List<TResult> rows)
    {
        if (model.CacheKey is { Length: > 0 } key && model.CacheTtl is { } ttl && Orm.Aop.Cache is { } cache)
            cache.Set(key, rows, ttl);
    }
}

/// <summary>算子的稳定标识：链式方法与 <c>Queryable.*</c> 调用都归到这里，所以两侧不可能各写一套语义（§5.5）。</summary>
internal enum QueryOp
{
    Where,
    Select,
    OrderBy,
    OrderByDescending,
    ThenBy,
    ThenByDescending,
    Skip,
    Take,
    Distinct,
}
