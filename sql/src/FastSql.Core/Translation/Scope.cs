using FastSql.Query.Model;
using FastSql.Sql.Ast;
using System.Linq.Expressions;

namespace FastSql.Translation;

/// <summary>形参 → 表位的映射（§5.1）。<b>只按位置</b>：形参名不参与任何语义解析，
/// 所以改名、重命名重构、<c>nameof</c> 都安全。允许尾部形参截断（<c>(o, c) =></c> 写成 <c>(o) =></c>）。</summary>
public sealed class Scope
{
    readonly Dictionary<ParameterExpression, TableRef> _map;
    readonly Dictionary<ParameterExpression, GroupingShape>? _groupings;
    readonly NavigationJoinScout? _navs;

    Scope(QueryModel model, Dictionary<ParameterExpression, TableRef> map, NavigationJoinScout? navs)
    {
        Model = model;
        _map = map;
        _navs = navs;
    }

    Scope(QueryModel model, Dictionary<ParameterExpression, TableRef> map, Dictionary<ParameterExpression, GroupingShape> groupings,
        NavigationJoinScout? navs)
        : this(model, map, navs)
        => _groupings = groupings;

    public QueryModel Model { get; }

    public static Scope Create(QueryModel model, LambdaExpression lambda) => Bound(model, lambda, exactType: true);

    /// <summary>读侧算子那一档：与上面那条只差把<em>自动联表的翻译现场</em>递进来（§5.2）。
    /// <b>它不写成 <see cref="Create(QueryModel, LambdaExpression)"/> 的一个可选参数</b>：那颗闸门是内部机制，
    /// 挂在公开口上等于把"补一条 LEFT JOIN 这件事由谁负责"变成调用方可选项 —— 而写侧的
    /// <c>SET</c>/<c>WHERE</c> 与全局过滤器那两处拿到现场就只会得到一句"这条链补不出联表"的错，
    /// 因为它们的模型没有 <see cref="QueryModel.Joins"/> 那一格可落。
    /// <paramref name="scout"/> 可为 null：那是「这张表一根 to-one 边都没有」那一档（<c>QueryEngine.Scout</c>
    /// 的判据），此时读导航的出口就是原来那句"这不是列"，而这一颗 lambda 一分钱都不为自动联表付。</summary>
    internal static Scope Create(QueryModel model, LambdaExpression lambda, NavigationJoinScout? scout)
        => Bound(model, lambda, exactType: true, scout);

    /// <summary>分组链上那一段 lambda 的绑定（§5.3）。与 <see cref="Create"/> 差的不止一处判据，
    /// 而是<b>形参绑的东西换了</b>：<c>g</c> 是 <c>IGrouping&lt;TKey,TValue&gt;</c>，它不是一个表位 ——
    /// 它背后那张表在 <see cref="GroupingShape.Table"/> 上，而它的成员读法走 <c>g.Key</c> 而不是列契约。
    /// 所以这里<em>不</em>叠 <see cref="Bound"/> 的位置校验：那颗闸门判的是「形参类型必须等于表位实体」，
    /// 而 <c>IGrouping</c> 天生不等于，硬贴过去只会得到一句「第 1 个形参是 IGrouping`2，而表位 t0 的实体是 Order」——
    /// 那是一条把正确写法报成错误的消息。</summary>
    internal static Scope ForGrouping(QueryModel model, LambdaExpression lambda, GroupingShape grouping,
        NavigationJoinScout? navs = null)
    {
        if (lambda.Parameters.Count != 1)
            throw new UntranslatableExpressionException(lambda.ToString(),
                "分组后的 lambda 只有一个形参（那一个组）：多表的分组要先把两张表联起来（LeftJoin/InnerJoin），再在最外层分组");

        var groupings = new Dictionary<ParameterExpression, GroupingShape>(ReferenceEqualityComparer.Instance)
        {
            [lambda.Parameters[0]] = grouping,
        };

        return new Scope(model, new Dictionary<ParameterExpression, TableRef>(ReferenceEqualityComparer.Instance), groupings, navs);
    }

    /// <summary>这个形参是不是分组形参（<c>g</c>）。<c>g</c> 与表位形参在 <see cref="Contains"/> 上是同一档
    /// （都「引用了查询的参数」），但在成员读法与聚合读法上走两条完全不同的路，所以分开问。</summary>
    internal bool TryGroupingOf(ParameterExpression parameter, out GroupingShape grouping)
    {
        if (_groupings is not null && _groupings.TryGetValue(parameter, out grouping))
            return true;

        grouping = default;

        return false;
    }

