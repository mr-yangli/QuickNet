namespace QuickExcel;

/// <summary>Sets the export column width (Excel character units) for a member.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ExcelColumnWidthAttribute : Attribute
{
    public double Width { get; }

    public ExcelColumnWidthAttribute(double width) => Width = width;
}
