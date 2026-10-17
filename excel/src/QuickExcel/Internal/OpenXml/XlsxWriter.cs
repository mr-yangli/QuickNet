using System.Globalization;
using System.IO.Compression;
using System.Text;
using QuickExcel.Internal.Metadata;
using QuickExcel.Internal.OpenXml;

namespace QuickExcel.Internal.OpenXml;

/// <summary>
/// Streams a single-sheet (or multi-sheet via <see cref="ExcelSheet"/>) export into a valid xlsx
/// OPC package. Memory is O(1) in row count: rows are serialised one at a time into the worksheet
/// entry stream; only the small style/anchor/width bookkeeping is retained.
/// </summary>
internal sealed class XlsxWriter
{
    private const string RelOfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string RelWorksheet = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";
    private const string RelStyles = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles";
    private const string RelSharedStrings = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings";
    private const string RelDrawing = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing";

    private const string CtXml = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

    private readonly Stream _output;
    private readonly ExportOptions _options;
    private readonly Stylesheet _styles = new();
    private readonly SharedStrings? _shared;
    private readonly MediaCollector _media = new();

    public XlsxWriter(Stream output, ExportOptions options)
    {
        _output = output;
        _options = options;
        if (options.StringStorage == StringStorageMode.Shared)
            _shared = new SharedStrings();
    }

