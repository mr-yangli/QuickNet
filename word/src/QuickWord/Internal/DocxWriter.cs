using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace QuickWord.Internal;

/// <summary>
/// Streams a WordprocessingML (docx) package: one table per sheet, rows serialised one at a time into the
/// document entry — memory is O(columns), independent of row count, like the xlsx writer.
/// </summary>
internal sealed class DocxWriter
{
    // A4 portrait, 1" margins -> 9026 twips of usable width.
    private const long PageW = 11906, PageH = 16838, Margin = 1440;
    private const long ContentWidth = PageW - 2 * Margin;
    private const long MinColWidth = 720; // 0.5"

    private readonly WordOptions _options;

    public DocxWriter(WordOptions options) => _options = options;

    public void Write(Stream output, object value)
    {
        var sheets = SheetBuilder.Build(value);
        WritePackage(output, (sw, footer) => WriteDocument(sw, sheets, footer));
    }

    /// <summary>
    /// Prose document: one paragraph per line (a line's own <c>\n</c> splits further), optional <c>Title</c>
    /// heading. Used for hand-readable documents — a test paper, a notice — where a table would be the wrong shape.
    /// </summary>
    public void WriteLines(Stream output, IEnumerable<string> lines)
        => WritePackage(output, (sw, footer) => WriteLinesDocument(sw, lines, footer));

    private void WritePackage(Stream output, Action<StreamWriter, bool> body)
    {
        bool footer = !string.IsNullOrEmpty(_options.FooterText);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);

        WriteEntry(zip, "[Content_Types].xml", ContentTypes(footer));
        WriteEntry(zip, "_rels/.rels", RootRels);
        WriteEntry(zip, "word/_rels/document.xml.rels", DocumentRels(footer));
        WriteEntry(zip, "word/styles.xml", Styles);
        if (footer) WriteEntry(zip, "word/footer1.xml", BuildFooter(_options.FooterText!));

