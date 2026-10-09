namespace QuickWord;

/// <summary>Names one table within a multi-table export. Pass a sequence of these to <see cref="Word"/>.</summary>
public sealed class WordSheet
{
    public string Name { get; }
    public object Rows { get; }

    public WordSheet(string name, object rows)
    {
        Name = name;
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
    }
}
