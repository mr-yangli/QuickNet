using System.Globalization;

namespace QuickExcel.Internal;

/// <summary>
/// Value -> display text for non-Excel targets (Word/PDF tables). Honours <c>[ExcelFormat]</c> when set and
/// falls back to readable invariant defaults. Images render as empty text (image export to docx/pdf is v2).
/// </summary>
internal static class DisplayFormatter
{
    public static string Of(object? value, string? format)
    {
        switch (value)
        {
            case null: return string.Empty;
            case string s: return s;
            case bool b: return b ? "true" : "false";
            case ExcelImage: return string.Empty;
            case DateTime dt: return dt.ToString(format ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateTimeOffset dto: return dto.ToString(format ?? "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            case DateOnly d: return d.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture);
            case TimeOnly t: return t.ToString(format ?? "HH:mm:ss", CultureInfo.InvariantCulture);
            case TimeSpan ts: return format is null ? ts.ToString("c", CultureInfo.InvariantCulture)
                                                     : ts.ToString(format, CultureInfo.InvariantCulture);
            case float f: return f.ToString(format ?? "G9", CultureInfo.InvariantCulture);
            case double dbl: return dbl.ToString(format ?? "G17", CultureInfo.InvariantCulture);
            case decimal dec: return dec.ToString(format, CultureInfo.InvariantCulture);
            case Enum e: return e.ToString();
            case byte[] bytes: return Convert.ToBase64String(bytes);
            case IFormattable f2: return f2.ToString(format, CultureInfo.InvariantCulture);
            default: return value.ToString() ?? string.Empty;
        }
    }
}
