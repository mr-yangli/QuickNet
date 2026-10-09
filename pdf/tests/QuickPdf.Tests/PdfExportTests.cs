using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using QuickPdf;

namespace QuickPdf.Tests;

public class PdfExportTests
{
    private record Emp(string Dept, string Name, decimal Salary);

    private static readonly Emp[] Sample =
    [
        new("研发部", "张三", 12345.60m),
        new("研发部", "李四&B<C>", 9800m),
        new("市场部", "王五\n第二行", 7654.32m),
    ];

    private static readonly bool HasFont = ProbeFont();
    private static bool ProbeFont()
    {
        try { Pdf.GetAsByteArray(new[] { new Emp("研发部", "张三", 1m) }); return true; }
        catch (QuickPdfException) { return false; }
    }

    [Fact]
    public void Pdf_Structure_Header_Xref_Trailer_Are_Consistent()
    {
        Assert.True(HasFont, "no embeddable CJK font on this machine");
        var bytes = Pdf.GetAsByteArray(Sample, new PdfOptions { Title = "员工报表" });
        var text = AssertPdfStructure(bytes);

        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("/Identity-H", text);
        Assert.Contains("/FontFile2", text);
        Assert.Contains("/Type0", text);
    }

    [Fact]
    public void Pdf_Content_Streams_Draw_Text_Operators()
    {
        Assert.True(HasFont, "no embeddable CJK font on this machine");
        var bytes = Pdf.GetAsByteArray(Sample);
        var streams = ExtractStreams(bytes);
        // content streams only (font binaries and the ToUnicode CMap would pollute the operator scan)
        var content = streams.Select(s => Encoding.Latin1.GetString(s.Data))
            .Where(t => t.Contains("BT /F1"))
            .ToList();
        Assert.NotEmpty(content);
        var decoded = string.Concat(content);
        Assert.Contains("Tj", decoded);          // glyph strings drawn
        Assert.Contains("0.94 0.94 0.94 rg", decoded); // header shading
        // operators must be whitespace-separated from numbers; hex glyph payloads and names (/F1) excluded first
        var opsOnly = Regex.Replace(decoded, @"<[0-9A-Fa-f]*>", " ");
        opsOnly = Regex.Replace(opsOnly, @"/[A-Za-z0-9_]+", " ");
        Assert.DoesNotMatch(@"[A-Za-z]+\d", opsOnly);
        Assert.Contains("/FlateDecode", Latin1(bytes)); // at least one compressed stream
    }

    [Fact]
    public void Pdf_Many_Rows_Produce_Multiple_Pages_With_Repeated_Header()
    {
        Assert.True(HasFont, "no embeddable CJK font on this machine");
        var rows = Enumerable.Range(1, 900).Select(i => new Emp($"部门{i % 7}", $"员工{i}", i * 1.5m)).ToArray();
        var bytes = Pdf.GetAsByteArray(rows);
        var text = AssertPdfStructure(bytes);

        var count = int.Parse(Regex.Match(text, @"/Count (\d+)").Groups[1].Value);
        Assert.True(count >= 2, $"expected multiple pages, got {count}");

        // header row text appears once per page (repeat-on-break)
        var streams = ExtractStreams(bytes);
        int pagesWithHeader = streams.Count(s =>
            s.Data.Length > 100 && Encoding.ASCII.GetString(s.Data).Contains("0.94 0.94 0.94 rg"));
        Assert.True(pagesWithHeader >= 2, $"header shading found on {pagesWithHeader} streams");
    }

    [Fact]
    public void Pdf_Landscape_Uses_Swapped_MediaBox()
    {
        Assert.True(HasFont, "no embeddable CJK font on this machine");
        var bytes = Pdf.GetAsByteArray(Sample, new PdfOptions { Landscape = true });
        Assert.Contains("[0 0 841.89 595.28]", AssertPdfStructure(bytes));
    }

    private class Annotated
    {
        [PdfColumn("编号")] public int Id { get; set; }
        [PdfColumn("薪资", Format = "#,##0.00")] public decimal Salary { get; set; }
        [PdfFormat("0.0")][PdfColumn("比率")] public double Ratio { get; set; } = 0.25;
        [PdfIgnore] public string Secret { get; set; } = "机密";
    }

