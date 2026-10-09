using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace QuickWord.Tests;

public class TemplateTests : IDisposable
{
    // 8x6 red PNG + 12x4 blue PNG (same fixtures QuickExcel's image tests use)
    private static readonly byte[] Red =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAIAAABxZ0isAAAAEklEQVR4nGP8z4AdMOEQp4sEAJiFAQtsMIKbAAAAAElFTkSuQmCC");
    private static readonly byte[] Blue =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAwAAAAECAIAAAA1REndAAAAFElEQVR4nGNkYPjPQAgwEVRBVUUAk1MBB0ZRoqAAAAAASUVORK5CYII=");

    private const string Wns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string DocumentEntry = "word/document.xml";
    private static readonly XNamespace W = Wns;

    private readonly string _dir;
    public TemplateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quickword_tpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    private string TempPath(string n = "filled.docx") => Path.Combine(_dir, n);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Tags_Split_Across_Runs_Are_Repaired_And_Unknown_Tags_Stay_Visible()
    {
        var template = Package(new Dictionary<string, string>
        {
            [DocumentEntry] = Document("""
                <w:p><w:r><w:t>检查时间: </w:t></w:r><w:r><w:t>{{</w:t></w:r><w:proofErr w:type="spellStart"/><w:r><w:t>DateTime</w:t></w:r><w:r><w:t>}}</w:t></w:r></w:p>
                <w:p><w:r><w:t>当事人{{Party1}}签字</w:t></w:r></w:p>
                <w:p><w:r><w:t>前{{A}}</w:t></w:r><w:r><w:t>后{{A}}</w:t></w:r></w:p>
                <w:p><w:r><w:t>未知{{Unknown}}</w:t></w:r></w:p>
                """),
        });

        var values = new Dictionary<string, object>
        {
            ["DateTime"] = new DateTime(2026, 10, 7, 10, 30, 0),
            ["Party1"] = "张三",
            ["A"] = 12.5m,
        };

        var output = new MemoryStream();
        Word.SaveAsByTemplate(output, new MemoryStream(template), values);

        var doc = Word.Read(new MemoryStream(output.ToArray()));
        var texts = doc.Paragraphs.Select(p => p.Text).ToList();
        Assert.Equal(4, texts.Count);
        Assert.Equal("检查时间: 2026-10-07 10:30:00", texts[0]); // default DateTimeFormat, split tag stitched
        Assert.Equal("当事人张三签字", texts[1]);
        Assert.Equal("前12.5后12.5", texts[2]);                 // two tags in one part, both filled
        Assert.Equal("未知{{Unknown}}", texts[3]);               // unknown key is not silently deleted
    }

    [Fact]
    public void POCO_Values_Fill_Body_And_Preserve_Untouched_Parts()
    {
        var parts = new Dictionary<string, string>
        {
            [DocumentEntry] = Document("<w:p><w:r><w:t>{{Name}}／{{Count}}</w:t></w:r></w:p>"),
            ["word/styles.xml"] = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
                                + """<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">"""
                                + """<w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val="21"/></w:rPr></w:rPrDefault></w:docDefaults></w:styles>""",
        };
        byte[] stylesBefore = Encoding.UTF8.GetBytes(parts["word/styles.xml"]);

        using var pkg = new MemoryStream(Package(parts));
        using var filled = new MemoryStream();
        Word.SaveAsByTemplate(filled, pkg, new { Name = "李四", Count = 3 });

        var text = Word.Read(new MemoryStream(filled.ToArray())).Paragraphs.Single().Text;
        Assert.Equal("李四／3", text);

        using var zip = new ZipArchive(new MemoryStream(filled.ToArray()));
        using var sr = new StreamReader(zip.GetEntry("word/styles.xml")!.Open());
        Assert.Equal(Encoding.UTF8.GetString(stylesBefore), sr.ReadToEnd());
    }

