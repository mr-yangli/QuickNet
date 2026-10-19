using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的变更守卫（§12.4 第 2 项）。<b>这一家是三段式的又一个调用点，而"为什么"是第四种</b>：
/// T-SQL 与 Oracle 血统来自"布尔表达式不是一等值"，MySQL 血统来自"一句的候选要按版本发而 SPI 没有版本那一格"，
/// GBase 8s 来自"没有 IS DISTINCT FROM 那一格的候选"，本家是<b>写语句本身整格弃</b>
///（<see cref="ClickHouseDialect.Emit(in UpdateStatement, SqlWriter)"/> 抛）：
/// 合并语句退回那一步今天根本走不到写侧 —— 守卫的形状落在逐条路上，而逐条也不该走到守卫
///（每条一条、无行集可比）。所以这里的形状选择只服务一条：守住"如果哪天写格被接上，守卫必须已经是
/// 对的形状"。</para>
///
/// <para><see cref="Compares"/> 恒真：本家发得出型名的列全都可比 —— String 等值可比、数值各档可比，
/// 大对象那一档在类型侧就抛了（<see cref="ClickHouseTypes"/> 的 byte[] 臂），所以没有"问不出答案"的那一档。
/// <b>这与另几族不同</b>：其余关三颗大对象的那几家是"有那一型而不敢比"；本家不可比的那一型在类型表
/// 已经上不了场，于是这一格不需要名单。</para></summary>
public sealed class ClickHouseChangeGuard : IChangeGuard
{
    /// <summary>本家没有"不可比的那一型"在场（byte[] 在类型侧抛、String 原生可比），所以恒真。</summary>
    public bool Compares(ColumnContract column) => true;

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走
        //（与 T-SQL、Oracle 血统、MySQL 血统、虚谷、GBase 8s 五族逐字同判）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }
}
