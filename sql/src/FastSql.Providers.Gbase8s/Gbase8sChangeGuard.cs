using System.Collections.Frozen;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Providers.Gbase8s;

/// <summary>GBase 8s 的变更守卫（§12.4 第 2 项，第六家）。<b>形状上它是三段式的第五个调用点</b>
/// （<c>grep -rn "ChangeGuardShapes.ThreeWay" src/ --include=*.cs | grep -v "///"</c> 从今天起是六行：
/// 五行调用点加 <c>ChangeGuardShapes.cs</c> 那一行定义），而"第五个调用点"在这一格上第一次
/// <em>不等于</em>"第五家"，所以两处读数都摆出来：
/// <list type="bullet">
/// <item>按<em>调用点</em>数：三段式 5 处（T-SQL、Oracle 血统基类、MySQL 血统基类、虚谷、本家），
/// 一句形 1 处（<c>PostgreSqlChangeGuard</c> 的 <c>IS DISTINCT FROM</c>）。5 ∶ 1。</item>
/// <item>按<em>发得出这段文本的 provider</em> 数：那一句 <c>IS DISTINCT FROM</c> 今天被 PG 血统
/// <b>五颗叶子共用</b>（金仓、瀚高、GaussDB、GBase 8c、openGauss），而三段式那一侧是
/// T-SQL 1 + 虚谷 1 + 本家 1 + Oracle 血统 2 + MySQL 血统 3 = <b>8</b>。8 ∶ 5。
/// <b>这是本仓库第一次能按 provider 而不是按基类数这一判</b>（叶子数到五颗之前，
/// "五颗叶子共用一颗基类"这个形状在 PG 血统一族才刚立起来），而它把一条读惯了的口径翻了个方向：
/// 从调用点看三段式是少数派写法（"只有一家有一句"的直觉），从 provider 看它已经是多数派。
/// 两个数都记 §13.5，是因为只报一个都会被下一次改动读成"分布变了"。</item>
/// </list>
/// 而"为什么这一家也是三段"的<b>理由与前面四家又不同条</b>，这一格仍然是这一版最该读清楚的一处：
/// T-SQL 与 Oracle 血统那两家来自"<b>布尔表达式不是一等值</b>"（<c>(a IS NULL) &lt;&gt; (b IS NULL)</c>
/// 那个近似式在真库上过不去），MySQL 血统来自"能一句的候选都要按版本发而 SPI 没有版本那一格"，
/// 虚谷来自"那颗一句形在这一台活库上<em>正问出语法错</em>"【实测】（<c>E19260 unexpected DISTINCT</c>）。
/// 本家三条都不是，它是<b>第四种</b>：读到的语句清单里<em>没有</em> <c>IS DISTINCT FROM</c>，
/// 也没有 <c>&lt;=&gt;</c> 那类 NULL 安全等号，所以没有"一句而不用赌版本"的那个候选；
/// 而它<em>布尔是不是一等值</em>本次压根没问【文档·IBM，未逐页回读】。
/// <b>差别要抠出来：前三家各有一条正向理由，本家只有"没有候选"这一条负向的</b>，而负向理由在这一格
/// 够用（三段式在任何一家可用一句形时都成立，它是形状的下界而不是某一家的最优），却不够用来宣布
/// "这一家不可能有" —— 所以这一位是本家族里少数几颗<b>换一台库就可能改判</b>的档位，
/// 与 <see cref="Gbase8sDialect"/> 类注释末段同一处境，重开条件记 §14。
///
/// <para>可空性这一档与五族同判：NOT NULL 列上一句 <c>&lt;&gt;</c> 就够，所以守卫谓词在"只改了 NOT NULL 列"
/// 这一档上与那五家的文本形状只差引号字形与那颗占位符 —— 而本家差得<em>更多一处</em>：
/// 它的合并语句今天根本不发（<see cref="Gbase8sDialect.SupportsJoinedWrites"/> 为 false），见下面第四段。</para>
///
/// <para><b><see cref="Compares"/> 关三颗，而"关哪三颗"这一格是本家与虚谷分岔最大的一处</b>。
/// 对照读才看得出：<c>XuguChangeGuard</c> 上一版关三颗、交账前按厂商文档<em>改判成两颗</em>，
/// 因为 <c>TEXT</c> 在<b>那</b>一家是 <c>VARCHAR</c> 的同义字、带宽度因此可比【文档·厂商】。
/// 本家的 <c>TEXT</c> 读到的是<b>另一档</b>：它与 <c>LVARCHAR</c> 同族而<em>带上限</em>【文档·IBM】
/// （那一页本次没有逐字回读，所以上限的数字不写在这里），方向与虚谷那颗一致（可比）而<em>出处薄一层</em>：
/// 那一句有两档产物撑着，这里只有一句手册。于是本家关的是 <c>CLOB</c>、<c>BLOB</c>、<c>BYTE</c>：
/// 两颗二进制大对象加一颗字符大对象。三颗的<em>缺法</em>与虚谷那两颗同一条 ——
/// "问不出答案"，而本家连那一档都还要再降半格：虚谷是【文档没写过、产物没发过】，
/// 本家是【文档没写过、产物没有、而且这一次连活库都没有】。
/// <b>可达性这一格则与虚谷同档而不同于 <c>MySqlChangeGuard</c></b>：那颗 <c>json</c> 今天的类型映射
/// 压根产不出来，所以它是纯护栏；本家的 <c>byte[] → BLOB</c> 与"无宽度 <c>string</c> → CLOB"
/// 都是<em>默认路径</em>（<see cref="Gbase8sTypes.GetStoreType"/>），所以这一位一旦为 false 就会在真实实体上
/// 被读到，而它的后果是"这一列不参与变更检测"。<c>BYTE</c> 那颗是三颗里唯一的纯护栏：本仓库不发它，
/// 它只接住"别人建的表里真写着 <c>BYTE</c>"，多列一颗的成本是把"不认识"挡在外面。</para>
///
/// <para><b>那个后果今天仍然是零，这是同一个零的第六次</b>：<c>EntityTracker.Mergeable</c> 先问
/// <c>Dialect.SupportsJoinedWrites</c>（<c>EntityTracker.cs:860</c>），逐列问 <c>Compares</c> 排在它后面
/// （<c>:869</c>），而本家那颗开关也是 false（这一家的语句清单里没有 <c>UPDATE … FROM</c> 这一形
/// 【文档·IBM】）—— 于是"守卫关了一列 ⇒ 整批退回一句一实体"这个因果在本家仍然由方言闸门代跑，
/// 守卫自己的 <c>false</c> 今天只被离线那条手工构造 AST 的用例量到。§12.5 那句"不该写成
/// <c>if (是达梦)</c>"因此是第六个实例。<b>而本家比另五家多一句可说的</b>：那五家的 <c>false</c>
/// 里有几颗是"能力在而形状没接"，本家这一颗是<b>语句清单里没有这一形</b>，所以它是这一族第一颗
/// <em>翻不回 true</em> 的闸门 —— 换一台库、补一份文档都接不上它，要接得先发明一种把行集接进
/// <c>UPDATE</c> 的写法，而那是 <c>WHERE EXISTS</c> 那条等价路，不是这一颗开关
/// （<see cref="Gbase8sDialect.EmitJoinedSources"/> 那句抛里点名了它）。</para>
///
/// <para>一颗本家的不对称，与 <c>XuguChangeGuard</c> 末段那条"去尾空格把没变判成变了"并排读：
/// 本家没有读到" <c>CHAR</c> 存入时去尾"那一条【文档·IBM，未回读】，所以那一条的<em>反向</em>风险
/// （把变了判成没变、丢写）在这一家无从谈起，而它<em>不是</em>因为这一家更安全 —— 是因为没读到。
/// 这一句的价值只在指认重开点：拿到一台库时，两颗（这一句与上面那颗一句形）可以同一批问掉。</para>
///
/// <para>与五家同一立场：宁可退回逐条也不 <c>CHECKSUM</c>/哈希近似 —— 撞了哈希的行会被判成"没变"
/// 而把新值静默丢掉，那是丢写（§12.4 第 2 项）。</para></summary>
public sealed class Gbase8sChangeGuard : IChangeGuard
{
    /// <summary>问不出答案的型：<b>两颗大对象加 <c>BYTE</c> 那颗同族</b>，逐颗出处与可达性都写在类注释第三段。
    /// 三颗都带不出比较所需的宽度语义：<c>CLOB</c>/<c>BLOB</c> 那一档是 GB 量级，而 <c>&lt;&gt;</c>
    /// 在这种量上要么被库挡下、要么把整值读出来比 —— 两种都不是这一句该干的事。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "clob", "blob", "byte" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引 ——
        // 与 T-SQL、Oracle 血统、MySQL 血统、虚谷四家逐字同判。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完（与另五家同一条理由）。
        // 切在第一个 '(' 或空格之前：本家有带空格的型名（DATETIME YEAR TO FRACTION(3)），三颗大对象
        // 既不带括号也不带空格，而 [Column(DbType = "CLOB …")] 那种手写的带法也要落得下来 ——
        // 与虚谷那一格同形而更刚需：那一家是"同一档既有带括号也有不带括号"，这一家多一种"带空格"。
        var name = Gbase8sTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
