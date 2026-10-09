namespace QuickWord;

/// <summary>
/// A .docx body read as an ordered list of blocks (paragraphs and tables). Nothing is interpreted here —
/// headings, list items and tables are exposed as-is so a caller can apply its own document rules.
/// </summary>
public sealed class WordDocument
{
    internal WordDocument(List<WordBlock> blocks) => Blocks = blocks;

    /// <summary>Body content in document order.</summary>
    public IReadOnlyList<WordBlock> Blocks { get; }

    public IEnumerable<WordParagraph> Paragraphs => Blocks.OfType<WordParagraph>();

    public IEnumerable<WordTable> Tables => Blocks.OfType<WordTable>();
}
