using System.Data.Common;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.Highgo;

/// <summary>瀚高 HighGo 的驱动槽位（§9.1 行 907 的兑现，PG 血统的第四颗叶子 ——
/// 金仓、openGauss、GaussDB 之后，<c>PostgreSqlBase</c> 今天一基类四叶子）。
///
/// <para><b>零覆写</b>：产出自本类的每一颗字节都出自 <c>PostgreSqlProviderBase</c> 那一份文本层，
/// 与 <c>KingbaseProvider</c>、<c>OpenGaussProvider</c>、<c>GaussDbProvider</c> 三层壳逐字同形 ——
/// 见证在 <c>HighgoDialectTests</c> 的五路逐字相等链（leaf == 另三叶 == 血统），而本册另把血统四叶的
/// <b>两两互读补满</b>：此前 C(4,2)=6 条边只有四条有见证（金仓↔openGauss、金仓↔GaussDB、
/// 两颗分叉叶互读、各叶对血统），本笔交出瀚高↔两颗分叉叶的两条，六条边至此各有至少一颗。</para>
///
/// <para><b>这一家在 PG 血统里占一个别人没有的档：厂商行的坑列里一颗方言级分岔都没点名</b>。
/// 金仓那一行写着 <c>SERIAL</c> vs <c>IDENTITY</c> 与 Oracle 兼容模式开关，openGauss/GaussDB 那一共行
/// 写着 9.2 内核代数与 <c>RETURNING</c> 受限，而本行（§9.1 行 907）第三列"PG 系"、第四列"兼容 PG"、
/// 坑列只有权限模型与审计策略 —— 两颗都在方言层之外。于是那颗被逐笔重裁的 IDENTITY 问题对本家
/// <em>不是裁"不落"，而是问都不成立</em>：那一问整个挂在行 904 的内核代数上，行 907 没有代数可挂。
/// 本册照旧一个字不覆写、一位不翻案，而裁决的措辞与前三家不同 —— 前三家是"重裁、仍不落"，
/// 本家是"无点名的分岔可裁"。这两句的区别不许被下一笔抹平：它正是"文档明写的分岔才配覆写"
/// 那条判据的另一半（没有明写的分岔，连"裁过"这件事都不该假装做过）。真实例到场的第一问因此也换了：
/// 不是"这句 CREATE TABLE 收不收"，而是"这一家'兼容 PG'兼容的是哪一代 PG"——那一格行里没有读数。</para>
///
/// <para><b>§14 那格"零覆写者的两种读法"本笔一码不增</b>：<c>PostgreSqlCodeFirst.cs:30</c> 那颗
/// <c>IdentityClause</c> 虚口仍然从未有过覆写入口（血统构造直 new 具体类，三族血统只有 Oracle
/// 那一族留了 <c>CreateCodeFirst</c> 缝），到场的叶子从三颗涨到四颗，而"没有可表达的形状"这句话
/// 一个字不需要改。本家对那颗虚口的姿势与前两颗分叉叶同一：产出即血统，见证在
/// <c>HighgoDialectTests</c> 的无缝格。</para>
///
/// <para><b>内建表那颗 <c>assembly</c> 的名字来路是这一份内建表里的第三种</b>：填 <c>Npgsql</c>。
/// 金仓行写的是厂商文档名（Kdbndp），opengauss／gaussdb 两颗共享的是邻家库种的兼容臂（行级说法是
/// "厂商分发的是 Npgsql 的 fork"），而本行的来路是行内那三个字"兼容 PG"——线协议就是 PG 的，
/// Npgsql 是血统协议自己的驱动，不借邻家的臂。于是 PG 血统三颗键（opengauss／gaussdb／本行）
/// 同一份 assembly，成为"三键一 assembly"在这一族的第一次凑齐，与 mysql／tidb／oceanbase 那三键
/// <em>同构</em>（上一笔推广出的"系统形状"本笔续上一格），而两颗分叉叶共享的是兼容臂、本颗共享的
/// 是 primary，这个区别留在内建表注释 里而不是抹平。瀚高自己分发的托管驱动叫什么，
/// 本仓库没有可引读数——不猜名字（达梦行立的纪律），现场按厂商读数改回。整行"未实测"，
/// 刻意不写 <c>factoryType</c>（理由与达梦那一行逐字相同）。</para>
///
/// <para><b>"已适配"三句在这里的读法</b>（§9.1 (丁)）：与金仓之外另三家新叶子同档，一句都立不起来：
/// 驱动解析未核、覆盖面只有文本层、§13.4 三台授权里没有它。今天能说的只有那一句条件式：
/// "若这一家认得血统今天发的形状（它连'认不认'的行级前件都没有），则产出与另三家逐字同"。</para></summary>
[QuickSqlProvider(DbKind.Highgo)]
public sealed class HighgoProvider : PostgreSqlProviderBase
{
    /// <summary>内建表里的 <c>key</c>。assembly 的来路与"第三种共享形状"写在类注释第四段与内建表注释；
    /// 与全部"未实测"行同一处境：刻意不写 <c>factoryType</c>，走"扫导出类型里第一个
    /// <c>DbProviderFactory</c> 子类"那条兜底（理由与达梦那一行逐字相同）。</summary>
    public const string DriverKey = "highgo";

    public HighgoProvider(DbProviderFactory driverFactory)
        : base(DbKind.Highgo, driverFactory)
    {
    }
}