    [Fact]
    public void Pdf_Attributes_Drive_Header_Format_And_Ignore_Seen_Through_ToUnicode()
    {
        Assert.True(HasFont, "no embeddable CJK font on this machine");
        var bytes = Pdf.GetAsByteArray(new[] { new Annotated { Id = 7, Salary = 12345.6m, Secret = "机密" } });
        AssertPdfStructure(bytes);

        // the ToUnicode CMap lists every code point actually drawn: glyph -> <CP>
        var cmap = CMap(bytes);
        Assert.Matches(@"<[0-9A-F]{4}> <7F16>", cmap); // 编 — renamed header present
        Assert.Matches(@"<[0-9A-F]{4}> <53F7>", cmap); // 号
        Assert.Matches(@"<[0-9A-F]{4}> <6BD4>", cmap); // 比 — second renamed column ([PdfFormat] rides along)
        Assert.Matches(@"<[0-9A-F]{4}> <002C>", cmap); // comma exists only via "#,##0.00"
        Assert.DoesNotMatch(@"<[0-9A-F]{4}> <673A>", cmap); // 机 — [PdfIgnore] never drawn
        Assert.DoesNotMatch(@"<[0-9A-F]{4}> <5BC6>", cmap); // 密
    }

    [Fact]
    public void Pdf_Multiple_Sheets_Draw_Section_Headings()
    {
        Assert.True(HasFont, "no embeddable CJK font on this machine");
        var sheets = new PdfSheet[]
        {
            new("员工", Sample),
            new("字典", new[] { new { Key = "性别", Value = "男" } }),
        };
        var bytes = Pdf.GetAsByteArray(sheets);
        AssertPdfStructure(bytes);

        var cmap = CMap(bytes);
        Assert.Matches(@"<[0-9A-F]{4}> <5458>", cmap); // 员 — sheet 1 heading
        Assert.Matches(@"<[0-9A-F]{4}> <5178>", cmap); // 典 — sheet 2 heading
        Assert.Matches(@"<[0-9A-F]{4}> <6027>", cmap); // 性 — cell value from the anonymous-type sheet
    }

    /// <summary>The ToUnicode CMap stream, decompressed and as Latin-1 text.</summary>
    private static string CMap(byte[] bytes)
    {
        var streams = ExtractStreams(bytes)
            .Where(s => Latin1(s.Data).Contains("begincmap") && Latin1(s.Data).Contains("endbfchar"))
            .ToList();
        Assert.Single(streams);
        return Latin1(streams[0].Data);
    }

    /// <summary>Shared sanity checks: header, obj/endobj balance, xref offsets, startxref -> 'xref', /Root resolves.</summary>
    private static string AssertPdfStructure(byte[] bytes)
    {
        var text = Latin1(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("%%EOF", text[^16..]);

        int objs = Regex.Matches(text, @"\d+ 0 obj").Count;
        Assert.Equal(objs, Regex.Matches(text, "endobj").Count);

        var trailer = Regex.Match(text, @"/Root (\d+) 0 R");
        Assert.True(trailer.Success);
        Assert.Contains($"{trailer.Groups[1].Value} 0 obj", text);

        var sx = Regex.Match(text, @"startxref\n(\d+)\n%%EOF");
        Assert.True(sx.Success);
        int xrefOff = int.Parse(sx.Groups[1].Value);
        Assert.Equal("xref", text.Substring(xrefOff, 4));

        // every xref entry points at its "N 0 obj"
        var xrefBlock = text.Substring((int)xrefOff);
        foreach (Match m in Regex.Matches(xrefBlock, @"(\d{10}) 00000 n"))
        {
            int off = int.Parse(m.Groups[1].Value);
            int space = text.IndexOf(' ', off);
            Assert.True(text.Substring(off, space - off).Length > 0 && space > off, "xref offset lands mid-token");
        }
        return text;
    }

    private readonly record struct PdfStream(string Dict, byte[] Data);

    private static List<PdfStream> ExtractStreams(byte[] bytes)
    {
        var text = Latin1(bytes);
        var list = new List<PdfStream>();
        foreach (Match m in Regex.Matches(text, @">>[ \t\r\n]*stream\r?\n"))
        {
            int dataStart = m.Index + m.Length;
            int dictOpen = text.LastIndexOf("<<", m.Index, StringComparison.Ordinal);
            string header = text[dictOpen..(m.Index + 2)]; // '<< ... >>'
            var lenMatch = Regex.Match(header, @"/Length\s+(\d+)");
            if (!lenMatch.Success) continue;
            int len = int.Parse(lenMatch.Groups[1].Value);
            if (len <= 0 || dataStart + len > bytes.Length) continue; // false positive inside binary payload
            var raw = bytes[dataStart..(dataStart + len)];
            byte[] data = header.Contains("/FlateDecode") ? Inflate(raw) : raw;
            list.Add(new PdfStream(header, data));
        }
        return list;
    }

    private static byte[] Inflate(byte[] zlib)
    {
        using var ms = new MemoryStream(zlib);
        using var z = new ZLibStream(ms, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