    [Fact]
    public void ImagePlaceholder_Becomes_Inline_Drawing_In_The_Footer()
    {
        var parts = new Dictionary<string, string>
        {
            [DocumentEntry] = Document("""
                <w:p><w:r><w:t>正文</w:t></w:r></w:p><w:sectPr><w:footerReference w:type="even" r:id="rId9"/></w:sectPr>
                """),
            ["word/footer2.xml"] = Footer("""
                <w:p><w:r><w:t>签 字 人： </w:t></w:r><w:r><w:rPr><w:sz w:val="30"/></w:rPr><w:t>{{Party1Sign</w:t></w:r><w:r><w:t>}}</w:t></w:r><w:r><w:t>　{{Date}}</w:t></w:r></w:p>
                """),
            ["word/_rels/document.xml.rels"] = Relationships(
                """<Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer2.xml"/>"""),
            ["[Content_Types].xml"] = ContentTypes(footerPart: true),
        };

        var values = new Dictionary<string, object>
        {
            ["Party1Sign"] = new ImagePlaceholder(Red) { Width = 70, Height = 30 },
            ["Date"] = "2026年10月7日",
        };

        using var filled = new MemoryStream();
        Word.SaveAsByTemplate(filled, new MemoryStream(Package(parts)), values);
        byte[] bytes = filled.ToArray();

        using var zip = new ZipArchive(new MemoryStream(bytes));

        // media part carries the original bytes, and the footer gains a relationship pointing at it
        var media = Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal));
        Assert.Equal("word/media/image1.png", media.FullName);
        using (var ms = new MemoryStream())
        {
            media.Open().CopyTo(ms);
            Assert.True(Red.AsSpan().SequenceEqual(ms.ToArray().AsSpan()));
        }

        var footerRels = XDocument.Load(zip.GetEntry("word/_rels/footer2.xml.rels")!.Open());
        var rel = Assert.Single(footerRels.Root!.Elements());
        Assert.Equal("rId1", (string?)rel.Attribute("Id"));
        Assert.Equal("media/image1.png", (string?)rel.Attribute("Target"));
        Assert.EndsWith("/image", (string?)rel.Attribute("Type"));

        // [Content_Types].xml declares png, which the template never needed before
        var ct = XDocument.Load(zip.GetEntry("[Content_Types].xml")!.Open());
        Assert.Contains(ct.Root!.Elements(), e =>
            (string?)e.Attribute("Extension") == "png" &&
            (string?)e.Attribute("ContentType") == "image/png");

        // the tag is gone, a drawing took its place with the requested pixel size, and the sibling text survived
        var footer = XDocument.Load(zip.GetEntry("word/footer2.xml")!.Open());
        string footerXml = footer.ToString();
        Assert.DoesNotContain("{{", footerXml, StringComparison.Ordinal);
        Assert.Contains("2026年10月7日", footerXml, StringComparison.Ordinal);

        var drawing = Assert.Single(footer.Descendants(XNamespace.Get(Wns) + "drawing"));
        var inline = drawing.Element(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing") + "inline")!;
        var extent = inline.Element(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing") + "extent")!;
        Assert.Equal(70 * 9525L, (long?)extent.Attribute("cx"));
        Assert.Equal(30 * 9525L, (long?)extent.Attribute("cy"));
        Assert.Equal("rId1", (string?)inline
            .Descendants(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/main") + "blip")
            .Single().Attribute(XNamespace.Get(Rns) + "embed"));

        // the run the tag lived in keeps its formatting (WordprocessingML attributes are namespace-qualified)
        var imageRun = drawing.Parent!;
        Assert.Equal("30", (string?)imageRun.Element(W + "rPr")?.Element(W + "sz")?.Attribute(W + "val"));
    }

    [Fact]
    public void Existing_Media_And_Relationships_Are_Numbered_Around()
    {
        var parts = new Dictionary<string, string>
        {
            [DocumentEntry] = Document("""
                <w:p><w:r><w:t>{{One}}{{Two}}</w:t></w:r></w:p>
                <w:p><w:r><w:drawing><wp:inline xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"><wp:docPr id="7" name="Existing"/></wp:inline></w:drawing></w:r></w:p>
                """),
            ["word/_rels/document.xml.rels"] = Relationships(
                """<Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>"""),
            ["[Content_Types].xml"] = ContentTypes(footerPart: false),
        };

        using var filled = new MemoryStream();
        Word.SaveAsByTemplate(filled, new MemoryStream(Package(parts, extraMedia: Blue)), new Dictionary<string, object>
        {
            ["One"] = new ImagePlaceholder(Red),
            ["Two"] = new ImagePlaceholder(Red), // same bytes as One -> one media part, shared
        });

        using var zip = new ZipArchive(new MemoryStream(filled.ToArray()));
        var media = zip.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal))
                              .Select(e => e.FullName).OrderBy(n => n).ToList();
        // image1.jpeg came from the template, so new media starts at 2 — and identical bytes are stored once
        Assert.Equal(new[] { "word/media/image1.jpeg", "word/media/image2.png" }, media);

        var rels = XDocument.Load(zip.GetEntry("word/_rels/document.xml.rels")!.Open());
        var ids = rels.Root!.Elements().Select(e => (string?)e.Attribute("Id")).ToList();
        Assert.Contains("rId3", ids);   // existing relationship preserved
        Assert.Equal(new[] { "rId3", "rId4", "rId5" }, ids); // rId4/rId5 both target the shared image2.png

        var doc = XDocument.Load(zip.GetEntry(DocumentEntry)!.Open());
        var embeds = doc.Descendants(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/main") + "blip")
                        .Select(b => (string?)b.Attribute(XNamespace.Get(Rns) + "embed")).ToList();
        Assert.Equal(new[] { "rId4", "rId5" }, embeds);

        // docPr ids continue past the template's existing id=7 instead of colliding with it
        var docPrIds = doc.Descendants(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing") + "docPr")
                          .Select(e => (string?)e.Attribute("id")).ToList();
        Assert.Equal(3, docPrIds.Count);
        Assert.Contains("7", docPrIds);
        Assert.Equal(3, docPrIds.Distinct().Count());
    }

    [Fact]
    public void Native_Size_Is_Used_When_The_Placeholder_States_None()
    {
        var template = Package(new Dictionary<string, string>
        {
            [DocumentEntry] = Document("<w:p><w:r><w:t>{{Pic}}</w:t></w:r></w:p>"),
        });

        using var filled = new MemoryStream();
        Word.SaveAsByTemplate(filled, new MemoryStream(template),
            new Dictionary<string, object> { ["Pic"] = new ImagePlaceholder(Blue) }); // 12x4

        using var zip = new ZipArchive(new MemoryStream(filled.ToArray()));
        var doc = XDocument.Load(zip.GetEntry(DocumentEntry)!.Open());
        var extent = doc.Descendants(XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing") + "extent").Single();
        Assert.Equal(12 * 9525L, (long?)extent.Attribute("cx"));
        Assert.Equal(4 * 9525L, (long?)extent.Attribute("cy"));
    }

    [Fact]
    public void Path_Overloads_Round_Trip_Through_Disk()
    {
        string templatePath = TempPath("tpl.docx");
        File.WriteAllBytes(templatePath, Package(new Dictionary<string, string>
        {
            [DocumentEntry] = Document("<w:p><w:r><w:t>编号：{{Code}}</w:t></w:r></w:p>"),
        }));

        string outPath = TempPath("out.docx");
        Word.SaveAsByTemplate(outPath, templatePath, new Dictionary<string, object> { ["Code"] = "FX-001" });
        Assert.Equal("编号：FX-001", Word.Read(outPath).Paragraphs.Single().Text);

        string outStreamPath = TempPath("out2.docx");
        using (var fs = File.Create(outStreamPath))
            Word.SaveAsByTemplate(fs, templatePath, new { Code = "FX-002" });
        Assert.Equal("编号：FX-002", Word.Read(outStreamPath).Paragraphs.Single().Text);

        Assert.Equal("编号：FX-003", Word.Read(new MemoryStream(
            Word.GetAsByteArrayByTemplate(templatePath, new { Code = "FX-003" }))).Paragraphs.Single().Text);
    }

    // ---- fixtures ----

    private static string Document(string body) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
        + $"""<w:document xmlns:w="{Wns}" xmlns:r="{Rns}"><w:body>"""
        + body + "</w:body></w:document>";

    private static string Footer(string body) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
        + """<w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" """
        + """xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">"""
        + body + "</w:ftr>";

    private static string Relationships(string inner) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
        + """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">"""
        + inner + "</Relationships>";

    private static string ContentTypes(bool footerPart) =>
        """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
        + """<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">"""
        + """<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>"""
        + """<Default Extension="xml" ContentType="application/xml"/>"""
        + """<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>"""
        + (footerPart
            ? """<Override PartName="/word/footer2.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>"""
            : string.Empty)
        + "</Types>";

    private static byte[] Package(Dictionary<string, string> parts, byte[]? extraMedia = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (name, content) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
            if (extraMedia is not null)
            {
                var entry = zip.CreateEntry("word/media/image1.jpeg");
                using var es = entry.Open();
                es.Write(extraMedia);
            }
        }
        return ms.ToArray();
    }
}
