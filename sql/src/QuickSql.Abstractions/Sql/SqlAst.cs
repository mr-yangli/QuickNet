namespace QuickSql.Sql.Ast;

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

/// <summary>分表读侧的形状（§16.4）：<b>一片一条完整的 SELECT，用 <c>UNION ALL</c> 串起来，整串占一个表位</b>。
/// 与 <see cref="SourceDerived"/> 的分别是内层的<em>份数</em>——那一份永远是一个，这一份按选片范围有 N 个。
/// <see cref="Branches"/> 里每条都不许带 ORDER BY 与分页：那些只长在外层，否则
/// 「取前 10 行」会变成「每片各取 10 行」，而两家的语法都执行得动（SqlSugar 的 <c>Clone()</c>
/// 把 <c>Take/Skip/OrderBy</c> 原样复制进每一片，正是这一格的反面教材）。
/// 别名沿用根表位那一个（<c>t0</c>），所以外层的谓词与选列不必重写。
/// <para><see cref="All"/> 是 2026-10-30 添的：链式合并那一口（<c>UnionAll</c>/<c>Union</c>）的
/// 第二档要的是<b>整句去重</b>的 <c>UNION</c>，分表那一档照旧 <c>UNION ALL</c>。两个关键字在任何一家
/// 都是句子级的东西，没有方言分岔，所以它进形状（AST）而不进方言。</summary>
public sealed record SourceUnion(TableRef Ref, IReadOnlyList<SelectStatement> Branches, bool All = true) : TableSource(Ref);

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

/// <summary>行号（<c>ROW_NUMBER</c>）：OVER 里那两段是句子的形状而不是模板的填充料，所以它不是
/// 一颗 <see cref="FunctionExpression"/>——模板走 <c>string.Format</c>，而 <c>PARTITION BY</c> 与
/// <c>ORDER BY</c> 各是一段带关键字的清单（清单可空、方向逐颗），填进 <c>{i}</c> 要把语法塞进 provider 的模板里。
///
/// <para><b>它与方言的关系与日期三口同档</b>：共用层出文本，十八家只出自己的引号（列名走
/// <c>EmitExpression</c> 的既有步法），一张模板都不登记。<see cref="PartitionBy"/> 可空 =
/// 整集一序号；<see cref="OrderBy"/> <b>不可空</b>——SQL Server 那一族要求 OVER 里至少一颗序，
/// 而"没有序的行号"在任何一家都不是一个能回答的问题，所以这一格由翻译器保证有序，方言层不必再判。
/// 两颗清单里的列引用都归<em>外层那一句</em>的表位，本节点不引入新的 FROM 位。</para></summary>
public sealed record RowNumberExpression(IReadOnlyList<SqlExpression> PartitionBy, IReadOnlyList<OrderExpression> OrderBy) : SqlExpression;

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

/// <summary>整语句 upsert 的 AST（<c>InsertOrUpdate</c> 链在无句尾槽的那一家发的东西）。它是一颗<b>形态中立</b>
/// 的行集载体：两种真实写法都从它出——SQLite 的 <c>INSERT … ON CONFLICT (键) DO UPDATE SET 列 = excluded.列</c>
/// 与 <c>MERGE INTO</c> 那一族（SqlServer ∶ Oracle 血统 ∶ 虚谷 ∶ Gbase8s）。
///
/// <para><b>它与 <see cref="InsertStatement"/> 的分别是走的门不同，不是内容</b>：句尾那一族（MySQL ∶ PG 血统）
/// 有 <see cref="Providers.IBulkStrategy.UpsertClause"/> 那颗槽，内核照旧发一颗 <see cref="InsertStatement"/>
/// 再接一段尾巴——那条路 <c>UpdateRange(...).WithBulk(Upsert)</c> 已经跑绿过，本档原样复用、一处不动；而
/// <b>没有那颗槽的一家</b>（典型是整颗 <c>Bulk</c> 交回 null 的 SQLite——它接不出句尾不是因为不支 upsert，
/// 而是 §12.3 那句"没有可举证的参数预算 ⇒ Bulk 交回 null"把它连槽一起挡在外面）只能把整个发射交给方言，
/// 由它发这一颗 <see cref="UpsertStatement"/>。于是这颗 AST 带上了句尾那一族不需要在 AST 上带的分工
/// （<see cref="Keys"/> 与 <see cref="Settable"/>），因为发它的那一方拿不到内核替它拼好的列尺子。</para>
///
/// <para><b>这颗 AST 与那颗 <c>UpsertClause</c> 槽是同一个意思的两面</b>（列清单 = 可写 ++ 键、键进冲突判定不进
/// 写入那一支），只是载体不同：槽交的是"给 <c>INSERT</c> 补一句尾巴"的字符串，这颗交的是"发一整句"的 AST。
/// 未来若把 <c>UpsertClause</c> 从 <see cref="Providers.IBulkStrategy"/> 挪进方言层（它本质是语法不是批量档位），
/// 两家能并成一条出路；那一挪要重接已经跑绿的 <c>UpdateRange</c> 档，不记在这一颗里，见 §14。</para></summary>
public sealed record UpsertStatement
{
    /// <summary>目标表，也是"没命中就插"那一支插进去的表。</summary>
    public required TableRef Table { get; init; }

    /// <summary>写入清单的顺序 = <see cref="Settable"/> ++ <see cref="Keys"/>，与句尾族那条
    /// <c>INSERT … VALUES</c> 逐位同序），<see cref="Rows"/> 的每一行按它排列。</summary>
    public required IReadOnlyList<Metadata.ColumnContract> Columns { get; init; }

    /// <summary>行 × 列。每格都是参数（这一版不产 <c>DEFAULT</c> 格，与 <see cref="InsertStatement"/> 同一口径）。</summary>
    public required IReadOnlyList<IReadOnlyList<SqlExpression?>> Rows { get; init; }

    /// <summary>冲突判定的键（<c>ON 目标.键 = 行集.键</c> ∶ <c>ON CONFLICT (键)</c>）：顺序同契约，复合键装得下
    /// （多条件合取与 <c>(a, b)</c> 目标各是合法文本，与 <c>DeleteRange</c> 那句"IN 装不下元组"是两件事）。</summary>
    public required IReadOnlyList<Metadata.ColumnContract> Keys { get; init; }

    /// <summary>"命中就改"那一支改写的列 = 除键、库里给值的列与并发列以外的全部列，与句尾族的
    /// <see cref="Providers.UpsertColumns.Settable"/> 同一把尺。</summary>
    public required IReadOnlyList<Metadata.ColumnContract> Settable { get; init; }
}
