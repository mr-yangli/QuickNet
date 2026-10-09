using System.Xml;

namespace QuickExcel.Internal.OpenXml;

/// <summary>
/// Parses xl/styles.xml just far enough to answer "is style index s a date/time format?" — the
/// only style information import needs to turn serial numbers back into DateTime values.
/// </summary>
internal sealed class StylesReader
{
    private static readonly HashSet<int> BuiltinDateTimeFmtIds = new()
    {
        14,15,16,17,18,19,20,21,22,27,30,36,45,46,47,50,51,52,53,54,55,56,57,58
    };

    private readonly int[] _cellXfNumFmtIds;
    private readonly bool[] _isDateTime;

    private StylesReader(int[] fmtIds, bool[] flags)
    {
        _cellXfNumFmtIds = fmtIds;
        _isDateTime = flags;
    }

    /// <summary>A styles reader with no styles — used when a package omits xl/styles.xml.</summary>
    public static readonly StylesReader Empty = new(Array.Empty<int>(), Array.Empty<bool>());

    public bool IsDateTimeStyle(int styleIndex) =>
        styleIndex >= 0 && styleIndex < _isDateTime.Length && _isDateTime[styleIndex];

    public static StylesReader Parse(Stream stylesPart)
    {
        if (stylesPart is MemoryStream ms && ms.Length == 0) return Empty;
        var customFmtCodes = new Dictionary<int, string>();
        var xfIds = new List<int>();
        bool inCellXfs = false;

        using var xml = XmlReader.Create(stylesPart, Settings);
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "cellXfs") inCellXfs = false;
                continue;
            }
            switch (xml.LocalName)
            {
                case "numFmt":
                {
                    string? id = xml.GetAttribute("numFmtId");
                    string? code = xml.GetAttribute("formatCode");
                    if (id is not null && code is not null && int.TryParse(id, out int fid))
                        customFmtCodes[fid] = code;
                    break;
                }
                case "cellXfs":
                    inCellXfs = true;
                    break;
                case "xf" when inCellXfs:
                {
                    string? id = xml.GetAttribute("numFmtId");
                    xfIds.Add(int.TryParse(id, out int fid) ? fid : 0);
                    break;
                }
            }
        }

        var flags = new bool[xfIds.Count];
        for (int i = 0; i < xfIds.Count; i++)
        {
            int fid = xfIds[i];
            flags[i] = BuiltinDateTimeFmtIds.Contains(fid)
                || (customFmtCodes.TryGetValue(fid, out string? code) && LooksLikeDateTimeFormat(code));
        }
        return new StylesReader(xfIds.ToArray(), flags);
    }

    /// <summary>True if a custom number format contains date/time tokens outside quotes/brackets.</summary>
    internal static bool LooksLikeDateTimeFormat(string fmt)
    {
        // strip quoted literals
        var sb = new System.Text.StringBuilder(fmt.Length);
        bool inQuote = false, inBracket = false;
        foreach (char c in fmt)
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (inQuote) continue;
            if (c == '[') { inBracket = true; continue; }
            if (c == ']') { inBracket = false; continue; }
            if (inBracket) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        string s = sb.ToString();
        // date: y, d present, or m alongside them; time: h/s, or m between h and s
        bool hasY = s.Contains('y');
        bool hasD = s.Contains('d');
        bool hasH = s.Contains('h');
        bool hasS = s.Contains('s');
        bool hasM = s.Contains('m');
        return (hasY || hasD) || (hasH || hasS) || (hasM && (hasH || hasS || hasY || hasD));
    }

    private static readonly XmlReaderSettings Settings = new()
    {
        IgnoreComments = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = false
    };
}
