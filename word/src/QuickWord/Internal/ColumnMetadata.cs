namespace QuickWord.Internal;

/// <summary>
/// One column of a Word table: header text, compiled value getter, format string and width.
/// QuickWord's own copy — export-only, so the import machinery (setter / header index / factory /
/// validation) that QuickExcel's richer ExcelColumnMetadata carries is deliberately absent here.
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
