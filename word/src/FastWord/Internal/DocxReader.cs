using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FastWord.Internal;

/// <summary>
/// Reads a WordprocessingML package back into a block model. The document entry is walked with a forward-only
/// <see cref="XmlReader"/> and every block parse consumes its own subtree, so nested tables and text boxes never
/// leak into the body or into their parent row.
/// </summary>
internal static class DocxReader
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DocumentEntry = "word/document.xml";

    public static WordDocument Read(Stream package, WordReadOptions options)
    {
        try
        {
            return ReadCore(package, options);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            // A truncated zip or malformed XML is the same caller problem as a missing part — one exception type to catch.
            throw new InvalidDataException($"Not a readable .docx package: {ex.Message}", ex);
        }
    }

    private static WordDocument ReadCore(Stream package, WordReadOptions options)
    {
        // Upload pipes (HttpRequest.Body) are forward-only; ZipArchive needs to seek the central directory.
        MemoryStream? buffered = null;
        if (!package.CanSeek)
        {
            buffered = new MemoryStream();
            package.CopyTo(buffered);
            buffered.Position = 0;
            package = buffered;
        }

        using (buffered)
        {
            RequireZipSignature(package);
            using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
            ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(
                e => string.Equals(e.FullName, DocumentEntry, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                throw new InvalidDataException($"Not a .docx package: {DocumentEntry} is missing.");

            using var xml = entry.Open();
            using var xr = XmlReader.Create(xml, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                XmlResolver = null,
            });

            var blocks = new List<WordBlock>();
            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element || xr.NamespaceURI != W) continue;
                switch (xr.LocalName)
                {
                    case "p":
                    {
                        var paragraph = ParseParagraph(xr);
                        if (options.IncludeEmptyParagraphs || paragraph.Text.Length > 0)
                            blocks.Add(paragraph);
                        break;
                    }
                    case "tbl":
                        blocks.Add(ParseTable(xr));
                        break;
                }
            }
            return new WordDocument(blocks);
        }
    }

    /// <summary>Fail with a readable message instead of the ZIP parser's "central directory is corrupt".</summary>
    private static void RequireZipSignature(Stream s)
    {
        long start = s.Position;
        int a = s.ReadByte(), b = s.ReadByte();
        s.Position = start;
        if (a != 0x50 || b != 0x4B) // "PK"
            throw new InvalidDataException("Not a .docx package: missing the ZIP signature.");
    }

    /// <summary>
    /// Called on the <c>w:p</c> start element; returns positioned on its end element. An empty element
    /// (<c>&lt;w:p/&gt;</c>, which is what the writer emits between tables) yields no end element at all, so every
    /// parse below bails out on <see cref="XmlReader.IsEmptyElement"/> instead of waiting for one.
    /// </summary>
    private static WordParagraph ParseParagraph(XmlReader xr)
    {
        if (xr.IsEmptyElement) return new WordParagraph(string.Empty, null, false); // <w:p/> has no end element

        int depth = xr.Depth;
        var sb = new StringBuilder();
        string? style = null;
        bool isList = false, inText = false, inField = false;

        while (xr.Read())
        {
            switch (xr.NodeType)
            {
                case XmlNodeType.Element when xr.NamespaceURI == W:
                    switch (xr.LocalName)
                    {
                        case "t": inText = !xr.IsEmptyElement; break;
                        case "tab": sb.Append('\t'); break;
                        case "br":
                        case "cr": sb.Append('\n'); break;
                        // field codes and tracked-change deletions are not visible text
                        case "instrText":
                        case "delText": inField = !xr.IsEmptyElement; break;
                        case "pStyle": style = xr.GetAttribute("val", W); break;
                        case "numPr": isList = true; break;
                    }
                    break;

                case XmlNodeType.EndElement when xr.NamespaceURI == W:
                    switch (xr.LocalName)
                    {
                        case "t": inText = false; break;
                        case "instrText":
                        case "delText": inField = false; break;
                        case "p" when xr.Depth == depth:
                            return new WordParagraph(sb.ToString(), style, isList);
                    }
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    if (inText && !inField) sb.Append(xr.Value);
                    break;
            }
        }
        return new WordParagraph(sb.ToString(), style, isList);
    }

    /// <summary>Called on the <c>w:tbl</c> start element; returns positioned on its end element.</summary>
    private static WordTable ParseTable(XmlReader xr)
    {
        int depth = xr.Depth;
        var rows = new List<IReadOnlyList<string>>();
        if (xr.IsEmptyElement) return new WordTable(rows);
        while (xr.Read())
        {
            if (xr.NamespaceURI != W) continue;
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "tr") rows.Add(ParseRow(xr));
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "tbl" && xr.Depth == depth) break;
        }
        return new WordTable(rows);
    }

    private static IReadOnlyList<string> ParseRow(XmlReader xr)
    {
        int depth = xr.Depth;
        var cells = new List<string>();
        if (xr.IsEmptyElement) return cells;
        while (xr.Read())
        {
            if (xr.NamespaceURI != W) continue;
            if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "tc") cells.Add(ParseCell(xr));
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "tr" && xr.Depth == depth) break;
        }
        return cells;
    }

    /// <summary>Cell paragraphs join as <c>\n</c> (one blank line per empty paragraph); a nested table is flattened in.</summary>
    private static string ParseCell(XmlReader xr)
    {
        int depth = xr.Depth;
        var sb = new StringBuilder();
        bool first = true;
        void Append(string text)
        {
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(text);
        }
        if (xr.IsEmptyElement) return string.Empty;

        while (xr.Read())
        {
            if (xr.NamespaceURI != W) continue;
            if (xr.NodeType == XmlNodeType.Element)
            {
                switch (xr.LocalName)
                {
                    case "p": Append(ParseParagraph(xr).Text); break;
                    case "tbl": Append(Flatten(ParseTable(xr))); break;
                }
            }
            else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "tc" && xr.Depth == depth) break;
        }
        return sb.ToString();
    }

    private static string Flatten(WordTable nested)
        => string.Join('\n', nested.Rows.Select(r => string.Join('\t', r)));
}
