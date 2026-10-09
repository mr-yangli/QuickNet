using System.Globalization;
using System.Xml;

namespace QuickExcel.Internal.OpenXml;

/// <summary>
/// A single worksheet row reused across iterations to keep allocation flat while streaming.
/// Values are already resolved to CLR primitives (string / double / bool / DateTime). Consumers must
/// read a row fully before advancing the enumerator.
/// </summary>
internal sealed class ExcelRow
{
    private Dictionary<int, object?> _cells = new();
    public int RowNumber { get; private set; }
    public int MaxColumn { get; private set; } = -1;

    public void Start(int rowNum)
    {
        RowNumber = rowNum;
        MaxColumn = -1;
        if (_cells.Count > 0) _cells.Clear();
    }

    public void Set(int column, object? value)
    {
        if (value is null) return;
        _cells[column] = value;
        if (column > MaxColumn) MaxColumn = column;
    }

    public bool HasAny => _cells.Count > 0;
    public bool TryGet(int column, out object? value) => _cells.TryGetValue(column, out value);

    public IEnumerable<KeyValuePair<int, object?>> Cells() => _cells;

    /// <summary>Snapshot into a fresh dictionary (used for dynamic query results that outlive iteration).</summary>
    public Dictionary<int, object?> Snapshot() => new(_cells);
}

/// <summary>
/// Forward-only SAX reader over one worksheet's XML. Resolves shared strings, inline strings and
/// style-based dates; unknown/empty cells are omitted. Exposes a single reusable <see cref="ExcelRow"/>.
/// </summary>
internal sealed class WorksheetReader : IDisposable
{
    private readonly XmlReader _xml;
    private readonly string[] _shared;
    private readonly StylesReader _styles;
    private readonly ExcelRow _row = new();
    private readonly List<MergeRange>? _merges;
    private int _mergePtr;
    private readonly List<(int r0, int c0, int r1, int c1, object? value)> _openMerges = new();

    public WorksheetReader(XmlReader xml, string[] shared, StylesReader styles, List<MergeRange>? merges = null)
    {
        _xml = xml;
        _shared = shared;
        _styles = styles;
        if (merges is { Count: > 0 })
        {
            merges.Sort((a, b) => a.Row0 != b.Row0 ? a.Row0.CompareTo(b.Row0) : a.Col0.CompareTo(b.Col0));
            _merges = merges;
        }
    }

    /// <summary>The current row after <see cref="Read"/>. Reused instance — snapshot() it to retain.</summary>
    public ExcelRow Row => _row;
    public int RowNumber => _row.RowNumber;
    public bool HasAny => _row.HasAny;
    public bool TryGet(int column, out object? value) => _row.TryGet(column, out value);
    public IEnumerable<KeyValuePair<int, object?>> Cells() => _row.Cells();

    /// <summary>Advance to the next row. Returns false at end of sheet.</summary>
    public bool Read()
    {
        // Find the next <row>.
        while (_xml.Read())
        {
            if (_xml.NodeType == XmlNodeType.Element && _xml.LocalName == "row")
                break;
        }
        if (_xml.EOF || _xml.NodeType != XmlNodeType.Element || _xml.LocalName != "row")
            return false;

        int rowNum = ParseRowNumber(_xml.GetAttribute("r"));
        _row.Start(rowNum > 0 ? rowNum : _row.RowNumber + 1);

        if (_xml.IsEmptyElement)
        {
            ApplyMerges();
            return true;
        }

        while (_xml.Read())
        {
            if (_xml.NodeType == XmlNodeType.EndElement && _xml.LocalName == "row")
                break;
            if (_xml.NodeType != XmlNodeType.Element || _xml.LocalName != "c")
                continue;

            string? cellRef = _xml.GetAttribute("r");
            string? type = _xml.GetAttribute("t");
            string? styleStr = _xml.GetAttribute("s");
            int column = cellRef is not null ? CellReference.ParseColumn(cellRef) : _row.MaxColumn + 1;
            int style = styleStr is not null && int.TryParse(styleStr, out int s) ? s : 0;
            bool selfClosing = _xml.IsEmptyElement;

            object? value = null;
            if (!selfClosing)
                value = ReadCellValue(type, style);

            if (value is not null)
                _row.Set(column, value);
        }
        ApplyMerges();
        return true;
    }