    /// <summary>全局过滤器那一档的绑定（§10.7）。与 <see cref="Create"/> 只差一处判定：<b>形参声明的类型
    /// 允许是表位实体的接口或基类</b> —— 规格示例第一句就是 <c>orm.Filters.Add&lt;ISoftDelete&gt;(e =&gt; !e.Deleted)</c>，
    /// 那份 lambda 的形参挂在接口上，而它要贴进的是 <c>Order</c> 那一格；按相等判就永远贴不上，
    /// 于是"按接口注册一次、所有实现类都带上这个条件"这件事没有落点。成员解析仍走表位实体的契约
    /// （<c>ByMember["Deleted"]</c> 取自 <c>Order</c>），所以列名与列序没有第二套真相。
    ///
    /// <para><b>为什么不给 <see cref="Create"/> 加一个开关参数</b>：那条链上"形参类型必须等于表位实体"
    /// 是 <c>Select((x, index) =&gt; …)</c> 与"把没联过的表写进 lambda"这两类误用的现行判据，
    /// 把它放宽成可赋值等于拆读侧的闸门换过滤器的方便。这里两个入口、一份实现，
    /// 上面那两道拒绝在两侧同样成立。</para></summary>
    public static Scope ForFilter(QueryModel model, LambdaExpression lambda) => Bound(model, lambda, exactType: false);

    static Scope Bound(QueryModel model, LambdaExpression lambda, bool exactType, NavigationJoinScout? navs = null)
    {
        var map = new Dictionary<ParameterExpression, TableRef>(ReferenceEqualityComparer.Instance);

        if (lambda.Parameters.Count > model.Tables.Count)
            throw new UntranslatableExpressionException(lambda.ToString(),
                $"lambda 有 {lambda.Parameters.Count} 个形参，但 FROM 序列只有 {model.Tables.Count} 张表");

        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            var parameter = lambda.Parameters[i];
            var table = model.Tables[i];

            // 形参按位置绑表，所以类型必须对得上。对不上说明 FROM 序列与 lambda 的形状不是同一次联表的结果 ——
            // 最常见的就是 Queryable.Select 的 (x, index) 重载：第二个形参是行号而不是表，绑上去会得到
            // 一个"看起来能编译、结果集错列"的模型，所以在这里就拒掉。
            if (table.Contract is { ClrType: var expected } && !Matches(parameter.Type, expected, exactType))
                throw new UntranslatableExpressionException(lambda.ToString(),
                    $"第 {i + 1} 个形参是 {parameter.Type.Name}，而表位 {table.Alias} 的实体是 {expected.Name}：形参按位置绑表。" +
                    "带行号的 Select 重载、以及把没联过的表写进 lambda，都是这个形状。");

            map[parameter] = table;
        }

