namespace QuickExcel;

/// <summary>
/// Marks a member as an image column. On export the image is floated onto the cell via a drawing anchor;
/// on import the anchored image bytes are mapped back to the member.
/// Controls placement size in pixels.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ExcelImageAttribute : Attribute
{
    /// <summary>Rendered width in pixels. 0 keeps the native size.</summary>
    public int Width { get; set; }

    /// <summary>Rendered height in pixels. 0 keeps the native size.</summary>
    public int Height { get; set; }

    /// <summary>When true the row height / column width is auto grown so the image fits.</summary>
    public bool AutoFitRow { get; set; } = true;

    public ExcelImageAttribute() { }
    public ExcelImageAttribute(int width, int height)
    {
        Width = width;
        Height = height;
    }
}
