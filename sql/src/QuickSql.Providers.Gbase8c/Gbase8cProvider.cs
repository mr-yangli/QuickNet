using System.Data.Common;
using QuickSql.Providers.PostgreSqlBase;

namespace QuickSql.Providers.Gbase8c;

/// <summary>GBase 8c 的驱动槽位（§9.1 行 905 的兑现，PG 血统的第五颗叶子 ——
/// 金仓、openGauss、GaussDB、瀚高之后，<c>PostgreSqlBase</c> 那一笔的当时位是"一基类四叶子"，今天五叶子）。
///
/// <para><b>零覆写</b>：产出自本类的每一颗字节都出自 <c>PostgreSqlProviderBase</c> 那一份文本层，
/// 与 <c>KingbaseProvider</c>、<c>OpenGaussProvider</c>、<c>GaussDbProvider</c>、<c>HighgoProvider</c>
/// 四层壳逐字同形 —— 见证在 <c>Gbase8cDialectTests</c> 的六路逐字相等链（leaf == 另四叶 == 血统）。
/// 本册另把血统五叶的<b>两两互读从六条边补到十条</b>：瀚高那一册收满 C(4,2)=6，本家进链交出
/// 8c↔另四叶的四条，一册补四边（此前一册最多补两条）。"必然彼此同形"照旧不许只当必然。</para>
///
/// <para><b>这一家在 PG 血统里独占的档：它是这一族第一颗"厂商行点名的分岔落在发射面上、
/// 而仓库恰好不发那一格"的叶子</b>。行 905 的坑列写着两颗：「与 openGauss 高度相似」「分片语法差异」，
/// 后一颗是本族五份厂商行里第一颗<em>明写的方言级分岔</em>落在建表语句自身之上（瀚高行点的两颗都在
/// 方言层之外，openGauss／GaussDB 那共行的三颗都在语义层）。而它落在 <c>CREATE TABLE</c> 的
/// <b>句内子句位</b>上（分片是那张表的一个可选子句），不是另一张语句上——这与神通那一笔的第三落点是
/// 两种形状：神通那格的分岔整个住在 <c>CREATE SCHEMA</c>（本仓库不发的那一句），本家这格的分岔住在
/// 本仓库<em>照发</em>的那一句里、只是那一子句不写。于是"文档明写的分岔才配覆写"那条判据的第三落点
/// 内部第一次分两档，而两档的<em>重开条件</em>不同：跨语句那一档要等厂商语义变，句内缺省这一档要等的
/// 是仓库自己哪天长出分片 DDL —— 那天它立刻从发射面外进到面内。可跑见证是
/// <c>Gbase8cDialectTests.The_named_divergence_occupies_a_clause_this_repository_never_writes</c>：
/// 本家产出的建表文本与另四叶逐字同，而那句里连 <c>DISTRIBUTED</c> 的一颗字母都没有。</para>
///
/// <para><b>IDENTITY 那一问在本家换了一种来路：前件是借来的</b>。金仓行自己点过 <c>SERIAL</c> vs
/// <c>IDENTITY</c>，openGauss／GaussDB 那共行自己写着 9.2 内核代数（那颗【推断】由本行直接支撑），
/// 瀚高行什么也没写（那一问对它<em>不成立</em>）——而本行写了一句指向邻家的话："与 openGauss 高度相似"。
/// 这是 §9.1 里第一份<em>把自己的分岔问题转引到另一行</em>的厂商读数：那一问因此既不是"重裁、不落"
/// 也不是"问都不成立"，而是"行不答那一问，但行把自己指向了替它答的那一家"。所以真实例到场的第一问
/// 第六次换主语，而这是第一次换在<em>参照物</em>上：不是"这台库认不认那句 CREATE TABLE"，也不是
/// "它兼容的是哪一代 PG"（瀚高那一问，行内没有参照物所以只能问代数），而是
/// "那句'高度相似'像到哪一层——像到内核代数的话，本家就落进 openGauss 那一格的老问题"。
/// 那一层行里没给读数，本笔不替它引申（不猜"8c 就是 openGauss 的发行版"——那是仓库外的知识）。</para>
///
/// <para><b>§14 那格"零覆写者的三种读法"本笔一码不增</b>：<c>PostgreSqlCodeFirst.cs</c> 那颗
/// <c>IdentityClause</c> 虚口仍然从未有过覆写入口（血统构造直 new 具体类，三族血统只有 Oracle 那一族
/// 留了 <c>CreateCodeFirst</c> 缝），到场验过这句话的叶子从四颗涨到五颗，"没有可表达的形状"一个字
/// 不需要改。而本家第一次让那句判据的两半<em>同时</em>落在同一颗叶子上：裁决有前件（借来的那颗）而
/// 表达无形状（无缝）—— 神通那笔"有明写分岔而分岔在发射面之外"与第三笔"没有缝裁决就无可表达的形状"
/// 两句此前各量各的，本笔在同一颗壳上相遇。</para>
///
/// <para><b>清单那颗 <c>assembly</c> 的名字来路是这一份清单里的第三种来路的第二颗</b>：填 <c>Npgsql</c>。
/// 金仓行写的是厂商文档名（Kdbndp），opengauss／gaussdb 两颗共享的是邻家库种的兼容臂，而瀚高与本行
/// 两颗的来路都是行内那三个字"兼容 PG"（第四列原词逐字相同）——线协议就是 PG 的，Npgsql 是血统协议
/// 自己的驱动，不借邻家的臂。于是 PG 血统凑齐"四颗键、同一份 assembly"，与 mysql／tidb／oceanbase
/// 那三键同构而更<em>分岔</em>一档：那三颗共享的是同族协议的真 primary，这四颗里面来路两种各占两颗
/// （兼容臂 ∶ 直称 primary ＝ 2 ∶ 2），这个区别留在清单 $comment 里不抹平。GBase 自己分发的托管驱动
/// 叫什么，本仓库没有可引读数——不猜名字（达梦那一行立的纪律），现场按厂商读数改回。整行"未实测"，
/// 刻意不写 <c>factoryType</c>（理由与达梦那一行逐字相同）。</para>
///
/// <para><b>"已适配"三句在这里的读法</b>（§9.1 (丁)）：与另四家 PG 新叶子同档，一句都立不起来：
/// ① <c>Npgsql</c> 这颗名字的解析与装载现场未核（从未成功过一次）；② 覆盖面只有文本层，零条语句在
/// 任何一台 8c 上执行过；③ 中文从未问过。今天能说的只有那一句条件式："若这一家认得血统今天发的形状
/// （它那行只说'像 openGauss'，而像到哪一层没有读数），则产出与另四家逐字同"。</para></summary>
[QuickSqlProvider(DbKind.Gbase8c)]
public sealed class Gbase8cProvider : PostgreSqlProviderBase
{
    /// <summary>清单里的 <c>key</c>。assembly 的来路与"第三种来路的第二颗"写在类注释第六段与清单 $comment；
    /// 与全部"未实测"行同一处境：刻意不写 <c>factoryType</c>，走"扫导出类型里第一个
    /// <c>DbProviderFactory</c> 子类"那条兜底（理由与达梦那一行逐字相同）。</summary>
    public const string DriverKey = "gbase8c";

    public Gbase8cProvider(DbProviderFactory driverFactory)
        : base(DbKind.Gbase8c, driverFactory)
    {
    }
}
