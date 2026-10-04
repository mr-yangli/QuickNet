using System.Collections.Frozen;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Providers.MySqlBase;

/// <summary>MySQL 血统的变更守卫（§12.4 第 2 项，第四家）。当时是两家一句 vs 两家三段，
/// <b>v0.31 顶成一家一句 ∶ 四家三段</b>（第五家落在三段这一边，而它的理由与前三家又不同一条）——
/// 三段的 <see cref="ChangeGuardShapes"/> 在这一家又有了第二个使用者（今天是第三个），而"为什么这一家也是三段"
/// 与 T-SQL、Oracle 血统两家的理由<b>不是同一条</b>，这一格是这一版最该记的东西：
/// <list type="bullet">
/// <item>那两家的三段式来自"<b>布尔表达式不是一等值</b>"，所以 <c>(a IS NULL) &lt;&gt; (b IS NULL)</c>
/// 那个近似式过不去（T-SQL 那一条是真库跑出来的报错，见 <c>SqlServerChangeGuard</c>）。</item>
/// <item>这一家<em>有</em> <c>IS DISTINCT FROM</c>（8.0.29 起），也有 <c>&lt;=&gt;</c>（"NULL 安全的等号"，
/// 三值逻辑下两侧同空算相等，语义正好是守卫要的那一个）。所以按形状算，它本可以走金仓那一档的一句。</item>
/// <item>这里仍然取三段，理由只有一条：<b>共用层发不出 <c>&lt;=&gt;</c></b>。<see cref="BinaryOperator"/> 里
/// 没有那一颗，而 <c>OperatorText</c> 在 <c>DialectBase</c> 里是静态的 —— 加一颗运算符是接口契约的事，
/// 不在 provider 的单方权限内。<c>IS DISTINCT FROM</c> 那一颗倒是有（金仓那一族在用），但它是按版本发的，
/// 8.0.29 之前不存在，而 SPI 里没有"库的版本"这一格（同一个缺口形状见 §14 的兼容模式那一行）。
/// 三段式是<em>这一家三个候选里</em>唯一一条不需要知道版本就能发的形状（金仓那一句式同样不看版本，
/// 所以这句话只在候选集内成立，不是五家之间的判据）。<b>而第五家的候选集里压根没有"一句"那一支</b>：
/// 两档出处里 <c>IS DISTINCT FROM</c> 与 <c>&lt;=&gt;</c> 都没出现过，所以同一颗三段式在它那里不是选择而是唯一形状
/// （见 <c>XuguChangeGuard</c> 类注释第一条）。同一个共用形状因此有三种来路，这一格比位数更值得记。</item>
/// </list>
/// <b>可空性这一档与两族同判</b>：NOT NULL 列上一句 <c>&lt;&gt;</c> 就够，所以 T-SQL / Oracle / 这一家的
/// 合并语句在"只改了 NOT NULL 列"这一档上是同一份文本形状（只差引号字形与参数前缀，而那是 §9.2 的折叠项）。
///
/// <para><b>另一处不对称：这一家的 <see cref="Compares"/> 第一次是"按问不出答案关的"而不是"按确定的限制关的"</b>。
/// 第三家关掉 LOB 是厂商明文的限制（LOB 连等号都不接受）。但<em>落地两家今天都在生产路径上问不到这一位，
/// v0.31 起是三家</em>：<c>EntityTracker.Mergeable</c> 先问 <c>Dialect.SupportsJoinedWrites</c>（<c>EntityTracker.cs:860</c>），
/// 逐列问 <c>Compares</c> 排在它后面（<c>:869</c>），而这三家那颗开关都是 false ——
/// 所以"守卫关了一列 ⇒ 整批退回一句一实体"这个<em>后果</em>在这三家是由方言闸门代跑的，
/// 守卫自己的 <c>false</c> 今天只被离线那条手工构造 AST 的用例量到
/// （本条上一版写作"于是那一家的合并档整批退回逐条"，交账时按发射顺序重数推翻：<b>后果对、归因错</b>）。
/// 撇开可达性只看文本层，这一家与 T-SQL 同档：<c>TEXT</c>/<c>BLOB</c> 可以进比较式，
/// 所以同一根没有上限的 <c>string</c> 在 <c>NVARCHAR(MAX)</c> 与 <c>TEXT</c> 上产出的守卫形状相同
/// （NOT NULL 一句 <c>&lt;&gt;</c>、可空三段 <c>OR</c>），而 Oracle 血统那一家产得出同一份三段式、
/// 却发不出承载它的那一句。这里只关 <c>json</c> 一型，
/// 而理由与那一族不同：不是"确定不能用"，是"<c>&lt;&gt;</c> 在它上面的行为问不出来"（比较运算符对 JSON
/// 的支持有一个版本线，而 <c>3142</c> 那一档报错的真实触发条件是排序与 <c>&lt;</c>/<c>&gt;</c>，不是守卫发的这一句），
/// 加上今天的类型映射根本产不出 <c>json</c> 列（<c>Json&lt;T&gt;</c> 在 <c>src/</c> 里仍是 0 处），所以它是护栏。</para>
///
/// <para>还有一颗只在合并档上才显形的库种事实：<b>这一家的 <c>&lt;&gt;</c> 走列上的排序规则</b>，
/// 而默认那几颗 <c>utf8mb4_…_ci</c> 既不区分大小写也不区分重音，于是"库里是 <c>ABC</c>、新值 <c>abc</c>"
/// 会被守卫判成没变而把这一行挡在 <c>UPDATE</c> 之外 —— 存储的字节确实会变，被挡掉就是丢写。
/// 同一件事在 T-SQL 上一直成立（默认排序规则就是 CI），在达梦上取决于实例参数，在金仓上取决于列的
/// <c>COLLATE</c>。所以它不是这一家的新缺陷，而是落地四家共有、只有两家会被跨库对读发现的那一档
/// （第五家的排序规则本次没有读到出处【无出处】，所以这句"共有"没有替它签字；它在这一格<em>反向</em>的那颗
/// 不对称——存入时去尾空格——记 <c>XuguChangeGuard</c> 类注释第四段）；
/// 判据（要不要给守卫强制 <c>BINARY</c> 比较）归 §14 的跨库差异表。
/// <b>而它在今天这一家还要多等一层</b>：承载它的是合并档，而那一档被方言闸门先挡住了，
/// 所以这一颗不对称要等这一家接上某种多行写形状（<c>MERGE INTO</c> 或 <c>UPDATE … JOIN</c>）才会显形。</para>
///
/// <para>与另四家同一立场：宁可退回逐条也不 <c>CHECKSUM</c>/哈希近似 —— 撞了哈希的行会被判成"没变"而把新值
/// 静默丢掉，那是丢写（§12.4 第 2 项）。</para></summary>
public sealed class MySqlChangeGuard : IChangeGuard
{
    /// <summary>问不出答案的型：<b>只有一颗</b>，而这颗今天产不出来（见类注释）。与第三家那四颗 LOB 对照着读：
    /// 同一个接口方法在两家关的是完全不同的东西（一边是厂商明文的限制、一边是问不出答案），
    /// 而两家的<em>生产后果</em>今天都为零 —— 它们都被前面那颗方言闸门挡住了，见类注释第二段。
    /// <b>v0.31 起这是三份而不是两份</b>：<c>XuguChangeGuard</c> 关两颗大对象，关的理由与这一家同一档
    /// （问不出答案），而它那一颗是<em>默认路径</em>产得出的型，所以三家里只有它"一旦闸门开了就真会关到"。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "json" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引 ——
        // 与 T-SQL、Oracle 血统两家逐字同判（v0.31 的第五家同判，于是这一格是五家里的四家同判、金仓一家那句）。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完（与另四家同一条理由）。
        // 切在第一个 '(' 或空格之前：TIME(3) 与 DECIMAL(20,0) 都要落到基名上，而 json 那一颗没有带过限定词。
        var name = MySqlTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
