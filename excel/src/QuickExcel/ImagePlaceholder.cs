namespace QuickExcel;

/// <summary>
/// An image value for a <c>{{@placeholder}}</c> in a template, with an optional explicit render size.
/// Without Width/Height the image's native pixel size is used. Implicitly convertible from
/// <see cref="ExcelImage"/> and <see cref="T:byte[]"/>.
/// </summary>
public sealed class ImagePlaceholder
{
    public ExcelImage Image { get; }

    /// <summary>Render width in pixels. Null = native width.</summary>
    public int? Width { get; set; }

    /// <summary>Render height in pixels. Null = native height.</summary>
    public int? Height { get; set; }

    public ImagePlaceholder(ExcelImage image) => Image = image ?? throw new ArgumentNullException(nameof(image));

    public ImagePlaceholder(byte[] data) : this(new ExcelImage(data)) { }

    public static ImagePlaceholder FromFile(string path, int? width = null, int? height = null) =>
        new(ExcelImage.FromFile(path)) { Width = width, Height = height };

    public static implicit operator ImagePlaceholder(ExcelImage image) => new(image);
    public static implicit operator ImagePlaceholder(byte[] data) => new(data);

    internal int EffectiveWidth => Width ?? (Image.PixelWidth > 0 ? Image.PixelWidth : 100);
    internal int EffectiveHeight => Height ?? (Image.PixelHeight > 0 ? Image.PixelHeight : 100);
}
