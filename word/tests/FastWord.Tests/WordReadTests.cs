using System.IO.Compression;
using System.Text;
using FastWord;

namespace FastWord.Tests;

public class WordReadTests
{
    private const string DocumentEntry = "word/document.xml";

    private record Emp(string Dept, string Name, decimal Salary);

    private static readonly Emp[] Sample =
    [
        new("研发部", "张三", 12345.60m),
        new("研发部", "李四&B<C>", 9800m),
        new("市场部", "王五\n第二行", 7654.32m),
    ];

    [Fact]
    public void Read_RoundTrips_The_Writers_Table_Header_And_Cells()
    {
        var doc = ReadFrom(Word.GetAsByteArray(Sample));

        var table = Assert.Single(doc.Tables);
        Assert.Equal(4, table.Rows.Count); // header + 3 data
        Assert.Equal(new[] { "Dept", "Name", "Salary" }, table.Rows[0]);
        Assert.Equal("研发部", table.Rows[1][0]);
        Assert.Equal("李四&B<C>", table.Rows[2][1]);   // XML escaping comes back decoded
        Assert.Equal("王五\n第二行", table.Rows[3][1]); // multi-paragraph cell rejoins as \n
        Assert.Equal("7654.32", table.Rows[3][2]);
        Assert.All(table.Rows, r => Assert.Equal(3, r.Count));
    }

    [Fact]
    public void Read_Query_Returns_FastExcel_Shaped_Dictionaries()
    {
        var doc = ReadFrom(Word.GetAsByteArray(Sample));
        var table = doc.Tables.Single();

        var rows = table.Query().ToList();
        Assert.Equal(Sample.Length, rows.Count);
        Assert.Equal(new[] { "Dept", "Name", "Salary" }, rows[0].Keys.ToList());
        Assert.Equal("王五\n第二行", rows[2]["Name"]);

        var positional = table.Query(useHeaderRow: false).ToList();
        Assert.Equal(Sample.Length + 1, positional.Count);
        Assert.Equal("Dept", positional[0]["Column1"]);
    }

    [Fact]
    public void Read_Keeps_Title_And_Heading_Styles_And_Drops_Separator_Paragraphs()
    {
        var sheets = new WordSheet[] { new("员工", Sample), new("字典", new[] { new { Key = "性别", Value = "男/女" } }) };
        var doc = ReadFrom(Word.GetAsByteArray(sheets, new WordOptions { Title = "季度报表" }));

        var first = doc.Blocks[0] as WordParagraph;
        Assert.Equal("季度报表", first!.Text);
        Assert.Equal("DocTitle", first.StyleName);
        Assert.False(first.IsListItem);

        // body order: title, sheet heading, table, sheet heading, table
        Assert.Equal(2, doc.Tables.Count());
        var headings = doc.Paragraphs.Where(p => p.StyleName == "Heading1").Select(p => p.Text).ToList();
        Assert.Equal(new[] { "员工", "字典" }, headings);
        Assert.All(doc.Paragraphs, p => Assert.NotEmpty(p.Text)); // the <w:p/> separators are off by default

        var withBlanks = ReadFrom(Word.GetAsByteArray(sheets), new WordReadOptions { IncludeEmptyParagraphs = true });
        Assert.Contains(withBlanks.Paragraphs, p => p.Text.Length == 0);
        Assert.True(withBlanks.Blocks.Count > doc.Blocks.Count);
    }

    [Fact]
    public void Read_Honours_Tabs_Breaks_List_Markers_And_Ignores_Field_Codes()
    {
        var doc = ReadFrom(PackageWith($"""
            <w:body>
              <w:p><w:pPr><w:pStyle w:val="ListParagraph"/><w:numPr><w:ilvl w:val="0"/></w:numPr></w:pPr>
                 <w:r><w:t>题型：</w:t><w:tab/><w:t>单选</w:t></w:r></w:p>
              <w:p><w:r><w:t>第一行</w:t><w:br/><w:t>第二行</w:t><w:instrText xml:space="preserve"> PAGE </w:instrText></w:r></w:p>
              <w:p><w:r><w:t xml:space="preserve"> </w:t></w:r></w:p>
              <w:p><w:r><w:t>重复</w:t></w:r><w:r><w:t>重复</w:t></w:r></w:p>
            </w:body>
            """));

        var list = (WordParagraph)doc.Blocks[0];
        Assert.True(list.IsListItem);
        Assert.Equal("ListParagraph", list.StyleName);
        Assert.Equal("题型：\t单选", list.Text);

        var breaks = (WordParagraph)doc.Blocks[1];
        Assert.Equal("第一行\n第二行", breaks.Text); // instrText (field code) is not visible text

        var padded = (WordParagraph)doc.Blocks[2];
        Assert.Equal(" ", padded.Text); // a preserve-space run is content, not a separator

        Assert.Equal("重复重复", ((WordParagraph)doc.Blocks[3]).Text); // runs inside one paragraph concatenate
    }

