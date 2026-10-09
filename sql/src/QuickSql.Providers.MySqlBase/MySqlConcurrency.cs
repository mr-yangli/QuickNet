using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.MySqlBase;

/// <summary>MySQL 血统的乐观并发（§4）。比较式与另四家同形（<c>`Version` = @p</c>），
/// 分岔仍然在<b>谁负责让那一格变</b>，而这一家给的答案是五家里最靠近 T-SQL 的那一个：
/// <list type="bullet">
/// <item>没有 <c>rowversion</c> 那种 8 字节二进制版本戳（所以 <see cref="ConcurrencyMode.RowVersion"/> 不是它），
/// 但<b>有</b>一列由库自动改写的原生写法：<c>DATETIME(3) … ON UPDATE CURRENT_TIMESTAMP(3)</c>。
/// 那是这一家独有的一档 —— 金仓那一族要做同一件事得建 <c>BEFORE UPDATE</c> 触发器，Oracle 血统同理，
/// 而这里它是列定义上的一个子句。</item>
/// <item>所以 <see cref="ConcurrencyMode.TimestampColumn"/> 在这一家说的是"两种都可以"：库给值，或者应用给值。
/// 今天落在后一半，因为发出去的那一条 DDL 里没有那个子句，而契约上没有任何一格表示"这一列归库给"
/// （<c>StoreFillsValue</c> 今天只有 <c>SqlServerTypes</c> 会置位）。这一家要接它需要两步：
/// 类型映射按"并发标记 + DateTime"置 <c>StoreFillsValue = true</c>，CodeFirst 写那个子句。
/// <b>第一步不是免费的</b>：置了它，那一列就从 <c>INSERT</c> 的列清单里掉出去（§12.3 的"值归库给"分支），
/// 一个没打算用库维护的现场会突然插不进那一列。所以先不置，把两步一起记进 §14，
/// 触发条件是"第一次有人要求在 MySQL 上用库维护的版本列跑并发用例"。</item>
/// </list>
/// <b>今天这一档的实际后果要说清，否则就是静默的假乐观锁</b>：只写 DDL 而不给那一列赋值的话，
/// 保存永远成功而冲突永远不发生。这与金仓那一族的处境<em>同形而不同因</em>（那一家是"语法里没有这一档，
/// 只能建触发器"，这一家是"语法里有这一档而今天没接"），所以重开的成本两家差一个量级 ——
/// 这句话本身也是接第四家才看得见的，而 v0.31 的第五家把它推得更远：那一家连"语法里有"都没读到
/// （<c>XuguConcurrency</c> 第一条），于是这一格从"两家的缺法不同"变成"三家三种缺法"。</summary>
public sealed class MySqlConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另四家同一个调用点（发射之前合取进 AST 的 <c>WHERE</c>，§10.1）。
    /// NULL 那一档也由调用方先折成 <c>IS NULL</c>：<c>`col` = NULL</c> 恒为 UNKNOWN，每次保存都成假冲突。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
