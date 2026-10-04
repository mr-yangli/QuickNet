using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Providers.Sqlite;

/// <summary>SQLite 的变更守卫（§12.4 第 2 项，第七家）。<b>形状落在三段这一边，而本家是这一边第一颗
/// "两个候选都在同一台活库上过"的</b>：今天的分布从 <c>5 家三段 ∶ 1 家一句</c> 变成
/// <b><c>6 ∶ 1</c></b>，而本家落进来的理由是第三条来路里<em>第二个</em>实例（第一条是
/// <c>MySqlChangeGuard</c> 那句"能一句的候选要按版本发，而 SPI 里没有版本那一格"）——
/// <list type="bullet">
/// <item><b>本家确实有 <c>IS DISTINCT FROM</c>，而且三处都量过</b>：投影里【实测·D1】、
/// <c>UPDATE … WHERE</c> 里【实测·2112】、以及<b>大对象上</b>【实测·3105 那一行第五列，
/// <c>"B" IS DISTINCT FROM @b</c> 在 BLOB 列上判得对】。所以"没有那一颗算子"在本家不成立，
/// 这一点与虚谷那一家（三形正问各交语法错，<c>XuguChangeGuard</c> 第一段）恰好是两侧。</item>
/// <item><b>但它有一道版本门槛</b>：那颗算子是引擎 3.39 才有的【文档】，而 <c>DbCapabilities</c>
/// 里没有"库的版本"那一格（§14，与 <c>SqliteDialect</c> 类注释末段那句"本文件所有'能用'的读数都自带
/// 一个没地方声明的前提"同一颗）。本家这一格的处境比 MySQL 那一家更别扭：<b>引擎是内嵌的</b>，
/// 版本由应用那一侧的包图决定而不是由某位数据库管理员决定，所以"这一台是几点几"这件事
/// <em>连问的入口都没有</em>——问别的家可以连上库问服务器版本，本家要问的是自己进程里那颗
/// <c>e_sqlite3</c>，而它今天只在探针里被 <c>SELECT sqlite_version()</c> 问过【实测·A1 交回 3.46.1】，
/// SPI 侧没有那一格。</item>
/// <item><b>三段式在这一家不看版本</b>，而且它在本家<em>整段量过</em>：<c>&lt;&gt;</c>、<c>IS NULL</c>、
/// <c>IS NOT NULL</c>、平铺 <c>OR</c> 四段在活库上按四种状态各数一遍（见下面第二段）。
/// 于是本家与 MySQL 血统那一家同边而同来路，与前三家（布尔不是一等值）不同来路，与虚谷（唯一发得出）
/// 又不同：<b>本家是"两句式发得出而故意不发"的第一例</b>——另六家里没有一家同时握有两句式的实测。</item>
/// </list>
/// 可空性这一档与六家同判：NOT NULL 列上一句 <c>&lt;&gt;</c> 就够（§12.4 第 2 项）。
///
/// <para><b>【第二段：三段式那四格在本家第一次是逐颗数出来的，含两侧同空那一格】</b>
/// 另六家的三段式都只被"它发得出"这一档证过（T-SQL 那条是布尔限制的反证），本家这里把
/// <c>ChangeGuardShapes.ThreeWay</c> 的原文形状按参数三种取值各问一次，两行靶数据（一行有值、一行全 NULL）
/// 同时在场：
/// <list type="bullet">
/// <item>参数与原值同 ⇒ 有值行 <c>0</c>、NULL 行 <c>1</c>【实测·3101】（后者是对的：库里没值而这次要写值，是变化）。</item>
/// <item>参数换成不同文本 ⇒ 两行都 <c>1</c>【实测·3102】。</item>
/// <item>参数给 <c>NULL</c> ⇒ 有值行 <c>1</c>、<b>NULL 行交回 <c>&lt;dbnull&gt;</c> 而不是 <c>0</c></b>
/// 【实测·3103 ∶ 3204 两颗同一格】。这一颗要说清，因为它是三段式与句式的<em>唯一</em>语义差：
/// <c>NULL &lt;&gt; NULL</c> 是 UNKNOWN，而两句式的 <c>NULL IS DISTINCT FROM NULL</c> 是 <c>0</c>【实测·3203】。
/// <b>它在守卫这里不构成缺陷</b>：这一位落在 <c>UPDATE … WHERE</c> 的位置上，UNKNOWN 与 <c>0</c> 同样不匹配，
/// 也就是"库里与行集里都是空 ⇒ 这一行不用写"，而那正是想要的判语；差异只在有人把它当<em>投影里的值</em>
/// 取出来时才会显形（本仓库的用法是谓词，§12.4 第 2 项）。六家共用同一个形状而没人在活库上数过这一格，
/// 所以这条读数顺带替那六家把它从"应该如此"改成"本家量过"。</item>
/// <item>整段直接当 <c>UPDATE</c> 的 <c>WHERE</c>：命中的 affrows 数得出（<c>2</c> 与"只有那一行"两种）
/// 【实测·3109 ∶ 3205 ∶ 3206】。</item>
/// </list></para>
///
/// <para><b><see cref="Compares"/> 恒真，而这是七家第一颗不关任何一型的守卫</b>。本文件因此没有另六家那两段
/// （一颗 <c>Incomparable</c> 集合加一个 <c>BaseType</c> 切分 helper）——<b>那两段的缺席本身就是这一格的读数</b>，
/// 所以它比另六家都需要写明白：
/// <list type="bullet">
/// <item><b>大对象在本家比得动，而且是在我们真正会用的那种量上比得动</b>：BLOB 列上
/// <c>=</c> ∶ <c>&lt;&gt;</c> ∶ <c>IS</c> ∶ <c>IS DISTINCT FROM</c> 四形各有所值（非空行 1∶0∶1∶0，
/// NULL 行前两形 UNKNOWN、后两形 0∶1）【实测·3105】，三段式在 BLOB 上同样数得出【实测·3106】；
/// 量不是理论上的最大值而是当场写进库再读回来比的<b>200,000 字节 ∶ 1,000,000 字符</b>两颗
/// 【实测·3108a ∶ 3108b 写入，3108c 回读 <c>'blob',200000,'text',1000000</c>，
/// 3108d ∶ 3108e 各自 <c>=1 ∶ &lt;&gt;0</c>】。</item>
/// <item><b>六家那一格的缺法在这里一种都不成立</b>：Oracle 血统那四颗是厂商明文的 LOB 限制（等号都不收）、
/// 虚谷那两颗是"问不出答案"、MySQL 血统那颗 <c>json</c> 是护栏（今天的类型映射产不出它）。
/// 本家这两档都<em>问得出答案而答案是"能比"</em>：我们的类型映射只产 <c>INTEGER ∶ REAL ∶ TEXT ∶ BLOB</c>
/// 四颗基名（<c>SqliteTypes.GetStoreType</c> 那一段），带宽度时那颗写作 <c>VARCHAR(n)</c>（同一颗方法的
/// <c>string</c> 分支），而它落的是同一族文本比较——宽度在本家从不参与存储【实测·4001 ∶ 4002】。
/// 四颗的等号都过了。所以关掉任何一颗都是
/// 凭空造一处"这一列不参与变更检测"，而那正是接口注释里禁止的形状（<c>Compares</c> 为 false 时
/// 少挡一列就是丢写）。</item>
/// <item><b>可达性这一档本家也是唯一一颗"闸门与守卫不同向"的</b>：另三家里 <c>Compares</c> 的 false
/// 今天由 <c>SupportsJoinedWrites</c> 那道闸代跑（<c>MySqlChangeGuard</c> 第二段与 <c>XuguChangeGuard</c>
/// 第三段各记过一次），而本家那颗开关同样是 false、守卫这颗却恒真 —— 于是本家的守卫是七家里
/// 第一颗<em>没有"false 但走不到"那一层缓冲</em>的：它为真的一切后果都是真的。</item>
/// </list></para>
///
/// <para><b>两颗只有本家才有的守卫后果，方向一正一反，都有编号</b>：
/// <list type="bullet">
/// <item><b>钱这一列的"变了没有"是逐字符判的</b>。定点在本家落 <c>TEXT</c>（<c>SqliteTypes</c> 第三条），
/// 绑定侧写的是不变形文本（<c>SqliteValues.Normalize</c>），于是守卫比的是那两串文本：
/// 库里 <c>'12.3400'</c> 而新值 <c>12.34m</c> → 文本 <c>'12.34'</c> → <c>&lt;&gt;</c> 成立【实测·3201 第一列 0 ∶
/// 3205 命中】，<b>守卫判"变了"而多发一次写</b>。这与虚谷那颗"存入时去尾空格"同方向（空写），
/// 与 MySQL 血统那颗 CI 排序规则反方向（那一个是丢写）。方向要说死：<b>本家没有丢写的那一面</b>——
/// 两个 <c>decimal</c> 若不变形文本相同则数值必相同，所以"文本没变"蕴含"值没变"，
/// 那一蕴含关系是 <c>Normalize</c> 那一段的绑定形状给的，不是库给的。</item>
/// <item><b>跨库差异表里那一颗 CI 风险在本家为 0，而且是量出来的 0</b>：<c>'Abc' = 'abc'</c> 交回 <c>0</c>
/// 【实测·97 第一列】，而同一条语句的第二列 <c>'Abc' COLLATE NOCASE = 'abc'</c> 交回 <c>1</c> ——
/// 也就是说文本比较<em>默认</em>逐字节，<c>NOCASE</c> 那一档存在但要有人显式写出来。
/// 更要紧的是它显式写了也只到 ASCII：同一格上 <c>'ä' = 'Ä' COLLATE NOCASE</c> 仍是 <c>0</c>
/// 【实测·2239 三列：1 ∶ 0 ∶ 0】。所以 T-SQL 与 MySQL 血统那两家"把真变化判成没变"的那一颗，
/// 在本家要么不存在、要么得有人自己把 <c>NOCASE</c> 写进列声明里 —— 而那一格是手写的
/// <c>[Column(DbTypes = …)]</c> 路径，provider 不翻译也不校验（<c>XuguCodeFirst</c> 类注释里
/// rowversion 那一条立的正是同一句通例），记 §14。
/// <b>同一批读数里还有一颗方向相反的</b>：<c>LIKE</c> 在本家默认<em>不</em>分大小写
/// （<c>'A' LIKE 'a'</c> 为 <c>1</c> 而 <c>'A' GLOB 'a'</c> 为 <c>0</c>【实测·96】），
/// 于是"守卫说没变而 <c>WHERE … LIKE …</c> 说变了"这一对在本家是可以同时成立的——
/// 两半用的是两套比较规则，而这是查询侧那一格的事，记在 <c>SqliteExpressions</c> 与 §14，不在这里改守卫。</item>
/// </list></para>
///
/// <para>与六家同一立场：宁可退回逐条也不 <c>CHECKSUM</c>/哈希近似 —— 撞了哈希的行会被判成"没变"
/// 而把新值静默丢掉，那是丢写（§12.4 第 2 项）。这一句在本家要按<em>两格</em>读：
/// <b>本家没有内置哈希函数</b>（本次三十多轮里没有一颗 <c>*hash*</c> 被问到过，而
/// <c>UUID()</c> 那一颗当场报 <c>no such function</c>【实测·2134】，所以"引擎里有现成的近似式"这件事
/// 在本家是<em>没有读数的</em>），同时<b>本家也不需要它</b>——<c>&lt;&gt;</c> 在 200KB 的量上比得动【实测·3108d】，
/// 这一段没有像 Oracle 血统那一家那样被库逼出一个"必须换算法"的档位。</para>
public sealed class SqliteChangeGuard : IChangeGuard
{
    /// <summary>恒真，判据与后果都写在类注释第三段。这里不留 <c>Incomparable</c> 那颗集合，
    /// 因为一颗空集合会把"为什么没有"这件事读成"还没填"；<b>本家的答案是"没有要关的型"，
    /// 而它带着 3105 ∶ 3108 那两批编号</b>。</summary>
    public bool Compares(ColumnContract column) => true;

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引 ——
        // 与 T-SQL、Oracle 血统、MySQL 血统、虚谷、GBase 8s 五家逐字同判（本家是第六家）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        // 不用 IS DISTINCT FROM：那句在本家量得到而它有 3.39 那道版本门槛，而 SPI 没有版本格（类注释第一条）。
        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }
}
