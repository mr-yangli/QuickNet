using System.Globalization;

namespace QuickExcel.Internal;

/// <summary>
/// Converts a raw worksheet value (string / double / bool / DateTime produced by the reader) into a
/// target CLR type, tolerating nullables, enums, GUIDs and Excel date serials. Returns a diagnostic
/// message on failure so import can report a located cell error rather than throw.
/// </summary>
internal static class ValueConverter
{
    public static bool TryConvert(object? raw, Type target, bool trim, out object? value, out string? error)
    {
        value = null;
        error = null;

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        bool isNullable = underlying != target || !target.IsValueType;

        if (raw is null)
        {
            if (isNullable || underlying.IsValueType && Nullable.GetUnderlyingType(target) is not null)
            {
                value = underlying.IsValueType && Nullable.GetUnderlyingType(target) is null ? default : null;
                return true;
            }
            value = null;
            return true; // required-ness is enforced by validation, not conversion
        }

        // byte[] target: accept image, raw bytes, or base64 text
        if (underlying == typeof(byte[]))
        {
            if (raw is ExcelImage bi) { value = bi.Data.ToArray(); return true; }
            if (raw is byte[] rb) { value = rb; return true; }
            string bs = (raw.ToString() ?? string.Empty).Trim();
            if (bs.Length == 0) { value = Array.Empty<byte>(); return true; }
            try { value = Convert.FromBase64String(bs); return true; }
            catch { error = "cell is not valid base64 image data"; return false; }
        }

        // ExcelImage target expects reader-provided bytes already wrapped
        if (underlying == typeof(ExcelImage))
        {
            if (raw is ExcelImage img) { value = img; return true; }
            if (raw is byte[] b) { value = new ExcelImage(b); return true; }
            error = "cell is not an image";
            return false;
        }

        if (underlying == typeof(string))
        {
            string s = raw switch
            {
                double d => FormatNumber(d),
                _ => raw.ToString() ?? string.Empty
            };
            value = trim ? s.Trim() : s;
            return true;
        }

        // fast path: reader already produced the right type
        if (underlying.IsInstanceOfType(raw))
        {
            value = raw;
            return true;
        }

        string text = raw switch
        {
            double d => FormatNumber(d),
            bool bl => bl ? "true" : "false",
            _ => (raw.ToString() ?? string.Empty)
        };
        if (trim) text = text.Trim();

        if (text.Length == 0)
        {
            value = underlying.IsValueType ? Activator.CreateInstance(underlying) : null;
            return true;
        }

        try
        {
            if (underlying == typeof(int)) { value = ParseInt(text); return true; }
            if (underlying == typeof(long)) { value = long.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(short)) { value = short.Parse(text, NumberStyles.Any, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(byte)) { value = byte.Parse(text, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(uint)) { value = uint.Parse(text, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(ulong)) { value = ulong.Parse(text, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(sbyte)) { value = sbyte.Parse(text, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(ushort)) { value = ushort.Parse(text, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(float)) { value = Convert.ToSingle(ParseDoubleOrThrow(text), CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(double)) { value = ParseDoubleOrThrow(text); return true; }
            if (underlying == typeof(decimal)) { value = decimal.Parse(text, NumberStyles.Any, CultureInfo.CurrentCulture); return true; }
            if (underlying == typeof(bool)) { value = ParseBool(text); return true; }
            if (underlying == typeof(Guid)) { value = Guid.Parse(text); return true; }
            if (underlying.IsEnum) { value = Enum.Parse(underlying, text, ignoreCase: true); return true; }
            if (underlying == typeof(DateTime)) { value = ParseDateTime(text, raw); return true; }
            if (underlying == typeof(DateOnly))
            {
                var dt = ParseDateTime(text, raw);
                value = DateOnly.FromDateTime(dt);
                return true;
            }
            if (underlying == typeof(DateTimeOffset))
            {
                var dt = ParseDateTime(text, raw);
                value = new DateTimeOffset(dt.ToUniversalTime());
                return true;
            }
            if (underlying == typeof(TimeSpan))
            {
                if (raw is double serial) { value = TimeSpan.FromDays(serial); return true; }
                value = TimeSpan.Parse(text, CultureInfo.InvariantCulture);
                return true;
            }
            if (underlying == typeof(Uri)) { value = new Uri(text, UriKind.RelativeOrAbsolute); return true; }

            // last resort
            value = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            error = $"cannot convert \"{Truncate(text)}\" to {underlying.Name}";
            return false;
        }
    }

    private static object ParseInt(string text)
    {
        // tolerate "1,234" and "1234.0"
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double d) && d == Math.Truncate(d)) return (int)d;
        throw new FormatException();
    }

    private static bool ParseDouble(string text, out double result) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out result)
        || double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result);

    private static double ParseDoubleOrThrow(string text)
    {
        if (ParseDouble(text, out double d)) return d;
        throw new FormatException();
    }

    private static bool ParseBool(string text) => text.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "y" => true,
        "false" or "0" or "no" or "n" => false,
        _ => throw new FormatException()
    };

    private static DateTime ParseDateTime(string text, object? raw)
    {
        if (raw is double serial) return DateTime.FromOADate(serial);
        var ci = CultureInfo.CurrentCulture;
        if (DateTime.TryParse(text, ci, DateTimeStyles.None, out var dt)) return dt;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
        // common explicit formats
        string[] formats = { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-dd HH:mm:ss", "MM/dd/yyyy", "dd-MM-yyyy", "yyyy/M/d H:mm" };
        if (DateTime.TryParseExact(text, formats, ci, DateTimeStyles.None, out dt)) return dt;
        throw new FormatException();
    }

    private static string FormatNumber(double d) =>
        d == Math.Truncate(d) && Math.Abs(d) < 1e15
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("R", CultureInfo.InvariantCulture);

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
