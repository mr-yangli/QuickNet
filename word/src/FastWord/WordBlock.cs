namespace FastWord;

/// <summary>What kind of top-level content a <see cref="WordBlock"/> is.</summary>
public enum WordBlockKind
{
    Paragraph,
    Table,
}

/// <summary>One block of body content: either a <see cref="WordParagraph"/> or a <see cref="WordTable"/>.</summary>
public abstract class WordBlock
{
    internal WordBlock(WordBlockKind kind) => Kind = kind;

    public WordBlockKind Kind { get; }
}
