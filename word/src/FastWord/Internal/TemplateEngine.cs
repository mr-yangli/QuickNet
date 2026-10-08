using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FastWord.Internal;

/// <summary>
/// Template renderer for .docx packages: replaces <c>{{Name}}</c> tags in the document body, in headers and
/// in footers, repairs tags Word split across runs, and turns <see cref="ImagePlaceholder"/> values into
/// inline drawings (media part + relationship + content type). Every other part — styles, settings, themes,
/// fonts, numbering — is copied byte-for-byte, so a hand-formatted legal template keeps its layout.
/// </summary>
internal static class TemplateEngine
{
    private const string Wns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Wpns = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string Ans = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Picns = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private const string Relsns = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Ctns = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string ImageRelType = Rns + "/image";
    private const long EmuPerPixel = 9525; // 96 dpi

    private static readonly XNamespace W = Wns;
    private static readonly XNamespace R = Rns;
    private static readonly XNamespace Wp = Wpns;
    private static readonly XNamespace A = Ans;
    private static readonly XNamespace Pic = Picns;
    private static readonly XNamespace PkgRel = Relsns;
    private static readonly XNamespace Ct = Ctns;
    private static readonly XName XmlSpace = "{http://www.w3.org/XML/1998/namespace}space";

    private static readonly Regex PlaceholderRx = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled);
    private static readonly Regex RidRx = new(@"Id\s*=\s*""rId(\d+)""", RegexOptions.Compiled);
    private static readonly Regex FillableRx = new(@"^word/(document|header\d*|footer\d*)\.xml$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Render(Stream templateStream, Stream outputStream, object value, TemplateOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);

        // ZipArchive needs to seek, and the caller's template may be a forward-only stream.
        using var buffer = new MemoryStream();
        templateStream.CopyTo(buffer);
        buffer.Position = 0;

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true, Encoding.UTF8))
        {
            var ctx = new RenderContext(zip, value, options);
            foreach (string partName in zip.Entries
                     .Where(e => FillableRx.IsMatch(e.FullName))
                     .Select(e => e.FullName)
                     .ToList())
                ctx.FillPart(partName);
            ctx.Flush();
        }

        buffer.Position = 0;
        buffer.CopyTo(outputStream);
    }

    private sealed class RenderContext
    {
        private readonly ZipArchive _zip;
        private readonly Dictionary<string, object?> _values;
        private readonly TemplateOptions _options;
        private readonly MediaCollector _media;
        private readonly Dictionary<string, string> _contentTypes = new(StringComparer.OrdinalIgnoreCase);

        public RenderContext(ZipArchive zip, object value, TemplateOptions options)
        {
            _zip = zip;
            _options = options;
            _values = BuildValueMap(value);
            _media = new MediaCollector(NextMediaIndex(zip));
        }

        public void FillPart(string partName)
        {
            string? xml = ReadEntryText(partName);
            if (xml is null || !xml.Contains("{{", StringComparison.Ordinal)) return;

            var doc = XDocument.Parse(xml, LoadOptions.None);
            var images = new List<(string RelId, MediaPart Media)>();
            int nextRid = NextRelationshipId(_zip, partName);
            int nextDocPrId = NextDocPrId(doc);
            bool mutated = false;

            foreach (var paragraph in doc.Descendants(W + "p").ToList())
                mutated |= FillParagraph(paragraph, images, _media, ref nextRid, ref nextDocPrId);

            if (!mutated) return;
            ReplaceEntry(partName, doc);
            if (images.Count > 0)
                WriteRels(partName, images);
        }

        public void Flush()
        {
            foreach (MediaPart part in _media.Parts)
            {
                if (_zip.GetEntry(part.PartName) is not null) continue;
                _contentTypes[part.Extension] = part.ContentType;
                var entry = _zip.CreateEntry(part.PartName, CompressionLevel.NoCompression);
                using var es = entry.Open();
                es.Write(part.Data);
            }
            if (_contentTypes.Count > 0) PatchContentTypes();
        }

        // ---- paragraph fill ----

        private bool FillParagraph(XElement paragraph, List<(string RelId, MediaPart Media)> images,
            MediaCollector media, ref int nextRid, ref int nextDocPrId)
        {
            var texts = new List<XElement>();
            var owners = new List<XElement>();
            foreach (var run in paragraph.Elements(W + "r"))
                foreach (var t in run.Elements(W + "t"))
                {
                    texts.Add(t);
                    owners.Add(run);
                }
            if (texts.Count == 0) return false;

            int n = texts.Count;
            var starts = new int[n];
            var ends = new int[n];
            var whole = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                string s = texts[i].Value;
                starts[i] = whole.Length;
                whole.Append(s);
                ends[i] = whole.Length;
            }
            string full = whole.ToString();
            var matches = PlaceholderRx.Matches(full);
            if (matches.Count == 0) return false;

            var rebuilt = new StringBuilder[n];
            for (int i = 0; i < n; i++) rebuilt[i] = new StringBuilder();
            var drawings = new List<(int Segment, ImagePlaceholder Image)>();

            // A tag can straddle runs (Word breaks "{{Date" + "time" + "}}" with proofErr markers between),
            // so text is rebuilt over the concatenation and then distributed back over the same w:t nodes.
            void CopyLiteral(int from, int to)
            {
                if (to <= from) return;
                for (int i = 0; i < n; i++)
                {
                    if (ends[i] <= from) continue;
                    if (starts[i] >= to) break;
                    int a = Math.Max(from, starts[i]);
                    int b = Math.Min(to, ends[i]);
                    rebuilt[i].Append(full, a, b - a);
                }
            }

            int SegmentOf(int index)
            {
                for (int i = 0; i < n; i++)
                    if (index < ends[i]) return i;
                return n - 1;
            }

            int cursor = 0;
            foreach (Match m in matches)
            {
                CopyLiteral(cursor, m.Index);
                string key = m.Groups[1].Value.Trim();
                bool found = _values.TryGetValue(key, out object? v);
                int seg = SegmentOf(m.Index);

                if (v is ImagePlaceholder placeholder)
                    drawings.Add((seg, placeholder));
                else if (!found)
                    rebuilt[seg].Append(m.Value); // unknown tag stays visible rather than vanishing
                else
                    rebuilt[seg].Append(RenderText(v));

                cursor = m.Index + m.Length;
            }
            CopyLiteral(cursor, full.Length);

            bool mutated = false;
            for (int i = 0; i < n; i++)
            {
                string text = rebuilt[i].ToString();
                if (text == texts[i].Value) continue;
                texts[i].Value = text;
                texts[i].SetAttributeValue(XmlSpace, "preserve");
                mutated = true;
            }

            var anchorPerSegment = new Dictionary<int, XElement>();
            foreach (var (seg, placeholder) in drawings)
            {
                MediaPart part = media.Add(placeholder.Image);
                string relId = $"rId{nextRid++}";
                images.Add((relId, part));

                XElement drawingRun = BuildImageRun(owners[seg], part, relId,
                    placeholder.EffectiveWidth, placeholder.EffectiveHeight, nextDocPrId++);
                XElement insertAfter = anchorPerSegment.TryGetValue(seg, out var previous) ? previous : owners[seg];
                insertAfter.AddAfterSelf(drawingRun);
                anchorPerSegment[seg] = drawingRun;
                mutated = true;
            }

            return mutated;
        }

        private string RenderText(object? value) => value switch
        {
            null or DBNull => string.Empty,
            string s => s,
            DateTime d => d.ToString(_options.DateTimeFormat, CultureInfo.CurrentCulture),
            DateOnly d => d.ToString(_options.DateOnlyFormat, CultureInfo.CurrentCulture),
            DateTimeOffset d => d.ToString(_options.DateTimeFormat, CultureInfo.CurrentCulture),
            IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? string.Empty
        };

        private static XElement BuildImageRun(XElement ownerRun, MediaPart media, string relId,
            int widthPx, int heightPx, int docPrId)
        {
            long cx = widthPx * EmuPerPixel;
            long cy = heightPx * EmuPerPixel;

            var inline = new XElement(Wp + "inline",
                new XAttribute("distT", 0), new XAttribute("distB", 0),
                new XAttribute("distL", 0), new XAttribute("distR", 0),
                new XElement(Wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(Wp + "effectExtent",
                    new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", 0), new XAttribute("b", 0)),
                new XElement(Wp + "docPr", new XAttribute("id", docPrId), new XAttribute("name", $"Picture {docPrId}")),
                new XElement(Wp + "cNvGraphicFramePr",
                    new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                new XElement(A + "graphic",
                    new XElement(A + "graphicData",
                        new XAttribute("uri", Picns),
                        new XElement(Pic + "pic",
                            new XElement(Pic + "nvPicPr",
                                new XElement(Pic + "cNvPr", new XAttribute("id", docPrId), new XAttribute("name", media.FileName)),
                                new XElement(Pic + "cNvPicPr")),
                            new XElement(Pic + "blipFill",
                                new XElement(A + "blip", new XAttribute(R + "embed", relId)),
                                new XElement(A + "stretch", new XElement(A + "fillRect"))),
                            new XElement(Pic + "spPr",
                                new XElement(A + "xfrm",
                                    new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                    new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))));

            XElement? rPr = ownerRun.Element(W + "rPr");
            var run = new XElement(W + "r",
                // Fixed prefixes keep the output readable; Word resolves these by namespace URI anyway.
                new XAttribute(XNamespace.Xmlns + "wp", Wpns),
                new XAttribute(XNamespace.Xmlns + "a", Ans),
                new XAttribute(XNamespace.Xmlns + "pic", Picns),
                new XAttribute(XNamespace.Xmlns + "r", Rns),
                rPr is null ? null : new XElement(rPr),
                new XElement(W + "drawing", inline));
            return run;
        }

        // ---- package plumbing ----

        private static Dictionary<string, object?> BuildValueMap(object value)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (value is IDictionary<string, object> typed)
            {
                foreach (var (key, item) in typed) map[key] = item;
                return map;
            }
            if (value is IDictionary untyped)
            {
                foreach (DictionaryEntry entry in untyped)
                    map[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = entry.Value;
                return map;
            }
            foreach (PropertyInfo p in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                map[p.Name] = p.GetValue(value);
            }
            return map;
        }

        private string? ReadEntryText(string name)
        {
            ZipArchiveEntry? entry = _zip.GetEntry(name);
            if (entry is null) return null;
            using var es = entry.Open();
            using var sr = new StreamReader(es, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }

        private void ReplaceEntry(string name, XDocument doc)
        {
            using var ms = new MemoryStream();
            doc.Save(ms, SaveOptions.DisableFormatting);
            byte[] bytes = ms.ToArray();

            _zip.GetEntry(name)?.Delete();
            var entry = _zip.CreateEntry(name, CompressionLevel.SmallestSize);
            using var es = entry.Open();
            es.Write(bytes);
        }

        private static string RelsPath(string partName)
        {
            int slash = partName.LastIndexOf('/');
            return string.Concat(partName.AsSpan(0, slash + 1), "_rels/", partName.AsSpan(slash + 1), ".rels");
        }

        private static int NextRelationshipId(ZipArchive zip, string partName)
        {
            ZipArchiveEntry? entry = zip.GetEntry(RelsPath(partName));
            if (entry is null) return 1;
            using var sr = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            int max = 0;
            foreach (Match m in RidRx.Matches(sr.ReadToEnd()))
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > max)
                    max = n;
            return max + 1;
        }

        private static int NextMediaIndex(ZipArchive zip)
        {
            int max = 0;
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)) continue;
                string file = Path.GetFileNameWithoutExtension(entry.FullName);
                if (!file.StartsWith("image", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(file.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > max)
                    max = n;
            }
            return max + 1;
        }

        private static int NextDocPrId(XDocument doc)
        {
            int max = 0;
            foreach (var element in doc.Descendants(Wp + "docPr"))
                if (int.TryParse((string?)element.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > max)
                    max = n;
            return max + 1;
        }

        private void WriteRels(string partName, List<(string RelId, MediaPart Media)> images)
        {
            string relsName = RelsPath(partName);
            string? existing = ReadEntryText(relsName);
            var doc = existing is null
                ? new XDocument(new XElement(PkgRel + "Relationships"))
                : XDocument.Parse(existing, LoadOptions.None);

            foreach (var (relId, media) in images)
                doc.Root!.Add(new XElement(PkgRel + "Relationship",
                    new XAttribute("Id", relId),
                    new XAttribute("Type", ImageRelType),
                    new XAttribute("Target", string.Concat("media/", media.FileName))));

            ReplaceEntry(relsName, doc);
        }

        private void PatchContentTypes()
        {
            const string name = "[Content_Types].xml";
            string? xml = ReadEntryText(name);
            if (xml is null) return;

            var doc = XDocument.Parse(xml, LoadOptions.None);
            bool changed = false;
            foreach (var (extension, contentType) in _contentTypes)
            {
                if (doc.Root!.Elements(Ct + "Default")
                        .Any(d => string.Equals((string?)d.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)))
                    continue;
                doc.Root.Add(new XElement(Ct + "Default",
                    new XAttribute("Extension", extension), new XAttribute("ContentType", contentType)));
                changed = true;
            }
            if (changed) ReplaceEntry(name, doc);
        }
    }
}
