using System.Data.Common;
using QuickSql.Providers.MySqlBase;

namespace QuickSql.Providers.TiDb;

/// <summary>TiDB 的驱动槽位（§9.1 的 TiDB 那一行，"缺的只是一层壳与它的内建表行"那一格的兑现日）。
///
/// <para><b>它是 MySQL 血统的第一颗真叶子</b>——说"第一颗"要带一个限定，否则会被本仓库自己的历史挑刺：
/// <c>QuickSql.Providers.MySql</c> 那层壳更早落地、同样零覆写，但它是<b>本尊</b>挂上血统，而本类是
/// <b>分叉库</b>挂上同一根缝，两件事在 §4 的继承链上不是同一格。挂上之后产出与
/// <c>MySqlProviderBase</c> / <c>MySql</c> 壳逐字同形（同一套口子的同一份产出，见证在
/// <c>TiDbDialectTests</c> 的全family同形断言），于是那三档血统今天有了 2∶1∶1∶1 的叶子分布：
/// PG 系一颗（金仓）、Oracle 系一颗（达梦）、MySQL 系两颗（本尊与这一颗）。</para>
///
/// <para><b>§9.1 那一行为本家点过名的两颗覆写点，逐颗对完、当场结掉，而结论都是"覆写"</b>——
/// 结法是一句一句对的，不是一句"都不分岔"带过：
/// <list type="bullet">
/// <item><c>IdentityClause</c>（<c>MySqlCodeFirst</c>，今天仍是零覆写者的 <c>protected virtual</c>）：
/// TiDB 的自增写法就是 <c>AUTO_INCREMENT</c>，与 MySQL 同词同位置【推断：TiDB 以 MySQL 语法为兼容基线，
/// 本机无一枚实例可问，重开条件同 §13.1 第 4 项的官方镜像】。不覆写。</item>
/// <item><b>表选项那一段</b>（<c>MySqlCodeFirst</c> 的 <c>const TableOptions</c>，原文写着
/// "等 TiDB 那一层来那天再虚化"）：<b>那天来了，裁决是仍不虚化</b>。理由不是省事，是那条老纪律
/// （<c>MySqlProvider</c> 类注释第二条原句："不留一颗没人覆写的虚函数"）——虚化只在叶子需要
/// <em>不同值</em>时才有意义，而 TiDB 这一颗需要的值是同一颗：<c>ENGINE = InnoDB</c> 在 TiDB 上
/// 被接受并被忽略【推断，未实测】，事务由它自己那一份 Percolator 模型提供，这颗选项发出去不改变任何行为；
/// <c>DEFAULT CHARSET = utf8mb4</c> 与 TiDB 的默认一致。真要按表调 <c>SHARD_ROW_ID_BITS</c>
/// 那是每表选项而不是 provider 默认，SPI 里没有那一格（§14 的"契约写死 vs 现场策略"那一族），
/// 不为一颗今天没人调的旋钮开虚口。</item>
/// </list>
/// 于是这一颗叶子的形状与金仓那 33 行同档：<b>空壳本身就是交付物</b>，它证明的是那根缝接得住第二颗叶子。</para>
///
/// <para><b>真正的分岔全部在语义层，文本一个字不替它们说话</b>（逐条都是本仓库欠着的账，不是新闻）：
/// <list type="bullet">
/// <item><b>自增是批量预分配的</b>（<c>AUTO_ID_CACHE</c> 一类系统变量）——"多行 <c>VALUES</c> 的 id 连续"
/// 这一条在 TiDB 上比 MySQL 本尊更不成立，§12.4 第 7 项那句"不要拿主键当业务流水号"在这一家是首当其冲，
/// §9.1 要求的"不可用作序列号"明写就以本段为落点。今天 Core 的语句后回读走逐条 +
/// <c>SELECT LAST_INSERT_ID()</c>（v0.34 的口子），逐条形状下这一家与 MySQL 同读数是【推断】，未实测。</item>
/// <item><b>LIMIT 大偏移退化</b>：纯性能层，发射形状不动（与金仓/MySQL 同一条尾巴，含 2^64−1 哨兵）。</item>
/// <item><b>旧版无 FK 强制</b>：本仓库今天不在 DDL 里发外键（§5 的取舍），所以这一条分岔落在同一侧文本上
/// 反而无害——它不改产出，只改"库会不会替你兜底"，那一句已经写在共用层的约束里。</item>
/// </list></para>
///
/// <para><b>"已适配"三句在这里的读法</b>（§9.1 (丁)）：本家比另外四家更弱一档——它连一颗实例、
/// 一次驱动解析的见证都没有（驱动内建表里与 <c>mysql</c> 那一行共用同一颗 assembly <c>MySqlConnector</c>，
/// 那一行"从未解析成功过"的处境本行全文继承）。所以这一家今天能说的只有文本层那一句：
/// 产出自本类的每一颗字节都出自 <c>MySqlProviderBase</c> 那 1 557 行，与"MySQL 接受它"隔着一整个未实测的距离。</para></summary>
[QuickSqlProvider(DbKind.TiDb)]
public sealed class TiDbProvider : MySqlProviderBase
{
    /// <summary>内建表里的 <c>key</c>。它是 内建表 <c>BuiltinDrivers</c> 里<b>第一颗与另一行共用同一份 assembly 的键</b>
    /// （<c>mysql</c> 与本行都解析 <c>MySqlConnector</c>）：两颗键指向同一个程序集不是冗余，是把"驱动"与
    /// "库种"这两格解开的第一个现场——驱动按协议族装，库种按方言假设装，中间那格今天由 provider 自己站死。
    /// 与另几行同一处境：从未在一台机器上解析成功过，同样刻意不写 <c>factoryType</c>（兜底理由与达梦那一行逐字相同）。</summary>
    public const string DriverKey = "tidb";

    public TiDbProvider(DbProviderFactory driverFactory)
        : base(DbKind.TiDb, driverFactory)
    {
    }
}
