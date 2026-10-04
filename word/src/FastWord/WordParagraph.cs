namespace FastWord;

/// <summary>
/// A body paragraph flattened to text: <c>w:t</c> content, <c>w:tab</c> as <c>\t</c>, <c>w:br</c>/<c>w:cr</c> as
/// <c>\n</c>. Field instruction text (<c>w:instrText</c>) is dropped — only what the reader sees is kept.
/// </summary>
public sealed class WordParagraph : WordBlock
{
    internal WordParagraph(string text, string? styleName, bool isListItem)
        : base(WordBlockKind.Paragraph)
    {
        Text = text;
        StyleName = styleName;
        IsListItem = isListItem;
    }

    /// <summary>Paragraph text, line breaks as <c>\n</c>.</summary>
    public string Text { get; }

    /// <summary>
    /// Raw <c>w:pStyle/w:val</c> (e.g. <c>DocTitle</c>, <c>Heading1</c>), null when the paragraph uses the
    /// default style. This is the style <em>id</em> from the package, not a localised display name.
    /// </summary>
    public string? StyleName { get; }

    /// <summary>True when the paragraph carries numbering (<c>w:numPr</c>), i.e. a bullet or list item.</summary>
    public bool IsListItem { get; }
}
