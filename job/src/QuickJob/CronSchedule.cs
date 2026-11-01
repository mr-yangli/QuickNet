namespace QuickJob;

using System.Globalization;

/// <summary>
/// A parsed cron schedule. Accepts the standard 5-field form (minute hour dom month dow) plus
/// 6-field (leading seconds) and 7-field (trailing year) forms, the <c>@</c> aliases,
/// 3-letter case-insensitive month + weekday names, ranges/steps/lists and <c>?</c>, with
/// vixie-cron's dom/dow OR-rule when both day fields are restricted. <see cref="Next"/> walks
/// wall-clock time of the given timezone — DST turns handled by converting back to UTC and re-checking.
/// </summary>
public sealed record CronSchedule
{
    /// <summary>Five years is the horizon after which a slot-less expression is declared unre-fireable.</summary>
    public const int MaxHorizonDays = 366 * 5 + 1;

    private readonly ulong _seconds;   // bit index = second
    private readonly ulong _minutes;   // bit index = minute
    private readonly uint _hours;      // bit index = hour
    private readonly uint _doms;       // bit index = day - 1
    private readonly uint _months;     // bit index = month - 1
    private readonly uint _dows;       // bit index = weekday, 0 = Sunday
    private readonly IReadOnlySet<int>? _years; // null = any year

    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronSchedule(ulong seconds, ulong minutes, uint hours, uint doms, uint months, uint dows, IReadOnlySet<int>? years, string template, bool hasSeconds)
    {
        _seconds = seconds;
        _minutes = minutes;
        _hours = hours;
        _doms = doms;
        _months = months;
        _dows = dows;
        _years = years;
        _domRestricted = doms != uint.MaxValue >> (32 - 31);
        _dowRestricted = dows != uint.MaxValue >> (32 - 7);
        Template = template;
        HasSeconds = hasSeconds;
    }

    /// <summary>The canonical expression as parsed (whitespace-normalised, alias-expanded).</summary>
    public string Template { get; init; }

    /// <summary>True when the template carried a leading seconds field.</summary>
    public bool HasSeconds { get; init; }

    /// <summary>Parses, throwing <see cref="QuickJobException"/> with the offending field named.</summary>
    public static CronSchedule Parse(string expression)
        => TryParse(expression, out var schedule, out var error) ? schedule! : throw new QuickJobException(error!);

