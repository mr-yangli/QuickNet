using System;
using System.Globalization;

namespace QuickJob.Generators;

/// <summary>时长文本的编译期镜像。语义向运行期 DurationText.TryParse 对齐（TimeSpan 文本 + 五种
/// 后缀档），在 netstandard2.0 里手写解析（不赌 TryParse(IFormatProvider) 的依赖面）。编译期只是
/// 第一道网：拿不准就放行，运行期 DurationText 对同一串重判。</summary>
static class DurationShape
{
    internal static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text!.Trim();
        if (trimmed.Length == 0) return false;

        return TryColonForm(trimmed, out value) || TrySuffixForm(trimmed, out value);
    }

    /// <summary>"4.17:25:30.11" / "17:25:30" / "25:30" 档（天数走点分隔，其余冒号）。</summary>
    static bool TryColonForm(string text, out TimeSpan value)
    {
        value = default;
        var days = 0;
        var head = text;

        var colon = text.IndexOf(':');
        var dot = text.IndexOf('.');
        if (dot > 0 && colon > dot)
        {
            if (!int.TryParse(text.Substring(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out days)) return false;
            head = text.Substring(dot + 1);
        }

        var parts = head.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return false;

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return false;
        if (minutes > 59) return false;

        var seconds = 0.0;
        if (parts.Length == 3)
        {
            var seg = parts[2];
            var dot2 = seg.IndexOf('.');
            var wholeText = dot2 < 0 ? seg : seg.Substring(0, dot2);
            if (!int.TryParse(wholeText, NumberStyles.None, CultureInfo.InvariantCulture, out var whole)) return false;
            seconds = whole;
            if (dot2 >= 0)
            {
                // "0.11" 形式吃小数，防前导点被解析器拒
                if (!double.TryParse("0" + seg.Substring(dot2), NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)) return false;
                seconds += frac;
            }
            if (seconds >= 60.0) return false;
        }

        value = new TimeSpan(days, hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
        // 运行期走 BCL TimeSpan.TryParse，接受 "00:00:00"；只用不用零交给调用方（Every 需要 > 0 的那条另有诊断）。
        return true;
    }

    /// <summary>"5m" / "1500ms" / "2d" / "1.5h" 档。</summary>
    static bool TrySuffixForm(string text, out TimeSpan value)
    {
        value = default;
        var probe = 0;
        while (probe < text.Length && (char.IsDigit(text[probe]) || text[probe] == '.')) probe++;
        if (probe == 0 || probe == text.Length) return false;
        if (!double.TryParse(text.Substring(0, probe), NumberStyles.Float, CultureInfo.InvariantCulture, out var magnitude)) return false;

        var unit = text.Substring(probe).ToLowerInvariant();
        value = unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(magnitude),
            "s" => TimeSpan.FromSeconds(magnitude),
            "m" => TimeSpan.FromMinutes(magnitude),
            "h" => TimeSpan.FromHours(magnitude),
            "d" => TimeSpan.FromDays(magnitude),
            _ => TimeSpan.Zero,
        };
        return value > TimeSpan.Zero;
    }
}