    /// <summary>
    /// Propagates merged blocks' top-left value into the empty cells the range covers on the current row.
    /// OOXML only stores the value once (top-left); Excel/WPS just paint the whole block. Ranges activate on
    /// their top-left row (capturing the value there) and stay open until their last row streams past.
    /// </summary>
    private void ApplyMerges()
    {
        if (_merges is null) return;
        int rn = _row.RowNumber;

        while (_mergePtr < _merges.Count && _merges[_mergePtr].Row0 <= rn)
        {
            var m = _merges[_mergePtr++];
            object? topLeft = null;
            if (rn == m.Row0) _row.TryGet(m.Col0, out topLeft);
            if (topLeft is not null) _openMerges.Add((m.Row0, m.Col0, m.Row1, m.Col1, topLeft));
        }
        if (_openMerges.Count == 0) return;

        for (int i = _openMerges.Count - 1; i >= 0; i--)
        {
            var (r0, c0, r1, c1, value) = _openMerges[i];
            if (rn > r1) { _openMerges.RemoveAt(i); continue; }
            for (int c = c0; c <= c1; c++)
            {
                if (rn == r0 && c == c0) continue;
                if (_row.TryGet(c, out _)) continue; // cells only ever hold non-null values
                _row.Set(c, value);
            }
        }
    }

    private object? ReadCellValue(string? type, int style)
    {
        switch (type)
        {
            case "inlineStr":
                return ReadInlineString();
            case "s": // shared string index
            {
                string? idx = ReadValueText();
                if (idx is not null && int.TryParse(idx, out int i) && i >= 0 && i < _shared.Length)
                    return _shared[i];
                return null;
            }
            case "str": // cached formula string result
            case "e":   // error
                return ReadValueText();
            case "b":
            {
                string? v = ReadValueText();
                return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }
            default:
            {
                string? v = ReadValueText();
                if (v is null || v.Length == 0) return null;
                if (!double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    return v; // non-numeric in a numeric cell -> keep text
                if (_styles.IsDateTimeStyle(style))
                {
                    try { return DateTime.FromOADate(d); } catch { return d; }
                }
                return d;
            }
        }
    }

    private string? ReadInlineString()
    {
        // <c t="inlineStr"><is><t>...</t></is></c>  (is may contain multiple <r><t> runs)
        var sb = new System.Text.StringBuilder();
        int depth = 1;
        bool inT = false;
        while (_xml.Read())
        {
            if (_xml.NodeType == XmlNodeType.Element)
            {
                if (_xml.LocalName == "t") inT = true;
                else depth++;
            }
            else if (_xml.NodeType == XmlNodeType.Text && inT)
                sb.Append(_xml.Value);
            else if (_xml.NodeType == XmlNodeType.EndElement)
            {
                if (_xml.LocalName == "t") inT = false;
                else if (_xml.LocalName == "is") break;
                else depth--;
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private string? ReadValueText()
    {
        // Reader is positioned at the <c> start element. Scan forward to the matching </c>,
        // capturing any <v> text. ReadElementContentAsString leaves the reader just after </v>,
        // which is often </c> itself — must not blindly Read() again or we skip the cell boundary.
        string? result = null;
        while (_xml.Read())
        {
            if (_xml.NodeType == XmlNodeType.Element && _xml.LocalName == "v")
            {
                result = _xml.ReadElementContentAsString();
                if (_xml.NodeType == XmlNodeType.EndElement && _xml.LocalName == "c")
                    break; // already on the closing tag
            }
            else if (_xml.NodeType == XmlNodeType.EndElement && _xml.LocalName == "c")
                break;
        }
        return result;
    }

    private static int ParseRowNumber(string? r) =>
        r is not null && int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;

    public void Dispose() => _xml.Dispose();
}
