using System.Collections.Frozen;
using QuickSql.Providers;
using QuickSql.Sql;

namespace QuickSql.Providers.SqlServer;

/// <summary>C# 形状 → T-SQL 模板表（§5.8）。键一律是 <c>Fn.Key(符号, 元数)</c> 的形态，
/// 所以 <c>Math.Round(x)</c> 与 <c>Math.Round(x, 2)</c> 各占一条，不会因为 <c>string.Format</c>
/// 忽略多余实参而静默生成少一截的 SQL。表里没有的键会抛 <c>UntranslatableExpressionException</c>。</summary>
public sealed class SqlServerExpressions : IExpressionRules
{
    FrozenDictionary<string, FunctionMapping> _map = Build();

    readonly object _gate = new();

    public string ConcatOperator => "+";

    /// <summary>T-SQL 的 <c>LIKE</c> 没有默认转义符，只有 <c>ESCAPE</c> 子句，所以固定用反斜杠并总把子句写出来。</summary>
    public char LikeEscapeCharacter => '\\';

    /// <summary>不开正则：T-SQL 的 <c>LIKE</c> 只有字符类通配，模式匹配得走 CLR 集成或 <c>PATINDEX</c>，语义与 <c>Regex</c> 差得远。</summary>
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
        [QuickFn.Key(QuickFn.Concat, 2)] = new("CONCAT({0}, {1})"),
        [QuickFn.Key(QuickFn.Length, 1)] = new("LEN({0})"),
        [QuickFn.Key(QuickFn.Upper, 1)] = new("UPPER({0})"),
        [QuickFn.Key(QuickFn.Lower, 1)] = new("LOWER({0})"),
        [QuickFn.Key(QuickFn.Trim, 1)] = new("LTRIM(RTRIM({0}))"),
        [QuickFn.Key(QuickFn.TrimStart, 1)] = new("LTRIM({0})"),
        [QuickFn.Key(QuickFn.TrimEnd, 1)] = new("RTRIM({0})"),
        [QuickFn.Key(QuickFn.Blank, 1)] = new("({0} IS NULL OR LTRIM(RTRIM({0})) = N'')"),

        // C# 的 Substring 是 0 基，T-SQL 是 1 基；单参重载没有"到结尾"的写法，给一个够长的长度即可（超出会被裁）。
        [QuickFn.Key(QuickFn.Substring, 2)] = new("SUBSTRING({0}, {1} + 1, LEN({0}))", RequiresNonNegativeLength: true),
        [QuickFn.Key(QuickFn.Substring, 3)] = new("SUBSTRING({0}, {1} + 1, {2})", RequiresNonNegativeLength: true),
        // 元数把接收者算在内（Template 收到的实参清单里第一个就是它），所以三槽的 REPLACE 登记在 /3、
        // 两槽的 CHARINDEX 在 /2。这两行原本写成了 /2 与 /1，于是那一格永远查不到：不是渲染错，
        // 是 <c>str.Replace</c>/<c>str.IndexOf</c> 从来没被翻译出来过（仓库里连一条含这两个函数的黄金样本都没有）。
        // 撞出来的是第二个方言族（金仓的 STRPOS），两家同源同错，见 §13.5。
        [QuickFn.Key(QuickFn.Replace, 3)] = new("REPLACE({0}, {1}, {2})"),

        // CHARINDEX 返回 1 基且找不到给 0，而 IndexOf 是 0 基且找不到给 -1；实参顺序也是反的。
        [QuickFn.Key(QuickFn.IndexOf, 2)] = new("(CHARINDEX({1}, {0}) - 1)"),
        [QuickFn.Key(QuickFn.ToDate, 1)] = new("CAST({0} AS date)"),
        [QuickFn.Key(QuickFn.CastText, 1)] = new("CAST({0} AS nvarchar(max))"),

        [QuickFn.Key(QuickFn.DateYear, 1)] = new("DATEPART(year, {0})"),
        [QuickFn.Key(QuickFn.DateMonth, 1)] = new("DATEPART(month, {0})"),
        [QuickFn.Key(QuickFn.DateDay, 1)] = new("DATEPART(day, {0})"),
        [QuickFn.Key(QuickFn.DateHour, 1)] = new("DATEPART(hour, {0})"),
        [QuickFn.Key(QuickFn.DateMinute, 1)] = new("DATEPART(minute, {0})"),
        [QuickFn.Key(QuickFn.DateSecond, 1)] = new("DATEPART(second, {0})"),
        [QuickFn.Key(QuickFn.Now, 0)] = new("SYSDATETIME()"),
        [QuickFn.Key(QuickFn.UtcNow, 0)] = new("SYSUTCDATETIME()"),
        [QuickFn.Key(QuickFn.AddYears, 2)] = new("DATEADD(year, {1}, {0})"),
        [QuickFn.Key(QuickFn.AddMonths, 2)] = new("DATEADD(month, {1}, {0})"),
        [QuickFn.Key(QuickFn.AddDays, 2)] = new("DATEADD(day, {1}, {0})"),
        [QuickFn.Key(QuickFn.AddHours, 2)] = new("DATEADD(hour, {1}, {0})"),
        [QuickFn.Key(QuickFn.AddMinutes, 2)] = new("DATEADD(minute, {1}, {0})"),
        [QuickFn.Key(QuickFn.AddSeconds, 2)] = new("DATEADD(second, {1}, {0})"),

        [QuickFn.Key(QuickFn.Round, 1)] = new("ROUND({0}, 0)"),
        [QuickFn.Key(QuickFn.Round, 2)] = new("ROUND({0}, {1})"),
        [QuickFn.Key(QuickFn.Abs, 1)] = new("ABS({0})"),
        [QuickFn.Key(QuickFn.Ceiling, 1)] = new("CEILING({0})"),
        [QuickFn.Key(QuickFn.Floor, 1)] = new("FLOOR({0})"),
        [QuickFn.Key(QuickFn.Power, 2)] = new("POWER({0}, {1})"),
        [QuickFn.Key(QuickFn.Sqrt, 1)] = new("SQRT({0})"),

        // 没有 GREATEST/LEAST（2022 才有），而标量子查询会把表达式变成非 SARGable 的子查询：用 CASE 展开。
        [QuickFn.Key(QuickFn.Greatest, 2)] = new("(CASE WHEN {0} >= {1} THEN {0} ELSE {1} END)"),
        [QuickFn.Key(QuickFn.Least, 2)] = new("(CASE WHEN {0} <= {1} THEN {0} ELSE {1} END)"),
        [QuickFn.Key(QuickFn.Coalesce, 2)] = new("COALESCE({0}, {1})"),
        [QuickFn.Key(QuickFn.NullIf, 2)] = new("NULLIF({0}, {1})"),

        [QuickFn.Key(QuickFn.Count, 1)] = new("COUNT({0})"),
        [QuickFn.Key(QuickFn.CountDistinct, 1)] = new("COUNT(DISTINCT {0})"),
        [QuickFn.Key(QuickFn.Sum, 1)] = new("SUM({0})"),
        [QuickFn.Key(QuickFn.Average, 1)] = new("AVG({0})"),
        [QuickFn.Key(QuickFn.Min, 1)] = new("MIN({0})"),
        [QuickFn.Key(QuickFn.Max, 1)] = new("MAX({0})"),
    }.ToFrozenDictionary(StringComparer.Ordinal);
}
