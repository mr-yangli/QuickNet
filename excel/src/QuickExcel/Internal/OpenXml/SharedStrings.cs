using System.Text;

namespace QuickExcel.Internal.OpenXml;

/// <summary>
/// Interns unique strings and emits xl/sharedStrings.xml. In this mode every cell references the
/// table by index (t="s"), so repeated text costs one dictionary hit at write time and zero bytes per row.
/// Default export uses inline strings instead, which needs no bookkeeping.
/// </summary>
internal sealed class SharedStrings
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string> _values = new();
    private int _uniqueCount;

    public int Count => _values.Count;
    public int TotalReferences { get; private set; }

    public int GetOrAdd(string value)
    {
        TotalReferences++;
        if (_index.TryGetValue(value, out int idx)) return idx;
        idx = _values.Count;
        _values.Add(value);
        if (value.Length > 0 && value[0] is ' ' or '\t' or '\n' or '\r') _uniqueCount++; // track xml:space need
        _index[value] = idx;
        return idx;
    }

    public string BuildXml()
    {
        var sb = new StringBuilder(Math.Max(256, _values.Count * 24));
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"")
          .Append(TotalReferences).Append("\" uniqueCount=\"").Append(_values.Count).Append("\">");
        foreach (var v in _values)
        {
            sb.Append("<si><t");
            if (v.Length > 0 && (v[0] == ' ' || v[^1] == ' ' || v[0] == '\t' || v[^1] == '\t' || v[0] == '\n' || v[^1] == '\n' || v[0] == '\r' || v[^1] == '\r'))
                sb.Append(" xml:space=\"preserve\"");
            sb.Append('>').Append(XmlUtility.Escape(v)).Append("</t></si>");
        }
        sb.Append("</sst>");
        return sb.ToString();
    }
}
