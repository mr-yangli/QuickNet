using System.Globalization;
using System.Text;

namespace FastPdf.Internal;

/// <summary>
/// Streams sheet data (normalised through <see cref="SheetBuilder"/>) onto PDF pages:
/// one table per sheet, wrapped cell text, shaded + repeated header rows, grid lines, page breaks.
/// Text is drawn with an embedded Identity-H CID font: every glyph is addressed by its glyph id,
/// so any viewer that can read the PDF shows Chinese whether or not the font is installed.
/// </summary>
internal static class PdfTableRenderer
{
    private const double A4W = 595.28, A4H = 841.89;

    public static byte[] Render(object value, PdfOptions options)
    {
        var font = FontResolver.Resolve(options);
        var doc = new PdfDoc();

        // Deterministic layout: 1 catalog, 2 pages tree, 3 Type0, 4 CIDFont, 5 descriptor,
        // 6 font file, 7 ToUnicode, 8 resources; pages/content from 9 on.
        int catalog = doc.ReserveObject();
        int pages = doc.ReserveObject();
        int type0 = doc.ReserveObject();
        int cidFont = doc.ReserveObject();
        int desc = doc.ReserveObject();
        // Whole CJK fonts are ~20 MB: compress them fast, not maximally (SmallestSize costs tens of seconds).
        int fontFile = doc.AddStream(font.FontData, $"/Length1 {font.FontData.Length}", System.IO.Compression.CompressionLevel.Fastest);
        int toUnicode = doc.ReserveObject();
        int resources = doc.ReserveObject();

        var state = new DrawState(doc, font, options);
        state.RenderAll(value);

        string baseFont = SanitizeName(font.PostScriptName);

        doc.SetObject(desc,
            $"<< /Type /FontDescriptor /FontName /{baseFont} /Flags {(font.FixedPitch ? 5 : 4)} " +
            $"/FontBBox [{Scale(font.BboxXMin, font.UnitsPerEm)} {Scale(font.BboxYMin, font.UnitsPerEm)} " +
            $"{Scale(font.BboxXMax, font.UnitsPerEm)} {Scale(font.BboxYMax, font.UnitsPerEm)}] " +
            $"/ItalicAngle 0 /Ascent {Scale(font.Ascender, font.UnitsPerEm)} /Descent {Scale(font.Descender, font.UnitsPerEm)} " +
            $"/CapHeight {Scale((int)(font.Ascender * 0.72), font.UnitsPerEm)} /StemV 80 /FontFile2 {fontFile} 0 R >>");

        doc.SetObject(cidFont,
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} " +
            $"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {desc} 0 R /DW 1000 /W [{state.BuildWidths()}] /CIDToGIDMap /Identity >>");

        doc.SetObject(type0,
            $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H " +
            $"/DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");

        doc.SetStream(toUnicode, BuildToUnicode(state.GlyphToFirstCp));
        doc.SetObject(resources, $"<< /Font << /F1 {type0} 0 R >> >>");
        doc.SetObject(pages, $"<< /Type /Pages /Kids [{string.Join(" ", state.PageIds.Select(id => $"{id} 0 R"))}] /Count {state.PageIds.Count} >>");
        doc.SetObject(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");

        return doc.Build(catalog);
    }

    private static int Scale(int fontUnit, int upm) => (int)Math.Round(fontUnit * 1000.0 / upm);

