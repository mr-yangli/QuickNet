namespace QuickJob;

using System.Globalization;

/// <summary>Flexible duration text for attributes and the DSL: "00:05:00", "5m", "90s", "2d", "1500ms".</summary>
internal static class DurationText
{
    /// <summary>Parses flexible duration text; throws <see cref="QuickJobException"/> with the option's name in the message on failure.</summary>
    public static TimeSpan Parse(string text, string ownerOption)
        => TryParse(text, out var span)
            ? span
            : throw new QuickJobException($"[QuickJob] '{ownerOption}' is not a parseable duration: '{text}' — use TimeSpan text (\"00:05:00\") or suffix forms (\"30s\", \"5m\", \"2h\", \"2d\", \"1500ms\")");

    internal const string MsUnit = "ms";
    internal const string SecondsUnit = "s";
    internal const string MinutesUnit = "m";
    internal const string HoursUnit = "h";
    internal const string DaysUnit = "d";

    /// <summary>Non-throwing flavour.</summary>
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();

        // TimeSpan 文本一网打尽（不带后缀的自然走 BCL）
        if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out value)) return true;

        var probe = 0;
        while (probe < trimmed.Length && (char.IsDigit(trimmed[probe]) || trimmed[probe] == '.' || trimmed[probe] == ',')) probe++;
        if (probe == 0 || probe == trimmed.Length) return false;
        var number = trimmed[..probe];
        var unit = trimmed[probe..].ToLowerInvariant();

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude)) return false;
        value = unit switch
        {
            MsUnit => TimeSpan.FromMilliseconds(magnitude),
            SecondsUnit => TimeSpan.FromSeconds(magnitude),
            MinutesUnit => TimeSpan.FromMinutes(magnitude),
            HoursUnit => TimeSpan.FromHours(magnitude),
            DaysUnit => TimeSpan.FromDays(magnitude),
            _ => TimeSpan.Zero,
        };
        return value > TimeSpan.Zero && unit.Length > 0;
    }
}
