using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的乐观并发（§4，第五家）。比较式与另四家<em>逐字同形</em>（<c>"Version" = :p</c>），
/// 分岔仍然只在<b>谁负责让那一格变</b>，而这一家落的是"应用给值"那一侧：
/// <list type="bullet">
/// <item><b>没有 <c>rowversion</c> 那一档</b>【文档·按缺席读】：本次读到的那一页类型表列的是
/// 布尔/整数/浮点/定点/字符/大对象/二进制/日期时刻/<c>GUID</c> 那一族，里面没有"由库自动递增的二进制版本戳"
/// 那一型，两份产物也没有发过它。<b>所以 <see cref="ConcurrencyMode.RowVersion"/> 不是它，而这一句是
/// "按缺席读"而不是"按否定读"</b>：那一页没写"没有这种列"，只写了这么一列名字。五家的分布因此是
/// <c>1 家库给值（T-SQL）∶ 4 家应用给值</c>，而第四家与第五家的<em>缺法不同一条</em>（见下一条）。
/// <b>v0.32 第三笔把这一条推翻了一半</b>【实测否定】：<c>CREATE TABLE … ("ID" INT, "V" ROWVERSION)</c>
/// 在这一台库上<em>建得成</em>，目录里那一列的型名就读作 <c>ROWVERSION</c>（<c>TYPE_NAME="ROWVERSION"</c>、
/// <c>SCALE=-1</c>；小写形 <c>rowversion</c> 同绿），见 <c>obj/logs/v32-recon-rvbool-5.log</c> 的 RV-01a/b/c。
/// 更要紧的是它<em>真的会自己变</em>，而这一句要连着它的控制那颗一起读：灌一行而不带 V（RV-11b）→
/// 立刻把 V 抄进一张自建快照表（RV-11c，走 <c>CREATE TABLE … AS SELECT</c>，值不过线）→
/// <em>改动之前</em>两表的 V 相等数到 <b>1</b>（RV-11d，这颗是"这一型的 <c>=</c> 比得了"的前提）→
/// 改一次别的列（RV-11e）→ 再比数到 <b>0</b>（RV-11f）→ 第二次改动之后仍是 <b>0</b>（RV-11g/h）。
/// 所以那一页"没列它"从今天起读作"文档没列它，而库收它、也递增它"。<b>可它对我们仍然不可用，卡在第四颗读数上</b>：
/// 那一列的值<em>取不回来</em> —— 带它的投影一取行就抛 <c>System.DataOutOfBindException</c>（RV-08b、RV-08d、
/// RV-09e 三场同一颗红；<c>SET "V" = 5</c> 先被 <c>E16010</c> 拒、<c>LENGTHB("V")</c> 被 <c>E10049</c> 拒，
/// 两号的厂商中文文本在裸连会话的日志里是残字，所以这里只留号、判读不建在文本上，§13 第 4 项规矩 ②）。
/// 红的位置钉得很窄，四颗对照把别的解释都排掉了：同一张表不带 V 的 <c>COUNT(*)</c> 绿（RV-09a = 1）、
/// 带 V 而<em>零行</em>的同一句绿（RV-09b，<c>FIELDS=2 ROWS_SHOWN=0</c>，所以元数据与型名登记两条路都过）、
/// V 只进 <c>COUNT("V")</c> 也绿（RV-09c = 1 —— 缺省那一格库确实给了值，它不是 NULL）。于是这一档的形状
/// 从"库没有那一型"换成"库有、会给值、会自增、不许写，而驱动这一侧交不出那个值"。上面那句
/// "1 家库给值 ∶ 4 家应用给值"因此要重读成 <b>2 家库会给值，而其中这一家读不回</b>；卡点在 SPI 不在 provider
/// （"要不要拒收 <c>[Column(TypeName = "rowversion")]</c>"与「值归库给」缺的那颗事后回读槽位是同一颗，
/// 两格都记 §14，裁决归用户）—— <b>这个括号到 v0.34 止拆成了两件事</b>：事后回读那颗槽位第一笔已经建起来
/// （<c>SupportsPostStatementKeyRead</c>，今天只接单一自增键的形状，§12.4 第 1 项），而"要不要拒收"这一格
/// 第二笔按「全部完善」的整批授权自决为<b>拒收</b>：DDL 发射点抛（<c>XuguCodeFirst.EmitColumnDefinition</c>），
/// 判据就是本条引的那批读数 —— 建得成、会自增、读不回，发出去是一列"看着能用"的废列（§5.8）。
/// "要不要"从此有了答案；"能不能拿库给值的版本戳做并发"仍然没有 —— 那要等 reader 侧补 <c>DataOutOfBindException</c>
/// 那一格（A-06c）与 SPI 补"排除并发列 + 事后回读"的形状，两问各自仍开在 §14。</item>
/// <item><b>它也没有 MySQL 血统那一档"列上的自动改写子句"</b>：<c>ON UPDATE CURRENT_TIMESTAMP</c>
/// 在那一家是列定义的一部分【文档】，而这一家的两份产物与本次读到的文档里都没有出现过同型的子句 ——
/// 于是 <see cref="ConcurrencyMode.TimestampColumn"/> 在这一家说的是<b>只有一种可能</b>：
/// 那一列由应用自己写新值。与 <c>MySqlConcurrency</c> 对照着读才看得出差别：那一家是"语法里有这一档而今天没接"
/// （接它要两步：<c>StoreFillsValue</c> 置位 + CodeFirst 写子句），这一家是"连语法都没读到"，
/// 所以那一家缺的是工程，这一家缺的是事实 —— 重开成本前者低、后者要先有一台库。</item>
/// </list>
/// <b>今天这一档的实际后果与另三家同形，仍然要说清</b>：只写 DDL 而不给那一列赋值的话，
/// 保存永远成功而冲突永远不发生。这一家在这一格上比另四家多一处<em>看着像有</em>的东西：
/// 厂商文档给 <c>SYS_UUID()/UUID()/GEN_RANDOM_UUID()/NEWID()</c> 四颗取号函数【文档】，
/// 也就是"这一列的值归库给"在这一家<em>有现成的表达式</em>，而 <c>ColumnContract</c> 里今天
/// 没有任何一格能声明"插入时不要带上这一列，事后回读" —— <c>StoreFillsValue</c> 只配合
/// <c>rowversion</c> 用过一次（§12.3 那一支），而它回读的是一行的戳而不是新行的键。
/// 三颗一起记 §14：这一家"库给值"的三个出口（<c>RETURNING … INTO</c> 要 OUT 绑定、
/// 取号函数要列级默认值那一格、<c>IDENTITY_MODE</c> 要会话参数建模）各缺一个槽位，
/// 缺的都不是实现而是形状。</summary>
public sealed class XuguConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另四家同一个调用点（发射之前合取进 AST 的 <c>WHERE</c>，§10.1）。
    /// 空值那一档由调用方先折成 <c>IS NULL</c> 再下来：<c>"col" = :p</c> 在 NULL 上恒为 UNKNOWN，
    /// 每次保存都会成一次假冲突。这一家在这里没有分岔【文档没写过、产物没发过】，
    /// 而它<em>可能</em>有的分岔写在 <see cref="XuguTypes"/> 类注释第五条 ——
    /// 那一族的 <c>CHAR/VARCHAR</c> 存入时去掉尾部空格，所以版本列若是字符串型，
    /// "变了没有"在这一家是"去尾空格之后变了没有"。这一格不改这里的代码（谓词两侧都是原值），
    /// 它改的是<em>守卫</em>那一侧的判据，见 <see cref="XuguChangeGuard"/>。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