    private static string SanitizeName(string raw)
    {
        var sb = new StringBuilder(raw.Length + 8);
        foreach (char c in raw)
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '+' or '.') sb.Append(c);
        string s = sb.ToString();
        if (s.Length == 0) s = "FastPdfFont";
        if (char.IsDigit(s[0])) s = "F-" + s;
        return s;
    }

    /// <summary>Unicode code point -> CID glyph id, grouped into bfchar blocks of at most 100.</summary>
    private static byte[] BuildToUnicode(Dictionary<int, int> gidToCp)
    {
        var entries = gidToCp.OrderBy(kv => kv.Key).ToList();
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int i = 0; i < entries.Count; i += 100)
        {
            int n = Math.Min(100, entries.Count - i);
            sb.Append(n).Append(" beginbfchar\n");
            for (int j = 0; j < n; j++)
            {
                var (gid, cp) = entries[i + j];
                sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                  .Append(CpToUtf16Hex(cp)).Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string CpToUtf16Hex(int cp)
    {
        if (cp <= 0xFFFF) return cp.ToString("X4", CultureInfo.InvariantCulture);
        int v = cp - 0x10000;
        int hi = 0xD800 + (v >> 10), lo = 0xDC00 + (v & 0x3FF);
        return hi.ToString("X4", CultureInfo.InvariantCulture) + lo.ToString("X4", CultureInfo.InvariantCulture);
    }

    /// <summary>Holds the page cursor + used-glyph bookkeeping while the table streams out.</summary>
    private sealed class DrawState
    {
        private const double PadX = 4, PadY = 3;

        private readonly PdfDoc _doc;
        private readonly Ttf _font;
        private readonly PdfOptions _o;
        private readonly List<int> _pageIds = new();
        private readonly SortedSet<int> _usedGids = new();
        private readonly Dictionary<int, int> _gidToCp = new();
        private readonly double _pageW, _pageH, _m, _rowLimit;

        private StringBuilder _ops = new();
        private double _y; // top-down cursor, page units
        private string[]? _headerCells;
        private double[] _colX = [];  // column boundaries incl. left and right edges
        private double[] _colW = [];

        public IReadOnlyList<int> PageIds => _pageIds;
        public Dictionary<int, int> GlyphToFirstCp => _gidToCp;

        public DrawState(PdfDoc doc, Ttf font, PdfOptions o)
        {
            _doc = doc; _font = font; _o = o;
            _pageW = o.Landscape ? A4H : A4W;
            _pageH = o.Landscape ? A4W : A4H;
            _m = Math.Max(20, o.MarginPt);
            _rowLimit = _pageH - _m;
            NewPageContent();
        }

        public void RenderAll(object value)
        {
            var sheets = SheetBuilder.Build(value);

            if (_o.Title is { Length: > 0 } title)
            {
                double ts = _o.TitleSizePt;
                foreach (var line in Wrap(title, ContentWidth(), ts))
                {
                    EnsureSpace(ts * 1.3);
                    DrawLine(_m, _y + ts * 0.85, line, ts, bold: false);
                    _y += ts * 1.3;
                }
                _y += 6;
            }

            for (int s = 0; s < sheets.Count; s++)
            {
                var sheet = sheets[s];
                if (s > 0) _y += 14; // breathing room between tables

                if (sheets.Count > 1)
                {
                    double hs = _o.FontSizePt + 3;
                    EnsureSpace(hs * 1.4);
                    DrawLine(_m, _y + hs * 0.85, sheet.Name, hs, bold: true);
                    _y += hs * 1.4 + 2;
                }

                ComputeColumns(sheet.Columns);
                _headerCells = _o.PrintHeader
                    ? sheet.Columns.Select(c => c.HeaderName).ToArray()
                    : null;

                if (_headerCells is not null)
                    DrawRow(_headerCells, header: true);

                foreach (var item in sheet.Rows)
                {
                    if (item is null) continue;
                    var texts = new string[sheet.Columns.Length];
                    for (int c = 0; c < sheet.Columns.Length; c++)
                        texts[c] = DisplayFormatter.Of(sheet.Columns[c].Getter(item), sheet.Columns[c].FormatString);
                    DrawRow(texts, header: false);
                }
                _headerCells = null;
            }

            FinishPage();
        }

        private double ContentWidth() => _pageW - 2 * _m;

        private void ComputeColumns(ColumnMetadata[] cols)
        {
            // Explicit widths (Excel char units -> points) are honoured; the rest share what is left.
            double budget = ContentWidth();
            var widths = new double[cols.Length];
            double explicitSum = 0; int autoCount = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i].Width > 0) { widths[i] = cols[i].Width * 6.0; explicitSum += widths[i]; }
                else { widths[i] = -1; autoCount++; }
            }
            double autoShare = autoCount > 0 ? Math.Max(36, (budget - explicitSum) / autoCount) : 0;
            for (int i = 0; i < cols.Length; i++)
                if (widths[i] < 0) widths[i] = autoShare;

            double total = widths.Sum();
            if (total > budget && total > 0)
                for (int i = 0; i < widths.Length; i++) widths[i] *= budget / total;

            _colW = widths;
            _colX = new double[cols.Length + 1];
            for (int i = 0; i < cols.Length; i++) _colX[i + 1] = _colX[i] + widths[i];
        }

        private void DrawRow(string[] cells, bool header)
        {
            double size = _o.FontSizePt;
            double lh = size * 1.45;

            var cellLines = new List<string>[cells.Length];
            int maxLines = 1;
            for (int c = 0; c < cells.Length; c++)
            {
                var lines = Wrap(cells[c], _colW[c] - 2 * PadX, size);
                cellLines[c] = lines;
                if (lines.Count > maxLines) maxLines = lines.Count;
            }
            double rowH = maxLines * lh + 2 * PadY;

            if (_y + rowH > _rowLimit && _y > _m)
            {
                FinishPage();
                NewPageContent();
                // Repeat the column header at the top of every new page (once).
                if (!header && _headerCells is not null)
                    DrawRow(_headerCells, header: true);
            }

            double top = _y;
            if (header)
                Op(string.Format(CultureInfo.InvariantCulture,
                    "{0:0.###} {1:0.###} {2:0.###} rg {3:0.###} {4:0.###} {5:0.###} {6:0.###} re f\n",
                    0.94, 0.94, 0.94, _colX[0], PageY(top + rowH), _colX[^1] - _colX[0], rowH));

            var sb = new StringBuilder();
            sb.Append("0.6 0.6 0.6 RG 0.6 w\n");
            double yb = PageY(top), yt = PageY(top + rowH);
            sb.Append(F(_colX[0])).Append(' ').Append(F(yb)).Append(" m ").Append(F(_colX[^1])).Append(' ').Append(F(yb)).Append(" l\n");
            sb.Append(F(_colX[0])).Append(' ').Append(F(yt)).Append(" m ").Append(F(_colX[^1])).Append(' ').Append(F(yt)).Append(" l\n");
            for (int c = 0; c <= _colW.Length; c++)
                sb.Append(F(_colX[c])).Append(' ').Append(F(yb)).Append(" m ").Append(F(_colX[c])).Append(' ').Append(F(yt)).Append(" l\n");
            sb.Append("S\n");
            Op(sb.ToString());

            for (int c = 0; c < cells.Length; c++)
            {
                var lines = cellLines[c];
                for (int li = 0; li < lines.Count; li++)
                {
                    double baseline = top + PadY + size * 0.82 + li * lh;
                    DrawLine(_colX[c] + PadX, baseline, lines[li], size, bold: header);
                }
            }

            _y = top + rowH;
        }

        private void DrawLine(double xLeft, double baselineTopDown, string text, double size, bool bold)
        {
            if (text.Length == 0) return;
            string hex = EncodeText(text);
            // reset fill/stroke to black: the header shading's 0.94 rg would otherwise tint all text
            Op("0 g 0 G\n");
            if (bold)
            {
                // faux bold via fill+stroke render mode: one Tj (overprinting would duplicate text on copy)
                Op(string.Format(CultureInfo.InvariantCulture,
                    "BT /F1 {1:0.###} Tf 2 Tr 0.35 w 1 0 0 1 {0:0.###} {2:0.###} Tm <{3}> Tj 0 Tr ET\n",
                    xLeft, size, PageY(baselineTopDown), hex));
                return;
            }
            Op(TextOp(hex, xLeft, baselineTopDown, size));
        }

        private string TextOp(string hex, double x, double baselineTopDown, double size) => string.Format(
            CultureInfo.InvariantCulture,
            "BT /F1 {1:0.###} Tf 1 0 0 1 {0:0.###} {2:0.###} Tm <{3}> Tj ET\n",
            x, size, PageY(baselineTopDown), hex);

        private string EncodeText(string text)
        {
            var sb = new StringBuilder(text.Length * 4);
            for (int i = 0; i < text.Length;)
            {
                int cp = char.ConvertToUtf32(text, i);
                i += char.IsHighSurrogate(text[i]) ? 2 : 1;
                int gid = _font.TryGlyphId(cp, out int g) && g != 0 ? g : 0;
                if (gid != 0)
                {
                    _usedGids.Add(gid);
                    if (!_gidToCp.ContainsKey(gid)) _gidToCp[gid] = cp;
                }
                sb.Append(gid.ToString("X4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public string BuildWidths()
        {
            var sb = new StringBuilder();
            using var e = _usedGids.GetEnumerator();
            if (!e.MoveNext()) return "";
            int runStart = e.Current, prev = e.Current;
            var run = new List<int> { _font.WidthThousand(prev) };
            void Flush() => sb.Append(runStart).Append(" [").Append(string.Join(" ", run)).Append("] ");
            while (e.MoveNext())
            {
                if (e.Current == prev + 1) { run.Add(_font.WidthThousand(e.Current)); prev = e.Current; }
                else
                {
                    Flush();
                    runStart = prev = e.Current;
                    run.Clear();
                    run.Add(_font.WidthThousand(prev));
                }
            }
            Flush();
            return sb.ToString().TrimEnd();
        }

        private void EnsureSpace(double needed)
        {
            if (_y + needed > _rowLimit && _y > _m)
            {
                FinishPage();
                NewPageContent();
            }
        }

        private void NewPageContent()
        {
            _ops = new StringBuilder();
            _y = _m;
        }

        private void FinishPage()
        {
            int content = _doc.AddStream(Encoding.ASCII.GetBytes(_ops.ToString()));
            int page = _doc.ReserveObject();
            _doc.SetObject(page,
                string.Format(CultureInfo.InvariantCulture,
                    "<< /Type /Page /Parent 2 0 R /Resources 8 0 R /Contents {0} 0 R /MediaBox [0 0 {1:0.##} {2:0.##}] >>",
                    content, _pageW, _pageH));
            _pageIds.Add(page);
        }

        private double PageY(double topDown) => _pageH - topDown;
        private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private void Op(string s) => _ops.Append(s);

        // ---- text wrapping -------------------------------------------------------------

        /// <summary>
        /// Greedy wrap at font-measured width. CJK ideographs break per character; Latin words and
        /// digit runs are atomic; explicit newlines are honoured. Oversized single tokens hard-break.
        /// </summary>
        private List<string> Wrap(string text, double availW, double size)
        {
            var lines = new List<string>();
            if (text.Length == 0) { lines.Add(""); return lines; }

            foreach (var para in text.Split('\n'))
            {
                if (para.Length == 0) { lines.Add(""); continue; }
                var cur = new StringBuilder();
                double curW = 0;
                foreach (var token in Tokenize(para))
                {
                    double w = Measure(token, size);
                    if (curW + w > availW && cur.Length > 0)
                    {
                        lines.Add(cur.ToString().TrimEnd());
                        cur.Clear(); curW = 0;
                        if (token.Length > 0 && IsSpaceToken(token[0])) continue;
                    }
                    if (w > availW) // single oversized token: hard-break by character
                    {
                        if (cur.Length > 0) { lines.Add(cur.ToString().TrimEnd()); cur.Clear(); curW = 0; }
                        var seg = new StringBuilder();
                        double segW = 0;
                        for (int i = 0; i < token.Length;)
                        {
                            int cp = char.ConvertToUtf32(token, i);
                            int len = char.IsHighSurrogate(token[i]) ? 2 : 1;
                            double cw = CpWidth(cp, size);
                            if (segW + cw > availW && seg.Length > 0)
                            {
                                lines.Add(seg.ToString());
                                seg.Clear(); segW = 0;
                            }
                            seg.Append(token, i, len);
                            segW += cw;
                            i += len;
                        }
                        if (seg.Length > 0) { cur = seg; curW = segW; }
                        continue;
                    }
                    cur.Append(token);
                    curW += w;
                }
                if (cur.Length > 0) lines.Add(cur.ToString().TrimEnd());
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        private static bool IsCjk(int cp) =>
            cp is >= 0x1100 and <= 0x115F or                       // Hangul Jamo
                       >= 0x2E80 and <= 0x303E or                  // CJK radicals, kangxi, punctuation
                       >= 0x3041 and <= 0x33FF or                  // kana, enclosed CJK
                       >= 0x3400 and <= 0x4DBF or                  // ext A
                       >= 0x4E00 and <= 0x9FFF or                  // unified
                       >= 0xF900 and <= 0xFAFF or                  // compat
                       >= 0xFE30 and <= 0xFE4F or                  // CJK compat forms
                       >= 0xFF00 and <= 0xFF60 or                  // fullwidth forms
                       >= 0x20000 and <= 0x2FFFF or                // ext B+
                       >= 0x30000 and <= 0x3FFFF;
        private static bool IsSpaceToken(int cp) => cp is ' ' or '\t';

        private static IEnumerable<string> Tokenize(string s)
        {
            for (int i = 0; i < s.Length;)
            {
                int cp = char.ConvertToUtf32(s, i);
                int len = char.IsHighSurrogate(s[i]) ? 2 : 1;
                if (IsSpaceToken(cp))
                {
                    int j = i + len;
                    while (j < s.Length && IsSpaceToken(char.ConvertToUtf32(s, j)))
                        j += char.IsHighSurrogate(s[j]) ? 2 : 1;
                    yield return s[i..j];
                    i = j;
                }
                else if (IsCjk(cp))
                {
                    yield return s.Substring(i, len);
                    i += len;
                }
                else
                {
                    int j = i + len;
                    while (j < s.Length)
                    {
                        int c2 = char.ConvertToUtf32(s, j);
                        int l2 = char.IsHighSurrogate(s[j]) ? 2 : 1;
                        if (IsSpaceToken(c2) || IsCjk(c2)) break;
                        j += l2;
                    }
                    yield return s[i..j];
                    i = j;
                }
            }
        }

        private double Measure(string s, double size)
        {
            double w = 0;
            for (int i = 0; i < s.Length;)
            {
                int cp = char.ConvertToUtf32(s, i);
                int len = char.IsHighSurrogate(s[i]) ? 2 : 1;
                w += CpWidth(cp, size);
                i += len;
            }
            return w;
        }

        private double CpWidth(int cp, double size)
        {
            int gid = _font.TryGlyphId(cp, out int g) ? g : 0;
            return _font.WidthThousand(gid) * size / 1000.0;
        }
    }
}
