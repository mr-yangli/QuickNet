using System.Data.Common;
using QuickSql.Providers.MySqlBase;

namespace QuickSql.Providers.OceanBase;

/// <summary>OceanBase（MySQL 模）的驱动槽位（§9.1 的 OceanBase 那一行，v0.35 第三笔裁下"先定"方向的落地）。
///
/// <para><b>它是 MySQL 血统的第三颗叶子、第二颗分叉库叶子</b>：本尊 <c>MySql</c> 壳（更早、零覆写）、
/// <c>TiDb</c>（v0.35 第三笔、第一颗分叉叶子）、本类（第二颗分叉叶子）。挂上之后产出与
/// <c>MySqlProviderBase</c> / <c>MySql</c> 壳 / <c>TiDb</c> 壳<b>四方逐字同形</b>（见证在
/// <c>OceanBaseDialectTests</c> 的全family同形断言链），于是 MySQL 这一根血统缝今天有了三颗叶子——
/// 而"缝接得住叶子"这件事在第二颗叶子（TiDB）上就已经证过了，本类带来的<em>不是</em>同一个证明的第三份复件。</para>
///
/// <para><b>本类真正带来的新东西是一格"分岔在哪一层"的反例，而它逼着读这一行的人改口</b>：
/// TiDB 是<em>无条件</em>的 MySQL 兼容（它自己就是一门 MySQL 协议的重实现，"这一颗是 MySQL 模"不需要问），
/// 所以对 TiDB 而言空壳本身就是完整的交付。OceanBase<em>不是</em>——它是 MySQL / Oracle 双模，
/// "模式在连接上决定"（§9.1 那一行原话），于是本类的存在等于一句<em>没有被建模的断言</em>：
/// "拿 <c>MySqlProviderBase</c> 的文本发这条连接，是因为这条连接被建在了 MySQL 模"。而这条断言今天问不出来——
/// SPI 里没有一颗"这一族的这个库今天是哪个模"的槽位，这与金仓那一家"Oracle 兼容模式开关改变方言假设"
/// 是<em>同一颗缺口</em>（§14 那行参数名单里的两个实例 —— 户数由 §14 自己按名结算，代码不带数）。</para>
///
/// <para><b>于是本类的形状是一条明确的取舍，不是一句"先接着再说"</b>：接的是 MySQL 模那颗叶子（三家里
/// 只有它今天有对应的方言层可挂），<em>不接</em> Oracle 模那颗，也不为这颗叶子开任何"模式"槽位——
/// 因为开槽位要连着 §14 那份参数名单一起裁决（金仓的兼容模式、虚谷的 <c>COMPATIBLE_MODE</c>、这一家的双模，
/// 是同一颗公开形状的三个来客），那一句归 §4 SPI 与 §9.5 <c>doctor</c>，不是本笔能顺手定的。
/// 这一家今天能<em>诚实</em>说的只有：产出自本类的每一颗字节都出自 <c>MySqlProviderBase</c> 那一份文本层，
/// 它与"MySQL 模的 OceanBase 接受它"之间隔着<em>两层</em>未实测——第一层是"MySqlConnector 从没解析成功过"
/// （内建表里 <c>mysql</c>／<c>tidb</c>／<c>oceanbase</c> 三颗键共用同一份 assembly，那一行整行「未实测」），
/// 第二层是"这条连接今天到底在哪个模"问不出来。前一层与 TiDB 同处境，后一层是本家<em>独有</em>的，
/// 也是本类不许被读成"OceanBase 已适配"的原因。</para>
///
/// <para><b>§9.1 给这一行点名的语义分岔（分区表、全局索引、<c>AUTO_ID_CACHE</c> 导致自增不连续）
/// 全部落在文本层之外，一个都不进这一颗叶子</b>：前两者是"要不要在 DDL 里写 <c>PARTITION BY</c>／<c>GLOBALLY</c>"
/// 的形状问题，而本仓库今天不在契约里表达分区（§5 的取舍，与 §9.1 那句"按厂商版本挂到基类只覆写差异"里
/// "没有差异可覆写就一字不改"同一条纪律）；<c>AUTO_ID_CACHE</c> 与 TiDB 那颗是同一类系统变量，
/// "多行 <c>VALUES</c> 的 id 连续"在 OceanBase 上同样不成立、同样不改发射形状（Core 今天走逐条 + 回读，v0.34 的口子）。
/// 两颗点名覆写点（<c>MySqlCodeFirst</c> 的 <c>IdentityClause</c> 与 <c>const TableOptions</c>）逐颗对完也都不覆写：
/// MySQL 模的自增写法与表选项语法按 MySQL 兼容层走【推断，未实测，本机无一枚 OceanBase 实例可问】，
/// 需要的是<em>同一颗</em>值——按 <c>TiDbProvider</c> 立下的那条纪律（不留一颗没人覆写的虚函数、
/// 也不为"大概相同"新虚化一颗 <c>const</c>），本类对两颗都选继承。<em>真要</em>按表写 <c>PARTITION BY</c>
/// 那是每表选项而不是 provider 默认，SPI 里没有那一格（§14 的"契约写死 vs 现场策略"那一族）。</para>
///
/// <para><b>"已适配"三句在这里的读法</b>（§9.1 (丁)）：与 <c>TiDbProvider</c> 同档而更弱——它连"这一颗是 MySQL 模"
/// 都不敢替现场断，因为那句在 OceanBase 上是<em>连接级事实</em>而非<em>库种事实</em>。所以本家今天三句都立不起来：
/// ① 驱动 —— 内建表里 <c>oceanbase</c> 键从未在任何机器上解析过，它继承的 <c>mysql</c> 行本身整行「未实测」；
/// ② 覆盖面 —— 零条语句在任何一台 OceanBase 上执行过（§13.4 授权的三台里没有它，本机也没有）；
/// ③ 中文 —— 从未问过。重开触发同 <c>TiDbProvider</c>：真实例到场之日（§13.1 第 4 项姿势交付），
/// 而 OceanBase 那一天<em>多</em>一件要先问的：确认那台连接建在 MySQL 模——这一问今天没有任何程序入口能答。</para></summary>
[QuickSqlProvider(DbKind.OceanBase)]
public sealed class OceanBaseProvider : MySqlProviderBase
{
    /// <summary>内建表里的 <c>key</c>。它是 内建表 <c>BuiltinDrivers</c> 里<b>第三颗与 <c>mysql</c>／<c>tidb</c> 共用
    /// 同一份 assembly（<c>MySqlConnector</c>）的键</b>——"驱动按协议族装、库种按方言假设装"那格解开的第三个现场。
    /// 与前两行同一处境：从未在一台机器上解析成功过，同样刻意不写 <c>factoryType</c>（走扫第一个
    /// <c>DbProviderFactory</c> 子类的兜底，理由与达梦那一行逐字相同）。<b>而这一颗比前两颗多一层不确定</b>：
    /// 它的 <c>assembly</c> 只在"这条连接建在 MySQL 模"时才走 <c>MySqlConnector</c>——若现场把这台库建在 Oracle 模，
    /// 该走的是另一颗（今天内建表里没有、因为 §9.1 那行 Oracle 模那颗叶子被裁了后置）。这一格差异今天无处声明，
    /// 缺口记 §14 那颗"模式在连接上决定"（金仓兼容模式的第二个实例）。</summary>
    public const string DriverKey = "oceanbase";

    public OceanBaseProvider(DbProviderFactory driverFactory)
        : base(DbKind.OceanBase, driverFactory)
    {
    }
}
