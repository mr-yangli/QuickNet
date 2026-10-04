using FastSql.Providers;

namespace FastSql.Providers.SqlServer;

/// <summary>批量高速层的现状（§12.1）。P0 只交付 L3 多值语句与 L5 逐条，所以这里只报预算：
/// <b>分块按参数个数算而不是按行数</b>（§12.3），500 行 × 30 列在 2100 上限下是 5 块而不是 1 块。
/// <c>SqlBulkCopy</c>（L1）与 <c>MERGE</c>（UPDATE L1）在 P2 接入，届时 <see cref="Describe"/> 要跟着改。</summary>
public sealed class SqlServerBulk : IBulkStrategy
{
    public int ParameterBudget => 2100;

    public string Describe => "L3 多值 VALUES + L5 逐条；参数预算 2100（§12.3）；SqlBulkCopy/MERGE 待 P2";
}
