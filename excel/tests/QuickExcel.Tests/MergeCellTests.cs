using System.IO.Compression;
using QuickExcel.Internal.OpenXml;

namespace QuickExcel.Tests;

/// <summary>
/// OOXML stores a merged block's value only in its top-left cell; QuickExcel rehydrates it into the
/// rest of the block while streaming (ImportOptions.FillMergedCells, on by default).
/// The fixture is a hand-built xlsx so merge geometry is exact.
/// </summary>
public class MergeCellTests : IDisposable
{
    private readonly string _dir;
    public MergeCellTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "quickexcel_merge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string BuildFile()
    {
        // A2:A4 vertical block ("研发部"), B5:A5 horizontal block (reversed corners, must normalise).
        return BuildFileWithSheet("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="inlineStr"><is><t>部门</t></is></c><c r="B1" t="inlineStr"><is><t>员工</t></is></c><c r="C1" t="inlineStr"><is><t>薪资</t></is></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>研发部</t></is></c><c r="B2" t="inlineStr"><is><t>张三</t></is></c><c r="C2"><v>100</v></c></row>
                <row r="3"><c r="B3" t="inlineStr"><is><t>李四</t></is></c><c r="C3"><v>200</v></c></row>
                <row r="4"><c r="B4" t="inlineStr"><is><t>王五</t></is></c><c r="C4"><v>300</v></c></row>
                <row r="5"><c r="A5" t="inlineStr"><is><t>实习</t></is></c><c r="C5"><v>50</v></c></row>
              </sheetData>
              <mergeCells count="2">
                <mergeCell ref="A2:A4"/>
                <mergeCell ref="B5:A5"/>
              </mergeCells>
            </worksheet>
            """);
    }

    private string BuildFileWithSheet(string sheetXml, string name = "merge.xlsx")
    {
        var p = Path.Combine(_dir, name);
        using (var fs = File.Create(p))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void Add(string ename, string content)
            {
                var e = zip.CreateEntry(ename);
                using var w = new StreamWriter(e.Open(), new System.Text.UTF8Encoding(false));
                w.Write(content);
            }

            Add("[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            Add("_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add("xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", sheetXml);
        }
        return p;
    }

    public class MergeRow
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string? 部门 { get; set; }
        public string? 员工 { get; set; }
        public decimal 薪资 { get; set; }
    }

    [Fact]
    public void Dynamic_Query_Fills_Vertical_And_Horizontal_Merges()
    {
        var rows = Excel.Query(BuildFile()).ToList();
        Assert.Equal(4, rows.Count);

        Assert.Equal("研发部", rows[0]["部门"]); // top-left row keeps its own value
        Assert.Equal("研发部", rows[1]["部门"]); // A3 <- A2
        Assert.Equal("研发部", rows[2]["部门"]); // A4 <- A2
        Assert.Equal("实习", rows[3]["员工"]);   // B5 <- A5 (reversed corners normalised)
        Assert.Equal(200m, Convert.ToDecimal(rows[1]["薪资"]));
    }

    [Fact]
    public void Typed_Import_Fills_Before_Validation()
    {
        // 部门 is [Required]: without the fill, rows 3/4 would fail validation.
        var result = Excel.Import<MergeRow>(BuildFile());
        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Data.Count);
        Assert.All(result.Data, r => Assert.NotNull(r.部门));
        Assert.Equal(new[] { "研发部", "研发部", "研发部", "实习" }, result.Data.Select(r => r.部门));
    }

    [Fact]
    public void FillMergedCells_Off_Leaves_Non_TopLeft_Cells_Empty()
    {
        var rows = Excel.Query(BuildFile(), true, new ImportOptions { FillMergedCells = false }).ToList();
        Assert.Equal("研发部", rows[0]["部门"]);
        Assert.Null(rows[1]["部门"]); // physically absent cell -> null, not the merged value
        Assert.Null(rows[3]["员工"]);
    }

    [Fact]
    public void Roundtrip_Exported_File_Has_No_Merges_And_Is_Unaffected()
    {
        // Our exporter never writes mergeCells; default-on scanning must be a harmless no-op.
        var p = Path.Combine(_dir, "plain.xlsx");
        Excel.SaveAs(p, new[] { new MergeRow { 部门 = "A", 员工 = "x", 薪资 = 1 } });
        var rows = Excel.Query<MergeRow>(p).ToList();
        Assert.Single(rows);
        Assert.Equal("A", rows[0].部门);
    }

    [Fact]
    public void Large_Sheet_Merges_Far_Past_Chunk_Boundaries_Are_Found()
    {
        // 20k rows push <mergeCells> many scanner chunks deep. Rows 6-8 / 19001-19002 are physically
        // absent cells (empty <row/> elements) that only materialise through fill.
        var sheet = new System.Text.StringBuilder();
        sheet.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
            """);
        sheet.Append("""<row r="1"><c r="A1" t="inlineStr"><is><t>号</t></is></c></row>""");
        for (int r = 2; r <= 20_001; r++)
            sheet.Append(r is 6 or 7 or 8 or 19_001 or 19_002
                ? $"<row r=\"{r}\"/>"
                : $"<row r=\"{r}\"><c r=\"A{r}\"><v>{r}</v></c></row>");
        sheet.Append("""
              </sheetData>
              <mergeCells count="2">
                <mergeCell ref="A5:A8"/>
                <mergeCell ref="A19000:A19002"/>
              </mergeCells>
            </worksheet>
            """);

        var rows = Excel.Query(BuildFileWithSheet(sheet.ToString(), "big_merge.xlsx")).ToList();
        Assert.Equal(20_000, rows.Count);
        Assert.Equal(5d, rows[3]["号"]);      // row 5, top-left
        Assert.Equal(5d, rows[4]["号"]);      // row 6 <- fill
        Assert.Equal(5d, rows[6]["号"]);      // row 8 <- fill
        Assert.Equal(9d, rows[7]["号"]);      // row 9 untouched
        Assert.Equal(19_000d, rows[18_998]["号"]); // row 19000, top-left
        Assert.Equal(19_000d, rows[19_000]["号"]); // row 19002 <- fill
        Assert.Equal(19_003d, rows[19_001]["号"]); // row 19003 untouched
    }

    [Fact]
    public void Ref_Parsing_Edge_Cases()
    {
        Assert.True(MergeCellScanner.TryParseRef("$A$2:$A$4", out var r));
        Assert.Equal((2, 0, 4, 0), ((int)r.Row0, r.Col0, r.Row1, r.Col1));

        Assert.True(MergeCellScanner.TryParseRef("A:A", out var fullCol)); // full-column: rows 1..max
        Assert.Equal((1, 0, 1_048_576, 0), ((int)fullCol.Row0, fullCol.Col0, fullCol.Row1, fullCol.Col1));

        Assert.True(MergeCellScanner.TryParseRef("c5:b5", out var swapped));
        Assert.Equal((5, 1, 5, 2), ((int)swapped.Row0, swapped.Col0, swapped.Row1, swapped.Col1));

        Assert.False(MergeCellScanner.TryParseRef("A1", out _));      // single cell
        Assert.False(MergeCellScanner.TryParseRef("A1:ZZZ9", out _)); // horizontal blow-up guard
        Assert.False(MergeCellScanner.TryParseRef("hello", out _));
        Assert.False(MergeCellScanner.TryParseRef(null, out _));
    }
}
