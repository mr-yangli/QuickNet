using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.OracleBase;

/// <summary>C# 形状 → Oracle 血统模板表（§5.8，第三家）。键与另两族同一套 <c>Fn.Key(符号, 元数)</c>，
/// 所以三边比的是同一批符号；少一个键的形状也相同 —— 那一格在翻译器里抛
/// <c>UntranslatableExpressionException</c>，与"这一家没有这个函数"是同一条文案，得人再分一次。
///
/// <para><b>四处与两族不同形，其中一处是这一族独有的语义陷阱</b>：
/// <list type="bullet">
/// <item><c>str.blank</c>：另四家发 <c>(x IS NULL OR TRIM(x) = '')</c>，这一族发 <c>(TRIM(x) IS NULL)</c>。
/// 原因是那一家的 <c>''</c> 就是 <c>NULL</c>：<c>TRIM('  ') = ''</c> 的比较两侧同为 NULL，结果是 UNKNOWN，
/// 于是"空格串算不算空"这一格在另四家绿着的写法在这里<em>永远不成立</em> —— 不报错、不红、只是少一批行。
/// 这是 §10 那一类"看着能用而语义少一截"的形状，也是第三家第一次让 <c>Blank</c> 这一格有了第三种模板
/// （v0.30 的第四家没有新增第四形，它站回 <c>= ''</c> 那三家那一边 —— 它没有"'' 就是 NULL"这一条；
/// v0.31 的第五家同边，所以那一边现在是四家，而这一族的孤形仍然是孤形）。</item>
/// <item><c>date.addDays/addHours/…</c>：日期加法在这一族是 <c>+ NUMTODSINTERVAL(n, 'DAY')</c>，
/// 不是金仓那种 <c>+ n * INTERVAL '1 day'</c>，也不是 T-SQL 的 <c>DATEADD</c>。带函数而不是裸加号，
/// 是因为 <c>TIMESTAMP + NUMBER</c> 在这一族是类型错误（<c>DATE + NUMBER</c> 才是天数），
/// 而契约上"这列是 DATE 还是 TIMESTAMP"发射层不知道 —— 选谁都不能错的那个写法只有这一个。</item>
/// <item><c>math.round/ceiling/floor</c>：不需要金仓那三层 <c>CAST(… AS numeric)</c>。
/// 那一个 <c>CAST</c> 是"9.2 内核上这些函数只吃 numeric"逼出来的（<c>PostgreSqlExpressions</c> 的类注释），
/// 而这一族的 NUMBER 不分档，<c>BINARY_DOUBLE</c> 进去是隐式转换 —— 抄过来会得到三条多此一 cast 的语句。</item>
/// <item><c>cast.date</c>：<c>TRUNC(x)</c> 而不是 <c>CAST(x AS date)</c>。这一族的 <c>CAST(… AS DATE)</c>
/// 会把小数秒<em>连同时区一起</em>按会话规则处理，而 <c>TRUNC</c> 说的就是"截到当天零点"，
/// 与 T-SQL 的 <c>CAST(… AS date)</c> 语义同一档。代价：DATE 列上 <c>TRUNC</c> 是空操作而不是错，
/// 所以这一格在两族之间的差异在文本上看不出来，只能靠模板表本身核。</item>
/// </list></para>
///
/// <para>两处语义与两族同符号不同解，对拍查不出来（与 <c>PostgreSqlExpressions</c> 记的那两格同一档）：
/// <c>str.length</c> 在这一族计字符数（<c>LENGTH</c>），而 T-SQL 的 <c>LEN</c> 不计尾部空格；
/// <c>agg.average</c> 在 <c>NUMBER</c> 上返回带标度的数（不截断），与 T-SQL 的 <c>AVG(int)</c> 截断不同 ——
/// 那是 §14 跨库对账要显式处理的差，不是哪一家的缺陷。</para>
///
/// <para>还有一格<b>刻意不声明</b>：<c>SupportsRegex</c> 留 false。这一族确实有 <c>REGEXP_LIKE</c>
/// （落地四家里<em>只有 T-SQL 没有</em>：金仓那系与 MySQL 血统都有各自的一颗），但 §5.7 的 P0 行里没有正则
/// 那个槽位，声明一个没有消费者的"支持"正是 §13.5 那本台账要避免的东西。<b>v0.31 起"没有"那一档是两家</b>：
/// 第五家的两档出处里正则一颗都没出现过（<c>XuguExpressions</c> 类注释末段），所以这句"只有 T-SQL"
/// 从今天起要说成"只有 T-SQL 与第五家"。</para></summary>
public sealed class OracleExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    public string ConcatOperator => "||";

    /// <summary>与两族同形：这一族的 <c>LIKE</c> 默认转义符也是反斜杠，把 <c>ESCAPE '\'</c> 一起写出来
    /// 仍然正确，三家的 <c>LIKE</c> 文本于是逐字同形（黄金样本对读时少一处噪音）。
    /// <b>【v0.31】那句"三家逐字同形"到五家数出来是四家</b>：第五家取的也是反斜杠，所以这一档现在是
    /// T-SQL、金仓、这一族与第五家四份同形，唯一的分岔仍然只有 MySQL 血统那颗 <c>ESCAPE '~'</c>。
    /// 判据可重数：<c>grep -rn "LikeEscapeCharacter =&gt;" src/ --include=*.cs</c> 交出 5 行 = 4 ∶ 1
    /// （这句 grep 自己不会被数进去，因为文档里那个箭头写成了实体）。"逐字同形"省的是黄金样本对读时的
    /// 一处噪音，第五家那一颗的取值理由记在 <c>XuguExpressions</c> 的 <c>LikeEscapeCharacter</c> 注释里。</summary>
    public char LikeEscapeCharacter => '\\';

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

    static FrozenDictionary<string, FunctionMapping> Build() => new Dictionary<string, FunctionMapping>(StringComparer.Ordinal)
    {
        // CONCAT 在这一族只有二参数版本（与 T-SQL 同，与金仓那一家"CONCAT 吃任意个"不同），
        // 所以元数 2 的键在这里没有第三种模板要写：三参数串接在翻译器那一侧本来就是两两嵌套。
        [Fn.Key(Fn.Concat, 2)] = new("CONCAT({0}, {1})"),
        [Fn.Key(Fn.Length, 1)] = new("LENGTH({0})"),
        [Fn.Key(Fn.Upper, 1)] = new("UPPER({0})"),
        [Fn.Key(Fn.Lower, 1)] = new("LOWER({0})"),
        [Fn.Key(Fn.Trim, 1)] = new("TRIM({0})"),
        [Fn.Key(Fn.TrimStart, 1)] = new("LTRIM({0})"),
        [Fn.Key(Fn.TrimEnd, 1)] = new("RTRIM({0})"),

        // '' 就是 NULL，所以"空或空白"一句判 IS NULL 就够：加一段 = '' 的比较不是多余而是<em>错</em>
        // （UNKNOWN 不是 TRUE，那一行不会被选中）。类注释里有这一格的完整推演。
        [Fn.Key(Fn.Blank, 1)] = new("(TRIM({0}) IS NULL)"),

        // C# 的 Substring 是 0 基、这一族是 1 基，所以 +1 与两族同形；写法用 SUBSTR(x, s, len)，
        // 与 T-SQL 那一家同形（金仓用的是 SQL 标准的 SUBSTRING … FROM … FOR …）。
        [Fn.Key(Fn.Substring, 2)] = new("SUBSTR({0}, {1} + 1)", RequiresNonNegativeLength: true),
        [Fn.Key(Fn.Substring, 3)] = new("SUBSTR({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),
        [Fn.Key(Fn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // INSTR 返回 1 基且找不到给 0，与 IndexOf 差一格；实参顺序是"串在前、子串在后"，
        // 与金仓那一族的 STRPOS 同形（T-SQL 的 CHARINDEX 是反的）。
        [Fn.Key(Fn.IndexOf, 2)] = new("(INSTR({0}, {1}) - 1)"),
        [Fn.Key(Fn.ToDate, 1)] = new("TRUNC({0})"),
        [Fn.Key(Fn.CastText, 1)] = new("TO_CHAR({0})"),

        [Fn.Key(Fn.DateYear, 1)] = new("EXTRACT(year FROM {0})"),
        [Fn.Key(Fn.DateMonth, 1)] = new("EXTRACT(month FROM {0})"),
        [Fn.Key(Fn.DateDay, 1)] = new("EXTRACT(day FROM {0})"),
        [Fn.Key(Fn.DateHour, 1)] = new("EXTRACT(hour FROM {0})"),
        [Fn.Key(Fn.DateMinute, 1)] = new("EXTRACT(minute FROM {0})"),
        [Fn.Key(Fn.DateSecond, 1)] = new("EXTRACT(second FROM {0})"),

        // 两个都是"库那台机器上的此刻"而不是 CLR 进程的此刻，与两族同一性质：放进逐行表达式里整批同值。
        // 注意 SYSTIMESTAMP 带会话时区而 CURRENT_TIMESTAMP 也带 —— 与金仓那一族的 LOCALTIMESTAMP
        // （不带）差在这一格，跨库对读文本时会看到"同一个 Now 两种类型"。
        [Fn.Key(Fn.Now, 0)] = new("CURRENT_TIMESTAMP"),
        [Fn.Key(Fn.UtcNow, 0)] = new("(SYS_EXTRACT_UTC(SYSTIMESTAMP))"),

        // 年月走 ADD_MONTHS：它是这一族唯一把"月末对齐"写进规则的写法（1 月 31 日 + 1 月 = 2 月 28/29 日），
        // 而金仓那一家用 interval '1 month' 也是对齐的，两族同形而理由不同。
        [Fn.Key(Fn.AddYears, 2)] = new("ADD_MONTHS({0}, 12 * ({1}))"),
        [Fn.Key(Fn.AddMonths, 2)] = new("ADD_MONTHS({0}, {1})"),
        [Fn.Key(Fn.AddDays, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'DAY'))"),
        [Fn.Key(Fn.AddHours, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'HOUR'))"),
        [Fn.Key(Fn.AddMinutes, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'MINUTE'))"),
        [Fn.Key(Fn.AddSeconds, 2)] = new("({0} + NUMTODSINTERVAL({1}, 'SECOND'))"),

        [Fn.Key(Fn.Round, 1)] = new("ROUND({0})"),
        [Fn.Key(Fn.Round, 2)] = new("ROUND({0}, {1})"),
        [Fn.Key(Fn.Abs, 1)] = new("ABS({0})"),
        [Fn.Key(Fn.Ceiling, 1)] = new("CEIL({0})"),
        [Fn.Key(Fn.Floor, 1)] = new("FLOOR({0})"),
        [Fn.Key(Fn.Power, 2)] = new("POWER({0}, {1})"),
        [Fn.Key(Fn.Sqrt, 1)] = new("SQRT({0})"),

        // GREATEST/LEAST 在这一族是原生算子（五家里<em>只有 T-SQL</em> 要用 CASE 展开，金仓、MySQL 血统与第五家都原生），
        // 所以这一格与金仓逐字同形，而对拍里它是"第三家落回第二家形状"的那一类，不是新分岔。
        [Fn.Key(Fn.Greatest, 2)] = new("GREATEST({0}, {1})"),
        [Fn.Key(Fn.Least, 2)] = new("LEAST({0}, {1})"),
        [Fn.Key(Fn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [Fn.Key(Fn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        [Fn.Key(Fn.Count, 1)] = new("COUNT({0})"),
        [Fn.Key(Fn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [Fn.Key(Fn.Sum, 1)] = new("SUM({0})"),
        [Fn.Key(Fn.Average, 1)] = new("AVG({0})"),
        [Fn.Key(Fn.Min, 1)] = new("MIN({0})"),
        [Fn.Key(Fn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
