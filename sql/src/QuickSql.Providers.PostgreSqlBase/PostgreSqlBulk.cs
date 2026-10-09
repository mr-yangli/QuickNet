using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using QuickSql.Providers;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>批量高速层的现状（§12.1）。<b>【本笔改判 —— 原文照抄在前，改判在后】</b>
/// 原文：「与 T-SQL 那一族同一档：只报预算，高速通道（这一家是 <c>CopyFrom</c>，不是
/// <c>SqlBulkCopy</c>）在 P2 才接，所以 <c>Bulk_Copy</c> 能力位不声明」—— 今天接上了，
/// 接的形态与原文预告一致但落点不同名：<see cref="ResolveCopyChannel"/> 交回的是
/// <see cref="PostgreSqlBulkCopyChannel"/>（<c>BeginBinaryImportAsync</c> → 二进制 COPY 的逐行推入面），
/// 能力位跟着在本档的族掩码里声明。与 T-SQL 那一档的"同档"从此坐实：两家都是反射绑定、
/// 都在第一次取通道时才找类型、都让缺成员整家退 L3 —— 差别写在该通道的抬头里（载体那一格本家不用
/// <c>DataTable</c>，装载语句就是列清单本身）。</summary>
///
/// <para><see cref="ParameterBudget"/> 取 <b>32767</b>：PG 前端/后端协议里 <c>Parse</c> 与 <c>Bind</c> 消息的
/// 参数个数是一个<b>有符号 int16</b>，这是协议上限而不是某个驱动的实测上限（驱动可能更早报错）。
/// 出处是协议文档，不是现场实测 —— 接上活库后这一格要重测，判据见 §13.5 的出处台账。
/// L1 通道不吃这颗预算（逐行推、没有参数表），它管的是退到 L3 之后的分块。</para>
///
/// <para><b>Merged 档不表态（默认 false），而且这是全仓库唯一一颗有"协议级禁止"靠山的 false</b>：
/// §9.4 点名 PG 血统一次发送里塞 <c>;</c> 分隔的多条语句在扩展协议下不合法。
/// 于是 <c>UpdateRange</c> 的 Auto 在本族落逐条，<c>DeleteRange</c> 的 IN 档不受此影响（那是一<em>条</em>语句）。
/// 与 MySQL 那颗 true 恰成对照：那一家的表态有 postRead 回填支的每轮绿为读数，本家的不表态有协议条文为读数，
/// 两边都不是猜；而"条文说禁"与"驱动会不会自己拆"是两件事，后者今天没有读数，
/// 等集成册补跑一场多语句批再判（记 §14 的本格在 <c>SupportsMergedStatements</c> 的抬头）。
/// <b>本笔集成册在活 PG 上跑的是<em>反面</em>那一半</b>：Auto 落逐条、点名 Merged 降级并说出 provider、
/// <c>FallbackPolicy.Never</c> 拒在连接之前，三格全绿而 IN 档照旧一条语句删整批
/// 【实测·集成册 <c>BulkWritePathTests</c>，<c>obj/logs/v40-postgresql-bulkwrite-2.log</c>】。
/// 那条条文因此没有被本笔推翻，也没有被本笔证实——"Npgsql 会不会自己拆一条批"那一格仍空着。</para>
public sealed class PostgreSqlBulk : IBulkStrategy
{
    readonly Assembly _driver;

    IBulkCopyChannel? _channel;

    public PostgreSqlBulk(DbProviderFactory driverFactory)
    {
        // 与 SqlServerBulk / MySqlBulk 同一条纪律：构造期零反射，只记"工厂住在哪个程序集"。
        // 这一族这句话比另两家更实：清单上 PG 血统五颗键共填 Npgsql 一颗 assembly，装载类找不找得到，
        // 全看现场装的到底是哪一支——TryResolve 的形状扫描就是那两种现场的分工处。
        _driver = driverFactory.GetType().Assembly;
    }

    public int ParameterBudget => 32_767;

