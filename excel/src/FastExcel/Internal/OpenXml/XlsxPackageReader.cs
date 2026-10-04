using System.IO.Compression;
using System.Xml;

namespace FastExcel.Internal.OpenXml;

internal sealed record WorksheetInfo(string Name, string PartPath, string SheetId);

/// <summary>
/// Opens an .xlsx OPC package and exposes forward-only worksheet enumeration. Loads the parts that
/// must be random-accessible (sharedStrings, styles) up front, then streams each sheet with an
/// <see cref="XmlReader"/> so row memory stays O(width), not O(rows).
/// </summary>
internal sealed class XlsxPackageReader : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<WorksheetInfo> _sheets = new();
    private string[]? _sharedStrings;
    private StylesReader? _styles;

    private XlsxPackageReader(ZipArchive zip) => _zip = zip;

    public IReadOnlyList<WorksheetInfo> Sheets => _sheets;

    public static XlsxPackageReader Open(Stream stream)
    {
        var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var reader = new XlsxPackageReader(zip);
        reader.LoadStructure();
        return reader;
    }

    private void LoadStructure()
    {
        // workbook.xml: sheet names + their rIds, in document order.
        var wb = Find("xl/workbook.xml");
        if (wb is null) throw new InvalidDataException("Not a valid xlsx: xl/workbook.xml missing.");
        var declared = new List<(string name, string rid, string sid)>();
        using (var xml = XmlReader.Create(wb.Open(), Settings))
        {
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheet")
                    declared.Add((xml.GetAttribute("name") ?? "Sheet", ReadRId(xml), xml.GetAttribute("sheetId") ?? "0"));
            }
        }

        // workbook rels: rId -> target part (relative to xl/).
        var idToTarget = new Dictionary<string, string>();
        var rels = Find("xl/_rels/workbook.xml.rels");
        if (rels is not null)
        {
            using var xml = XmlReader.Create(rels.Open(), Settings);
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship")
                    idToTarget[xml.GetAttribute("Id") ?? ""] = xml.GetAttribute("Target") ?? "";
        }

        foreach (var (name, rid, sid) in declared)
        {
            string target = idToTarget.TryGetValue(rid, out var t) && t.Length > 0
                ? t
                : $"worksheets/sheet{_sheets.Count + 1}.xml";
            string partPath = target.StartsWith("/") ? target[1..] : "xl/" + target;
            _sheets.Add(new WorksheetInfo(name, partPath, sid));
        }
        if (_sheets.Count == 0)
            throw new InvalidDataException("Workbook declares no worksheets.");

        LoadSharedStrings();
        LoadStyles();
    }

    private void LoadSharedStrings()
    {
        var sst = Find("xl/sharedStrings.xml");
        if (sst is null) { _sharedStrings = Array.Empty<string>(); return; }
        var list = new List<string>();
        using var xml = XmlReader.Create(sst.Open(), Settings);
        var sb = new System.Text.StringBuilder();
        bool inSi = false;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "si") { inSi = true; sb.Clear(); }
            else if (xml.NodeType == XmlNodeType.Text && inSi)
            {
                sb.Append(xml.Value);
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "si")
            {
                list.Add(sb.ToString());
                inSi = false;
            }
        }
        _sharedStrings = list.ToArray();
    }

    private void LoadStyles()
    {
        var st = Find("xl/styles.xml");
        _styles = st is not null ? StylesReader.Parse(st.Open()) : StylesReader.Empty;
    }

    public WorksheetReader ReadSheet(ImportOptions? options = null)
    {
        WorksheetInfo info = ResolveSheet(options);
        var entry = _zip.GetEntry(info.PartPath)
            ?? throw new InvalidDataException($"Worksheet part '{info.PartPath}' missing.");
        bool fill = options?.FillMergedCells ?? true;
        List<MergeRange>? merges = fill ? MergeCellScanner.Scan(entry) : null;
        var xml = XmlReader.Create(entry.Open(), Settings);
        return new WorksheetReader(xml, _sharedStrings!, _styles!, merges);
    }

    /// <summary>Returns anchored images (keyed by zero-based row/col) for the sheet selected in <paramref name="options"/>.</summary>
    public Dictionary<(int row, int col), ExcelImage> ReadImagesFor(ImportOptions? options = null)
    {
        var info = ResolveSheet(options);
        return new SheetImageReader(_zip).ReadAnchored(info.PartPath);
    }

    public ZipArchive Archive => _zip;

    /// <summary>Shared string table, empty when the package stores strings inline.</summary>
    public string[] SharedStrings => _sharedStrings ?? Array.Empty<string>();

    public WorksheetInfo ResolveSheet(string? sheetName)
    {
        return _sheets.Count == 0
            ? throw new InvalidOperationException("Workbook has no worksheets.")
            : sheetName is null
                ? _sheets[0]
                : _sheets.Find(s => string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException($"Sheet '{sheetName}' not found.");
    }

    private WorksheetInfo ResolveSheet(ImportOptions? options) => ResolveSheet(options?.SheetName);

    private ZipArchiveEntry? Find(string path) => _zip.GetEntry(path);

    private static string ReadRId(XmlReader xml)
    {
        for (int i = 0; i < xml.AttributeCount; i++)
        {
            xml.MoveToAttribute(i);
            if (xml.LocalName == "id" && xml.NamespaceURI.EndsWith("relationships"))
            {
                string v = xml.Value;
                xml.MoveToElement();
                return v;
            }
            xml.MoveToElement();
        }
        // some writers use r:id without ns detection fallback
        return xml.GetAttribute("r:id") ?? xml.GetAttribute("id") ?? "";
    }

    private static Stream EmptyStream() => new MemoryStream(Array.Empty<byte>());

    internal static readonly XmlReaderSettings Settings = new()
    {
        IgnoreComments = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,
        CloseInput = true
    };

    public void Dispose() => _zip.Dispose();
}