    [Fact]
    public void Read_Nested_Table_Inside_A_Cell_Is_Flattened_And_Stays_Out_Of_The_Body()
    {
        var doc = ReadFrom(PackageWith("""
            <w:body>
              <w:tbl>
                <w:tr><w:tc><w:p><w:r><w:t>题干</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>外</w:t></w:r></w:p>
                        <w:tbl><w:tr><w:tc><w:p><w:r><w:t>A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                    </w:tc></w:tr>
                <w:tr><w:tc><w:p><w:r><w:t>答案</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>C</w:t></w:r></w:p></w:tc></w:tr>
              </w:tbl>
            </w:body>
            """));

        var table = Assert.Single(doc.Tables); // the inner table never surfaces as a body table
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("题干", table.Rows[0][0]);
        Assert.Equal("外\nA\tB", table.Rows[0][1]); // cell paragraphs join by \n, nested rows by \t
        Assert.Equal("C", table.Rows[1][1]);
    }

    [Fact]
    public void Read_Query_Deduplicates_Blank_And_Repeated_Headers()
    {
        var doc = ReadFrom(PackageWith("""
            <w:body>
              <w:tbl>
                <w:tr><w:tc><w:p><w:r><w:t>名称</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>名称</w:t></w:r></w:p></w:tc><w:tc><w:p/></w:tc></w:tr>
                <w:tr><w:tc><w:p><w:r><w:t>卷A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>卷B</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>卷C</w:t></w:r></w:p></w:tc></w:tr>
              </w:tbl>
            </w:body>
            """));

        var row = doc.Tables.Single().Query().Single();
        Assert.Equal(new[] { "名称", "名称(2)", "Column3" }, row.Keys.ToList());
        Assert.Equal("卷B", row["名称(2)"]);
        Assert.Equal("卷C", row["Column3"]);
    }

    [Fact]
    public void Read_Rejects_Non_Docx_Bytes_With_A_Readable_Error()
    {
        var garbage = Encoding.UTF8.GetBytes("this is not a zip at all");
        var ex = Assert.Throws<InvalidDataException>(() => Word.Read(new MemoryStream(garbage)));
        Assert.Contains("docx", ex.Message);

        using var zipWithoutBody = new MemoryStream();
        using (var zip = new ZipArchive(zipWithoutBody, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            zip.CreateEntry("hello.txt").Open().Close();
        zipWithoutBody.Position = 0;
        Assert.Throws<InvalidDataException>(() => Word.Read(zipWithoutBody));
    }

    [Fact]
    public void Read_Wraps_Malformed_Package_In_The_Same_Exception_Type()
    {
        // a zip that opens but whose document part is truncated — callers should still only catch InvalidDataException
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            using var w = new StreamWriter(zip.CreateEntry(DocumentEntry).Open(), new UTF8Encoding(false));
            w.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
                  + """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p>""");
        }
        ms.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(() => Word.Read(ms));
        Assert.Contains("docx", ex.Message);
    }

    [Fact]
    public void Read_Buffers_A_NonSeekable_Stream()
    {
        var bytes = Word.GetAsByteArray(Sample);
        var doc = Word.Read(new ReadOnlyStream(new MemoryStream(bytes)));

        Assert.Equal(4, doc.Tables.Single().Rows.Count);
    }

    [Fact]
    public void SaveLines_Writes_Prose_That_Reads_Back_Line_For_Line()
    {
        var lines = new[]
        {
            "一、单选题（每题 2 分）",
            "1. 以下哪个 & 符号 < 会被转义>",
            "A. 甲",
            "B. 乙",
            "答案：A",
            "",
            "二、判断题（每题 1 分）",
        };
        var bytes = Word.GetLinesAsByteArray(lines, new WordOptions { Title = "2026 年度知识竞赛" });

        var doc = ReadFrom(bytes, new WordReadOptions { IncludeEmptyParagraphs = true });
        var texts = doc.Paragraphs.Select(p => p.Text).ToList();
        Assert.Equal(["2026 年度知识竞赛", .. lines], texts);
        Assert.Equal("DocTitle", doc.Paragraphs.First().StyleName);
        Assert.Empty(doc.Tables);

        // blank spacer lines are what the paper layout uses between sections; default read drops them
        Assert.Equal(lines.Count(l => l.Length > 0), ReadFrom(bytes).Paragraphs.Count() - 1);
    }

    private static WordDocument ReadFrom(byte[] package, WordReadOptions? options = null)        => Word.Read(new MemoryStream(package), options);

    /// <summary>A minimal .docx: only the part the reader actually opens.</summary>
    private static byte[] PackageWith(string body)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            using var w = new StreamWriter(zip.CreateEntry(DocumentEntry).Open(), new UTF8Encoding(false));
            w.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""
                  + """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">"""
                  + body + "</w:document>");
        }
        return ms.ToArray();
    }

    /// <summary>Forward-only on purpose: mimics an upload pipe where the framework cannot seek.</summary>
    private sealed class ReadOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
