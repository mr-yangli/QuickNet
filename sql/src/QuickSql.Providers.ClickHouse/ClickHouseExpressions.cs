using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.ClickHouse;

/// <summary>ClickHouse 的函数表（§4 的 IExpressionRules）。<b>这张表的"缺"与"有"同等重要，两半都逐条给理由</b>，
/// 而本笔刻意把"有"的那一半收得很窄：只注册本轮已逐条钉到原文的格子，其余整批不注册 —— 
/// 与 GBase 8s 那一家"手册没逐页回读就整批不注册"同一判据而来路更硬：这一家的字符串函数页本轮逐行读过：
/// <list type="bullet">
/// <item><b><c>length(s) 量字节、<c>lengthUTF8(s) 量码点</c></b>【文档·ClickHouse：string-functions 页
/// 明写 lengthUTF8 "Counts the number of code points"】：C# 的 <c>string.Length</c> 量的是 UTF-16 码元
///（BMP 内=码点），语义对齐的是<b>码点</b>那一颗，所以 <see cref="QuickFn.Length"/> 键注册
/// <c>lengthUTF8</c>，反而把字面更短的 <c>length</c> 留在外面 —— 那颗在中文的一个 BMP 汉字上是 3 字节，
/// 用它实现 .Length 是静默错那一类。</item>
/// <item><b>upper/lower 只转 ASCII Latin</b>【文档·ClickHouse：string-functions 页注明 converts all
/// characters to ASCII】，别名 <c>ucase</c>/<c>lcase</c>：照常注册，代价（中文不转）写在下面模板旁。</item>
/// <item><b>substring 是字节索引</b>【文档·ClickHouse：string-functions 页 substring 那一行】：
/// 索引从 1 起、量字节，<c>offset = 0</c> 报回空串。C# 的 <c>startIndex</c> 从 0 起量码元，
/// 所以两参形落 <c>substring({0}, {1} + 1)</c> 的形状照抄 —— 而这一格即埋一个已知的偏差：
/// 参数序换算只对"索引一不偏"这一档正确，多字节里的换挡（偏移在中段字符之内）本来就不等价，
/// 未实测，记 §14。</item>
/// <item><b>trim 族是三颗独立名</b>【文档·ClickHouse：string-functions 页没有一颗普通 <c>trim</c>，
/// 只有 trimBoth/trimLeft/trimRight】：三个键各落各的那颗。</item>
/// <item><b>concat</b>【文档·ClickHouse：string-functions 页】：任一实参为 NULL 则整串 NULL【文档·ClickHouse】，
/// 与 C# 的 <c>string.Concat</c> 把 null 当空串不同，照常注册（语义差记在模板旁）。</item>
/// <item><b>ToDate</b>：<c>CAST({0} AS Date)</c>。本家有 toStartOfMonth / toDate 之类的函数族
///【文档没逐页回读】，所以这里落的是最贴合"截断到日"语义的 CAST 形。</item>
/// </list>
///
/// <para>不注册的各颗，逐批给"为什么不是一时遗漏"：
/// <list type="bullet">
/// <item><c>math.*</c> 数学函数族、<c>date.year</c>/<c>date.month</c>/<c>date.day</c> 那一臂的
/// 取值函数、<c>date.now</c>/<c>date.utcNow</c>/<c>date.add*</c> 的时间算术族、<c>agg.*</c> 五聚、
/// <c>coalesce</c>/<c>nullif</c>：math-functions、datetime-functions、conditional-functions 三张页本轮
/// 抓了档案但<b>没有逐页回读</b>【文档没逐页回读】—— 与 GBase 8s 那一条同判，整批不注册、等一台引擎
/// 或一次回读。<b>代价要说清</b>：<c>Math.Round(x)</c>、<c>o.CreatedAt.Year</c>、聚合那几条链在本家
/// 会红，而在另几族绿过 —— "这一家更窄"是本笔的取向，不是本事。</item>
/// <item><c>str.blank</c>：共用层模板 <c>({0} IS NULL OR trimBoth({0}) = '')</c> 要同一颗实参用两次。
/// 本家占位符带名字（<c>@</c>），复用一颗参数不是 GBase 8s 那颗"位置形硬判据"；但 <c>Nullable(String)</c>
/// 列上"空串与 NULL 在谓词两侧各自怎么算"这一档没有回读完【文档没逐页回读】，所以宁可
/// <c>IsNullOrWhiteSpace</c> 交 <c>UntranslatableExpressionException</c>，也不发一颗没钉过的一句。</item>
/// <item><c>str.replace</c>/<c>str.indexOf</c>：本家在这两面的候选名读到了（replaceOne/replaceAll、
/// position/locate 一族），但形状/重复/换挡的语义格没有回读完【文档没逐页回读】，照上一条同判不注册。</item>
/// <item><c>math.greatest</c>/<c>math.least</c>：条件函数页有 greatest/least 那两颗【文档·ClickHouse】，
/// 但核心 Fn 那一列的多实参展开与 NULL 折向（每一格态）没有读过，暂不注册。</item>
/// </list></para>
///
/// <para><b>整张表今天没有任何一行在一台活库上跑过</b>，标【文档·ClickHouse】而不是【实测】，
/// 逐格口径记 §13.5 第十七本台账。</para></summary>
public sealed class ClickHouseExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    /// <summary><c>||</c>【推断：string-functions 页 concat 那一行旁注过"||"是它的运算符形，
    /// 没有单独的那一行可引】。这一格在 <c>src/</c> 里零消费者（与另几族同一格，那句判据在
    /// <c>MySqlDialectTests</c> 里钉着），所以这里填的不是行为而是台账。</summary>
    public string ConcatOperator => "||";

    /// <summary>反斜杠。共用层每次都显式发 <c>ESCAPE</c> 子句（<c>DialectBase</c> 那一颗），
    /// 所以这一位的真语义是"本家允许哪一个字符做转义符"而不是"库里默认是哪一个"【文档没逐页回读，
    /// 取 SQL - 那颗标形】。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>false。本家找模式也走 SQL LIKE 波形那两颗通配（<c>%</c>、<c>_</c>），正则匹配
    /// 是另一族函数（<c>match()</c>/<c>extract</c> 那一档【文档没逐页回读】），不是 LIKE 的语义。</summary>
    public bool SupportsRegex => false;

    public bool TryGetFunction(string key, out FunctionMapping mapping) => _map.TryGetValue(key, out mapping!);

    public void Register(string key, FunctionMapping mapping)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, FunctionMapping>(_map, StringComparer.Ordinal);

            next[key] = mapping;
            _map = next.ToFrozenDictionary(StringComparer.Ordinal);
        }
    }

    /// <summary>键的取值集见类注释。现场要补一颗函数时，注册进去的那一条也要带着同一条出处纪律 ——
    /// <see cref="Register"/> 在本家不是"随便填一支模板"的口子。</summary>
    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // 码点那一颗，不是 length 字节那一颗：C# 的 .Length 量 UTF-16 码元，对着 BMP 里=码点；
        // 中文一个 BMP 汉字在 length 上是 3 字节，用它实现 .Length 是静默错那一类【文档·ClickHouse】。
        [QuickFn.Key(QuickFn.Length, 1)] = new("lengthUTF8({0})"),

        // 与 GBase 8s 同一档：三个键各落各的独立名，本家没有"普通 trim"那一颗。
        [QuickFn.Key(QuickFn.Trim, 1)] = new("trimBoth({0})"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("trimLeft({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("trimRight({0})"),

        // + 1：CLR 的 startIndex 从 0 起，substring 从 1 起（这一条与另几族同一换算）。
        // 两臂的实参编号都是 0,1(,2) 且升序。RequiresNonNegativeLength 与 GBase 8s 那两臂同判：
        // CLR 侧负长度的语义（截到末尾）与本家（报错）不同为条件性【文档没逐页回读】，门先在外侧挡住。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("substring({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("substring({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),

        // 任一实参 NULL 则整串 NULL【文档·ClickHouse：string-functions 页 concat 那一行】，
        // 与 C# 的 string.Concat 把 null 当空串不同。括号是上一版几族同一取舍：共用层把结果拼进
        // WHERE 时不带自己的括号，优先级钉住。
        [QuickFn.Key(QuickFn.Concat, 2)] = new("concat({0}, {1})"),

        [QuickFn.Key(QuickFn.Upper, 1)] = new("upper({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("lower({0})"),

        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS Date)"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
