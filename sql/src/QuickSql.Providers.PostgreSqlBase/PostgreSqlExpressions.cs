using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.PostgreSqlBase;

/// <summary>C# 形状 → PG 系模板表（§5.8）。键与 T-SQL 那一族同一套 <c>Fn.Key(符号, 元数)</c>，
/// 所以两边比的是同一批符号 —— 少一个键不是"少一条模板"，而是那一格在翻译器里会抛
/// <c>UntranslatableExpressionException</c>，报错形状与"这一家没有这个函数"完全一样，得人再分一次。
/// （v0.31 起这句话的量纲是<em>五家</em>：这一套键从 v0.22 就是共用层的资产，第四、第五家接进来时
/// 都按同一批符号建表，所以"少一个键"在五家对拍里是这一族另外那四对各少一条可比项。）
///
/// <b>三处刻意多包了一层 <c>CAST(… AS numeric)</c></b>（<c>ROUND</c>/<c>CEIL</c>/<c>FLOOR</c>）：
/// 那一族这些函数的单参数版本在 PG 14 之前只吃 <c>numeric</c>，而 <c>double precision</c> 那一档是 14 才补的。
/// 金仓 V8 与 openGauss 的内核分别是 9.2/9.2 一线，所以这里按"最老的那个内核"写，而不是按最新的语法。
///
/// <b>两处与 T-SQL 的语义不逐字相同，是对拍查不出来的那种</b>（记在这里而不是藏在模板里）：
/// <list type="bullet">
/// <item><c>str.length</c>：T-SQL 的 <c>LEN</c> 不计尾部空格，这一家的 <c>LENGTH</c> 计（对 <c>text</c> 而言）。
/// 同一条 <c>x.TrimEnd().Length == 3</c> 在两家可以差一个空格（第五家同名那一颗落在哪一侧<b>没有出处</b>，
/// 所以这句"两家"不跟着涨 —— 它记的是"未知"那一格，见 <c>XuguExpressions</c> 类注释第三段第一条）。</item>
/// <item><c>agg.average</c>：T-SQL 的 <c>AVG(int)</c> 是整数平均（截断），这一家返回 <c>numeric</c>（不截断）。
/// 于是同一个 DTO 槽位，一家拿到 <c>3</c> 一家拿到 <c>3.3333</c> —— 那是 §14 的跨库对账要显式处理的差，
/// 而不是哪一家的缺陷。</item>
/// </list></summary>
public sealed class PostgreSqlExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    public string ConcatOperator => "||";

    /// <summary>这一家的 <c>LIKE</c> 默认转义符本来就是反斜杠，把 <c>ESCAPE '\'</c> 一起写出来仍然正确，
    /// 而且让 T-SQL 与 PG 的 <c>LIKE</c> 文本在这一格上逐字同形（黄金样本对读时少一处噪音）。
    /// 这一格今天已经是<b>四家共用</b>一颗 <c>'\'</c>（T-SQL、这一族、Oracle 血统、v0.31 的第五家），
    /// 而唯一的偏离在第四家：MySQL 血统取 <c>'~'</c>，它是五家里唯一一处"转义符本身换字符"
    /// （判据在 <c>MySqlExpressions.LikeEscapeCharacter</c>）。
    /// 前提：<c>standard_conforming_strings</c> 为 on（PG 9.1 起默认），否则 <c>'\'</c> 会被当成转义串而报错。</summary>
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
        [Fn.Key(Fn.Concat, 2)] = new("CONCAT({0}, {1})"),
        [Fn.Key(Fn.Length, 1)] = new("LENGTH({0})"),
        [Fn.Key(Fn.Upper, 1)] = new("UPPER({0})"),
        [Fn.Key(Fn.Lower, 1)] = new("LOWER({0})"),
        [Fn.Key(Fn.Trim, 1)] = new("BTRIM({0})"),
        [Fn.Key(Fn.TrimStart, 1)] = new("LTRIM({0})"),
        [Fn.Key(Fn.TrimEnd, 1)] = new("RTRIM({0})"),
        [Fn.Key(Fn.Blank, 1)] = new("({0} IS NULL OR BTRIM({0}) = '')"),

        // C# 的 Substring 是 0 基、这一家是 1 基，所以 +1 两侧同形；单参重载在这一家有原生写法
        // （T-SQL 要伪造一个"够长的长度"），这是 §9.4 那句"共用层只发符号"的红利。
        [Fn.Key(Fn.Substring, 2)] = new("SUBSTRING({0} FROM {1} + 1)", RequiresNonNegativeLength: true),
        [Fn.Key(Fn.Substring, 3)] = new("SUBSTRING({0} FROM {1} + 1 FOR {2})", RequiresNonNegativeLength: true),
        // 元数含接收者：REPLACE 三槽在 /3、STRPOS 两槽在 /2。这两格原本照 T-SQL 那一族的旧键抄成了 /2 与 /1，
        // 于是永远查不到 —— 不是渲染错，是这两个函数在两家都从来没被翻译出来过（全仓库没有一条含它们的黄金样本）。
        // ⚠ 上面那句括号里的"没有一条"在 v0.31 复核时被推翻，且推翻的是它自己那一版：REPLACE 与 STRPOS
        // 各有一条黄金样本，而两条都是 v0.22（写下这句的版本）自己新增的
        // —— <c>golden/sqlserver/string-replace-and-indexof.sql</c> 有 <c>REPLACE</c>、
        // <c>golden/kingbase/string-functions.sql</c> 有 <c>STRPOS</c>（<c>grep -rl</c> 各 1 份）。
        // 原句照录，判据改成可重数的那一句：<b>修复与样本同版落地，所以"从来没被翻译出来过"只在修复之前成立</b>。
        // v0.31 的第五家给这一格添的是第三形（<c>INSTR(串, 子串, 1, 1)</c> 四槽），它在
        // <c>golden/dameng</c> 与 <c>golden/xugu</c> 各有一条，而 <c>REPLACE</c> 那一颗在第五家<em>没有</em>样本。
        [Fn.Key(Fn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // STRPOS 返回 1 基且找不到给 0，而 IndexOf 是 0 基且找不到给 -1（与 CHARINDEX 同一格换算）；
        // 参数顺序与 CHARINDEX 相反，这一家的实参就是"串在前、子串在后"。
        [Fn.Key(Fn.IndexOf, 2)] = new("(STRPOS({0}, {1}) - 1)"),
        [Fn.Key(Fn.ToDate, 1)] = new("CAST({0} AS date)"),
        [Fn.Key(Fn.CastText, 1)] = new("CAST({0} AS text)"),

        [Fn.Key(Fn.DateYear, 1)] = new("EXTRACT(year FROM {0})"),
        [Fn.Key(Fn.DateMonth, 1)] = new("EXTRACT(month FROM {0})"),
        [Fn.Key(Fn.DateDay, 1)] = new("EXTRACT(day FROM {0})"),
        [Fn.Key(Fn.DateHour, 1)] = new("EXTRACT(hour FROM {0})"),
        [Fn.Key(Fn.DateMinute, 1)] = new("EXTRACT(minute FROM {0})"),
        [Fn.Key(Fn.DateSecond, 1)] = new("EXTRACT(second FROM {0})"),

        // 两个都是"语句/事务起点的时刻"而不是 CLR 进程的此刻，与 SYSDATETIME() 同性质：
        // 放进逐行表达式里整批同值。UTC 那一档用 AT TIME ZONE 换掉时区，得到不带时区的 UTC 时刻。
        [Fn.Key(Fn.Now, 0)] = new("LOCALTIMESTAMP"),
        [Fn.Key(Fn.UtcNow, 0)] = new("(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')"),

        // 这一家没有 DATEADD：时间加法是 timestamp + interval，而 interval 可以乘一个数，
        // 所以"加 n 个单位"是一句而不是一个函数。括号包着乘数：{1} 若是表达式，没有它就成了 a - b * interval。
        [Fn.Key(Fn.AddYears, 2)] = new("({0} + ({1}) * INTERVAL '1 year')"),
        [Fn.Key(Fn.AddMonths, 2)] = new("({0} + ({1}) * INTERVAL '1 month')"),
        [Fn.Key(Fn.AddDays, 2)] = new("({0} + ({1}) * INTERVAL '1 day')"),
        [Fn.Key(Fn.AddHours, 2)] = new("({0} + ({1}) * INTERVAL '1 hour')"),
        [Fn.Key(Fn.AddMinutes, 2)] = new("({0} + ({1}) * INTERVAL '1 minute')"),
        [Fn.Key(Fn.AddSeconds, 2)] = new("({0} + ({1}) * INTERVAL '1 second')"),

        [Fn.Key(Fn.Round, 1)] = new("ROUND(CAST({0} AS numeric))"),
        [Fn.Key(Fn.Round, 2)] = new("ROUND(CAST({0} AS numeric), {1})"),
        [Fn.Key(Fn.Abs, 1)] = new("ABS({0})"),
        [Fn.Key(Fn.Ceiling, 1)] = new("CEIL(CAST({0} AS numeric))"),
        [Fn.Key(Fn.Floor, 1)] = new("FLOOR(CAST({0} AS numeric))"),
        [Fn.Key(Fn.Power, 2)] = new("POWER({0}, {1})"),
        [Fn.Key(Fn.Sqrt, 1)] = new("SQRT({0})"),

        // 原生 GREATEST/LEAST：T-SQL 那一族要用 CASE 展开，这里不用 —— 两处黄金样本的差就是这一格。
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
