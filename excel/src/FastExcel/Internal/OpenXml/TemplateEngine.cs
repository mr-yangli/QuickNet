using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FastExcel.Internal.OpenXml;

/// <summary>
/// MiniExcel-style template renderer: loads an .xlsx template, substitutes <c>{{Name}}</c>
/// placeholders, expands <c>{{collection.field}}</c> rows (preserving cell styles), embeds
/// <c>{{@image}}</c> placeholders as floating pictures, and re-packages everything else
/// (styles, theme, template logos, other sheets) byte-for-byte.
/// </summary>
internal static class TemplateEngine
{
    private static readonly Regex PlaceholderRx = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private static readonly XNamespace Rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static void Render(Stream templateStream, Stream outputStream, object value, TemplateOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        using var pkg = XlsxPackageReader.Open(templateStream);
        var sheetInfo = pkg.ResolveSheet(options.SheetName);
        string sheetPart = sheetInfo.PartPath;
        string[] sst = pkg.SharedStrings;

        var sheetEntry = pkg.Archive.GetEntry(sheetPart)
            ?? throw new InvalidDataException($"Template worksheet part '{sheetPart}' is missing.");
        XDocument doc;
        using (var es = sheetEntry.Open()) doc = XDocument.Load(es);

        var ns = doc.Root!.Name.Namespace;
        var sheetData = doc.Root.Element(ns + "sheetData");
        if (sheetData is null)
        {
            sheetData = new XElement(ns + "sheetData");
            doc.Root.Add(sheetData);
        }

        // ---- analyse ----
        var templateRows = sheetData.Elements(ns + "row").ToList();
        sheetData.RemoveNodes();

        var media = new MediaCollector(NextMediaIndex(pkg.Archive));
        var anchors = new List<DrawingAnchor>();
        int curRow = 1;
        int maxCol = 0;
        int i = 0;
        while (i < templateRows.Count)
        {
            var info = AnalyzeRow(templateRows[i], ns, sst, value);
            if (info.CollectionName is null)
            {
                RenderRow(info.Row, ns, sst, info, item: null, collectionName: null, root: value,
                          options, media, anchors, curRow);
                sheetData.Add(info.Row);
                maxCol = Math.Max(maxCol, info.MaxColumn);
                curRow++;
                i++;
                continue;
            }

            // consecutive rows bound to the same collection form one expansion block
            int j = i;
            while (j < templateRows.Count)
            {
                var next = AnalyzeRow(templateRows[j], ns, sst, value);
                if (next.CollectionName is null ||
                    !string.Equals(next.CollectionName, info.CollectionName, StringComparison.OrdinalIgnoreCase))
                    break;
                j++;
            }

            // re-analyse (cached by re-running; cheap for template sizes)
            if (info.CollectionItems is { Count: > 0 })
            {
                for (int k = i; k < j; )
                {
                    var bi = AnalyzeRow(templateRows[k], ns, sst, value);
                    foreach (var item in info.CollectionItems)
                    {
                        var clone = new XElement(bi.Row);
                        RenderRow(clone, ns, sst, bi, item, info.CollectionName, value, options, media, anchors, curRow);
                        sheetData.Add(clone);
                        maxCol = Math.Max(maxCol, bi.MaxColumn);
                        curRow++;
                    }
                    k++;
                }
            }
            // collection empty => the block's rows are dropped entirely
            i = j;
        }

        int lastRow = curRow - 1;
        UpdateDimension(doc, ns, lastRow, maxCol);

        // ---- package assembly ----
        string sheetRelsPart = RelsPath(sheetPart);
        string? existingSheetRels = pkg.Archive.GetEntry(sheetRelsPart) is { } sre
            ? Encoding.UTF8.GetString(ReadEntry(sre))
            : null;
        string? existingDrawingPart = FindDrawingTarget(existingSheetRels, sheetPart);

        var overrides = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var additions = new List<(string Name, byte[] Data)>();
        string contentTypes = Encoding.UTF8.GetString(ReadEntry(
            pkg.Archive.GetEntry("[Content_Types].xml")
            ?? throw new InvalidDataException("Template has no [Content_Types].xml.")));

        if (anchors.Count > 0)
        {
            var dw = new DrawingWriter(NextDrawingIndex(pkg.Archive));
            foreach (var a in anchors) dw.Add(a);
            var (ourDrawingXml, ourDrawingRels) = dw.Build();

            if (existingDrawingPart is not null && pkg.Archive.GetEntry(existingDrawingPart) is { } edp)
            {
                // Case A: merge our anchors into the template's drawing so logos survive.
                var drawingRelsEntry = pkg.Archive.GetEntry(RelsPath(existingDrawingPart));
                int ridOffset = MaxRid(drawingRelsEntry is not null ? Encoding.UTF8.GetString(ReadEntry(drawingRelsEntry)) : "");
                var mergedDrawing = MergeDrawings(Encoding.UTF8.GetString(ReadEntry(edp)), ourDrawingXml, ridOffset);
                string mergedRels = MergeRelationships(
                    drawingRelsEntry is not null ? Encoding.UTF8.GetString(ReadEntry(drawingRelsEntry)) : EmptyRelationships(),
                    ourDrawingRels, ridOffset);
                overrides[existingDrawingPart] = Encoding.UTF8.GetBytes(mergedDrawing);
                overrides[RelsPath(existingDrawingPart)] = Encoding.UTF8.GetBytes(mergedRels);
            }
            else
            {
                // Case B: brand-new drawing part + rels + sheet rel + <drawing> element.
                string drawingPart = dw.PartName;
                int rid = MaxRid(existingSheetRels ?? "") + 1;
                string ridName = "rId" + rid;
                string target = "../drawings/" + NameOf(drawingPart);
                string newSheetRels = AddRelationship(existingSheetRels, ridName,
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing", target);

                if (pkg.Archive.GetEntry(sheetRelsPart) is not null)
                    overrides[sheetRelsPart] = Encoding.UTF8.GetBytes(newSheetRels);
                else
                    additions.Add((sheetRelsPart, Encoding.UTF8.GetBytes(newSheetRels)));

                additions.Add((drawingPart, Encoding.UTF8.GetBytes(ourDrawingXml)));
                additions.Add((RelsPath(drawingPart), Encoding.UTF8.GetBytes(ourDrawingRels)));

                AddDrawingElement(doc, ns, ridName);
                contentTypes = EnsureContentType(contentTypes,
                    $"<Override PartName=\"/{drawingPart}\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
            }

            foreach (var part in media.Parts)
            {
                additions.Add((part.PartName, part.Data));
                contentTypes = EnsureContentType(contentTypes,
                    $"<Default Extension=\"{part.Extension}\" ContentType=\"{part.ContentType}\"/>",
                    probe: true);
            }
        }

        // serialize the modified sheet last, after <drawing> insertion etc.
        overrides[sheetPart] = GetXmlBytes(doc);

        using var outZip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var e in pkg.Archive.Entries)
        {
            if (e.FullName == "[Content_Types].xml")
            {
                WriteEntry(outZip, e.FullName, Encoding.UTF8.GetBytes(contentTypes));
                continue;
            }
            if (overrides.TryGetValue(e.FullName, out var replaced))
            {
                WriteEntry(outZip, e.FullName, replaced);
                continue;
            }
            using var src = e.Open();
            var copy = outZip.CreateEntry(e.FullName, CompressionLevel.Optimal);
            using var dst = copy.Open();
            src.CopyTo(dst);
        }

        foreach (var (name, data) in additions)
            WriteEntry(outZip, name, data, name.StartsWith("xl/media/", StringComparison.OrdinalIgnoreCase)
                ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
    }

    // ------------------------------------------------------------------ row analysis

    private sealed class RowInfo
    {
        public required XElement Row;
        public int MaxColumn;
        public string? CollectionName;
        public List<object?>? CollectionItems;
    }

    private static RowInfo AnalyzeRow(XElement row, XNamespace ns, string[] sst, object root)
    {
        var info = new RowInfo { Row = row };
        foreach (var c in row.Elements(ns + "c"))
        {
            int col = ColumnOf(c);
            info.MaxColumn = Math.Max(info.MaxColumn, col);

            string? text = GetDisplayText(c, ns, sst);
            if (text is null || !text.Contains("{{")) continue;

            foreach (Match m in PlaceholderRx.Matches(text))
            {
                string expr = m.Groups[1].Value;
                if (expr.StartsWith('@')) continue; // image placeholder: no expansion
                int dot = expr.IndexOf('.');
                if (dot <= 0) continue;
                string head = expr[..dot];
                if (ValueResolver.TryGetMember(root, head, out object? coll) && ValueResolver.IsEnumerableValue(coll))
                {
                    if (info.CollectionName is not null &&
                        !string.Equals(info.CollectionName, head, StringComparison.OrdinalIgnoreCase))
                        throw new FastExcelTemplateException(
                            $"Row {row.Attribute("r")?.Value}: multiple collections in one template row are not supported " +
                            $"('{info.CollectionName}' and '{head}').");
                    info.CollectionName = head;
                    info.CollectionItems = ValueResolver.Materialize(coll!);
                }
            }
        }
        return info;
    }

    // ------------------------------------------------------------------ row rendering

    private static void RenderRow(XElement row, XNamespace ns, string[] sst, RowInfo info,
        object? item, string? collectionName, object root, TemplateOptions options,
        MediaCollector media, List<DrawingAnchor> anchors, int rowNum)
    {
        row.SetAttributeValue("r", rowNum);
        int anchorsBefore = anchors.Count;
        int fallbackCol = 0;

        foreach (var c in row.Elements(ns + "c").ToList())
        {
            int col = c.Attribute("r") is { } rAttr ? CellReference.ParseColumn(rAttr.Value) : fallbackCol;
            fallbackCol = col + 1;
            c.SetAttributeValue("r", XmlUtility.ColumnLetter(col) + rowNum);

            string? text = GetDisplayText(c, ns, sst);
            if (text is null || !text.Contains("{{")) continue;

            var matches = PlaceholderRx.Matches(text);
            if (matches.Count == 0) continue;

            bool whole = matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == text.Length;

            // whole-cell single expression: preserve value type where we can
            if (whole)
            {
                string expr = matches[0].Groups[1].Value;
                bool imageExpr = expr.StartsWith('@');
                if (imageExpr) expr = expr[1..];

                if (TryResolveExpr(expr, item, collectionName, root, out object? v))
                {
                    if (TryWriteImageCell(c, ns, v, col, rowNum, media, anchors, out _))
                        continue; // image anchored, cell already emptied
                    if (imageExpr)
                    {
                        SetEmpty(c, ns); // @expr but value is not image-like
                        continue;
                    }
                    WriteTyped(c, ns, v, options);
                }
                else
                {
                    SetEmpty(c, ns); // unresolvable
                }
                continue;
            }

            // mixed literal/expression text: concatenate
            var sb = new StringBuilder();
            int pos = 0;
            bool imageOnly = true;
            foreach (Match m in matches)
            {
                sb.Append(text, pos, m.Index - pos);
                pos = m.Index + m.Length;
                string expr = m.Groups[1].Value;
                if (expr.StartsWith('@'))
                {
                    if (TryResolveExpr(expr[1..], item, collectionName, root, out object? imgVal) &&
                        TryWriteImageCell(c, ns, imgVal, col, rowNum, media, anchors, out _))
                    {
                        continue; // image anchored; contributes no text
                    }
                    continue;
                }
                imageOnly = false;
                if (TryResolveExpr(expr, item, collectionName, root, out object? v))
                    sb.Append(v switch
                    {
                        DateTime dtv when !string.Equals(options.DateTimeFormat, "serial", StringComparison.OrdinalIgnoreCase)
                            => dtv.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture),
                        DateOnly dov => dov.ToString(options.DateOnlyFormat, CultureInfo.InvariantCulture),
                        _ => ValueResolver.ToDisplay(v)
                    });
            }
            sb.Append(text, pos, text.Length - pos);

            if (imageOnly && anchors.Count > anchorsBefore)
            {
                // cell ended up purely as an image host
                if (sb.Length == 0) { SetEmpty(c, ns); continue; }
            }
            WriteInlineString(c, ns, sb.ToString());
        }

        // auto row height for images on this row
        if (anchors.Count > anchorsBefore)
        {
            int maxPx = 0;
            for (int a = anchorsBefore; a < anchors.Count; a++)
                maxPx = Math.Max(maxPx, anchors[a].HeightPx);
            double neededPx = maxPx * 0.75; // pt = px * 72/96
            double? existing = TryGetDouble(row.Attribute("ht"));
            double ht = Math.Max(existing ?? 0, neededPx);
            row.SetAttributeValue("ht", ht.ToString("0.###", CultureInfo.InvariantCulture));
            row.SetAttributeValue("customHeight", "1");
        }
    }

    private static bool TryResolveExpr(string expr, object? item, string? collectionName, object root, out object? value)
    {
        if (item is not null && collectionName is not null)
        {
            int dot = expr.IndexOf('.');
            string head = dot > 0 ? expr[..dot] : expr;
            if (string.Equals(head, collectionName, StringComparison.OrdinalIgnoreCase))
                return ValueResolver.TryResolve(item, dot > 0 ? expr[dot..][1..] : "", out value);
        }
        return ValueResolver.TryResolve(root, expr, out value);
    }

    /// <summary>Writes an anchor when <paramref name="value"/> is image-like; leaves an empty styled cell.</summary>
    private static bool TryWriteImageCell(XElement c, XNamespace ns, object? value, int col, int rowNum,
        MediaCollector media, List<DrawingAnchor> anchors, out int heightPx)
    {
        heightPx = 0;
        ExcelImage img;
        int w, h;
        switch (value)
        {
            case ImagePlaceholder ph:
                img = ph.Image; w = ph.EffectiveWidth; h = ph.EffectiveHeight;
                break;
            case ExcelImage ei:
                img = ei; w = ei.PixelWidth > 0 ? ei.PixelWidth : 100; h = ei.PixelHeight > 0 ? ei.PixelHeight : 100;
                break;
            case byte[] bytes when bytes.Length > 0:
                img = new ExcelImage(bytes);
                w = img.PixelWidth > 0 ? img.PixelWidth : 100;
                h = img.PixelHeight > 0 ? img.PixelHeight : 100;
                break;
            default:
                return false;
        }

        var mp = media.Add(img);
        anchors.Add(new DrawingAnchor(rowNum - 1, col, mp, w, h));
        SetEmpty(c, ns);
        heightPx = h;
        return true;
    }

    // ------------------------------------------------------------------ cell writers

    private static void WriteTyped(XElement c, XNamespace ns, object? value, TemplateOptions options)
    {
        switch (value)
        {
            case null:
                SetEmpty(c, ns);
                break;
            case bool b:
                c.RemoveNodes();
                c.SetAttributeValue("t", "b");
                c.Add(new XElement(ns + "v", b ? "1" : "0"));
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                c.RemoveNodes();
                c.Attribute("t")?.Remove();
                c.Add(new XElement(ns + "v",
                    ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture)));
                break;
            case DateTime dt:
                if (string.Equals(options.DateTimeFormat, "serial", StringComparison.OrdinalIgnoreCase))
                {
                    c.RemoveNodes();
                    c.Attribute("t")?.Remove();
                    c.Add(new XElement(ns + "v", dt.ToOADate().ToString("0.#########", CultureInfo.InvariantCulture)));
                }
                else
                    WriteInlineString(c, ns, dt.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture));
                break;
            case DateOnly dod:
                if (string.Equals(options.DateOnlyFormat, "serial", StringComparison.OrdinalIgnoreCase))
                {
                    c.RemoveNodes();
                    c.Attribute("t")?.Remove();
                    c.Add(new XElement(ns + "v", (dod.DayNumber - 693595).ToString(CultureInfo.InvariantCulture)));
                }
                else
                    WriteInlineString(c, ns, dod.ToString(options.DateOnlyFormat, CultureInfo.InvariantCulture));
                break;
            default:
                WriteInlineString(c, ns, ValueResolver.ToDisplay(value));
                break;
        }
    }

    private static void WriteInlineString(XElement c, XNamespace ns, string text)
    {
        c.RemoveNodes();
        if (text.Length == 0)
        {
            c.Attribute("t")?.Remove();
            return;
        }
        c.SetAttributeValue("t", "inlineStr");
        c.Add(new XElement(ns + "is",
            new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
    }

    private static void SetEmpty(XElement c, XNamespace ns)
    {
        c.RemoveNodes();
        c.Attribute("t")?.Remove();
    }

    // ------------------------------------------------------------------ sheet helpers

    private static string? GetDisplayText(XElement c, XNamespace ns, string[] sst)
    {
        var t = (string?)c.Attribute("t");
        if (t == "s")
        {
            var v = c.Element(ns + "v");
            if (v is null || !int.TryParse(v.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)
                || idx < 0 || idx >= sst.Length)
                return null;
            return sst[idx];
        }
        if (t == "inlineStr")
        {
            var isEl = c.Element(ns + "is");
            return isEl is null ? null : string.Concat(isEl.DescendantNodes().OfType<XText>().Select(x => x.Value));
        }
        if (t == "str")
            return c.Element(ns + "v")?.Value;
        return null; // numeric / bool / error / formula-with-cached-number
    }

    private static int ColumnOf(XElement c) =>
        c.Attribute("r") is { } r ? CellReference.ParseColumn(r.Value) : 0;

    private static double? TryGetDouble(XAttribute? attr) =>
        attr is not null && double.TryParse(attr.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static void UpdateDimension(XDocument doc, XNamespace ns, int lastRow, int maxCol)
    {
        var dim = doc.Root?.Element(ns + "dimension");
        if (dim is null) return;
        string refValue = lastRow <= 0 ? "A1" : $"A1:{XmlUtility.ColumnLetter(Math.Max(0, maxCol))}{lastRow}";
        dim.SetAttributeValue("ref", refValue);
    }

    private static void AddDrawingElement(XDocument doc, XNamespace ns, string rid)
    {
        var root = doc.Root!;
        root.Elements(ns + "drawing").Remove();
        var el = new XElement(ns + "drawing", new XAttribute(Rns + "id", rid));
        string[] afterMe = ["legacyDrawing", "legacyDrawingHF", "picture", "oleObjects", "controls", "tableParts", "extLst"];
        XElement? anchor = null;
        foreach (var name in afterMe)
        {
            anchor = root.Element(ns + name);
            if (anchor is not null) break;
        }
        if (anchor is not null) anchor.AddBeforeSelf(el);
        else root.Add(el);
    }

    // ------------------------------------------------------------------ zip / parts plumbing

    private static string RelsPath(string partPath)
    {
        int slash = partPath.LastIndexOf('/');
        return slash < 0 ? $"_rels/{partPath}.rels"
            : $"{partPath[..slash]}/_rels/{partPath[(slash + 1)..]}.rels";
    }

    private static string DirOf(string partPath)
    {
        int slash = partPath.LastIndexOf('/');
        return slash < 0 ? "" : partPath[..slash];
    }

    private static string NameOf(string partPath)
    {
        int slash = partPath.LastIndexOf('/');
        return slash < 0 ? partPath : partPath[(slash + 1)..];
    }

    /// <summary>Finds the absolute part path of the drawing referenced by a sheet's rels, if any.</summary>
    private static string? FindDrawingTarget(string? sheetRelsXml, string sheetPartPath)
    {
        if (sheetRelsXml is null) return null;
        using var xml = XmlReader.Create(new StringReader(sheetRelsXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship")
            {
                var type = xml.GetAttribute("Type") ?? "";
                if (type.EndsWith("/drawing", StringComparison.OrdinalIgnoreCase))
                {
                    var target = xml.GetAttribute("Target") ?? "";
                    return ResolveTarget(target, DirOf(sheetPartPath));
                }
            }
        }
        return null;
    }

    private static string ResolveTarget(string target, string baseDir)
    {
        if (target.StartsWith('/')) return target[1..];
        string combined = baseDir + "/" + target;
        // normalize ../ segments
        var parts = new List<string>();
        foreach (var seg in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != ".") parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static int MaxRid(string? relsXml)
    {
        if (relsXml is null) return 0;
        int max = 0;
        foreach (Match m in Regex.Matches(relsXml, "Id=\"rId(\\d+)\""))
            if (int.TryParse(m.Groups[1].Value, out int n)) max = Math.Max(max, n);
        return max;
    }

    private static string AddRelationship(string? existingRelsXml, string rid, string type, string target)
    {
        string rel = $"<Relationship Id=\"{rid}\" Type=\"{type}\" Target=\"{target}\"/>";
        if (existingRelsXml is null)
            return EmptyRelationships().Replace("</Relationships>", rel + "</Relationships>");
        return existingRelsXml.Replace("</Relationships>", rel + "</Relationships>");
    }

    private static string EmptyRelationships() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"></Relationships>";

    /// <summary>Appends our anchor elements (with shifted embed rIds) to the template's existing drawing xml.</summary>
    private static string MergeDrawings(string existingDrawingXml, string ourDrawingXml, int ridOffset)
    {
        var ours = XDocument.Parse(ourDrawingXml);
        var existing = XDocument.Parse(existingDrawingXml);
        var root = existing.Root!;

        var anchorEls = ours.Root!.Elements().ToList();
        foreach (var a in anchorEls)
        {
            foreach (var blip in a.Descendants().Where(e => e.Name.LocalName == "blip"))
            {
                var embed = blip.Attributes().FirstOrDefault(x => x.Name.LocalName == "embed");
                if (embed is not null &&
                    int.TryParse(embed.Value.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    embed.SetValue("rId" + (n + ridOffset));
            }
        }
        // clone into existing tree (drops the redundant root-level ns declarations via XDocument round-trip)
        foreach (var a in anchorEls)
            root.Add(XElement.Parse(a.ToString(SaveOptions.DisableFormatting)));
        return existing.ToString(SaveOptions.DisableFormatting);
    }

    private static string MergeRelationships(string existingRelsXml, string ourRelsXml, int ridOffset)
    {
        var ours = XDocument.Parse(ourRelsXml);
        var sb = new StringBuilder();
        foreach (var rel in ours.Root!.Elements())
        {
            var id = (string?)rel.Attribute("Id") ?? "rId1";
            int n = int.TryParse(id.AsSpan(3), out var parsed) ? parsed : 1;
            var target = (string?)rel.Attribute("Target") ?? "";
            var type = (string?)rel.Attribute("Type") ?? "";
            sb.Append($"<Relationship Id=\"rId{n + ridOffset}\" Type=\"{type}\" Target=\"{target}\"/>");
        }
        return existingRelsXml.Replace("</Relationships>", sb + "</Relationships>");
    }

    private static string EnsureContentType(string contentTypes, string fragment, bool probe = false)
    {
        if (contentTypes.Contains(fragment[..Math.Min(fragment.Length, 60)])) return contentTypes;
        if (probe)
        {
            // fragment like <Default Extension="png" ...>
            var extMatch = Regex.Match(fragment, "Extension=\"([^\"]+)\"");
            if (extMatch.Success && contentTypes.Contains($"Extension=\"{extMatch.Groups[1].Value}\""))
                return contentTypes;
        }
        int idx = contentTypes.LastIndexOf("</Types>", StringComparison.Ordinal);
        if (idx < 0) return contentTypes;
        return contentTypes.Insert(idx, fragment);
    }

    private static int NextMediaIndex(ZipArchive zip)
    {
        int max = 0;
        foreach (var e in zip.Entries)
        {
            var m = Regex.Match(e.FullName, @"^xl/media/image(\d+)\.[^.]+$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) max = Math.Max(max, n);
        }
        return max + 1;
    }

    private static int NextDrawingIndex(ZipArchive zip)
    {
        int max = 0;
        foreach (var e in zip.Entries)
        {
            var m = Regex.Match(e.FullName, @"^xl/drawings/drawing(\d+)\.xml$", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) max = Math.Max(max, n);
        }
        return max + 1;
    }

    private static byte[] ReadEntry(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] GetXmlBytes(XDocument doc) =>
        Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));

    private static void WriteEntry(ZipArchive zip, string name, byte[] data,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var e = zip.CreateEntry(name, level);
        using var s = e.Open();
        s.Write(data);
    }
}
