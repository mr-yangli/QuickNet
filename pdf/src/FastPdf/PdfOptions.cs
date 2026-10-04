namespace FastPdf;

/// <summary>Settings for exporting a data table to PDF.</summary>
public sealed class PdfOptions
{
    /// <summary>Document title rendered above the table; null omits it.</summary>
    public string? Title { get; set; }

    /// <summary>Emit a shaded header row from the column names; repeated on every page. Default true.</summary>
    public bool PrintHeader { get; set; } = true;

    /// <summary>Use A4 landscape instead of portrait.</summary>
    public bool Landscape { get; set; }

    /// <summary>Page margins in points. Default 56.7 (2 cm).</summary>
    public double MarginPt { get; set; } = 56.7;

    /// <summary>Cell font size in points. Default 9.</summary>
    public double FontSizePt { get; set; } = 9;

    /// <summary>Title font size in points. Default 16.</summary>
    public double TitleSizePt { get; set; } = 16;

    /// <summary>
    /// TrueType font to embed. Null searches the usual locations for Microsoft YaHei / SimHei / Noto Sans CJK
    /// (Windows and common Linux/WPS installs). The chosen font is embedded whole (identity-H), so the PDF
    /// renders identically everywhere.
    /// </summary>
    public string? FontFile { get; set; }

    /// <summary>Extra directories (separated by the platform path separator) searched for fonts.</summary>
    public string? FontDirectory { get; set; }

    public PdfOptions() { }
    public PdfOptions Clone() => (PdfOptions)MemberwiseClone();
}
