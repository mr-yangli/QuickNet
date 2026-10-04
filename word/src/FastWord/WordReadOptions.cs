namespace FastWord;

/// <summary>Settings for reading a .docx package.</summary>
public sealed class WordReadOptions
{
    /// <summary>
    /// Keep paragraphs with no text. Default false: <see cref="Word"/> writes a blank separator paragraph
    /// after every table, and hand-made documents use them as spacing, so they carry no content.
    /// </summary>
    public bool IncludeEmptyParagraphs { get; set; }

    public WordReadOptions() { }
    public WordReadOptions Clone() => (WordReadOptions)MemberwiseClone();
}
