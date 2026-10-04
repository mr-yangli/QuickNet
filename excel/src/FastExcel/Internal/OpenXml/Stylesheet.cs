using System.Globalization;
using System.Text;

namespace FastExcel.Internal.OpenXml;

/// <summary>
/// Builds xl/styles.xml on demand. Style indices are interned by (numFmt, bold, alignment) so the
/// per-cell style lookup on the hot write path is a single dictionary hit.
/// </summary>
internal sealed class Stylesheet
{
    // Built-in ids we use for dates/times.
    public const int NumFmtGeneral = 0;
    public const int NumFmtDateShort = 14;      // m/d/yyyy
    public const int NumFmtDateTime = 22;       // m/d/yy h:mm
    public const int NumFmtTime = 21;           // h:mm:ss AM/PM

    // numFmtId >= 164 is the first slot available for custom formats.
    private const int FirstCustomNumFmtId = 164;

    private readonly Dictionary<string, int> _customNumFmtIds = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<string, int>> _customNumFmts = new();
    private readonly Dictionary<XfKey, int> _xfIndex = new();
    private readonly List<string> _xfs = new();

    public Stylesheet()
    {
        // Seed the always-present default xf (index 0 = General, non-bold).
        Intern(new XfKey(NumFmtGeneral, false, false));
    }

    /// <summary>Resolves the style index for a value category with an optional custom format string.</summary>
    public int GetStyleIndex(XlCellCategory category, string? formatString, bool bold = false, bool wrap = false)
    {
        int numFmtId = ResolveNumFmtId(category, formatString);
        return Intern(new XfKey(numFmtId, bold, wrap));
    }

    private int ResolveNumFmtId(XlCellCategory category, string? formatString)
    {
        if (!string.IsNullOrEmpty(formatString))
            return GetOrAddCustomNumFmt(formatString!);

        return category switch
        {
            XlCellCategory.Date => NumFmtDateShort,
            XlCellCategory.Time => NumFmtDateTime,
            _ => NumFmtGeneral
        };
    }

    private int GetOrAddCustomNumFmt(string fmt)
    {
        if (_customNumFmtIds.TryGetValue(fmt, out int id)) return id;
        id = FirstCustomNumFmtId + _customNumFmts.Count;
        _customNumFmtIds[fmt] = id;
        _customNumFmts.Add(new KeyValuePair<string, int>(fmt, id));
        return id;
    }

    private int Intern(XfKey key)
    {
        if (_xfIndex.TryGetValue(key, out int idx)) return idx;
        idx = _xfs.Count;
        _xfs.Add(BuildXf(key));
        _xfIndex[key] = idx;
        return idx;
    }

    private static string BuildXf(XfKey key)
    {
        var sb = new StringBuilder(96);
        sb.Append("<xf numFmtId=\"").Append(key.NumFmtId)
          .Append("\" fontId=\"").Append(key.Bold ? 1 : 0)
          .Append("\" fillId=\"0\" borderId=\"0\" xfId=\"0\"");
        if (key.NumFmtId != NumFmtGeneral) sb.Append(" applyNumberFormat=\"1\"");
        if (key.Bold) sb.Append(" applyFont=\"1\"");
        if (key.Wrap) sb.Append(" applyAlignment=\"1\"><alignment wrapText=\"1\"/></xf");
        else sb.Append("/>");
        return sb.ToString();
    }

    public string BuildXml()
    {
        var sb = new StringBuilder(512);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        if (_customNumFmts.Count > 0)
        {
            sb.Append("<numFmts count=\"").Append(_customNumFmts.Count).Append("\">");
            foreach (var kv in _customNumFmts)
                sb.Append("<numFmt numFmtId=\"").Append(kv.Value)
                  .Append("\" formatCode=\"").Append(EscapeFormat(kv.Key)).Append("\"/>");
            sb.Append("</numFmts>");
        }

        sb.Append("<fonts count=\"2\">")
          .Append("<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>")
          .Append("<font><b/><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>")
          .Append("</fonts>");

        sb.Append("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>");
        sb.Append("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");
        sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

        sb.Append("<cellXfs count=\"").Append(_xfs.Count).Append("\">");
        foreach (var xf in _xfs) sb.Append(xf);
        sb.Append("</cellXfs>");

        sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    private static string EscapeFormat(string fmt)
    {
        var sb = new StringBuilder(fmt.Length + 8);
        foreach (char c in fmt)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private readonly record struct XfKey(int NumFmtId, bool Bold, bool Wrap);
}
