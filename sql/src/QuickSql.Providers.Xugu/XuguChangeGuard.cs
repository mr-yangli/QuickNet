using System.Collections.Frozen;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Providers.Xugu;

/// <summary>虚谷的变更守卫（§12.4 第 2 项，第五家）。<b>形状上它是三段式的第四个使用者</b>
/// （<c>grep -rn "ChangeGuardShapes.ThreeWay" src/ --include=*.cs | grep -v "///"</c> 五行：四行调用点
/// 加 <c>ChangeGuardShapes.cs</c> 那一行定义，所以"三段式"是共用层的一份实现、四家各调一次），
/// 判据是可重数的：<see cref="ChangeGuardShapes.ThreeWay"/> 的分布因此变成 <c>4 家三段 ∶ 1 家一句</c>，
/// 只有金仓那一家仍是一句 <c>IS DISTINCT FROM</c>），而"为什么这一家也是三段"的<b>理由与前三家不同条</b>，
/// 这一格是这一版最该被读清楚的一处：
/// <list type="bullet">
/// <item>T-SQL 与 Oracle 血统那两家的三段式来自"<b>布尔表达式不是一等值</b>"，所以
/// <c>(a IS NULL) &lt;&gt; (b IS NULL)</c> 那个近似式过不去（T-SQL 那一条是真库跑出来的报错）。
/// MySQL 血统那一家来自"能一句的候选都要按版本发，而 SPI 里没有版本那一格"。</item>
/// <item>这一家两样都不是：本次读到的文档与两份产物里<em>都没有</em>出现过 <c>IS DISTINCT FROM</c>、
/// 也没有 <c>&lt;=&gt;</c> 那类 NULL 安全等号，所以这里没有"一句而不用赌版本"的那个候选 ——
/// 三段式不是<em>选择</em>而是<em>唯一发得出的形状</em>。
/// <b>【实测·正问，v0.32 第二格改判；原判【推断】（按缺席读）】</b>：那三形在这一台活库上<em>逐个问过了</em>，
/// 出处 <c>obj/logs/v32-recon-funcs-14-plain.log</c> ——
/// <c>IS DISTINCT FROM</c> 与 <c>IS NOT DISTINCT FROM</c> 各交 <c>[E19260] syntax error, unexpected DISTINCT</c>
/// （F-52 / F-52c，后者顺带把这一家的期望集印了出来：<c>expecting _FALSE or _NULL or _TRUE or _UNKNOWN</c>，
/// 即 <c>IS</c> 后面只接这四颗，压根没有 <c>DISTINCT</c> 这一槽），<c>&lt;=&gt;</c> 交
/// <c>syntax error, unexpected '&gt;'</c>（F-52b）。三形都<em>正问出语法错</em>，所以"唯一发得出"不再靠缺席读。
/// 而<em>另一半理由要一并翻掉</em>：前三家那条"布尔表达式不是一等值"<b>落不到这一家头上</b> ——
/// 三段式里那颗近似式在真行上双向数得清（F-52d <c>BOOL_VALUE=0</c> ∶ F-52e <c>BOOL_INVERSE=2</c>，
/// 相加正好等于靶表那两行，而靶表两颗都不可空 ⇒ <c>&lt;&gt;</c> 那一支恒不成立、<c>=</c> 那一支恒成立），
/// F-22/F-23 两问（同一份日志）也各自交出 <c>D=1</c> 与 <c>D=True</c>。
/// 于是这一家的三段式是"<b>只缺一颗一句形</b>"这一种，与 T-SQL/Oracle 血统那两家的
/// "<b>布尔不是一等值</b>"、MySQL 血统的"<b>要按版本发而 SPI 没有版本格</b>"是三条不同的理由，
/// 共用了同一份 <c>ChangeGuardShapes.ThreeWay</c>。</item>
/// </list>
/// 可空性这一档与三族同判：NOT NULL 列上一句 <c>&lt;&gt;</c> 就够，所以合并语句在"只改了 NOT NULL 列"
/// 这一档上与那三家的文本形状只差引号字形与参数前缀（§9.2 的折叠项）。
///
/// <para><b><see cref="Compares"/> 在这一家关两颗，而这两颗在这里第一次是"真会关到的"</b>。
/// 上一版这一格写的是三颗（<c>text</c>/<c>clob</c>/<c>blob</c>），交账前按厂商文档改判成两颗 ——
/// 因为 <c>TEXT</c> 在这一家是 <c>VARCHAR</c> 的同义字【文档】，它带宽度、因此可比；
/// 跟着另四族的"TEXT 就是无上限那一档"的直觉写会关掉一颗本可以比的列。
/// 剩下两颗的<em>缺法</em>各家又不同：Oracle 血统那四颗是厂商明文的限制（LOB 连等号都不接受），
/// 这一家这两颗是"问不出答案"【文档没写过、产物没发过】，与 <c>MySqlChangeGuard</c> 那颗 <c>json</c>
/// 同一档。<b>但同一颗接口方法在两家的可达性相反</b>：那颗 <c>json</c> 今天的类型映射压根产不出来，
/// 所以它是护栏；而这一家的 <c>byte[] → BLOB</c> 是<em>默认路径</em>（<see cref="XuguTypes.GetStoreType"/>
/// 第二条），<c>&gt;60000</c> 字符的 <c>string</c> 产 <c>CLOB</c> 也已经在发 —— 所以这一位一旦为 false
/// 就会在真实实体上被读到，而它的后果是"这一列不参与变更检测"。</para>
///
/// <para><b>而那个后果今天仍然是零，这是同一件事在第三家重复出现</b>：<c>EntityTracker.Mergeable</c>
/// 先问 <c>Dialect.SupportsJoinedWrites</c>（<c>EntityTracker.cs:860</c>），逐列问 <c>Compares</c> 排在它后面
/// （<c>:869</c>），而这一家那颗开关也是 false（<see cref="XuguDialect"/>）—— 于是"守卫关了一列 ⇒ 整批退回
/// 一句一实体"这个后果在<b>三家</b>身上都是由方言闸门代跑的，守卫自己的 <c>false</c> 今天只被离线那条
/// 手工构造 AST 的用例量到。§12.5 那句"不该写成 <c>if (是达梦)</c>"因此从"事实分布"变成了"事实分布的第三个实例"。</para>
///
/// <para>一颗这一族特有的不对称，与 <c>MySqlChangeGuard</c> 那段"排序规则决定守卫判不判得出"并排读，
/// 而它的<em>方向正相反</em>：厂商文档明写这一家的 <c>CHAR/VARCHAR</c> 在<em>存入时去掉尾部空格</em>【文档】，
/// 于是"库里是 <c>abc</c>、新值 <c>abc␣</c>"这一对在参数比较上<em>不等</em>（比较发生在写入之前，
/// 去尾是写入的动作），守卫会判"变了"并发出 <c>UPDATE</c>，而那一句写回去的规范化值与库里那一份逐字相同 ——
/// <b>多写一行而不是丢写一行</b>。MySQL 那一档是 CI 排序规则把真变化判成没变（丢写），这一档是把没变判成变了
/// （空写一次），两者都不需要修守卫，但只有前一个会丢数据，所以 §14 那张跨库差异表里两行都在、优先级不同。
/// <b>它也不在黄金样本里</b>：这一族今天没有一台真库能证"去尾发生在参数比较之前"，那句是从"存入时"三个字读的。</para>
///
/// <para>与四家同一立场：宁可退回逐条也不 <c>CHECKSUM</c>/哈希近似 —— 撞了哈希的行会被判成"没变"
/// 而把新值静默丢掉，那是丢写（§12.4 第 2 项）。</para></summary>
public sealed class XuguChangeGuard : IChangeGuard
{
    /// <summary>问不出答案的型：<b>两颗大对象</b>，理由与可达性都写在类注释第三段（而这一格上一版是三颗，
    /// <c>text</c> 那颗是按文档改掉的）。它们都带不出比较所需的宽度语义：CLOB 那一档 2GB、BLOB 那一档没有上限，
    /// 而 <c>&lt;&gt;</c> 在这种量上要么被库挡下、要么把整值读出来比 —— 两种都不是这一句该干的事。</summary>
    static readonly FrozenSet<string> Incomparable = new[] { "clob", "blob" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool Compares(ColumnContract column) => !Incomparable.Contains(BaseType(column));

    public SqlExpression BuildDifference(ColumnContract column, SqlExpression stored, SqlExpression incoming)
    {
        // NOT NULL 列上一句 <> 就够：两侧都不可能是 NULL，而多出来的两段 OR 只会让谓词更难走索引 ——
        // 与 T-SQL、Oracle 血统、MySQL 血统三家逐字同判。
        if (!column.IsNullable)
            return new SqlBinaryExpression(BinaryOperator.NotEqual, stored, incoming);

        return ChangeGuardShapes.ThreeWay(stored, incoming);
    }

    static string BaseType(ColumnContract column)
    {
        // 走 GetStoreType 而不是只读 StoreTypeName：后者只有 ApplyStorageDefaults 跑过才有值，
        // 而守卫在发射路径上调用，那一趟不保证已经走完（与另四家同一条理由）。
        // 切在第一个 '(' 或空格之前：TIME(3) 与 NUMERIC(18,4) 都要落到基名上，两颗大对象不带限定词，
        // 而 [Column(DbType = "CLOB …")] 那种手写的带法也要落得下来 —— 这一家恰好是五家里
        // 唯一一处"同一档型既有带限定词的形状（TIME）也有不带的形状（CLOB）"，所以切分不能只认括号。
        var name = XuguTypes.Instance.GetStoreType(column);
        var cut = name.IndexOfAny(['(', ' ']);

        return (cut < 0 ? name : name[..cut]).Trim();
    }
}
