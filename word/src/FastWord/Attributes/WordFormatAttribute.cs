namespace FastWord;

/// <summary>
/// Sets the format string applied to a cell when exporting (e.g. "yyyy-MM-dd HH:mm", "#,##0.00").
/// Same role FastExcel's <c>[ExcelFormat]</c> plays, as its own type.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class WordFormatAttribute : Attribute
{
    public string FormatString { get; }

    public WordFormatAttribute(string formatString) => FormatString = formatString;
}