    public string Describe => "L1 二进制 COPY（BeginBinaryImportAsync → NpgsqlBinaryImporter 逐行推，列清单由装载语句点名，" +
                              "白名单之外的列整批退 L3；不欠连接串开关）+ L3 多值 VALUES + L5 逐条；" +
                              "参数预算 32767（协议 int16 上限，未实测）；Merged 档不表态（扩展协议多语句为 §9.4 点名的禁止面）；" +
                              "Upsert 档已接（一句 INSERT … ON CONFLICT 主键 DO UPDATE SET … = EXCLUDED.…，本尊授权实例量过——那是一句，不欠上面那颗表态）";

    /// <remarks>惰性形状与另两家逐字同一条：无锁，命中进字段，落空每次重查。本家的落空概率结构上比
    /// 另两家高（借邻家臂的叶子装的可能是没有导入器的 fork），而落空的下一步是退文本层付 N 次往返——
    /// 为它省一次类型查找没有意义。</remarks>
    [RequiresUnreferencedCode("取通道是一次按类型名与形状的反射查找（PostgreSqlBulkCopyChannel.TryResolve），裁剪后的驱动交回 null，阶梯因此从 L3 起。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析导入器需要运行时生成代码。")]
    public IBulkCopyChannel? ResolveCopyChannel() => _channel ??= PostgreSqlBulkCopyChannel.TryResolve(_driver);

    /// <summary>一句 upsert 的句尾：<c>ON CONFLICT (键) DO UPDATE SET 列 = EXCLUDED.列</c>
    /// （2026-10-07 第二条裁决，PG 血统那六家一起领——同一颗 <c>PostgreSqlBulk</c>）。
    /// <para>形状在授权实例上量过：三值一批（其中一颗键库里没有）收下、缺的那一行插进去、已有两行被改掉
    /// 【实测·探针 <c>obj/probe-upsert</c>，<c>obj/logs/v40-upsert-shapes2.log</c>】。<b>与 Merged 档恰成对照</b>：
    /// 这一档是<em>一条</em>语句，所以它不欠那颗"多语句批收不收"的表态（<see cref="SupportsMergedStatements"/>
    /// 在本家仍是 false，§9.4 那句扩展协议的禁止面管不着它）——本家的 Auto 落逐条而点名 Upsert 拿得到，
    /// 这一格读起来别扭是正常的，两问本来就不同。</para>
    /// <para><b>冲突目标点的是主键列清单</b>：那要求库里有恰好覆盖这几列的唯一索引，而主键天然满足；
    /// 复合主键在这里装得下（<c>ON CONFLICT (a, b)</c> 是合法文本），与 <c>DeleteRange</c> 那句
    /// "IN 装不下元组"是两件事。若现场的表实际靠另一颗唯一键去重，本档不接那种调用姿势——
    /// 那一格要的是<em>指定冲突目标</em>的入口，今天没有，缺口记 §14。</para>
    /// <para>六家共一份的原因与 <c>FamilyCapabilities</c> 同一条：这一族借用的都是 PostgreSQL 的那一颗内核，
    /// 而 <c>ON CONFLICT … DO UPDATE</c> 是 9.5 起的语法【文档】，金仓与国产那几支各自的内核版本下限
    /// 今天没有一台活库可量（本血统五颗叶子里只有 PG 本尊有授权实例）。</para></summary>
    public string? UpsertClause(UpsertColumns columns)
    {
        var builder = new StringBuilder("ON CONFLICT (");

        for (var key = 0; key < columns.Keys.Count; key++)
        {
            if (key > 0)
                builder.Append(", ");

            builder.Append(columns.Keys[key]);
        }

        builder.Append(") DO UPDATE SET ");

        for (var column = 0; column < columns.Settable.Count; column++)
        {
            if (column > 0)
                builder.Append(", ");

            builder.Append(columns.Settable[column]).Append(" = EXCLUDED.").Append(columns.Settable[column]);
        }

        return builder.ToString();
    }
}
