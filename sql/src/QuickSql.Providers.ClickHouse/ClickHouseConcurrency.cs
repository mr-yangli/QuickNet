using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的乐观并发（§4）。比较式与另几族逐字同形（<c>"Version" = @p</c>），
/// 分岔只在<b>谁负责让那一格变</b>，而这一家落的是"应用给值"那一侧：
///
/// <para><b>没有 rowversion 那一档</b>【文档没逐页回读】：这一族读到的类型清单里没有"由库自动递增的
/// 版本戳"那一型，也没有 MySQL 血统那档列定义上的自动改写子句，所以
/// <see cref="ConcurrencyMode.TimestampColumn"/> 在本家说的是<b>只有一种可能</b>：版本列由应用自己写新值。
/// 与 <c>Gbase8sConcurrency</c> 同果而不同因——那一家连"列定义能挂什么"的清单都没读到，本家读到了
/// DEFAULT/MATERIALIZED/ALIAS 那几幕而其中没有"每次 UPDATE 自动重写"那一幕。</para>
///
/// <para>精度那半格照 GBase 8s 那一条抄不改：版本列若走 <see cref="ClickHouseTypes.GetStoreType"/> 的
/// DateTime 臂落 <c>DateTime64(3)</c>（毫秒），CLR 侧刻度 100 纳秒 —— 两次保存落在同一毫秒内时
/// <c>=</c> 成立、冲突被判成"没发生"。契约里没有"版本列精度"那一格（缺口记 §14），
/// 本家把这一格首写进一个 provider 注释的位置与 GBase 8s 那一句并排。</para></summary>
public sealed class ClickHouseConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另几族同一个调用点（发射之前合取进 AST 的 WHERE，§10.1）。空值那一档由调用方先折成
    /// <c>IS NULL</c> 再下来：<c>"col" = @p</c> 在 NULL 上恒为 UNKNOWN，每次保存都会成一次假冲突 ——
    /// 这一条本家与另几族同形，而它（三值逻辑）不是厂商特性。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
