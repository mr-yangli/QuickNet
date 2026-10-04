namespace FastSql.Sql.Ast;

public abstract record AstNode;

// ── 表源 ──────────────────────────────────────────────────────────────

/// <summary>FROM 序列中的一个表位。别名默认由 <see cref="Index"/> 生成（§5.1：形参名不参与解析）。</summary>
public sealed record TableRef(int Index, string Alias, Metadata.TableInfo Table, Metadata.EntityContract? Contract = null)
{
    public bool IsRoot => Index == 0;
}

public abstract record TableSource(TableRef Ref) : AstNode;

public sealed record SourceTable(TableRef Ref) : TableSource(Ref);

public sealed record SourceSubquery(TableRef Ref, SelectStatement Query) : TableSource(Ref);

/// <summary>派生表（§5.12）：外层那句由库规划，<see cref="Query"/> 是内层的整份 <see cref="SelectStatement"/>。
/// 它与 <see cref="SourceSubquery"/> 的分别是<em>谁写的内层</em>：那一份的内层来自表达式里的相关子查询
/// （WHERE / IN / EXISTS），这一份的内层来自同一个模型的另一份计划，于是"分组之后数有几组"
/// 与"分页之后那一段共有几行"第一次有了能发的句子。<b>只有一个内层，没有清单</b> ——
/// 一句 SELECT 的 FROM 位今天只承接一个源。</summary>
public sealed record SourceDerived(TableRef Ref, SelectStatement Query) : TableSource(Ref);

/// <summary>FROM 位换成一整段<b>用户自己写的</b> SELECT 文本（<c>WithSql</c>，规格 §11 的表源那一格）。
/// 与 <see cref="RawExpression"/> 同一前提：文本一颗不动，只在外面包一层括号再挂别名，
/// 因为那一列叫什么、按什么顺序，只有 SQL 作者知道。<see cref="Parameters"/> 是已经按本家前缀造好名的那一串，
/// 发射时逐颗登记进书写器 —— 名字活在文本里，所以登记而不落地名字。</summary>
public sealed record SourceRawText(TableRef Ref, string Sql, IReadOnlyList<AdoParameter> Parameters) : TableSource(Ref);

public sealed record SourceFunction(TableRef Ref, string Name, IReadOnlyList<SqlExpression> Arguments) : TableSource(Ref);

/// <summary>一个字面行集：<c>(VALUES (@p0,@p1),(@p2,@p3)) AS [t1] ([Id],[Name])</c>。
/// 它是 §12.5 合并语句的另一侧 —— 一批"键 + 要写的值"作为一个表参与连接，于是 N 只实体合成一条语句。
/// <see cref="Columns"/> 是这一侧的列名清单，<see cref="Rows"/> 的每一行都按它的顺序排列，
/// 两处必须同序：列清单写歪一格，值就整体串列，而串列的 UPDATE 每家库都执行得动。</summary>
public sealed record SourceRows(TableRef Ref, IReadOnlyList<Metadata.ColumnContract> Columns, IReadOnlyList<IReadOnlyList<SqlExpression>> Rows) : TableSource(Ref);

public enum JoinKind { Inner, Left, Right, Full, Cross }

public sealed record JoinClause(JoinKind Kind, TableSource Source, SqlExpression? On) : AstNode;

// ── 表达式 ────────────────────────────────────────────────────────────

public abstract record SqlExpression : AstNode;

public sealed record ColumnExpression(TableRef Table, Metadata.ColumnContract Column) : SqlExpression;

/// <summary>已经决定要参数化的值。<see cref="LiteralExpression"/> 与它的区别是「进不进参数集合」，
/// 由 <see cref="Values.IValueBinder"/> 单点决定，不由调用方现场判断。</summary>
public sealed record ParameterValue(object? Value, Metadata.ColumnContract? Hint) : SqlExpression;

public sealed record InlineLiteral(string Sql) : SqlExpression;

public sealed record SqlBinaryExpression(BinaryOperator Operator, SqlExpression Left, SqlExpression Right) : SqlExpression;

public sealed record SqlUnaryExpression(UnaryOperator Operator, SqlExpression Operand) : SqlExpression;

/// <summary>函数节点。<see cref="Template"/> 是 provider 已经解析过的 SQL 模板（<c>DATEADD(day, {0}, {1})</c>），
/// 实参按 <c>{i}</c> 填入。C# 方法名到模板的映射在 <c>IExpressionRules</c> 完成，方言层不再认 CLR 方法名（§4）。</summary>
public sealed record FunctionExpression(string Template, IReadOnlyList<SqlExpression> Arguments) : SqlExpression;