    /// <summary>Parses without throwing; <paramref name="error"/> carries the reason on failure.</summary>
    public static bool TryParse(string? expression, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression)) { error = "cron expression is empty"; return false; }

        var canonical = Canonicalize(expression.Trim());
        var fields = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length is < 5 or > 7)
        {
            error = $"cron expects 5-7 fields (min hour dom month dow, optional leading seconds, optional trailing year), got {fields.Length} in '{expression}'";
            return false;
        }

        var b = new Builder { HasSeconds = fields.Length >= 6, Template = canonical };
        var cursor = 0;
        if (b.HasSeconds)
        {
            if (!b.Build(fields[cursor++], new Field(0, 59, "second"), out error)) return false;
        }

        // min..dow 从 cursor 顺读 —— 6/7 字段档的第二字段才是 minute；倒着取 [^5..^1] 会在
        // 7 字档错位（dow 吃到 year token）。
        if (!b.Build(fields[cursor], new Field(0, 59, "minute"), out error)) return false;
        if (!b.Build(fields[cursor + 1], new Field(0, 23, "hour"), out error)) return false;
        if (!b.Build(fields[cursor + 2], new Field(1, 31, "day-of-month"), out error, allowQuestion: true)) return false;
        if (!b.Build(fields[cursor + 3], new Field(1, 12, "month"), out error, monthNames: true)) return false;
        if (!b.Build(fields[cursor + 4], new Field(0, 6, "day-of-week"), out error, allowQuestion: true, dowNames: true)) return false;

        if (fields.Length == 7 && !b.BuildYears(fields[^1], out error)) return false;

        schedule = new CronSchedule(b.Seconds, b.Minutes, b.Hours, b.Doms, b.Months, b.Dows, b.Years, b.Template, b.HasSeconds);
        return true;
    }

    /// <summary>The next matching instant strictly after <paramref name="from"/> (equal when <paramref name="inclusive"/>),
    /// evaluated in <paramref name="tz"/>'s wall clock. Null marks "can never fire from here"
    /// (Feb 30 expressions, a year field fully behind, five years of nothing).</summary>
    public DateTimeOffset? Next(DateTimeOffset from, TimeZoneInfo tz, bool inclusive = false)
    {
        var floor = inclusive ? from : from.AddTicks(1);
        if (_years is { } ys && !ys.Any(y => y >= floor.Year && y <= floor.Year + 5)) return null;

        var origin = TimeZoneInfo.ConvertTime(floor, tz);
        var walkingDay = origin.Date;
        var floorTod = origin.TimeOfDay;

        for (var walked = 0; walked <= MaxHorizonDays; walked++)
        {
            if (DayMatches(walkingDay))
            {
                if (TryFirstTOD(floorTod, out var tod))
                {
                    var utc = ToUtc(walkingDay + tod, tz);
                    if (utc >= floor) return utc;

                    // DST pulled UTC back below floor: re-read the wall clock and re-walk the same day.
                    var guard = 0;
                    while (utc < floor && guard++ < 8)
                    {
                        var rolled = TimeZoneInfo.ConvertTime(utc.AddTicks(1), tz);
                        walkingDay = rolled.Date;
                        floorTod = rolled.TimeOfDay;
                        if (!DayMatches(walkingDay)) break;
                        if (!TryFirstTOD(floorTod, out var retryTod)) break;
                        utc = ToUtc(walkingDay + retryTod, tz);
                    }
                    if (utc >= floor) return utc;
                }
            }
            walkingDay = walkingDay.AddDays(1);
            floorTod = TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>The latest matching instant strictly before <paramref name="from"/> (equal when
    /// <paramref name="inclusive"/>). Backward mirror of <see cref="Next"/> —
    /// <see cref="MissedSlotPolicy.CatchUpLatest"/> uses it to find the one late slot worth running.</summary>
    public DateTimeOffset? Previous(DateTimeOffset from, TimeZoneInfo tz, bool inclusive = false)
    {
        // exclusive first, every later decision is "at or before ceiling"
        var ceiling = inclusive ? from : from.AddTicks(-1);
        var localFloor = TimeZoneInfo.ConvertTime(ceiling, tz);
        var walkingDay = localFloor.Date;

        if (_years is { } ys && !ys.Any(y => y <= ceiling.Year && y >= ceiling.Year - 6)) return null;

        for (var walked = 0; walked <= MaxHorizonDays; walked++)
        {
            if (DayMatches(walkingDay))
            {
                if (TryLastTOD(walkingDay == localFloor.Date ? localFloor.TimeOfDay : EndOfDay, out var tod))
                {
                    var utc = ToUtc(walkingDay + tod, tz);
                    if (utc <= ceiling) return utc;

                    // DST pushed the slot beyond ceiling: step back the same day.
                    var guard = 0;
                    while (utc > ceiling && guard++ < 8)
                    {
                        var rolled = TimeZoneInfo.ConvertTime(utc, tz);
                        if (rolled.Date != walkingDay) break;
                        var earlierTod = rolled.TimeOfDay - TimeSpan.FromSeconds(1);
                        if (earlierTod < TimeSpan.Zero) break;
                        if (!TryLastTOD(earlierTod, out var retryTod)) break;
                        utc = ToUtc(walkingDay + retryTod, tz);
                    }
                    if (utc <= ceiling) return utc;
                }
            }
            walkingDay = walkingDay.AddDays(-1);
        }
        return null;
    }

    private static readonly TimeSpan EndOfDay = new(23, 59, 59);

    /// <summary>Bit-exact "would this exactly-seconded local wall-clock slot fire?" — the semantics
    /// the tests walk <see cref="Next"/> against.</summary>
    public bool MatchesLocal(DateTime local)
        => DayMatches(local.Date)
           && InWindow(_minutes, local.Minute)
           && InWindow(_hours, local.Hour)
           && (!HasSeconds ? local.Second == 0 : InWindow(_seconds, local.Second));

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
    {
        // 春季拨快造成"不存在的壁钟"（如美东 3 月第 2 个周日的 02:00）：BCL 会直接抛
        // ArgumentException —— 这里按标准偏移落到过渡瞬间，让逐日游走不炸。
        if (tz.IsInvalidTime(local))
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz.BaseUtcOffset).ToUniversalTime();
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    private bool DayMatches(DateTime day)
    {
        if (_years is { } ys && !ys.Contains(day.Year)) return false;
        if (!InWindow(_months, day.Month - 1)) return false;

        var domHit = InWindow(_doms, day.Day - 1);
        var dowHit = InWindow(_dows, day.DayOfWeek switch
        {
            DayOfWeek.Sunday => 0,
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 3,
            DayOfWeek.Thursday => 4,
            DayOfWeek.Friday => 5,
            _ => 6,
        });
        if (_domRestricted && _dowRestricted) return domHit || dowHit; // vixie OR rule
        return domHit && dowHit;
    }

    /// <summary>The first in-day slot at-or-after <paramref name="floor"/> whose hour/minute/(second) hits the masks.
    /// 无秒档在**分钟**粒度命中，但 floor 的秒数也算数（10:00:07 之后的最小命中是 10:15，不是 10:00）。</summary>
    private bool TryFirstTOD(TimeSpan floor, out TimeSpan tod)
    {
        tod = default;
        var hourFloor = floor.Hours;
        var minuteFloor = floor.Minutes;

        for (var h = hourFloor; h <= 23; h++)
        {
            if (!InWindow(_hours, h)) continue;
            var mStart = h == hourFloor ? minuteFloor : 0;
            for (var m = mStart; m <= 59; m++)
            {
                if (!InWindow(_minutes, m)) continue;
                if (!HasSeconds)
                {
                    tod = new TimeSpan(h, m, 0);
                    if (tod >= floor) return true;   // floor 自带的秒数部分也要过
                    continue;
                }
                var sStart = h == hourFloor && m == minuteFloor ? floor.Seconds : 0;
                for (var s = sStart; s <= 59; s++)
                {
                    if (!InWindow(_seconds, s)) continue;
                    var cand = new TimeSpan(h, m, s);
                    if (cand >= floor) { tod = cand; return true; } // 秒粒度也过 floor（tick 精度的 floor 用候选秒兜底）
                }
            }
        }
        return false;
    }

    /// <summary>The last in-day slot at-or-before <paramref name="ceiling"/> whose triple hits the masks.</summary>
    private bool TryLastTOD(TimeSpan ceiling, out TimeSpan tod)
    {
        tod = default;
        var hourCeil = ceiling.Hours;
        var minuteCeil = ceiling.Minutes;

        for (var h = hourCeil; h >= 0; h--)
        {
            if (!InWindow(_hours, h)) continue;
            var mEnd = h == hourCeil ? minuteCeil : 59;
            for (var m = mEnd; m >= 0; m--)
            {
                if (!InWindow(_minutes, m)) continue;
                if (!HasSeconds)
                {
                    tod = new TimeSpan(h, m, 0);
                    if (tod <= ceiling) return true; // ceiling 自带的秒数部分按分钟粒度末端截
                    continue;
                }
                var sEnd = h == hourCeil && m == minuteCeil ? ceiling.Seconds : 59;
                for (var s = sEnd; s >= 0; s--)
                {
                    if (!InWindow(_seconds, s)) continue;
                    var cand = new TimeSpan(h, m, s);
                    if (cand <= ceiling) { tod = cand; return true; } // 秒粒度也压 ceiling
                }
            }
        }
        return false;
    }

    private static bool InWindow(ulong mask, int value) => (mask & (1UL << value)) != 0;
    private static bool InWindow(uint mask, int value) => (mask & (1u << value)) != 0;

    // -- field parsing -------------------------------------------------------

    private readonly record struct Field(int Min, int Max, string Name);

    private sealed class Builder
    {
        internal ulong Seconds = (1UL << 60) - 1;
        internal ulong Minutes = (1UL << 60) - 1;
        internal uint Hours = uint.MaxValue >> (32 - 24);
        internal uint Doms = uint.MaxValue >> (32 - 31);
        internal uint Months = uint.MaxValue >> (32 - 12);
        internal uint Dows = uint.MaxValue >> (32 - 7);
        internal IReadOnlySet<int>? Years;
        internal bool HasSeconds;
        internal string Template = "";

        /// <summary>Writes one field's sets into the masks. <c>*</c>/<c>?</c> keep the all-set default;
        /// any restricted token first clears the field's mask — otherwise an <c>OR</c> into the
        /// all-ones default silently ignores the restriction.</summary>
        internal bool Build(string token, Field f, out string? error, bool allowQuestion = false, bool monthNames = false, bool dowNames = false)
        {
            error = null;
            if (token is "*" or "?")
            {
                if (token == "?" && !allowQuestion)
                {
                    error = $"'?' is only meaningful for day-of-month/day-of-week (field '{f.Name}') in template '{Template}'";
                    return false;
                }
                return true;
            }

            switch (f.Name) // 受限档：先清零再落位（默认掩码是全开）
            {
                case "second": Seconds = 0; break;
                case "minute": Minutes = 0; break;
                case "hour": Hours = 0; break;
                case "day-of-month": Doms = 0; break;
                case "month": Months = 0; break;
                case "day-of-week": Dows = 0; break;
            }

            foreach (var piece in token.Split(','))
            {
                var (lo, hi, step, ok) = ExpandPiece(piece, f, monthNames, dowNames, out error);
                if (!ok) return false;
                for (var v = lo; v <= hi; v += step)
                {
                    switch (f.Name)
                    {
                        case "second": Seconds |= 1UL << v; break;
                        case "minute": Minutes |= 1UL << v; break;
                        case "hour": Hours |= 1u << v; break;
                        case "day-of-month": Doms |= 1u << (v - 1); break;
                        case "month": Months |= 1u << (v - 1); break;
                        case "day-of-week": Dows |= 1u << v; break;
                    }
                }
            }
            return true;
        }

        private (int Lo, int Hi, int Step, bool Ok) ExpandPiece(string piece, Field f, bool monthNames, bool dowNames, out string? error)
        {
            error = null;
            string basePart;
            var step = 1;
            var slash = piece.IndexOf('/');
            var hasStep = slash >= 0;
            if (hasStep)
            {
                basePart = piece[..slash];
                if (!int.TryParse(piece[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                {
                    error = $"step '{piece}' must be a positive integer (field '{f.Name}', template '{Template}')";
                    return default;
                }
            }
            else basePart = piece;

            int lo, hi;
            if (basePart is "*" or "")
            {
                lo = f.Min;
                hi = f.Max;
            }
            else if (basePart.Contains('-'))
            {
                var ends = basePart.Split('-', 2);
                if (!TryValue(ends[0], f, monthNames, dowNames, out lo, out error)
                    || !TryValue(ends[1], f, monthNames, dowNames, out hi, out error))
                {
                    error ??= $"range '{basePart}' needs two resolvable values (field '{f.Name}', template '{Template}')";
                    return default;
                }
            }
            else
            {
                if (!TryValue(basePart, f, monthNames, dowNames, out var one, out error)) return default;
                if (hasStep)
                {
                    // vixie posture: a/s means a-max/s
                    lo = one;
                    hi = f.Max;
                }
                else
                {
                    // 裸数字就是它自己 —— 0 只点亮 0，不点亮 0..59
                    lo = one;
                    hi = one;
                }
            }

            if (lo > hi)
            {
                error = $"walk '{piece}' descends - a must be at most b (field '{f.Name}', template '{Template}')";
                return default;
            }
            if (lo < f.Min || hi < f.Min || lo > f.Max || hi > f.Max)
            {
                error = $"{basePart} steps outside {f.Min}..{f.Max} (field '{f.Name}', template '{Template}')";
                return default;
            }
            return (lo, hi, step, true);
        }

        private bool TryValue(string token, Field f, bool monthNames, bool dowNames, out int value, out string? error)
        {
            value = 0;
            error = null;
            if (dowNames && TryName(token, WeekdayNames, out var w)) value = w;
            else if (monthNames && TryName(token, MonthNames, out var mo)) value = mo;
            else if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                error = $"'{token}' is not a number or a 3-letter name (field '{f.Name}', template '{Template}')";
                return false;
            }

            if (f.Name == "day-of-week" && value == 7) value = 0; // vixie: 7 = 0 (Sunday)
            if (value < f.Min || value > f.Max)
            {
                error = $"{token} ({value}) is outside {f.Min}..{f.Max} (field '{f.Name}', template '{Template}')";
                return false;
            }
            return true;
        }

        private static bool TryName(string token, string[] names, out int value)
        {
            value = 0;
            if (token.Length != 3) return false;
            for (var i = 0; i < names.Length; i++)
            {
                if (!string.Equals(token, names[i], StringComparison.OrdinalIgnoreCase)) continue;
                value = names == WeekdayNames ? i : i + 1; // weekdays 0..6, months 1..12
                return true;
            }
            return false;
        }

        internal bool BuildYears(string token, out string? error)
        {
            error = null;
            if (token is "*" or "?") return true;
            var set = new HashSet<int>();
            foreach (var piece in token.Split(','))
            {
                string basePart;
                var step = 1;
                var slash = piece.IndexOf('/');
                if (slash >= 0)
                {
                    basePart = piece[..slash];
                    if (!int.TryParse(piece[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    {
                        error = $"year step '{piece}' must be positive (template '{Template}')";
                        return false;
                    }
                }
                else basePart = piece;

                int lo, hi;
                if (basePart is "*" or "") { lo = 1970; hi = 2099; }
                else if (basePart.Contains('-'))
                {
                    var ends = basePart.Split('-', 2);
                    if (!int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out lo)
                        || !int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out hi))
                    {
                        error = $"year range '{basePart}' needs two integers (template '{Template}')";
                        return false;
                    }
                }
                else
                {
                    if (!int.TryParse(basePart, NumberStyles.None, CultureInfo.InvariantCulture, out lo))
                    {
                        error = $"year '{basePart}' is not a number (template '{Template}')";
                        return false;
                    }
                    hi = lo;
                }
                if (lo < 1970 || hi > 2099 || lo > hi)
                {
                    error = $"year range '{piece}' must be ascending 1970-2099 (template '{Template}')";
                    return false;
                }
                for (var y = lo; y <= hi; y += step) set.Add(y);
            }
            Years = set;
            return true;
        }
    }

    private static readonly string[] WeekdayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly string[] MonthNames = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static string Canonicalize(string raw)
    {
        var lower = raw.ToLowerInvariant();
        return lower switch
        {
            "@yearly" or "@annually" => "0 0 0 1 1 *",
            "@monthly" => "0 0 0 1 * *",
            "@weekly" => "0 0 0 * * 0",
            "@daily" or "@midnight" => "0 0 0 * * *",
            "@hourly" => "0 0 * * * *",
            "@minutely" => "0 * * * * *",
            _ => raw,
        };
    }
}
