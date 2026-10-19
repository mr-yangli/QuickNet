using QuickSql.Metadata;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的 SQL 文本层（§9.4）。分页落在句尾且两子句独立可缺省 —— 与 PG 血统、MySQL 血统、
/// 虚谷那三家的 <c>LIMIT…OFFSET</c> 同一形状【文档·ClickHouse：SELECT 页的 LIMIT/OFFSET 两行
/// 与官方"if OFFSET is set without LIMIT..."那条说明】，所以这里取的是第五份"形状同、出处另立"
///（与虚谷那一句同一处境）。
///
/// <para>与已落地的几族分岔逐条：
/// <list type="bullet">
/// <item><b>恒假/恒真落布尔字面量</b>（<see cref="EmptySetPredicate"/> 取 <c>false</c>、
/// <see cref="AlwaysTruePredicate"/> 取 <c>true</c>）【文档·ClickHouse：布尔数据类型页 】——
/// 与 PG 血统那五颗叶子同形而不同因：那一家落的是"布尔类型的那一档"，本家是"Bool 是本家的原生型"
///（<see cref="ClickHouseTypes"/> 把 CLR bool 落 Bool）。<b>调用点数与那几族同一格</b>：
/// 恒假那颗有消费（空集合 <c>IN ()</c> 特化），恒真那颗 <c>src/</c> 里零消费。</item>
/// <item><b>布尔谓词落 <c>= true</c>/<c>= false</c></b>（<see cref="EmitBooleanEquality"/>）：
/// 与 PG 血统的 <c>IS TRUE</c>/<c>IS FALSE</c> 同格不同形。可空 Bool 列上 <c>= false</c> 匹配不到 NULL
/// 这一句仍然照 PG 那句原话成立（三值逻辑不是本家的特性），所以 <see cref="ClickHouseConcurrency"/>
/// 那一颗的注记照抄逻辑不改。</item>
/// <item><b>UPDATE/DELETE 整格拒发</b>（<see cref="Emit(in UpdateStatement, SqlWriter)"/> 与
/// <see cref="Emit(in DeleteStatement, SqlWriter)"/> 抛）：本家的 UPDATE 官方形状是
/// <c>ALTER TABLE … UPDATE … WHERE</c> 变更档（异步、后台 mutation）【文档·ClickHouse：alter 页】，
/// 不是"一次往返改完"的写路径语句；DELETE 官方给了 lightweight DELETE（<c>DELETE FROM t WHERE …</c>，
/// <see href="https://clickhouse.com/docs/sql-reference/statements/delete"/>）但同属变异那一档。
/// 所以支持两格在方法上整格抛 —— 宁可当场红，也不发一条"跑完要等 mutation 队列清完"的语句。</item>
/// <item><b>支持合并档的开关也顺势落 false</b>（<see cref="SupportsJoinedWrites"/>）：
/// 写语句整格弃，那颗合并档开关跟着落 false，读侧 EntityTracker 走那颗开关退回一句一实体那一支
///（§12.5），而 <see cref="EmitJoinedSources"/> 不覆写 —— 合并那一档走到也到不了写语句那一步。</item>
/// </list>
/// <see cref="EmitReturning"/> 转交 <see cref="ClickHouseKeys.EmitReturning"/>（那里抛），与
/// GBase 8s 那一份同一形状。</para>
///
/// <para><b>本类产出的每一条文本都没有在一台活库上跑过，而且本机没有任何一台可以跑</b>。
/// 逐格分岔与限制记 §13.5 与 §14 —— 这一格在本笔是起点不是结论：它不等于"改判"，等的是第一台引擎。</para></summary>
public sealed class ClickHouseDialect(ClickHouseTypes types, ClickHouseKeys keys, ClickHouseValues values, ClickHouseExpressions expressions)
    : DialectBase(IdentifierPolicy.ClickHouse, values, expressions)
{
    public override DbKind Kind { get; } = DbKind.ClickHouse;

    /// <summary>@，本笔读到的唯一一支【文档·ClickHouse：ADO.NET 客户端页的参数示例用 @&lt;name&gt; 那一形，
    /// :/&lt;name&gt; 没有"非法"的反面读数也没有"合法"的正读数，先只落 @ 那一支】。占位符三派的分布
    /// 由这一格续为：@ 五份（T-SQL、PG 血统、MySQL 血统、DuckDB 与本家）∶ : 两份（Oracle 血统、虚谷）∶
    /// ? 一份（GBase 8s）。前缀错误在带名字的那几派红在"名字对不上"，那类错可以靠测试在文本层抓住。</summary>
    public override char ParameterPrefix => '@';

    public override string GetStoreType(ColumnContract column) => types.GetStoreType(column);

    /// <summary>合并档开关跟着写格落 false：UPDATE/DELETE 两整格（下面两颗 Emit 覆写）都抛，
    /// 先让读侧 EntityTracker 走"一句一实体"那一支（§12.5）；合并那一档在这颗开关 false 时
    /// 走不到默认出口，所以 <see cref="EmitJoinedSources"/> 不覆写。</summary>
    public override bool SupportsJoinedWrites => false;

    /// <summary>恒假/恒真都写成布尔字面量【文档·ClickHouse：布尔数据类型页， true/false 两颗字面量在本家原生】。
    /// 调用点数与 PG 血统那五颗同一格：恒假那颗在共用层有消费（空集合 <c>IN ()</c> 特化），
    /// 恒真那颗 <c>src/</c> 零消费（接口注释里那句"合并语句的守卫"没有接）。缺口与另几族同行记 §14。</summary>
    public override string EmptySetPredicate => "false";

    public override string AlwaysTruePredicate => "true";

    /// <summary><c> = true</c>/<c> = false</c>：布尔是本家的原生型，与列那一侧 <c>Bool</c> 同边 ——
    /// <c>Boolean_Type</c> 能力位带着的那一件事走到谓词侧的落点。
    /// 可空 Bool 列上 <c>= false</c> 匹配不到 NULL 这一句照 PG 血统原话成立（三值逻辑不是本家的特性），
    /// <see cref="ClickHouseConcurrency"/> 那一格的判据与另几族同文。</summary>
    protected override void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " = true" : " = false");

    /// <summary>本家没有句首限定词那一档：SELECT 页的 LIMIT/OFFSET 都在句尾。</summary>
    protected override void EmitPaging(SqlWriter writer, SelectStatement select)
    {
        if (select.Limit is { } limit)
        {
            writer.Append(" LIMIT ");

            EmitExpression(limit, writer);
        }

        if (select.Offset is { } offset)
        {
            writer.Append(" OFFSET ");

            EmitExpression(offset, writer);
        }
    }

    /// <summary>转交：<see cref="ClickHouseKeys.EmitReturning"/> 在那里抛（写入那一路没有 RETURNING/OUTPUT
    /// 任何一支【文档没逐页回读】）。与 GBase 8s 同一立场：宁可当场红，也不发一条"看着像在回填"的语句。</summary>
    protected override void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning) => keys.EmitReturning(writer, returning);

    /// <summary>变异档：ClickHouse 的 UPDATE 官方形状是 <c>ALTER TABLE … UPDATE … WHERE</c>
    ///【文档·ClickHouse：alter 页，异步后台 mutation】。这一档不在本仓库的写路径形状内 ——
    /// 宁可当场红，也不发一条"跑完要等 mutation 队列清完"的语句。</summary>
    public override void Emit(in UpdateStatement update, SqlWriter writer)
        => throw new QuickSqlException(
            "ClickHouse 的 UPDATE 不在写路径上：官方形状是 ALTER TABLE … UPDATE … WHERE（后台异步 mutation），"
            + "本仓库今天不发变异语句，所以这里整格抛。");

    /// <summary>同上一格：DELETE 的官方 lightweight DELETE（<c>DELETE FROM t WHERE …</c>，
    /// https://clickhouse.com/docs/sql-reference/statements/delete）也属变异档，整格抛。</summary>
    public override void Emit(in DeleteStatement delete, SqlWriter writer)
        => throw new QuickSqlException(
            "ClickHouse 的 DELETE 走官方 lightweight DELETE（DELETE FROM t WHERE …，"
            + "https://clickhouse.com/docs/sql-reference/statements/delete），同属变异那一档，"
            + "本仓库今天不发变异语句，所以这里整格抛。");
}
