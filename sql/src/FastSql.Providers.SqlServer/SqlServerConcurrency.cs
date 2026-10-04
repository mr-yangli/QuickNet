using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql.Ast;

namespace FastSql.Providers.SqlServer;

/// <summary><c>rowversion</c> 列的乐观锁谓词（§4）。SQL Server 的 <c>rowversion</c> 是 8 字节二进制、
/// 由库自动递增，所以比较形态是 <c>[Version] = @p</c>；不要拿 <c>@@DBTS</c> 代替，那是数据库级的、不是行级的。</summary>
public sealed class SqlServerConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.RowVersion;

    /// <summary>比较式合取进 AST 的 <c>WHERE</c>，由 <c>EntityTracker.WhereWithTokens</c> 在发射之前调用；
    /// 文本级的改写钩子排在这之后（顺序与理由见 <c>IConcurrencyStrategy.BuildPredicate</c> 那一格与 §10.1）。
    /// 空值那一档走不到这里：<c>[列] = @p</c> 在 NULL 上是 UNKNOWN，每次保存都会成假冲突，所以调用方把它折成
    /// <c>IS NULL</c> 之后再下来。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
