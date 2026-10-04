using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FastExcel.Internal.OpenXml;

/// <summary>
/// WPS compatibility: WPS stores "in-cell" pictures as <c>=_xlfn.DISPIMG("ID_x",1)</c> formulas
/// plus an <c>xl/cellimages.xml</c> map (ID -> media part). This reads both sides so such sheets
/// import like any other image column.
/// </summary>
internal static class CellImageReader
{
    /// <summary>Maps the DISPIMG id (cNvPr/@name) to the image bytes stored in xl/media.</summary>
    public static Dictionary<string, ExcelImage>? Read(ZipArchive zip)
    {
        var part = zip.GetEntry("xl/cellimages.xml");
        if (part is null) return null;

        // rid -> media path
        var ridToTarget = new Dictionary<string, string>();
        var rels = zip.GetEntry("xl/_rels/cellimages.xml.rels");
        if (rels is not null)
        {
            using var rx = XmlReader.Create(rels.Open(), XlsxPackageReader.Settings);
            while (rx.Read())
                if (rx.NodeType == XmlNodeType.Element && rx.LocalName == "Relationship")
                {
                    var id = rx.GetAttribute("Id") ?? "";
                    var target = rx.GetAttribute("Target") ?? "";
                    ridToTarget[id] = Resolve(target);
                }
        }

        var map = new Dictionary<string, ExcelImage>(StringComparer.OrdinalIgnoreCase);
        using (var xml = XmlReader.Create(part.Open(), XlsxPackageReader.Settings))
        {
            string? currentName = null;
            string? currentRid = null;
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element) continue;
                switch (xml.LocalName)
                {
                    case "cNvPr":
                        currentName = xml.GetAttribute("name");
                        break;
                    case "blip":
                        currentRid = GetEmbedRid(xml);
                        break;
                }
                if (currentName is not null && currentRid is not null)
                {
                    if (ridToTarget.TryGetValue(currentRid, out var mediaPath)
                        && zip.GetEntry(mediaPath) is { } media && !map.ContainsKey(currentName))
                    {
                        using var ms = new MemoryStream();
                        using (var es = media.Open()) es.CopyTo(ms);
                        try { map[currentName] = new ExcelImage(new ReadOnlyMemory<byte>(ms.ToArray())); }
                        catch { /* unknown format: skip */ }
                    }
                    currentName = null;
                    currentRid = null;
                }
            }
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Scans a worksheet for DISPIMG formula cells and returns (zero-based row, col) -> image id.
    /// </summary>
    public static Dictionary<(int row, int col), string> ScanSheetFormulas(ZipArchive zip, string sheetPartPath)
    {
        var result = new Dictionary<(int, int), string>();
        var entry = zip.GetEntry(sheetPartPath);
        if (entry is null) return result;

        using var xml = XmlReader.Create(entry.Open(), XlsxPackageReader.Settings);
        int curRow = -1, curCol = -1;

        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element) continue;
            switch (xml.LocalName)
            {
                case "row":
                    if (int.TryParse(xml.GetAttribute("r"), out int rn)) curRow = rn - 1;
                    break;
                case "c":
                    var r = xml.GetAttribute("r");
                    if (!string.IsNullOrEmpty(r))
                    {
                        var (rr, cc) = CellReference.Parse(r);
                        curRow = rr; curCol = cc;
                    }
                    break;
                case "f":
                    string text = xml.ReadElementContentAsString(); // leaves reader at next node
                    int a = text.IndexOf("DISPIMG(\"", StringComparison.OrdinalIgnoreCase);
                    if (a >= 0)
                    {
                        a += 9;
                        int b = text.IndexOf('"', a);
                        if (b > a && curRow >= 0)
                            result[(curRow, curCol)] = text[a..b];
                    }
                    break;
            }
        }
        return result;
    }

    private static string GetEmbedRid(XmlReader xml)
    {
        for (int i = 0; i < xml.AttributeCount; i++)
        {
            xml.MoveToAttribute(i);
            if (xml.LocalName == "embed" && xml.NamespaceURI.EndsWith("relationships"))
            {
                string v = xml.Value;
                xml.MoveToElement();
                return v;
            }
        }
        xml.MoveToElement();
        return "";
    }

    private static string Resolve(string target)
    {
        if (target.StartsWith('/')) return target[1..];
        var sb = new StringBuilder("xl/");
        foreach (var seg in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") continue;            // rels are relative to xl/ for cellimages
            if (seg == ".") continue;
            sb.Append(seg).Append('/');
        }
        return sb.ToString().TrimEnd('/');
    }
}
