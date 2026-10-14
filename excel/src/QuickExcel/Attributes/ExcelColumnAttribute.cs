namespace QuickExcel;

/// <summary>
/// Marks a property as an Excel column and lets you rename it, order it, set width and format.
/// When <see cref="Name"/> is omitted the property name is used.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ExcelColumnAttribute : Attribute
{
    /// <summary>Header label written to / matched from the sheet. Defaults to the member name.</summary>
    public string? Name { get; set; }

    /// <summary>Additional header texts accepted on import (case/whitespace-insensitive).</summary>
    public string[]? Aliases { get; set; }

    /// <summary>Left-to-right ordering. Lower values come first; ties keep declaration order. Default 1000.</summary>
    public int Order { get; set; } = 1000;

    /// <summary>Column width in Excel character units. Negative means "not set" (auto).</summary>
    public double Width { get; set; } = -1;

    /// <summary>Optional format string. Shorthand for <see cref="ExcelFormatAttribute"/>.</summary>
    public string? Format { get; set; }

    /// <summary>When true the header cell is required and the column is exported even for null values.</summary>
    public bool Required { get; set; }

    /// <summary>
    /// Explicit zero-based column position for name-independent (positional) import/export mapping.
    /// -1 (default) means map by header name.
    /// </summary>
    public int ColumnIndex { get; set; } = -1;

    /// <summary>Trim leading/trailing whitespace on import. Default true.</summary>
    public bool AutoTrim { get; set; } = true;

    /// <summary>Bold the column's cells (header and body). Default false.</summary>
    public bool Bold { get; set; }

    /// <summary>Horizontal alignment for the column's cells (header and body). Default leaves Excel's own alignment.</summary>
    public ExcelAlign Align { get; set; } = ExcelAlign.Default;

    public ExcelColumnAttribute() { }
    public ExcelColumnAttribute(string name) => Name = name;
}
