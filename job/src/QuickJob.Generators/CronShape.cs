using System;
using System.Globalization;

namespace QuickJob.Generators;

/// <summary>Cron 结构的编译期镜像：字段数（5–7）、@别名、字段字符档、降序区间。
/// 语义（vixie OR 规则、DST、月/日组合）不在这层——那一格由运行期 CronSchedule.Parse 把关。
/// netstandard2.0 上没有 Range/Index 预定义类型，分片一律 Substring。</summary>
static class CronShape
{
    internal static string? Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "the expression is empty";
        var trimmed = text!.Trim();
        if (trimmed.Length == 0) return "the expression is empty";

        if (trimmed[0] == '@')
        {
            switch (trimmed.ToLowerInvariant())
            {
                case "@yearly": case "@annually": case "@monthly": case "@weekly":
                case "@daily": case "@midnight": case "@hourly": case "@minutely":
                    return null;
                default:
                    return $"'{trimmed}' is not a known @ alias (yearly/annually/monthly/weekly/daily/midnight/hourly/minutely)";
            }
        }

        var fields = trimmed.Split(SpaceChars, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length is < 5 or > 7)
            return $"cron expects 5-7 fields (min hour dom month dow, optional leading seconds, optional trailing year), got {fields.Length}";

        for (var i = 0; i < fields.Length; i++)
        {
            var fromRight = fields.Length - i - 1; // 0=dow 1=month 2=dom 3=hour 4=min 5=sec 6=year
            if (fromRight == 6) continue;          // 年份字段结构宽松，交给运行期
            if (ValidateField(fields[i], fromRight) is { } pieceError) return pieceError;
        }
        return null;
    }

    static string? ValidateField(string field, int fromRight)
    {
        var pieces = field.Split(CommaChars, StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0) return "empty token";
        foreach (var piece in pieces)
        {
            var stepPart = "1";
            var basePart = piece;
            var slash = piece.IndexOf('/');
            if (slash >= 0)
            {
                basePart = piece.Substring(0, slash);
                stepPart = piece.Substring(slash + 1);
                if (stepPart.Length == 0 || !int.TryParse(stepPart, NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step < 1)
                    return $"'{piece}' must have a positive numeric step";
            }

            if (basePart is "*" or "?" or "") continue;

            var lo = TryValue(SpanLeft(basePart), fromRight);
            if (lo is null) continue; // 解析不了的形状放行 —— 运行期会有更准的话
            var hi = TryValue(SpanRight(basePart), fromRight);
            if (hi is null) continue;
            if (lo > hi) return $"'{piece}' descends — a must be at most b";
        }
        return null;
    }

    /// <summary>区间左端（无 '-' 时就是单值本身）。</summary>
    static string SpanLeft(string basePart)
    {
        var dash = basePart.IndexOf('-');
        return dash >= 0 ? basePart.Substring(0, dash) : basePart;
    }

    /// <summary>区间右端（无 '-' 时就是单值本身）。</summary>
    static string SpanRight(string basePart)
    {
        var dash = basePart.IndexOf('-');
        return dash >= 0 ? basePart.Substring(dash + 1) : basePart;
    }

    static int? TryValue(string token, int fromRight)
    {
        if (token.Length == 0) return null;
        if (token.Length == 3 && char.IsLetter(token[0]))
        {
            var lower = token.ToLowerInvariant();
            if (fromRight == 1)
            {
                for (var i = 0; i < Months.Length; i++)
                    if (lower == Months[i]) return i + 1;
                return null;
            }
            if (fromRight == 0)
            {
                for (var i = 0; i < Weekdays.Length; i++)
                    if (lower == Weekdays[i]) return i;
                return null;
            }
            return null;
        }
        return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static readonly char[] SpaceChars = [' '];
    private static readonly char[] CommaChars = [','];
    private static readonly string[] Weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
}
