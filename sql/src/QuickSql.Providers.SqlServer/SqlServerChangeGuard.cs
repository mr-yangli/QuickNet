using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.SqlServer;

/// <summary>T-SQL 的变更守卫（§12.4 第 2 项）。合并语句靠它把"这一行的新值库里已经有了"的行挡在
/// <c>UPDATE</c> 之外，否则一条 <c>UPDATE ... FROM (VALUES)</c> 的 <c>@@ROWCOUNT</c> 是命中数而不是变化数，
/// 审计列会跟着全表刷新。
///
/// <b>为什么不是 <c>IS DISTINCT FROM</c></b>：T-SQL 没有这一型（金仓那一族有；MySQL 血统 8.0.29 起也有，
/// 但它要按版本发而 SPI 里没有"库的版本"那一格，所以那一家仍然取三段 —— 见 <c>MySqlChangeGuard</c>）。
///
/// <b>为什么也不能是 §12.4 原文给的那个近似式</b>：<c>(a IS NULL) &lt;&gt; (s.a IS NULL)</c> 在 T-SQL 里
/// 连语法都过不了 —— 布尔表达式在那个库不是一等值，不能出现在 <c>&lt;&gt;</c> 的一侧
/// （真库那条用例钉住了这个报错，所以这句不是推理）。于是可空列展开成三段 <c>OR</c>：不等、只有表侧空、
/// 只有行集侧空。那三段的<b>形状现在住在 <see cref="ChangeGuardShapes"/></b>（v0.23 提出去：第三家撞上是
/// 同一条限制，而"提出去"这个动作本身就是那一版的结论 —— 一个方言族接出来之后多数派换边了）。
/// 这一家留的是"要不要用那三段"的判断，因为它才是库种知识。
///
/// <b>不可比的那几型返回 <see langword="false"/>，而不是造一个 <c>CHECKSUM</c> 近似</b>：哈希会撞，
/// 撞上的那一行被判成「没变」，于是它的新值被静默丢掉 —— 那是丢写，比不合并严重一个量级。
/// 调用方拿到 false 要把这一批退回逐条（<c>EntityTracker</c> 的那一段）。</summary>
public sealed class SqlServerChangeGuard : IChangeGuard
{
    /// <summary>连等号都不接受的存储类型（LOB 三兄弟 + <c>xml</c> + 两套空间型 + <c>sql_variant</c>）。
    /// 名字取 <c>(</c> 之前的那一段，所以 <c>XML</c> 与 <c>xml</c>、带长度的 <c>VARCHAR(n)</c> 与不带的都能命中。
    /// <b><c>rowversion</c> 不在这一列里</b>：它名义上是 <c>binary(8)</c>，<c>WHERE [Version] = @p</c>
    /// 在两个授权实例上都被 v0.18 的并发用例跑通过，把它算成不可比就会让带并发列的表整批退回逐条。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "text", "ntext", "image", "xml", "geography", "geometry", "sql_variant" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        var different = new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引。
        if (!column.IsNullable)
            return different;

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 Hint：后者只有 <c>ApplyStorageDefaults</c> 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完。
        var name = SqlServerTypes.Instance.GetStoreType(column);
        var paren = name.IndexOf('(');

        return (paren < 0 ? name : name[..paren]).Trim();
    }
}