public sealed record CastExpression(SqlExpression Operand, string StoreType) : SqlExpression;

/// <summary>IN 表达式：目标值与一组常量值（<see cref="Values"/> 非空）或一条子查询（<see cref="Subquery"/> 非空）二选一。
/// 子查询形态产出 <c>target IN (SELECT ...)</c>，不需要额外的括号包裹——子查询的 <c>SelectStatement</c>
/// 由方言递归发射，自带圆括号。</summary>
public sealed record InExpression(
    SqlExpression Target,
    IReadOnlyList<SqlExpression>? Values = null,
    bool Negated = false,
    SelectStatement? Subquery = null) : SqlExpression;

public sealed record ExistsExpression(SelectStatement Query, bool Negated = false) : SqlExpression;

public sealed record ScalarSubqueryExpression(SelectStatement Query) : SqlExpression;

public sealed record CaseExpression(SqlExpression? Operand, IReadOnlyList<WhenClause> Whens, SqlExpression? Else) : SqlExpression;

public sealed record WhenClause(SqlExpression When, SqlExpression Then);

/// <summary>逃生舱（§11）：原样落地一段 SQL 片段。出现在黄金样本里时必须能被肉眼定位，
/// 因此 <see cref="Why"/> 是必填的——没有理由的 raw SQL 评审不过。</summary>
public sealed record RawExpression(string Sql, string Why) : SqlExpression;

public sealed record StarExpression(TableRef? Table) : SqlExpression;

public enum BinaryOperator
{
    Add, Subtract, Multiply, Divide, Modulo,
    Equal, NotEqual, LessThan, LessThanOrEqual, GreaterThan, GreaterThanOrEqual,
    And, Or, Like, BitwiseAnd, BitwiseOr,

    /// <summary>「不同则真，且两侧同空也算相等」。只有 PG 系与金仓有这一型（§12.4 第 2 项的变更守卫），
    /// 所以它排在最后：前面的成员顺序不动，谁按序号存过什么都不受影响。</summary>
    IsDistinctFrom,
}

public enum UnaryOperator { Not, Negate, IsNull, IsNotNull, IsTrue, IsFalse }

// ── 语句 ──────────────────────────────────────────────────────────────

public sealed record Projection(SqlExpression Node, string? Alias) : AstNode;

public sealed record OrderExpression(SqlExpression Node, bool Descending) : AstNode;

public sealed record SelectStatement
{
    public required IReadOnlyList<Projection> Columns { get; init; }

    public TableSource? From { get; init; }

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];

    public SqlExpression? Where { get; init; }

    public IReadOnlyList<SqlExpression> GroupBy { get; init; } = [];

    public SqlExpression? Having { get; init; }

    public IReadOnlyList<OrderExpression> OrderBy { get; init; } = [];

    public SqlExpression? Offset { get; init; }

    public SqlExpression? Limit { get; init; }

    public bool Distinct { get; init; }

    /// <summary>分页必须有确定序（§5.12）。方言层按能力位决定是补 <c>ORDER BY (SELECT NULL)</c> 占位、
    /// 还是像 SQL Server / 金仓那样直接报错。</summary>
    public bool RequireOrderingForPaging { get; init; } = true;
}

public sealed record InsertStatement
{
    public required TableRef Table { get; init; }

    public required IReadOnlyList<Metadata.ColumnContract> Columns { get; init; }

    /// <summary>行 × 列。null 表示该位置走数据库默认值。</summary>
    public required IReadOnlyList<IReadOnlyList<SqlExpression?>> Rows { get; init; }

    public IReadOnlyList<Metadata.ColumnContract> Returning { get; init; } = [];

    /// <summary>L3 多值语句的参数预算已在此算好（§12.3），方言只负责落地文本。</summary>
    public bool ExplicitColumnList { get; init; } = true;
}

public sealed record UpdateStatement
{
    public required TableRef Table { get; init; }

    public required IReadOnlyList<SetClause> Sets { get; init; }

    public SqlExpression? Where { get; init; }

    public IReadOnlyList<Metadata.ColumnContract> Returning { get; init; } = [];

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];
}

public sealed record SetClause(Metadata.ColumnContract Column, SqlExpression Value) : AstNode;

public sealed record DeleteStatement
{
    public required TableRef Table { get; init; }

    public SqlExpression? Where { get; init; }

    public IReadOnlyList<JoinClause> Joins { get; init; } = [];
}
