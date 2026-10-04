using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql.Ast;

namespace FastSql.Providers.Gbase8s;

/// <summary>GBase 8s 的乐观并发（§4，第六家）。比较式与另五家<em>逐字同形</em>
/// （<c>"Version" = ?</c>），分岔仍然只在<b>谁负责让那一格变</b>，而这一家落的是"应用给值"那一侧 ——
/// 它是这一格上<b>第一家"库给值"那条路连语法都没有读到</b>的家，而且是两条语法各缺一次：
/// <list type="bullet">
/// <item><b>没有 <c>rowversion</c> 那一档</b>【文档·IBM】：本仓库读到的那一族 Informix 型名清单
/// （整数/定点/浮点/字符/大对象/日期时刻）里没有"由库自动递增的二进制版本戳"，而这一家的存储型
/// 是<em>可以按名字穷举</em>的一族（没有扩展型、没有 <c>CREATE TYPE</c> 之外的隐藏档），
/// 所以这里"没读到"与"没有"的差距比虚谷那一格小 —— 但仍然是差距，不是等价。
/// 与虚谷那一格对照着读才看得出形状变了：那一家<em>有</em>那一型、<em>会</em>自增、而驱动读不回值，
/// 所以它的缺在<b>取值路</b>（v0.32 那批实测把三处红点逐颗钉住）；这一家是<b>型压根没读到</b>，
/// 缺在事实面。六家的分布因此是 <c>1 家库给值（T-SQL）∶ 5 家应用给值</c>，而第五家与第六家
/// 的缺法不同一条，这一格记 §13.5 的台账里。</item>
/// <item><b>也没有 MySQL 血统那一档"列上的自动改写子句"</b>（<c>ON UPDATE CURRENT_TIMESTAMP</c>）：
/// 这一家的列定义里能挂的自述子句读到的是 <c>DEFAULT</c>、<c>CONSTRAINT</c>（非空/唯一/引用/<c>CHECK</c>）、
/// <c>COMMENT IS</c> 那一族【文档·IBM】，其中 <c>DEFAULT</c> 只在<em>插入而未给值</em>时生效，
/// 没有任何一份读到的材料里出现过"每次 <c>UPDATE</c> 自动重写这一列"的子句。所以
/// <see cref="ConcurrencyMode.TimestampColumn"/> 在本家说的是<b>只有一种可能</b>：那一列由应用自己写新值。
/// 与 <c>MySqlConcurrency</c> 的分别是那句判据的第三个实例：那一家缺的是工程（两步就能接），
/// 这一家缺的是事实 —— 重开成本要先有一台库。</item>
/// </list>
/// <b>"库给值"这条路在本家还有两个现成的表达式，而它们各自缺一个槽位</b>：这一家有
/// <c>CURRENT</c>（时刻）与 <c>DBINFO('utc_current')</c>（整数秒）两颗会话级函数【文档·IBM】，
/// 所以"这一列的值归库给"在语法上不成问题；缺的是 <c>ColumnContract</c> 里没有
/// "插入时不要带上这一列、事后回读"那一格 —— <see cref="ColumnContract.StoreFillsValue"/>
/// 今天只有 <c>SqlServerTypes</c> 会置位，而它配的是"值随 <c>INSERT</c> 一起进 <c>RETURNING</c>"那条路，
/// 本家那条路压根不存在（<see cref="Gbase8sKeys.EmitReturning"/> 抛）。两颗一起记 §14。
///
/// <para><b>本家在这一格上多一处另五家都没有的形状问题，它出在占位符而不是出库</b>：
/// 位置形 <c>?</c> 让文本里没有名字，所以"谓词里那一颗参数是哪一颗"在这家<em>只由编号回答</em>
/// （<see cref="Gbase8sValues"/> 类注释第二条）。这一条今天成立得没有问题，因为编号在
/// <c>SqlWriter</c> 里按<em>文本序</em>发，而共用层把并发谓词合取进 <c>WHERE</c> 是在成文<em>之前</em>
/// （§10.1），所以那颗参数天然落在它该落的位置上；它要防的是<em>改发射顺序</em>的那一手 ——
/// 在另五家，顺序错了名字还能自证，在这一家错了就把两个值互换而不报错。
/// <c>DialectBase.EmitFunction</c> 里那颗"位置形模板必须 0,1,2… 各出现一次"的闸门
/// （v0.37 第一笔，Core）量的是同一件事的另一半。</para>
///
/// <para>精度这一档是本家<em>唯一一处</em>"用了会静默漏判"的形状，所以写在代码旁边而不是文档里：
/// 版本列若走 <see cref="Gbase8sTypes.GetStoreType"/> 的 <c>DateTime</c> 那一臂，落的是
/// <c>DATETIME YEAR TO FRACTION(3)</c>（毫秒），而 CLR 的 <see cref="DateTime"/> 带 100 纳秒的刻度 ——
/// 两次保存落在同一毫秒内时，库里那一格没有变，<c>=</c> 成立，冲突被判成"没发生"。
/// 另五家同一写法同样成立（T-SQL 的 <c>datetime2</c> 精度由列给、MySQL 那一族要显式写秒的小数位），
/// 所以这不是本家的分岔而是共用层的缺口，本家只是把它<em>第一次写在一个 provider 的注释里</em>：
/// 契约里没有"版本列的精度"那一格（§14）。至于本家把超出的位数<em>舍入</em>还是<em>截断</em>，
/// 本次没有读到那一页【文档·IBM，未逐页回读】，所以这里只说"读回来的值与写进去的可能不同档"，
/// 不说它往哪一档偏。</para></summary>
public sealed class Gbase8sConcurrency : IConcurrencyStrategy
{
    public ConcurrencyMode Mode => ConcurrencyMode.TimestampColumn;

    /// <summary>与另五家同一个调用点（发射之前合取进 AST 的 <c>WHERE</c>，§10.1）。
    /// 空值那一档由调用方先折成 <c>IS NULL</c> 再下来：<c>"col" = ?</c> 在 NULL 上恒为 UNKNOWN，
    /// 每次保存都会成一次假冲突 —— 这一条在本家与另五家同形，而它<em>不需要</em>问库
    /// （三值逻辑不是厂商特性），所以这一格是本文件里少数不用标【文档·IBM】的一句。
    /// 本家在这里没有分岔，而它可能有的分岔与虚谷那颗同型：那一族写的是"版本列若是字符串型，
    /// <c>CHAR</c> 去尾空格会让'变了没有'变成'去尾之后变了没有'"【文档】。本家也收 <c>CHAR</c>，
    /// 而它<em>是否</em>在比较时忽略尾部空格本次没有读到（§14 与 <see cref="Gbase8sChangeGuard"/>
    /// 同一颗缺口）—— 所以这里不改代码（谓词两侧都是原值），只把问题挂住。</summary>
    public SqlExpression BuildPredicate(SqlExpression target, ColumnContract column, object? originalValue)
        => new SqlBinaryExpression(BinaryOperator.Equal, target, new ParameterValue(originalValue, column));
}