        return new Scope(model, map, navs);
    }

    /// <summary>形参类型与表位实体合不合。<b>读侧按相等判，过滤器那一档按可赋值判</b>
    /// （<see cref="ForFilter"/> 的那段注释给了理由）。基类/接口形参能贴进实现类，而
    /// <c>int</c> 形参贴进 <c>Order</c> 仍然会被这一格拒掉 —— 放宽的是"同一继承链"，不是"任意类型"。</summary>
    static bool Matches(Type declared, Type entity, bool exactType)
        => exactType ? declared == entity : declared.IsAssignableFrom(entity);

    public TableRef TableOf(ParameterExpression parameter)
        => _map.TryGetValue(parameter, out var table)
            ? table
            : throw new UntranslatableExpressionException(parameter.FriendlyName(), "该形参没有对应的 FROM 表：形参顺序决定它绑定到哪张表");

    public bool Contains(ParameterExpression parameter)
        => _map.ContainsKey(parameter) || (_groupings?.ContainsKey(parameter) ?? false);

    /// <summary>关联子查询那一档的扩展：在外层 scope 的基础上把一个额外的形参绑到子查询的表位。
    /// <c>o.Items.Any(i =&gt; i.Quantity &gt; 2)</c> 里的 <c>i</c> 不在外层 lambda 的形参列表里，
    /// 但它要解析到子查询的那张表位上；外层形参（<c>o</c>）仍然绑在外层表位上，关联条件才拼得出来。
    /// 登记表跟着下去：这一层的 <c>i.Customer.City</c> 补出来的那条 LEFT JOIN 落的是<em>外层</em>那句的 FROM，
    /// 而外层 FROM 里的表在子查询里正好引用得到（关联引用的常规形状），所以同一颗 lambda 里两层的读法共用一份欠账。</summary>
    public Scope Derive(TableRef table, ParameterExpression parameter) => Extend(table, parameter, _navs);

    /// <summary>与 <see cref="Derive"/> 只差一件事：<b>把自动联表那一档关掉</b>（§5.2）。
    /// 用的那一条是<em>子查询自己</em>那张表位，它不在外层 <see cref="QueryModel.Tables"/> 里，
    /// 于是为它补的 ON（<c>内层表.FK = 新表.PK</c>）落进外层 FROM 时引用的是外层根本没有的那张表 ——
    /// 那是一句库会直接拒绝的错句，而不是一个能靠改名救回来的别名冲突。所以这一层的导航读法拿到的是
    /// 「认不出」，由 <c>ExpressionTranslator.Navigation</c> 那一句给出理由。</summary>
    internal Scope DeriveInner(TableRef table, ParameterExpression parameter) => Extend(table, parameter, navs: null);

    Scope Extend(TableRef table, ParameterExpression parameter, NavigationJoinScout? navs)
    {
        var newMap = new Dictionary<ParameterExpression, TableRef>(ReferenceEqualityComparer.Instance);

        foreach (var kvp in _map)
            newMap[kvp.Key] = kvp.Value;

        newMap[parameter] = table;

        if (_groupings is null)
            return new Scope(Model, newMap, navs);

        // 分组形参要跟着下去：`g.Sum(o => o.Amount) > g.Count()` 里后那一颗 `g` 仍在这个派生层被问到。
        var nested = new Dictionary<ParameterExpression, GroupingShape>(ReferenceEqualityComparer.Instance);

        foreach (var kvp in _groupings)
            nested[kvp.Key] = kvp.Value;

        return new Scope(Model, newMap, nested, navs);
    }

    /// <summary>这一串成员读法的宿主是不是<em>引用导航</em>，是就把它落成一张表位（§5.2 的自动联表）。
    /// 递归的那一层就是 <c>o.Customer.Manager.City</c> 这种多级读法：宿主 <c>o.Customer.Manager</c>
    /// 自己又是一条挂在 <c>t1</c> 上的边，所以先落宿主、再落这一格，表位一层一张。
    ///
    /// <para>交回 null 说的是"这一形不归我"，四种来路都从这里走：<b>那一格是列</b>（<c>o.CustomerId</c>
    /// 本来就是一条列读法）、<b>契约上没有那条边</b>（集合导航、未映射的计算成员、没注册的目标实体）、
    /// <b>这一层没有登记表</b>（写侧的 <c>SET</c>/<c>WHERE</c>、全局过滤器、联表 ON、相关子查询内层 ——
    /// 那几处补一条 JOIN 没有落点，理由各自在造 scope 的那一句里）、<b>这张表一根 to-one 边都没有</b>
    /// （<c>QueryEngine.Scout</c> 那一档：单表且契约上没有引用导航，就不建现场，于是这笔钱只由真读导航的人付）。
    /// 这一颗判断是全函数：
    /// 认不出就交回 null 由调用方去说它那句，而不是在这里抛出一条只覆盖一种来路的错。</para></summary>
    internal TableRef? NavigationTable(Expression node)
    {
        if (_navs is null)
            return null;

        if (node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } widen)
            node = widen.Operand;

        if (node is not MemberExpression { Expression: { } owner } member)
            return null;

        // 宿主既可能是形参（<c>o.Customer</c> 这一层），也可能自己又是一条边（<c>o.Customer.Manager</c>）。
        var host = owner as ParameterExpression is { } parameter ? _map.GetValueOrDefault(parameter) : NavigationTable(owner);

        return host is null ? null : _navs.Resolve(host, member.Member.Name);
    }

    public int TableIndex(ParameterExpression parameter) => TableOf(parameter).Index;

    public string NameOf(ParameterExpression parameter) => parameter.FriendlyName();
}

static class ParameterNameExtensions
{
    /// <summary>错误消息里给出形参名，但解析时从不读它——这两件事不冲突，别混为一谈。</summary>
    public static string FriendlyName(this ParameterExpression parameter) => parameter.Name ?? "«arg»";
}
