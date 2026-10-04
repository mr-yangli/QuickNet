using FastSql.Metadata;
using FastSql.Sql.Ast;

namespace FastSql.Query.Model;

/// <summary>查询的唯一真相（§5.5）。链式与 LINQ 两条路径都只能往这里追加，
/// 谁都不许自己生成 SQL 文本。不可变：每次派生新实例，所以 §5.11 的复用与并发枚举天然安全。</summary>
public sealed record QueryModel
{
    public required IReadOnlyList<TableRef> Tables { get; init; }

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];

    /// <summary>AND 折叠前的原样集合——保留它才能做到「链式与 LINQ 产出同一个模型」的可判定比较。</summary>
    public IReadOnlyList<SqlExpression> Predicates { get; init; } = [];

    public IReadOnlyList<OrderExpression> Ordering { get; init; } = [];

    public required ProjectionPlan Plan { get; init; }

    public IReadOnlyList<SqlExpression> GroupBy { get; init; } = [];

    public SqlExpression? Having { get; init; }

    public int? Offset { get; init; }

    public int? Limit { get; init; }

    public bool Distinct { get; init; }

    public string? CacheKey { get; init; }

    public TimeSpan? CacheTtl { get; init; }

    /// <summary>急切加载的导航属性树（§5.6）。空列表表示没有 Include。编译器按深度优先遍历生成
    /// LEFT JOIN 与扩展列，物化器按同一顺序读取并设置导航属性。</summary>
    public IReadOnlyList<IncludeNode> Includes { get; init; } = [];

    /// <summary>集合导航的批量加载意图（§5.7）。不改写主查询 SQL，执行层在主查询完成后
    /// 发第二条查询拿子实体，内存缝合回父实体的集合属性。</summary>
    public IReadOnlyList<IncludeManyNode> IncludeManys { get; init; } = [];

    /// <summary>拆分查询标志（§5.7）。为 <c>true</c> 时引用导航（<see cref="Includes"/>）不走 LEFT JOIN，
    /// 而是主查询之后单独发查询再内存缝合。不进规范文本：与跟不跟踪一样是执行策略而非查询形状。</summary>
    public bool SplitQuery { get; init; }

    /// <summary>§5.12 的归约规则：聚合语义不含序，所以 Count/Sum/Any 要把 ORDER BY 剥掉；
    /// 但带 <c>Skip/Take</c> 的形态必须保留（<c>OFFSET</c> 在没有 <c>ORDER BY</c> 时不保证行序，T-SQL 那一族还直接报错）。
    /// 剥不剥由模型决定，不由方言决定。</summary>
    public bool StripsOrdering => !Plan.UsesOrdering && Limit is null;

    public TableRef Root => Tables[0];

    /// <summary>等价性测试（§13.2）的判据：两个模型的规范文本必须逐字符相等。</summary>
    public string StableKey => QueryModelPrinter.Print(this);

    public QueryModel AddPredicate(SqlExpression predicate)
        => this with { Predicates = [.. Predicates, predicate] };

    public QueryModel AddOrdering(OrderExpression order)
        => this with { Ordering = [.. Ordering, order] };

    public QueryModel AddJoin(JoinClause join, TableRef table)
        => this with { Joins = [.. Joins, join], Tables = [.. Tables, table] };
}

/// <summary>投影计划决定物化形状，因此它属于模型而不是方言层。</summary>
public abstract record ProjectionPlan
{
    /// <summary>投影本身是否依赖 ORDER BY（实体/DTO/标量投影依赖，聚合不依赖）。</summary>
    public virtual bool UsesOrdering => true;
}

public sealed record EntityProjection(int TableIndex) : ProjectionPlan;

public sealed record ValueProjection(SqlExpression Node, Type ResultType) : ProjectionPlan;

/// <summary>DTO / 匿名类型投影。<see cref="Slots"/> 的顺序即结果集列序。</summary>
public sealed record ObjectProjection(Type ResultType, IReadOnlyList<ProjectionSlot> Slots, bool ViaConstructor) : ProjectionPlan;

public sealed record ProjectionSlot(string Name, SqlExpression Node, Type MemberType);

public sealed record AggregateProjection(AggregateKind Kind, SqlExpression Node, Type ResultType, bool Distinct) : ProjectionPlan
{
    public override bool UsesOrdering => false;
}

public enum AggregateKind { Count, LongCount, Sum, Average, Min, Max, Any, All }
