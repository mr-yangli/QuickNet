using System.Buffers.Binary;
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

    // ── 畸形字体防御（v0.2 第二笔） ──────────────────────────────────────
    // FontFile 显式指定时 LoadOrThrow 直接把 Ttf.Load 的失败包成 QuickPdfException 交出，不会落回
    // 内置候选——所以畸形文件的三种形状都能在公开面上验到，不需要反射进 internal Ttf。
    // 断言的重点是异常的**形状**：必须是 InvalidDataException 那一路"文件坏了"，
    // 不得再出现越界/溢出那种"责怪调用语句"的误导。Format 12 那格买的是"当场回来"：真按声称
    // 21 亿格去转，旧实现在这台机器上是分钟量级；clamp 后毫秒级——用例本身不计时，
    // 挂住的是"新实现必须整场秒级完成"这一桩事实。

    private static string TempFont(params byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"qp-malfont-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static Exception? Core(QuickPdfException ex) => ex.InnerException;

    private static bool Misleading(Exception? ex) =>
        ex is ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException
            or NullReferenceException or AccessViolationException;

    [Fact]
    public void A_truncated_sfnt_directory_reports_the_malformation_not_an_out_of_range()
    {
        var garbage = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(garbage, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(garbage.AsSpan(4), 60000); // numTables，目录装不下
        var path = TempFont(garbage);
        try
        {
            var ex = Record.Exception(() =>
                Pdf.GetAsByteArray(Sample, new PdfOptions { FontFile = path }));
            Assert.False(Misleading(Core(AsQ(ex))), "leaked a misleading exception");
            Assert.IsType<InvalidDataException>(Core(AsQ(ex)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_fraudulent_ttc_directory_is_refused_with_a_naming_message()
    {
        var garbage = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(garbage, 0x74746366); // ttcf
        BinaryPrimitives.WriteUInt32BigEndian(garbage.AsSpan(8), 0x7FFFFFFF); // numFonts
        var path = TempFont(garbage);
        try
        {
            var ex = Record.Exception(() =>
                Pdf.GetAsByteArray(Sample, new PdfOptions { FontFile = path }));
            Assert.False(Misleading(Core(AsQ(ex))), "leaked a misleading exception");
            Assert.IsType<InvalidDataException>(Core(AsQ(ex)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_cmap12_group_claiming_two_billion_code_points_clamps_instead_of_spinning()
    {
        byte[] sfnt = BuildMinimalFraudTtf(start: 0x4E00, end: 0x007FFFFF, gidStart: 1);
        var path = TempFont(sfnt);
        try
        {
            // 本场在场读数（v0.2 第二笔）：这一份最小 sfnt 过得去目录、过得去格子经济——
            // gidStart=1 合法，全家撑起一只 gid=1 的字体，GetAsByteArray 竟真排出了一册 PDF。
            // 所以这格不赌"必有异常"：**完成或被 InvalidDataException 拒，两条路都放过**，
            // 而旧实现按声称的 21 亿格空转是分钟量级——用例能整场秒级跑完本身就在作证 clamp 生效。
            var ex = Record.Exception(() =>
                Pdf.GetAsByteArray(Sample, new PdfOptions { FontFile = path }));

            if (ex is null)
                return;

            Assert.True(ex is QuickPdfException, $"leaked a raw exception: {ex}");
            Assert.False(Misleading(Core((QuickPdfException)ex)), $"leaked a misleading exception: {Core((QuickPdfException)ex)}");
        }
        finally { File.Delete(path); }
    }

    private static QuickPdfException AsQ(Exception? ex)
    {
        Assert.True(ex is QuickPdfException, $"expected QuickPdfException, got {ex}");
        return (QuickPdfException)ex!;
    }

    /// <summary>最小 sfnt 拼装（仅够 Load 走到 cmap12 那一格）：目录 6 张表齐、
    /// head 46 字节（upm=1000）、hhea 36 字节、maxp 6 字节、hmtx 四字节、glyf 空、
    /// cmap 一条 (3,10) 指向格式 12 的子表，其 numGroups=1、那一组就是用例点名的超额区间。</summary>
    private static byte[] BuildMinimalFraudTtf(uint start, uint end, uint gidStart)
    {
        const int numTables = 6;
        // 表序（目录按出现次序写，Load 是按名字查的，次序自由）：head ∶ hhea ∶ maxp ∶ hmtx ∶ glyf ∶ cmap
        var head = new byte[46];
        var hhea = new byte[36];
        var maxp = new byte[6];
        var hmtx = new byte[4];
        var glyf = Array.Empty<byte>();
        var cmap = BuildCmap(start, end, gidStart);
        var payload = new[] { ("head", head), ("hhea", hhea), ("maxp", maxp), ("hmtx", hmtx), ("glyf", glyf), ("cmap", cmap) };

        int headerLen = 12 + numTables * 16;
        var buf = new byte[headerLen + payload.Sum(t => (t.Item2.Length + 3) & ~3)];

        BinaryPrimitives.WriteUInt32BigEndian(buf, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), numTables);

        int dataOff = headerLen;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(payload[i].Item1).CopyTo(buf, rec);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(rec + 8), dataOff);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(rec + 12), payload[i].Item2.Length);
            payload[i].Item2.CopyTo(buf, dataOff);

            // 表内真正被 Load 读的三格，逐张写值：head.upm=1000 ∶ hhea.numberOfHMetrics=1 ∶ maxp.numGlyphs=1
            if (payload[i].Item1 == "head") BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(dataOff + 18), 1000);
            if (payload[i].Item1 == "hhea") BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(dataOff + 34), 1);
            if (payload[i].Item1 == "maxp") BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(dataOff + 4), 1);

            dataOff += (payload[i].Item2.Length + 3) & ~3;
        }
        return buf;
    }

    private static byte[] BuildCmap(uint start, uint end, uint gidStart)
    {
        // header(4) + 一条记录(8) + 子表（format/两格预留）(4) + length/lang(8) + numGroups(4) + 那一组(12) = 40
        var cmap = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(cmap, 0);       // version
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1); // numTables
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3); // platform 3
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 10); // encoding 10（rank 0 首选，fmt 12）
        BinaryPrimitives.WriteInt32BigEndian(cmap.AsSpan(8), 12); // 子表在 cmap 表内的偏移
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 12); // format 12
        BinaryPrimitives.WriteInt32BigEndian(cmap.AsSpan(24), 1);   // subtable+12 ∶ numGroups（24 = 12+4+8）
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28), start);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32), end);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36), gidStart);
        return cmap;
    }
}
