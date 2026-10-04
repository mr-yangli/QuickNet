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

    Scope(QueryModel model, Dictionary<ParameterExpression, TableRef> map)
    {
        Model = model;
        _map = map;
    }

    Scope(QueryModel model, Dictionary<ParameterExpression, TableRef> map, Dictionary<ParameterExpression, GroupingShape> groupings)
        : this(model, map)
        => _groupings = groupings;

    public QueryModel Model { get; }

    public static Scope Create(QueryModel model, LambdaExpression lambda) => Bound(model, lambda, exactType: true);

    /// <summary>分组链上那一段 lambda 的绑定（§5.3）。与 <see cref="Create"/> 差的不止一处判据，
    /// 而是<b>形参绑的东西换了</b>：<c>g</c> 是 <c>IGrouping&lt;TKey,TValue&gt;</c>，它不是一个表位 ——
    /// 它背后那张表在 <see cref="GroupingShape.Table"/> 上，而它的成员读法走 <c>g.Key</c> 而不是列契约。
    /// 所以这里<em>不</em>叠 <see cref="Bound"/> 的位置校验：那颗闸门判的是「形参类型必须等于表位实体」，
    /// 而 <c>IGrouping</c> 天生不等于，硬贴过去只会得到一句「第 1 个形参是 IGrouping`2，而表位 t0 的实体是 Order」——
    /// 那是一条把正确写法报成错误的消息。</summary>
    internal static Scope ForGrouping(QueryModel model, LambdaExpression lambda, GroupingShape grouping)
    {
        if (lambda.Parameters.Count != 1)
            throw new UntranslatableExpressionException(lambda.ToString(),
                "分组后的 lambda 只有一个形参（那一个组）：多表的分组要先把两张表联起来（LeftJoin/InnerJoin），再在最外层分组");

        var groupings = new Dictionary<ParameterExpression, GroupingShape>(ReferenceEqualityComparer.Instance)
        {
            [lambda.Parameters[0]] = grouping,
        };

        return new Scope(model, new Dictionary<ParameterExpression, TableRef>(ReferenceEqualityComparer.Instance), groupings);
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

    static Scope Bound(QueryModel model, LambdaExpression lambda, bool exactType)
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

        return new Scope(model, map);
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
    /// 但它要解析到子查询的那张表位上；外层形参（<c>o</c>）仍然绑在外层表位上，关联条件才拼得出来。</summary>
    public Scope Derive(TableRef table, ParameterExpression parameter)
    {
        var newMap = new Dictionary<ParameterExpression, TableRef>(ReferenceEqualityComparer.Instance);

        foreach (var kvp in _map)
            newMap[kvp.Key] = kvp.Value;

        newMap[parameter] = table;

        if (_groupings is null)
            return new Scope(Model, newMap);

        // 分组形参要跟着下去：`g.Sum(o => o.Amount) > g.Count()` 里后那一颗 `g` 仍在这个派生层被问到。
        var nested = new Dictionary<ParameterExpression, GroupingShape>(ReferenceEqualityComparer.Instance);

        foreach (var kvp in _groupings)
            nested[kvp.Key] = kvp.Value;

        return new Scope(Model, newMap, nested);
    }

    public int TableIndex(ParameterExpression parameter) => TableOf(parameter).Index;

    public string NameOf(ParameterExpression parameter) => parameter.FriendlyName();
}

static class ParameterNameExtensions
{
    /// <summary>错误消息里给出形参名，但解析时从不读它——这两件事不冲突，别混为一谈。</summary>
    public static string FriendlyName(this ParameterExpression parameter) => parameter.Name ?? "«arg»";
}