        var entry = zip.CreateEntry("word/document.xml", CompressionLevel.Fastest);
        using (var es = entry.Open())
        using (var sw = new StreamWriter(es, new UTF8Encoding(false), 1 << 16))
            body(sw, footer);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.SmallestSize);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    // ---------------------------------------------------------------------------------------------
    // document.xml (streamed)
    // ---------------------------------------------------------------------------------------------

    private void WriteProlog(StreamWriter sw)
    {
        sw.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sw.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" """
                 + """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><w:body>""");

        if (!string.IsNullOrEmpty(_options.Title))
            sw.Write($"""<w:p><w:pPr><w:pStyle w:val="DocTitle"/><w:jc w:val="center"/></w:pPr><w:r><w:t xml:space="preserve">{XmlUtility.Escape(_options.Title)}</w:t></w:r></w:p>""");
    }

    private static void WriteEpilog(StreamWriter sw, bool footer)
    {
        // footerReference must precede pgSz per the CT_SectPr sequence
        string footerRef = footer ? """<w:footerReference w:type="default" r:id="rId2"/>""" : "";
        sw.Write($"""<w:sectPr>{footerRef}<w:pgSz w:w="{PageW}" w:h="{PageH}"/><w:pgMar w:top="{Margin}" w:right="{Margin}" w:bottom="{Margin}" w:left="{Margin}" w:header="720" w:footer="720" w:gutter="0"/></w:sectPr>""");
        sw.Write("</w:body></w:document>");
    }

    private void WriteLinesDocument(StreamWriter sw, IEnumerable<string> lines, bool footer)
    {
        int szHalf = (int)Math.Round(_options.FontSizePt * 2);
        WriteProlog(sw);
        foreach (string line in lines)
            WritePlainParagraph(sw, line, szHalf);
        WriteEpilog(sw, footer);
    }

    /// <summary>One <c>w:p</c> per physical line, mirroring how a multi-line cell is written.</summary>
    private static void WritePlainParagraph(StreamWriter sw, string text, int szHalf)
    {
        int start = 0;
        do
        {
            int nl = text.IndexOf('\n', start);
            string line = nl < 0 ? text[start..] : text.Substring(start, nl - start);
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (line.Length == 0) sw.Write("<w:p/>");
            else sw.Write($"""<w:p><w:pPr><w:spacing w:before="40" w:after="40" w:line="240" w:lineRule="auto"/></w:pPr>"""
                        + $"""<w:r><w:rPr><w:sz w:val="{szHalf}"/><w:szCs w:val="{szHalf}"/></w:rPr>"""
                        + $"""<w:t xml:space="preserve">{XmlUtility.Escape(line)}</w:t></w:r></w:p>""");
            if (nl < 0) break;
            start = nl + 1;
        } while (start <= text.Length);
    }

    private void WriteDocument(StreamWriter sw, List<SheetInput> sheets, bool footer)
    {
        int szHalf = (int)Math.Round(_options.FontSizePt * 2);
        WriteProlog(sw);

        var row = new StringBuilder(1024);
        for (int s = 0; s < sheets.Count; s++)
        {
            var sheet = sheets[s];
            if (sheets.Count > 1 && !string.IsNullOrEmpty(sheet.Name))
                sw.Write($"""<w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t xml:space="preserve">{XmlUtility.Escape(sheet.Name)}</w:t></w:r></w:p>""");

            WriteTable(sw, row, sheet.Columns, sheet.Rows, szHalf);
            sw.Write("<w:p/>"); // Word requires a paragraph between/after tables
        }

        WriteEpilog(sw, footer);
    }

    private void WriteTable(StreamWriter sw, StringBuilder row, ColumnMetadata[] columns,
                            IEnumerable<object?> rows, int szHalf)
    {
        long[] widths = ComputeWidths(columns);

        sw.Write($"""<w:tbl><w:tblPr><w:tblW w:w="{widths.Sum()}" w:type="dxa"/><w:tblLayout w:type="fixed"/>""");
        sw.Write($"""<w:tblBorders><w:top w:val="single" w:sz="4" w:space="0" w:color="{_options.BorderColor}"/><w:left w:val="single" w:sz="4" w:space="0" w:color="{_options.BorderColor}"/><w:bottom w:val="single" w:sz="4" w:space="0" w:color="{_options.BorderColor}"/><w:right w:val="single" w:sz="4" w:space="0" w:color="{_options.BorderColor}"/><w:insideH w:val="single" w:sz="4" w:space="0" w:color="{_options.BorderColor}"/><w:insideV w:val="single" w:sz="4" w:space="0" w:color="{_options.BorderColor}"/></w:tblBorders>""");
        sw.Write("</w:tblPr><w:tblGrid>");
        foreach (long w in widths) sw.Write($"""<w:gridCol w:w="{w}"/>""");
        sw.Write("</w:tblGrid>");

        if (_options.PrintHeader)
        {
            row.Clear();
            row.Append("<w:tr>");
            for (int c = 0; c < columns.Length; c++)
                AppendCell(row, c, columns[c].HeaderName, header: true, szHalf, widths[c]);
            row.Append("</w:tr>");
            sw.Write(row.ToString());
        }

        foreach (var item in rows)
        {
            if (item is null) continue;
            row.Clear();
            row.Append("<w:tr>");
            for (int c = 0; c < columns.Length; c++)
            {
                var col = columns[c];
                object? v = col.Getter(item);
                AppendCell(row, c, DisplayFormatter.Of(v, col.FormatString), header: false, szHalf, widths[c]);
            }
            row.Append("</w:tr>");
            sw.Write(row.ToString());
        }
        sw.Write("</w:tbl>");
    }

    private void AppendCell(StringBuilder sb, int colIndex, string text, bool header, int szHalf, long width)
    {
        sb.Append($"""<w:tc><w:tcPr><w:tcW w:w="{width}" w:type="dxa"/>""");
        if (header) sb.Append($"""<w:shd w:val="clear" w:color="auto" w:fill="{_options.HeaderFill}"/>""");
        sb.Append("</w:tcPr>");

        // One <w:p> per physical line so \n renders as a real line break.
        int start = 0;
        do
        {
            int nl = text.IndexOf('\n', start);
            string line = nl < 0 ? text[start..] : text.Substring(start, nl - start);
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            sb.Append($"""<w:p><w:pPr><w:spacing w:before="40" w:after="40" w:line="240" w:lineRule="auto"/></w:pPr>""");
            string bold = header ? "<w:b/>" : "";
            if (line.Length > 0)
                sb.Append($"""<w:r><w:rPr>{bold}<w:sz w:val="{szHalf}"/><w:szCs w:val="{szHalf}"/></w:rPr><w:t xml:space="preserve">{XmlUtility.Escape(line)}</w:t></w:r>""");
            else if (header)
                sb.Append($"""<w:r><w:rPr>{bold}<w:sz w:val="{szHalf}"/></w:rPr><w:t xml:space="preserve"> </w:t></w:r>""");
            sb.Append("</w:p>");
            if (nl < 0) break;
            start = nl + 1;
        } while (start <= text.Length);

        sb.Append("</w:tc>");
    }

    private static long[] ComputeWidths(ColumnMetadata[] columns)
    {
        int n = columns.Length;
        var widths = new long[n];
        long explicitSum = 0;
        int autoCount = 0;
        for (int i = 0; i < n; i++)
        {
            double w = columns[i].Width;
            if (w > 0)
            {
                // Excel character width -> pixels (7w+5) -> twips (15 px)
                widths[i] = Math.Max(MinColWidth, (long)Math.Round(w * 105 + 75));
                explicitSum += widths[i];
            }
            else { autoCount++; }
        }

        long budget = ContentWidth;
        if (autoCount > 0)
        {
            long auto = Math.Max(MinColWidth, (budget - explicitSum) / autoCount);
            for (int i = 0; i < n; i++) if (widths[i] == 0) widths[i] = auto;
        }
        long total = widths.Sum();
        if (total > budget) // everything explicit (or generous auto): scale down proportionally
        {
            double k = (double)budget / total;
            for (int i = 0; i < n; i++) widths[i] = (long)(widths[i] * k);
        }
        return widths;
    }

    // ---------------------------------------------------------------------------------------------
    // fixed parts
    // ---------------------------------------------------------------------------------------------

    private static string ContentTypes(bool footer)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
            """);
        if (footer)
            sb.Append("\n  <Override PartName=\"/word/footer1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml\"/>");
        sb.Append("\n</Types>");
        return sb.ToString();
    }

    private static string RootRels => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    private static string DocumentRels(bool footer)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
            """);
        if (footer)
            sb.Append("\n  <Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer\" Target=\"footer1.xml\"/>");
        sb.Append("\n</Relationships>");
        return sb.ToString();
    }

    private static string Styles => """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
          <w:docDefaults><w:rPrDefault><w:rPr>
            <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="宋体" w:cs="Times New Roman"/>
            <w:sz w:val="21"/><w:szCs w:val="21"/><w:lang w:val="en-US" w:eastAsia="zh-CN"/>
          </w:rPr></w:rPrDefault></w:docDefaults>
          <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
          <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:spacing w:before="240" w:after="120"/><w:outlineLvl w:val="0"/></w:pPr>
            <w:rPr><w:b/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr></w:style>
          <w:style w:type="paragraph" w:styleId="DocTitle"><w:name w:val="Title"/><w:basedOn w:val="Normal"/>
            <w:pPr><w:spacing w:before="120" w:after="240"/><w:jc w:val="center"/></w:pPr>
            <w:rPr><w:b/><w:sz w:val="36"/><w:szCs w:val="36"/></w:rPr></w:style>
        </w:styles>
        """;

    // ---------------------------------------------------------------------------------------------
    // footer1.xml (only emitted when WordOptions.FooterText is set)
    // ---------------------------------------------------------------------------------------------

    /// <summary>One centred paragraph; <c>{PAGE}</c> / <c>{NUMPAGES}</c> tokens become live Word fields, everything else literal.</summary>
    private static string BuildFooter(string text)
    {
        const string RPr = """<w:rPr><w:sz w:val="18"/><w:szCs w:val="18"/><w:color w:val="595959"/></w:rPr>""";

        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">""");
        sb.Append("""<w:p><w:pPr><w:jc w:val="center"/></w:pPr>""");

        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('{', i);
            string literal = open < 0 ? text[i..] : text[i..open];
            if (literal.Length > 0)
                sb.Append($"""<w:r>{RPr}<w:t xml:space="preserve">{XmlUtility.Escape(literal)}</w:t></w:r>""");
            if (open < 0) break;

            int rest = text.Length - open;
            if (rest >= 10 && string.CompareOrdinal(text, open, "{NUMPAGES}", 0, 10) == 0) { AppendField(sb, RPr, "NUMPAGES"); i = open + 10; }
            else if (rest >= 6 && string.CompareOrdinal(text, open, "{PAGE}", 0, 6) == 0) { AppendField(sb, RPr, "PAGE"); i = open + 6; }
            else { sb.Append("""<w:r>""" + RPr + """<w:t xml:space="preserve">{</w:t></w:r>"""); i = open + 1; } // stray brace stays literal
        }
        sb.Append("</w:p></w:ftr>");
        return sb.ToString();
    }

    /// <summary>begin → instrText → separate → cached result → end: Word/WPS recompute the real number when paginating.</summary>
    private static void AppendField(StringBuilder sb, string rPr, string instr)
    {
        sb.Append($"""<w:r>{rPr}<w:fldChar w:fldCharType="begin"/></w:r>""");
        sb.Append($"""<w:r>{rPr}<w:instrText xml:space="preserve"> {instr} </w:instrText></w:r>""");
        sb.Append($"""<w:r>{rPr}<w:fldChar w:fldCharType="separate"/></w:r>""");
        sb.Append($"""<w:r>{rPr}<w:t>1</w:t></w:r>""");
        sb.Append($"""<w:r>{rPr}<w:fldChar w:fldCharType="end"/></w:r>""");
    }
}
