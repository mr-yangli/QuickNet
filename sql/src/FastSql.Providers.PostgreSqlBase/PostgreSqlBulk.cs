using FastSql.Providers;

namespace FastSql.Providers.PostgreSqlBase;

/// <summary>批量高速层的现状（§12.1）。与 T-SQL 那一族同一档：只报预算，高速通道（这一家是
/// <c>CopyFrom</c>，不是 <c>SqlBulkCopy</c>）在 P2 才接，所以 <c>Bulk_Copy</c> 能力位不声明。
///
/// <see cref="ParameterBudget"/> 取 <b>32767</b>：PG 前端/后端协议里 <c>Parse</c> 与 <c>Bind</c> 消息的
/// 参数个数是一个<b>有符号 int16</b>，这是协议上限而不是某个驱动的实测上限（驱动可能更早报错）。
/// 出处是协议文档，不是现场实测 —— 接上活库后这一格要重测，判据见 §13.5 的出处台账。</summary>
public sealed class PostgreSqlBulk : IBulkStrategy
{
    public int ParameterBudget => 32767;

    public string Describe => "L3 多值 VALUES + L5 逐条；参数预算 32767（协议 int16 上限，未实测）；CopyFrom 待 P2";
}
