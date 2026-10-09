using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>PG 系的乐观并发（§4）。比较式与 T-SQL 同形（<c>"Version" = @p</c>），
/// 分岔在<b>谁负责让那一格变</b>：这一家没有 <c>rowversion</c> 那种由库自动递增的类型，
/// 所以 <see cref="ConcurrencyMode.TimestampColumn"/> 说的是"应用或触发器维护的一列"，
/// 而不是"库自己会换的那 8 个字节"。
///
/// <b>这是本方言族唯一一处"能建、能比，但库不会替你维护"的能力</b>：CodeFirst 建出的那一列
/// 若不配一个 <c>BEFORE UPDATE</c> 触发器或应用侧赋值，保存永远成功而冲突永远不发生 ——
/// 静默的假乐观锁。触发器生成还没接线，缺口与重开条件记在 §14。
///
/// <b>那句"唯一"从下一版起就不再成立</b>：v0.23 的第三家、v0.30 的第四家、v0.31 的第五家各顶一次，
/// 今天"能建、能比、库不替你维护"是<b>四家</b>的处境（五家各声明一次 <see cref="ConcurrencyMode"/>，
/// 其中四处给 <c>TimestampColumn</c>，只有 T-SQL 那一处是库给值），而"库为什么不维护"在三条不同的路上：
/// 金仓与 Oracle 血统同条（语法里没有那一档，只能建触发器）、MySQL 血统是"语法里有
/// <c>ON UPDATE CURRENT_TIMESTAMP</c> 而今天没接"、v0.31 的第五家连"语法里有"都没读到。
/// 所以这一格从 v0.22 的"一家 vs 一家"走到了"四家 vs 一家"，而四家共用的只是 §14 那一格缺口，
/// <em>不</em>共用重开成本。逐家见 <c>OracleConcurrency</c> 头一段、<c>MySqlConcurrency</c> 第一条与
/// <c>XuguConcurrency</c> 第一条。</summary>
public sealed class PostgreSqlConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与 T-SQL 同一个调用点（发射之前合取进 AST 的 <c>WHERE</c>，§10.1）。
    /// NULL 那一档也由调用方先折成 <c>IS NULL</c>：<c>"col" = NULL</c> 恒为 UNKNOWN，每次保存都成假冲突。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
