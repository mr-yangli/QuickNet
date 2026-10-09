using QuickPdf.Internal;

namespace QuickPdf;

/// <summary>
/// Entry point for QuickPdf: exports any tabular data source (POCO / dictionary / ExpandoObject /
/// DataTable / <see cref="PdfSheet"/> sequences) to a PDF, using QuickPdf's own column
/// attributes (<c>[PdfColumn]</c>, <c>[PdfColumnWidth]</c>, <c>[PdfFormat]</c>, <c>[PdfIgnore]</c>).
/// A CJK-capable TrueType font from the host machine (Microsoft YaHei / SimHei / Noto …) is embedded
/// whole via Identity-H, so the file renders identically on any viewer. If no usable font is found a
/// <see cref="QuickPdfException"/> is thrown before anything is written.
/// </summary>
public static class Pdf
{
    /// <summary>Writes <paramref name="value"/> to a .pdf file, replacing it if it exists.</summary>
    public static void SaveAs(string path, object value, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        options ??= new PdfOptions();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(path, PdfTableRenderer.Render(value, options));
    }

    /// <summary>Writes <paramref name="value"/> as a complete PDF into a stream.</summary>
    public static void SaveAs(Stream stream, object value, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        options ??= new PdfOptions();
        var bytes = PdfTableRenderer.Render(value, options);
        stream.Write(bytes);
    }

    /// <summary>Asynchronously writes <paramref name="value"/> to a .pdf file.</summary>
    public static Task SaveAsAsync(string path, object value, PdfOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => SaveAs(path, value, options), ct);

    /// <summary>Serialises <paramref name="value"/> and returns the PDF bytes.</summary>
    public static byte[] GetAsByteArray(object value, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        options ??= new PdfOptions();
        return PdfTableRenderer.Render(value, options);
    }
}
