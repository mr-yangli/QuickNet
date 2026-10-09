namespace QuickWord;

/// <summary>
/// A table read out of the document body. Cell text joins the cell's paragraphs with <c>\n</c>, which is the
/// exact inverse of how <see cref="Word"/> writes a multi-line cell.
/// </summary>
public sealed class WordTable : WordBlock
{
    internal WordTable(List<IReadOnlyList<string>> rows) : base(WordBlockKind.Table) => Rows = rows;

    /// <summary>Rows in document order; rows can differ in cell count.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>Total cell count of the widest row.</summary>
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    /// <summary>
    /// Rows as name → value dictionaries, the same shape QuickExcel's <c>Query</c> returns. With
    /// <paramref name="useHeaderRow"/> the first row supplies the keys (blank or repeated headers get a
    /// positional suffix); otherwise keys are <c>Column1…ColumnN</c>. Remaining rows become the values.
    /// </summary>
    public IEnumerable<IDictionary<string, object?>> Query(bool useHeaderRow = true)
    {
        if (Rows.Count == 0) yield break;

        int start = useHeaderRow ? 1 : 0;
        var headers = useHeaderRow ? UniqueHeaders(Rows[0]) : GeneratedHeaders(ColumnCount);
        for (int r = start; r < Rows.Count; r++)
        {
            var row = Rows[r];
            var map = new Dictionary<string, object?>(headers.Count);
            for (int c = 0; c < headers.Count; c++)
                map[headers[c]] = c < row.Count ? row[c] : null;
            yield return map;
        }
    }

    private static List<string> GeneratedHeaders(int count)
        => Enumerable.Range(1, count).Select(i => $"Column{i}").ToList();

    private static List<string> UniqueHeaders(IReadOnlyList<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            string head = raw[i].Trim();
            if (head.Length == 0) head = $"Column{i + 1}";
            string key = head;
            for (int n = 2; !seen.Add(key); n++) key = $"{head}({n})";
            result.Add(key);
        }
        return result;
    }
}
