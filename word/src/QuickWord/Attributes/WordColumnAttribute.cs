namespace QuickWord;

/// <summary>
/// Marks a property as a table column and lets you rename it, order it, set width and format.
/// When <see cref="Name"/> is omitted the property name is used.
/// Same shape QuickExcel's <c>[ExcelColumn]</c> has, as its own type — QuickWord carries no project reference.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class WordColumnAttribute : Attribute
{
    /// <summary>Header label written to the table. Defaults to the member name.</summary>
    public string? Name { get; set; }

    /// <summary>Left-to-right ordering. Lower values come first; ties keep declaration order. Default 1000.</summary>
    public int Order { get; set; } = 1000;

    /// <summary>Column width in Excel character units. Negative means "not set" (auto).</summary>
    public double Width { get; set; } = -1;

    /// <summary>Optional format string. Shorthand for <see cref="WordFormatAttribute"/>.</summary>
    public string? Format { get; set; }

    public WordColumnAttribute() { }
    public WordColumnAttribute(string name) => Name = name;
}
