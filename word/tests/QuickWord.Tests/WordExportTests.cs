using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using QuickWord;

namespace QuickWord.Tests;

public class WordExportTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private record Emp(string Dept, string Name, decimal Salary);

    private static readonly Emp[] Sample =
    [
        new("研发部", "张三", 12345.60m),
        new("研发部", "李四&B<C>", 9800m),
        new("市场部", "王五\n第二行", 7654.32m),
    ];

    [Fact]
    public void Word_Writes_HeaderPlusData_Rows_And_Preserves_Escaping_And_Newlines()
    {
        var bytes = Word.GetAsByteArray(Sample);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        var doc = XDocument.Parse(Encoding.UTF8.GetString(ReadEntry(zip, "word/document.xml")));
        var rows = doc.Descendants(W + "tr").ToList();
        Assert.Equal(Sample.Length + 1, rows.Count); // header + data
        Assert.All(rows, tr => Assert.Equal(3, tr.Elements(W + "tc").Count()));

        var texts = doc.Descendants(W + "t").Select(t => t.Value).ToList();
        Assert.Contains("研发部", texts);
        Assert.Contains("李四&B<C>", texts); // XML escaping survived round-trip through the parser

        // \n inside a cell becomes multiple paragraphs in that cell
        var cell = rows[^1].Elements(W + "tc").ElementAt(1);
        Assert.Equal(2, cell.Elements(W + "p").Count());

        Assert.Contains("word/styles.xml", zip.Entries.Select(e => e.FullName));
    }

    [Fact]
    public void Word_MultiSheet_Writes_Title_And_Heading_Per_Sheet()
    {
        var sheets = new WordSheet[]
        {
            new("员工", Sample),
            new("字典", new[] { new { Key = "性别", Value = "男/女" } }),
        };
        var bytes = Word.GetAsByteArray(sheets, new WordOptions { Title = "季度报表" });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var xml = Encoding.UTF8.GetString(ReadEntry(zip, "word/document.xml"));
        Assert.Contains("季度报表", xml);
        Assert.Contains("员工", xml);
        Assert.Contains("字典", xml);
        Assert.Contains("pStyle w:val=\"Heading1\"", xml);
    }

    [Fact]
    public void Word_Dictionary_And_DataTable_Rows_Work()
    {
        var dict = new List<Dictionary<string, object>>
        {
            new() { ["A"] = 1, ["B"] = "x" },
            new() { ["A"] = 2, ["B"] = "y" },
        };
        var bytes = Word.GetAsByteArray(dict);
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var doc = XDocument.Parse(Encoding.UTF8.GetString(ReadEntry(zip, "word/document.xml")));
        Assert.Equal(3, doc.Descendants(W + "tr").Count());
    }

    private class Staff
    {
        [WordColumn("姓名", Order = 2, Width = 12)] public string Name { get; set; } = "";
        [WordColumn("编号", Order = 1)] public int Id { get; set; }
        [WordColumn("薪资", Order = 3, Format = "#,##0.00")] public decimal Salary { get; set; }
        [WordColumnWidth(10)] public string Memo { get; set; } = "";
        [WordIgnore] public string Secret { get; set; } = "机密";
    }

    [Fact]
    public void Word_Attributes_Drive_Header_Order_Width_Format_And_Ignore()
    {
        var bytes = Word.GetAsByteArray(new[]
        {
            new Staff { Id = 7, Name = "张三", Salary = 12345.6m, Memo = "备注", Secret = "机密" },
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var xml = Encoding.UTF8.GetString(ReadEntry(zip, "word/document.xml"));
        var doc = XDocument.Parse(xml);

        // [WordColumn] renames, Order sorts; unnamed member keeps its property name as header
        var headerCells = doc.Descendants(W + "tr").First().Elements(W + "tc")
            .Select(tc => string.Concat(tc.Descendants(W + "t").Select(t => t.Value))).ToList();
        Assert.Equal(new[] { "编号", "姓名", "薪资", "Memo" }, headerCells);

        // [WordIgnore] member is absent from both header and data
        Assert.DoesNotContain("机密", xml);
        Assert.All(doc.Descendants(W + "tr"), tr => Assert.Equal(4, tr.Elements(W + "tc").Count()));

        // [WordColumn(Format=...)] reaches DisplayFormatter: decimal 12345.6 -> "12,345.60"
        Assert.Contains("12,345.60", xml);

        // widths: 12 chars -> 12*105+75 = 1335 twips; [WordColumnWidth(10)] -> 1125
        Assert.Contains($"""<w:gridCol w:w="1335"/>""", xml);
        Assert.Contains($"""<w:gridCol w:w="1125"/>""", xml);
    }

    [Fact]
    public void Word_Footer_Emits_PageFields_When_Set_And_Stays_Off_By_Default()
    {
        // default: FooterText null -> no footer part, no content-type/rel entry (structure unchanged)
        using (var zip = new ZipArchive(new MemoryStream(Word.GetAsByteArray(Sample)), ZipArchiveMode.Read))
        {
            Assert.Null(zip.GetEntry("word/footer1.xml"));
            Assert.DoesNotContain("footer1.xml", Encoding.UTF8.GetString(ReadEntry(zip, "[Content_Types].xml")));
        }

        var bytes = Word.GetAsByteArray(Sample, new WordOptions { FooterText = "第 {PAGE} 页 / 共 {NUMPAGES} 页 & 保密" });
        using var zf = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        Assert.Contains("word/footer1.xml", zf.Entries.Select(e => e.FullName));
        Assert.Contains("footer1.xml", Encoding.UTF8.GetString(ReadEntry(zf, "[Content_Types].xml")));
        Assert.Contains("footer1.xml", Encoding.UTF8.GetString(ReadEntry(zf, "word/_rels/document.xml.rels")));

        var doc = XDocument.Parse(Encoding.UTF8.GetString(ReadEntry(zf, "word/document.xml")));
        Assert.Single(doc.Descendants(W + "footerReference"));

        var ftr = XDocument.Parse(Encoding.UTF8.GetString(ReadEntry(zf, "word/footer1.xml")));
        Assert.Equal(new[] { " PAGE ", " NUMPAGES " }, ftr.Descendants(W + "instrText").Select(e => e.Value));
        var texts = ftr.Descendants(W + "t").Select(t => t.Value).ToList();
        Assert.Contains("第 ", texts);
        Assert.Contains(" 页 & 保密", texts); // '&' round-trips through the escape
        Assert.All(texts, t => Assert.DoesNotContain("{PAGE", t)); // tokens became fields, not literal braces
    }

    private static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"missing {name}");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
