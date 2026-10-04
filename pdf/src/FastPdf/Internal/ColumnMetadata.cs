namespace FastPdf.Internal;

/// <summary>
/// One export column: header text, member type, ordering, width (Excel char units, -1 = auto),
/// optional format string, and a compiled getter. The Excel-side metadata's extra duties (setter,
/// validation) are deliberately absent here.
/// </summary>
internal sealed class ColumnMetadata
{
    public required string HeaderName { get; init; }
    public required Type MemberType { get; init; }
    public int Order { get; init; }
    public double Width { get; init; } = -1;
    public string? FormatString { get; init; }
    public required Func<object, object?> Getter { get; init; }
}
