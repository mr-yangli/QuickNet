using System.Globalization;

namespace FastExcel.Internal.OpenXml;

/// <summary>Logical cell value category; maps to both the cell "t" attribute and the style lookup.</summary>
internal enum XlCellCategory
{
    Empty,
    Text,
    Number,
    Bool,
    Date,
    Time
}

/// <summary>A CLR value reduced to what the sheet writer needs: a category plus its invariant string form.</summary>
internal readonly struct XlSerializedCell
{
    public XlCellCategory Category { get; }
    public string Value { get; }
    public XlSerializedCell(XlCellCategory category, string value)
    {
        Category = category;
        Value = value;
    }
}

internal static class ValueSerializer
{
    private const string Inv = "G";

    public static XlSerializedCell Serialize(object? value, out bool isImage)
    {
        isImage = false;
        switch (value)
        {
            case null:
                return new XlSerializedCell(XlCellCategory.Empty, string.Empty);
            case string s:
                return new XlSerializedCell(s.Length == 0 ? XlCellCategory.Empty : XlCellCategory.Text, s);
            case bool b:
                return new XlSerializedCell(XlCellCategory.Bool, b ? "1" : "0");
            case byte n: return Num(n);
            case sbyte n: return Num(n);
            case short n: return Num(n);
            case ushort n: return Num(n);
            case int n: return Num(n);
            case uint n: return Num(n);
            case long n: return Num(n);
            case ulong n: return Num(n);
            case float n: return Num(n.ToString("G9", CultureInfo.InvariantCulture));
            case double n: return Num(n.ToString("G17", CultureInfo.InvariantCulture));
            case decimal n: return Num(n.ToString(CultureInfo.InvariantCulture));
            case DateTime dt:
                return new XlSerializedCell(dt.TimeOfDay == TimeSpan.Zero ? XlCellCategory.Date : XlCellCategory.Time,
                    dt.ToOADate().ToString("R", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return new XlSerializedCell(XlCellCategory.Time, dto.UtcDateTime.ToOADate().ToString("R", CultureInfo.InvariantCulture));
            case DateOnly d:
                return new XlSerializedCell(XlCellCategory.Date, ToDateOnlySerial(d).ToString("R", CultureInfo.InvariantCulture));
            case TimeOnly t:
                return new XlSerializedCell(XlCellCategory.Time, (t.ToTimeSpan().TotalDays).ToString("R", CultureInfo.InvariantCulture));
            case TimeSpan ts:
                return new XlSerializedCell(XlCellCategory.Time, ts.TotalDays.ToString("R", CultureInfo.InvariantCulture));
            case Guid g:
                return new XlSerializedCell(XlCellCategory.Text, g.ToString());
            case Uri u:
                return new XlSerializedCell(XlCellCategory.Text, u.ToString());
            case Enum e:
                return new XlSerializedCell(XlCellCategory.Text, e.ToString());
            case ExcelImage img:
                isImage = true;
                return new XlSerializedCell(XlCellCategory.Empty, string.Empty);
            case byte[] bytes:
                // raw byte[] without image semantics -> base64 text
                return new XlSerializedCell(XlCellCategory.Text, Convert.ToBase64String(bytes));
            default:
                return new XlSerializedCell(XlCellCategory.Text, value.ToString() ?? string.Empty);
        }
    }

    private static XlSerializedCell Num(IFormattable v) =>
        new(XlCellCategory.Number, v.ToString(Inv, CultureInfo.InvariantCulture));
    private static XlSerializedCell Num(string s) => new(XlCellCategory.Number, s);

    /// <summary>DateOnly serial without the 1900 leap-day bug; matches Excel's date system.</summary>
    public static double ToDateOnlySerial(DateOnly d)
    {
        var dt = d.ToDateTime(new TimeOnly(0, 0));
        // OADate already includes the fake Feb-29-1900 for dates after 1900-03-01, matching Excel.
        return dt.ToOADate();
    }
}
