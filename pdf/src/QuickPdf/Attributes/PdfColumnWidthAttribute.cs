namespace QuickPdf;

/// <summary>Sets the export column width (Excel character units) for a member.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class PdfColumnWidthAttribute : Attribute
{
    public double Width { get; }

    public PdfColumnWidthAttribute(double width) => Width = width;
}
