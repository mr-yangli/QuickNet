using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.OracleBase;

/// <summary>Oracle 血统的乐观并发（§4，第三家）。比较式与两族同形（<c>"Version" = :p</c>），
/// 分岔在<b>这一家"库自己会换的那一列"长成了一个不能用的形状</b>：
/// <b>那句"两族"是 v0.23 的口径</b>：到 v0.31 比较式这一格是<b>五家同形</b> —— 重载里那一颗
/// <c>SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column))</c>
/// 在 <c>grep -rn</c> 下五个文件各 1 处命中，同的是<em>C# 那一行</em>而不是 SQL 文本
/// （占位符前缀各家不同，见 <c>OracleDialect.ParameterPrefix</c> 那一条的 2 ∶ 3）。
/// 真正分岔的是 <see cref="ConcurrencyMode"/>：五家 <c>4 家 TimestampColumn ∶ 1 家 RowVersion</c>，
/// 而四家里"库为什么不维护那一列"又有三种缺法（金仓与这一族同条：语法里没有这一档、只能建触发器；
/// MySQL 血统是"语法里有而今天没接"；v0.31 的第五家是"连语法都没读到"）—— 逐家写在
/// <c>MySqlConcurrency</c> 末段与 <c>XuguConcurrency</c> 第一条，这里不抄第三遍。
/// <list type="bullet">
/// <item>没有 <c>rowversion</c> 那种"每写一次自动递增、且能被 SELECT 回来"的列类型。</item>
/// <item>它有一个看起来像的东西 —— <c>ORA_ROWSCN</c> 伪列：每行的提交序号。但它<em>不是列</em>：
/// 不在 <c>ALL_TAB_COLUMNS</c> 里（<see cref="OracleSchemaReader"/> 读不到它）、不能建表时声明、
/// 默认按块共享（要 <c>INITTRANS 100</c> 才逐行），而且是一个 SCN 数而不是一个可映射的 CLR 槽位。
/// 所以 §4 的 <see cref="ConcurrencyMode"/> 里没有它的位置，这里也不假装它是 <c>RowVersion</c>。</item>
/// <item>于是这一档落到 <see cref="ConcurrencyMode.TimestampColumn"/>：<b>"那一列的值归应用或触发器维护"</b>。
/// 与金仓那一族同一条警告 —— 不配 <c>BEFORE UPDATE</c> 触发器也不在应用侧赋值时，保存永远成功而冲突永远不发生，
/// 那是静默的假乐观锁。触发器生成还没接线，缺口与重开条件记在 §14（与金仓那一格同一条，两族共用一个重开条件）。
/// <b>"两族"到 v0.31 是四族共一条</b>：金仓、这一族、MySQL 血统、虚谷四家都落在 <c>TimestampColumn</c> 上
/// （<c>grep -rn "public ConcurrencyMode Mode" src/ | grep -v "///" | wc -l</c> 是 5，其中四行的右值就是这一颗；
/// 那条 <c>grep -v</c> 是必要的 —— 少了它这一句自己的注释也会被数进去，五处变六处），
/// 而四家里<b>只有这两族</b>共用"触发器生成"那一条重开条件 —— 另两家各有各的出口，所以那句"共用一个"
/// 不能顺手涨成"四家共用一个"。</item>
/// </list>
/// <b>这一族多出来的一档风险</b>：既然库不维护那一列，实体上它多半是一个 <c>int</c> 或 <c>DateTime</c>，
/// 而 <c>DateTime</c> 那一档在两家之间会有精度问题（§9.3 记的 <c>TIMESTAMP(3)</c> 与 .NET 的 7 位小数秒），
/// 用时钟当版本号的项目在 SQL Server 上不一定出事，在这一族截到毫秒之后就<em>一定</em>会撞
/// —— 同一次保存里两行拿到同一个 <c>DateTime.Now</c> 是常态。这条不在这里修，写在 §14 那一格里。</summary>
public sealed class OracleConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与两族同一个调用点（发射之前合取进 AST 的 <c>WHERE</c>，§10.1）—— 那句"两族"是 v0.23 的口径，
    /// 今天走这一个调用点的是另四家。
    /// NULL 那一档由调用方先折成 <c>IS NULL</c>：<c>"col" = NULL</c> 在这一家恒为 UNKNOWN（与三值逻辑同一条规则，
    /// 虽然这一家的空串就是 NULL，那颗 NULL 也仍然比不出真），每次保存都会成假冲突。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
