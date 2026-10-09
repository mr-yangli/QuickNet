namespace QuickExcel;

/// <summary>
/// Sets the numeric/date format string applied to a cell when exporting (e.g. "yyyy-MM-dd HH:mm", "#,##0.00", "0.00%").
/// Mirrors Excel custom number formats; written into styles.xml.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ExcelFormatAttribute : Attribute
{
    public string FormatString { get; }

    public ExcelFormatAttribute(string formatString) => FormatString = formatString;
}