    public async Task WriteAsync(IEnumerable<SheetInput> sheets, CancellationToken ct)
    {
        using var zip = new ZipArchive(_output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        var sheetParts = new List<SheetRecord>();
        int sheetIndex = 1;

        foreach (var sheet in sheets)
        {
            ct.ThrowIfCancellationRequested();
            var record = await WriteSheetAsync(zip, sheet, sheetIndex++, ct).ConfigureAwait(false);
            sheetParts.Add(record);
        }

        // styles.xml (contents finalised during streaming)
        await WriteStringEntryAsync(zip, "xl/styles.xml", _styles.BuildXml(), ct).ConfigureAwait(false);

        // sharedStrings.xml
        string? sstPartName = null;
        if (_shared is not null && _shared.Count > 0)
        {
            sstPartName = "xl/sharedStrings.xml";
            await WriteStringEntryAsync(zip, sstPartName, _shared.BuildXml(), ct).ConfigureAwait(false);
        }

        await WriteWorkbookAndPackageAsync(zip, sheetParts, sstPartName is not null, ct).ConfigureAwait(false);
    }

    private async Task<SheetRecord> WriteSheetAsync(ZipArchive zip, SheetInput sheet, int sheetIndex, CancellationToken ct)
    {
        var partName = $"xl/worksheets/sheet{sheetIndex}.xml";
        var columns = sheet.Columns;
        int colCount = columns.Length;
        string lastCol = colCount > 0 ? XmlUtility.ColumnLetter(colCount - 1) : "A";
        var drawing = new DrawingWriter(sheetIndex);

        // Scope the worksheet entry stream: ZipArchive (Create mode) forbids creating new entries
        // while a prior entry stream is still open, so all drawing/rels parts are written after this block.
        {
            var entry = zip.CreateEntry(partName, _options.CompressionLevel);
            using var entryStream = entry.Open();
            using var sw = new StreamWriter(entryStream, new UTF8Encoding(false), _options.BufferSize);

        await sw.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>").ConfigureAwait(false);
        await sw.WriteAsync("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">").ConfigureAwait(false);
        await sw.WriteAsync($"<dimension ref=\"A1:{lastCol}1\"/>").ConfigureAwait(false);
        await sw.WriteAsync(BuildSheetViews(sheetIndex)).ConfigureAwait(false);
        await sw.WriteAsync("<sheetFormatPr defaultRowHeight=\"15\"/>").ConfigureAwait(false);

        // column widths (only for columns that declare one)
        string colsXml = BuildCols(columns);
        if (colsXml.Length > 0) await sw.WriteAsync(colsXml).ConfigureAwait(false);

        await sw.WriteAsync("<sheetData>").ConfigureAwait(false);

        var cellBuf = new StringBuilder(256);
        var rowBuf = new StringBuilder(288);
        int rowIndex = 0;

        if (_options.PrintHeader)
        {
            rowIndex = 1;
            rowBuf.Clear();
            rowBuf.Append("<row r=\"1\">");
            for (int c = 0; c < colCount; c++)
            {
                int headerStyle = _styles.GetStyleIndex(XlCellCategory.Text, null,
                    bold: _options.BoldHeader || columns[c].Bold, align: columns[c].Align);
                AppendTextCell(rowBuf, rowIndex, c, columns[c].HeaderName, headerStyle);
            }
            rowBuf.Append("</row>");
            await sw.WriteAsync(rowBuf.ToString()).ConfigureAwait(false);
        }

        foreach (var item in sheet.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (item is null) continue; // tolerate null entries in the sequence
            rowIndex++;

            // Build the row's cells first so we know the tallest image before emitting the <row> open tag.
            cellBuf.Clear();
            double maxRowHeightPx = 0;
            for (int c = 0; c < colCount; c++)
            {
                var col = columns[c];
                object? raw = col.Getter(item);
                WriteCell(cellBuf, rowIndex, c, col, raw, drawing, out double rowPx);
                if (rowPx > maxRowHeightPx) maxRowHeightPx = rowPx;
            }

            rowBuf.Clear();
            rowBuf.Append("<row r=\"").Append(rowIndex).Append('"');
            if (maxRowHeightPx > 0)
                rowBuf.Append(" ht=\"").Append((maxRowHeightPx * 0.75).ToString("0.##", CultureInfo.InvariantCulture))
                       .Append("\" customHeight=\"1\""); // height(points) = pixels * 72/96
            rowBuf.Append('>').Append(cellBuf).Append("</row>");
            await sw.WriteAsync(rowBuf.ToString()).ConfigureAwait(false);
        }

        await sw.WriteAsync("</sheetData>").ConfigureAwait(false);

        if (drawing.HasAnchors)
            await sw.WriteAsync($"<drawing r:id=\"rId1\"/>").ConfigureAwait(false);
        await sw.WriteAsync("</worksheet>").ConfigureAwait(false);
        await sw.FlushAsync().ConfigureAwait(false);
        } // worksheet entry closed here

        string? drawingName = null;
        if (drawing.HasAnchors)
        {
            var (drawingXml, relsXml) = drawing.Build();
            drawingName = $"xl/drawings/drawing{sheetIndex}.xml";
            await WriteStringEntryAsync(zip, drawingName, drawingXml, ct).ConfigureAwait(false);
            await WriteStringEntryAsync(zip, $"xl/drawings/_rels/drawing{sheetIndex}.xml.rels", relsXml, ct).ConfigureAwait(false);
            // sheet -> drawing relationship
            await WriteStringEntryAsync(zip, $"xl/worksheets/_rels/sheet{sheetIndex}.xml.rels",
                $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{RelDrawing}\" Target=\"../drawings/drawing{sheetIndex}.xml\"/></Relationships>", ct).ConfigureAwait(false);
        }

        return new SheetRecord(sheet.Name, partName, drawingName);
    }

    private void WriteCell(StringBuilder buf, int row, int colIndex, ExcelColumnMetadata col, object? raw, DrawingWriter drawing, out double rowPx)
    {
        rowPx = 0;
        // image handling
        if (col.IsImage)
        {
            ExcelImage? img = raw switch
            {
                ExcelImage direct => direct,
                byte[] bytes when bytes.Length > 0 => new ExcelImage(bytes),
                _ => null
            };
            if (img is null) return;
            var media = _media.Add(img);
            int w = col.ImageOptions?.Width is > 0 ? col.ImageOptions!.Width : (img.PixelWidth > 0 ? img.PixelWidth : 80);
            int h = col.ImageOptions?.Height is > 0 ? col.ImageOptions!.Height : (img.PixelHeight > 0 ? img.PixelHeight : 60);
            drawing.Add(new DrawingAnchor(row - 1, colIndex, media, w, h));
            if (col.ImageOptions is null || col.ImageOptions.AutoFitRow) rowPx = h + 4;
            return;
        }

        var cell = ValueSerializer.Serialize(raw, out bool isImg);
        if (isImg && raw is ExcelImage runtimeImg)
        {
            var media = _media.Add(runtimeImg);
            int w = runtimeImg.PixelWidth > 0 ? runtimeImg.PixelWidth : 80;
            int h = runtimeImg.PixelHeight > 0 ? runtimeImg.PixelHeight : 60;
            drawing.Add(new DrawingAnchor(row - 1, colIndex, media, w, h));
            if (rowPx < h) rowPx = h + 4;
            return;
        }

        switch (cell.Category)
        {
            case XlCellCategory.Empty:
                return;
            case XlCellCategory.Bool:
            {
                int s = _styles.GetStyleIndex(XlCellCategory.Text, col.FormatString, col.Bold, col.Align);
                AppendRef(buf, row, colIndex);
                buf.Append(" s=\"").Append(s).Append("\" t=\"b\"><v>").Append(cell.Value).Append("</v></c>");
                break;
            }
            case XlCellCategory.Number:
            case XlCellCategory.Date:
            case XlCellCategory.Time:
            {
                int s = _styles.GetStyleIndex(cell.Category, col.FormatString, col.Bold, col.Align);
                AppendRef(buf, row, colIndex);
                buf.Append(" s=\"").Append(s).Append("\"><v>").Append(cell.Value).Append("</v></c>");
                break;
            }
            case XlCellCategory.Text:
            default:
            {
                int s = _styles.GetStyleIndex(XlCellCategory.Text, col.FormatString, col.Bold, col.Align);
                if (_shared is not null)
                {
                    int idx = _shared.GetOrAdd(cell.Value);
                    AppendRef(buf, row, colIndex);
                    buf.Append(" s=\"").Append(s).Append("\" t=\"s\"><v>").Append(idx).Append("</v></c>");
                }
                else
                {
                    AppendRef(buf, row, colIndex);
                    buf.Append(" s=\"").Append(s).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                       .Append(XmlUtility.Escape(cell.Value)).Append("</t></is></c>");
                }
                break;
            }
        }
    }

    private void AppendTextCell(StringBuilder buf, int row, int colIndex, string text, int style)
    {
        AppendRef(buf, row, colIndex);
        buf.Append(" s=\"").Append(style).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
           .Append(XmlUtility.Escape(text)).Append("</t></is></c>");
    }

    private static void AppendRef(StringBuilder buf, int row, int colIndex)
    {
        buf.Append("<c r=\"").Append(XmlUtility.ColumnLetter(colIndex)).Append(row).Append('"');
    }

    private string BuildSheetViews(int sheetIndex)
    {
        if (!_options.FreezeHeaderRow)
            return "<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>";
        // freeze first row
        return "<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\">" +
               "<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>" +
               "<selection pane=\"bottomLeft\" activeCell=\"A2\" sqref=\"A2\"/></sheetView></sheetViews>";
    }

    private static string BuildCols(ExcelColumnMetadata[] columns)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < columns.Length; i++)
        {
            double w = columns[i].Width;
            if (w < 0) continue;
            sb ??= new StringBuilder();
            if (sb.Length == 0) sb.Append("<cols>");
            string letter = XmlUtility.ColumnLetter(i);
            sb.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1)
              .Append("\" width=\"").Append(w.ToString("0.###", CultureInfo.InvariantCulture))
              .Append("\" customWidth=\"1\"/>");
        }
        if (sb is null) return string.Empty;
        sb.Append("</cols>");
        return sb.ToString();
    }

    private async Task WriteWorkbookAndPackageAsync(ZipArchive zip, List<SheetRecord> sheets, bool hasShared, CancellationToken ct)
    {
        // workbook.xml
        var wb = new StringBuilder();
        wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
          .Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (int i = 0; i < sheets.Count; i++)
            wb.Append("<sheet name=\"").Append(XmlUtility.EscapeAttribute(UniqueSheetName(sheets[i].Name, i))).Append("\" sheetId=\"")
              .Append(i + 1).Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
        wb.Append("</sheets></workbook>");
        await WriteStringEntryAsync(zip, "xl/workbook.xml", wb.ToString(), ct).ConfigureAwait(false);

        // workbook rels: sheets..., styles, sharedStrings
        var wr = new StringBuilder();
        wr.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        int rid = 1;
        foreach (var s in sheets)
            wr.Append("<Relationship Id=\"rId").Append(rid++).Append("\" Type=\"").Append(RelWorksheet)
              .Append("\" Target=\"").Append(s.PartName["xl/".Length..]).Append("\"/>");
        int stylesRid = rid++;
        wr.Append("<Relationship Id=\"rId").Append(stylesRid).Append("\" Type=\"").Append(RelStyles).Append("\" Target=\"styles.xml\"/>");
        int? sstRid = null;
        if (hasShared)
        {
            sstRid = rid++;
            wr.Append("<Relationship Id=\"rId").Append(sstRid.Value).Append("\" Type=\"").Append(RelSharedStrings).Append("\" Target=\"sharedStrings.xml\"/>");
        }
        wr.Append("</Relationships>");
        await WriteStringEntryAsync(zip, "xl/_rels/workbook.xml.rels", wr.ToString(), ct).ConfigureAwait(false);

        // root rels
        await WriteStringEntryAsync(zip, "_rels/.rels",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{RelOfficeDocument}\" Target=\"xl/workbook.xml\"/></Relationships>", ct).ConfigureAwait(false);

        // media parts
        foreach (var m in _media.Parts)
            await WriteBinaryEntryAsync(zip, m.PartName, m.Data, ct).ConfigureAwait(false);

        // content types
        await WriteContentTypesAsync(zip, sheets, hasShared, ct).ConfigureAwait(false);
    }

    private async Task WriteContentTypesAsync(ZipArchive zip, List<SheetRecord> sheets, bool hasShared, CancellationToken ct)
    {
        var ct2 = new StringBuilder();
        ct2.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        ct2.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        ct2.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");

        foreach (var ext in CollectImageExtensions())
        {
            string mime = ext switch
            {
                "png" => "image/png",
                "jpeg" or "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "bmp" => "image/bmp",
                "tiff" or "tif" => "image/tiff",
                "emf" => "image/x-emf",
                "wmf" => "image/x-wmf",
                _ => "application/octet-stream"
            };
            ct2.Append("<Default Extension=\"").Append(ext).Append("\" ContentType=\"").Append(mime).Append("\"/>");
        }

        ct2.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        foreach (var s in sheets)
            ct2.Append("<Override PartName=\"/").Append(s.PartName).Append("\" ContentType=\"").Append(CtXml).Append("\"/>");
        ct2.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        if (hasShared)
            ct2.Append("<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>");
        foreach (var s in sheets)
            if (s.DrawingPartName is not null)
                ct2.Append("<Override PartName=\"/").Append(s.DrawingPartName).Append("\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
        ct2.Append("</Types>");

        await WriteStringEntryAsync(zip, "[Content_Types].xml", ct2.ToString(), ct).ConfigureAwait(false);
    }

    private IEnumerable<string> CollectImageExtensions()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _media.Parts) set.Add(m.Extension);
        return set;
    }

    private static string UniqueSheetName(string baseName, int index)
    {
        // Excel requires unique, non-empty names <=31 chars, no []:*?/\
        string name = string.IsNullOrEmpty(baseName) ? $"Sheet{index + 1}" : baseName;
        foreach (char c in new[] { '[', ']', ':', '*', '?', '/', '\\' })
            name = name.Replace(c, '_');
        if (name.Length > 31) name = name[..31];
        return name;
    }

    private static async Task WriteStringEntryAsync(ZipArchive zip, string partName, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(partName, CompressionLevel.Fastest);
        using var s = entry.Open();
        using var sw = new StreamWriter(s, new UTF8Encoding(false));
        await sw.WriteAsync(content).ConfigureAwait(false);
        await sw.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteBinaryEntryAsync(ZipArchive zip, string partName, byte[] data, CancellationToken ct)
    {
        var entry = zip.CreateEntry(partName, CompressionLevel.NoCompression);
        using var s = entry.Open();
        await s.WriteAsync(data.AsMemory(), ct).ConfigureAwait(false);
    }

    private sealed record SheetRecord(string Name, string PartName, string? DrawingPartName);
}

internal sealed record SheetInput(string Name, IEnumerable<object?> Rows, ExcelColumnMetadata[] Columns);
