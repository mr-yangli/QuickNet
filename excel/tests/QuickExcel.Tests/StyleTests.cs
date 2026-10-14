using System.IO.Compression;
using System.Text.RegularExpressions;

namespace QuickExcel.Tests;

/// <summary>
/// Column-level styling (Bold + Align) must land in xl/styles.xml as real xf entries and be
/// referenced by the matching cells in the sheet — header and body alike.
/// </summary>
public class StyleTests
{
    private sealed class Styled
    {
        [ExcelColumn(Bold = true)]
        public string? Name { get; set; }

        public decimal Amount { get; set; }

        [ExcelColumn(Align = ExcelAlign.Center)]
        public string? Code { get; set; }
    }

    private static (string Styles, string Sheet) ReadParts(object rows, ExportOptions options)
    {
        var bytes = Excel.GetAsByteArray(rows, options);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        string Read(string name)
        {
            using var s = zip.GetEntry(name)!.Open();
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        return (Read("xl/styles.xml"), Read("xl/worksheets/sheet1.xml"));
    }

    private static string[] CellXfs(string styles)
    {
        var m = Regex.Match(styles, "<cellXfs[^>]*>(.*?)</cellXfs>", RegexOptions.Singleline);
        return Regex.Matches(m.Groups[1].Value, "<xf\\b.*?(?:/>|</xf>)", RegexOptions.Singleline)
                    .Select(x => x.Value).ToArray();
    }

    private static Dictionary<string, int> CellStyles(string sheet)
    {
        var map = new Dictionary<string, int>();
        foreach (Match c in Regex.Matches(sheet, "<c r=\"([A-Z]+[0-9]+)\"([^>]*)>"))
        {
            var sm = Regex.Match(c.Groups[2].Value, "s=\"(\\d+)\"");
            map[c.Groups[1].Value] = sm.Success ? int.Parse(sm.Groups[1].Value) : 0;
        }
        return map;
    }

    [Fact]
    public void Bold_And_Center_Columns_Produce_Matching_Xfs_And_Cell_References()
    {
        var (styles, sheet) = ReadParts(
            new[] { new Styled { Name = "张三", Amount = 10m, Code = "X" } },
            new ExportOptions());

        var xfs = CellXfs(styles);
        var cells = CellStyles(sheet);

        // Column A (Name) is bold -> its body cell points at an applyFont xf.
        Assert.Contains("applyFont=\"1\"", xfs[cells["A2"]]);
        Assert.Contains("fontId=\"1\"", xfs[cells["A2"]]);

        // Column C (Code) is centered -> its body cell points at a horizontal=center xf.
        Assert.Contains("horizontal=\"center\"", xfs[cells["C2"]]);

        // Column B is neither -> a plain General xf with no font/alignment overrides.
        Assert.DoesNotContain("applyFont", xfs[cells["B2"]]);
        Assert.DoesNotContain("applyAlignment", xfs[cells["B2"]]);
    }

    [Fact]
    public void Header_Row_Follows_Column_Align_And_Independent_Bold()
    {
        var (styles, sheet) = ReadParts(
            new[] { new Styled { Name = "张三", Amount = 10m, Code = "X" } },
            new ExportOptions { BoldHeader = true });

        var xfs = CellXfs(styles);
        var cells = CellStyles(sheet);

        // Center column's header keeps the alignment (header and body share the column's Align).
        Assert.Contains("horizontal=\"center\"", xfs[cells["C1"]]);
        // BoldHeader bolds the whole header row, including the otherwise-plain B1.
        Assert.Contains("applyFont=\"1\"", xfs[cells["B1"]]);
    }

    [Fact]
    public void Right_Align_Emits_Horizontal_Right()
    {
        var (styles, _) = ReadParts(
            new[] { new Styled { Name = "张三", Amount = 10m, Code = "X" } },
            new ExportOptions());

        Assert.DoesNotContain("horizontal=\"right\"", styles); // no column asks for right
        Assert.Contains("horizontal=\"center\"", styles);
    }
}
