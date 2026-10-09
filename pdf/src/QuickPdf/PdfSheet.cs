namespace QuickPdf;

/// <summary>Names one table within a multi-table export. Pass a sequence of these to <see cref="Pdf"/>.</summary>
public sealed class PdfSheet
{
    public string Name { get; }
    public object Rows { get; }

    public PdfSheet(string name, object rows)
    {
        Name = name;
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
    }
}
