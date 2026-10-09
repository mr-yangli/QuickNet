namespace QuickWord;

/// <summary>Settings for exporting a data table to .docx.</summary>
public sealed class WordOptions
{
    /// <summary>Document heading rendered above the table; null omits it.</summary>
    public string? Title { get; set; }

    /// <summary>Emit a shaded, bold header row from the column names. Default true.</summary>
    public bool PrintHeader { get; set; } = true;

    /// <summary>Header row background as RRGGBB hex. Default "EEEEEE".</summary>
    public string HeaderFill { get; set; } = "EEEEEE";

    /// <summary>Cell font size in points (half-points internally). Default 10.5 (Word 五号).</summary>
    public double FontSizePt { get; set; } = 10.5;

    /// <summary>Comma-separated table border colour. Default "999999"; use "000000" for strong borders.</summary>
    public string BorderColor { get; set; } = "999999";

    /// <summary>
    /// Page footer repeated on every page, centred at 9 pt grey. Null (default) omits the footer part entirely.
    /// The tokens <c>{PAGE}</c> and <c>{NUMPAGES}</c> render as live Word page-number fields (the host
    /// repaginates on open); any other text is literal.
    /// </summary>
    public string? FooterText { get; set; }

    public WordOptions() { }
    public WordOptions Clone() => (WordOptions)MemberwiseClone();
}
