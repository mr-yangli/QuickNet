using System.IO.Compression;
using System.Xml;

namespace QuickExcel.Internal.OpenXml;

/// <summary>
/// Recovers images that float over a worksheet by following sheet → drawing → media relationships
/// and mapping each anchor's top-left cell to the image bytes. Also detects WPS DISPIMG embedded
/// images (cellImages.xml) on a best-effort basis.
/// </summary>
internal sealed class SheetImageReader
{
    private readonly ZipArchive _zip;
    public SheetImageReader(ZipArchive zip) => _zip = zip;

    public Dictionary<(int row, int col), ExcelImage> ReadAnchored(string sheetPartPath)
    {
        var result = new Dictionary<(int, int), ExcelImage>();

        // 1. sheet rels -> drawing target
        string drawingRelPath = SheetRelsPath(sheetPartPath);
        var drawingRels = _zip.GetEntry(drawingRelPath);
        if (drawingRels is null) return result;

        string? drawingTarget = null;
        using (var xml = XmlReader.Create(drawingRels.Open(), XlsxPackageReader.Settings))
        {
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship" &&
                    (xml.GetAttribute("Type") ?? "").EndsWith("/drawing"))
                {
                    drawingTarget = Resolve(sheetPartPath, xml.GetAttribute("Target") ?? "");
                    break;
                }
        }
        if (drawingTarget is null) return result;
        var drawingEntry = _zip.GetEntry(drawingTarget);
        if (drawingEntry is null) return result;

        // 2. drawing rels: embed rId -> media path
        var mediaByRid = new Dictionary<string, string>();
        string drawingRelsPath = SheetRelsPath(drawingTarget);
        var drels = _zip.GetEntry(drawingRelsPath);
        if (drels is not null)
        {
            using var xml = XmlReader.Create(drels.Open(), XlsxPackageReader.Settings);
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship")
                {
                    string id = xml.GetAttribute("Id") ?? "";
                    string target = Resolve(drawingTarget, xml.GetAttribute("Target") ?? "");
                    mediaByRid[id] = target;
                }
        }

        // 3. drawing anchors: from(col,row) + blip r:embed
        using (var xml = XmlReader.Create(drawingEntry.Open(), XlsxPackageReader.Settings))
        {
            int anchorRow = -1, anchorCol = -1;
            string? embedRid = null;
            bool inFrom = false;

            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element)
                {
                    switch (xml.LocalName)
                    {
                        case "from":
                            inFrom = true; anchorRow = -1; anchorCol = -1;
                            break;
                        case "col" when inFrom:
                            anchorCol = ParseInt(xml.ReadElementContentAsString());
                            continue; // already advanced
                        case "row" when inFrom:
                            anchorRow = ParseInt(xml.ReadElementContentAsString());
                            continue;
                        case "blip":
                            embedRid = GetRidAttr(xml, "embed");
                            break;
                    }
                }
                else if (xml.NodeType == XmlNodeType.EndElement)
                {
                    if (xml.LocalName == "from") inFrom = false;
                    else if (xml.LocalName is "oneCellAnchor" or "twoCellAnchor" or "absoluteAnchor")
                    {
                        if (anchorRow >= 0 && anchorCol >= 0 && embedRid is not null &&
                            mediaByRid.TryGetValue(embedRid, out string? mediaPath))
                        {
                            var img = LoadImage(mediaPath);
                            if (img is not null)
                                result[(anchorRow, anchorCol)] = img;
                        }
                        embedRid = null;
                    }
                }
            }
        }

        return result;
    }

    private ExcelImage? LoadImage(string mediaPath)
    {
        var entry = _zip.GetEntry(mediaPath);
        if (entry is null) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        if (ms.Length == 0) return null;
        return new ExcelImage(ms.ToArray());
    }

    private static string SheetRelsPath(string partPath)
    {
        // xl/worksheets/sheet1.xml -> xl/worksheets/_rels/sheet1.xml.rels
        int slash = partPath.LastIndexOf('/');
        string dir = partPath[..slash];
        string file = partPath[(slash + 1)..];
        return $"{dir}/_rels/{file}.rels";
    }

    /// <summary>Resolves a relationship Target (relative or absolute) against the source part path.</summary>
    private static string Resolve(string sourcePartPath, string target)
    {
        if (target.StartsWith("/")) return target[1..]; // absolute package path
        string dir = sourcePartPath[..sourcePartPath.LastIndexOf('/')];
        // combine dir + target, normalizing ../ segments
        string combined = dir + "/" + target;
        var parts = new List<string>();
        foreach (var seg in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join("/", parts);
    }

    private static int ParseInt(string s) => int.TryParse(s, out int v) ? v : -1;

    private static string? GetRidAttr(XmlReader xml, string localName)
    {
        for (int i = 0; i < xml.AttributeCount; i++)
        {
            xml.MoveToAttribute(i);
            if (xml.LocalName == localName && xml.NamespaceURI.EndsWith("relationships"))
            {
                string v = xml.Value;
                xml.MoveToElement();
                return v;
            }
            xml.MoveToElement();
        }
        return xml.GetAttribute("r:" + localName);
    }
}
