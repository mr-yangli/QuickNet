using System.IO.Compression;
using FastExcel.Internal.OpenXml;
using FastExcel.Tests.Models;

namespace FastExcel.Tests;

public class TemplateTests : IDisposable
{
    private static readonly byte[] Red = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAgAAAAGCAIAAABxZ0isAAAAEklEQVR4nGP8z4AdMOEQp4sEAJiFAQtsMIKbAAAAAElFTkSuQmCC");
    private static readonly byte[] Blue = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAwAAAAECAIAAAA1REndAAAAFElEQVR4nGNkYPjPQAgwEVRBVUUAk1MBB0ZRoqAAAAAASUVORK5CYII=");

    private readonly string _dir;
    public TemplateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fastexcel_tpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    private string TempPath(string n) => Path.Combine(_dir, n);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // A template written by FastExcel itself: placeholder strings are just cell values.
    private string MakeBasicTemplate()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "报告：{{Title}}", ["B"] = "生成于 {{When}}", ["C"] = "" },
            new Dictionary<string, object?> { ["A"] = "工号", ["B"] = "姓名", ["C"] = "薪资" },
            new Dictionary<string, object?> { ["A"] = "{{items.Id}}", ["B"] = "{{items.Name}}", ["C"] = "{{items.Salary}}" },
        };
        var p = TempPath("template.xlsx");
        Excel.SaveAs(p, rows, new ExportOptions { PrintHeader = false });
        return p;
    }

    public sealed class Item { public int Id { get; set; } public string Name { get; set; } = ""; public decimal Salary { get; set; } }

    [Fact]
    public void Scalar_And_Collection_Expansion()
    {
        var tpl = MakeBasicTemplate();
        var outp = TempPath("report.xlsx");

        var value = new Dictionary<string, object?>
        {
            ["Title"] = "季度报告",
            ["When"] = new DateTime(2026, 9, 28, 10, 30, 0),
            ["items"] = new List<Item>
            {
                new() { Id = 1, Name = "张三", Salary = 1000.5m },
                new() { Id = 2, Name = "李四", Salary = 2000m },
                new() { Id = 3, Name = "王五", Salary = 3000.25m },
            },
        };

        Excel.SaveAsByTemplate(outp, tpl, value);

        var all = Excel.Query(outp, useHeaderRow: false).ToList();
        // rows: 1=title, 2=header, 3..5=expanded items
        Assert.Equal(5, all.Count);
        // rows: 1=title, 2=header, 3..5=items
        Assert.Equal("报告：季度报告", all[0]["A"]);
        Assert.Equal("工号", all[1]["A"]);
        Assert.Equal("张三", all[2]["B"]);
        Assert.Equal(2, Convert.ToInt32(all[3]["A"]));
        Assert.Equal(3000.25m, Convert.ToDecimal(all[4]["C"]));
    }

    [Fact]
    public void Empty_Collection_Drops_Expansion_Rows()
    {
        var tpl = MakeBasicTemplate();
        var outp = TempPath("empty.xlsx");
        var value = new Dictionary<string, object?> { ["Title"] = "无", ["items"] = new List<Item>() };
        Excel.SaveAsByTemplate(outp, tpl, value);
        var all = Excel.Query(outp, useHeaderRow: false).ToList();
        Assert.Equal(2, all.Count); // title + header only
    }

    [Fact]
    public void POCO_Root_Works()
    {
        var tpl = MakeBasicTemplate();
        var outp = TempPath("poco.xlsx");
        var value = new Report
        {
            Title = "POCO 报告",
            items = new List<Item> { new() { Id = 7, Name = "赵六", Salary = 7m } }
        };
        Excel.SaveAsByTemplate(outp, tpl, value);
        var all = Excel.Query(outp, useHeaderRow: false).ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal("赵六", all[2]["B"]);
    }

    public class Report
    {
        public string Title { get; set; } = "";
        public List<Item> items { get; set; } = new();
    }

    [Fact]
    public void ImagePlaceholder_Anchor_And_Media()
    {
        var tplRows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "{{@logo}}", ["B"] = "公司徽标" },
            new Dictionary<string, object?> { ["A"] = "{{items.Name}}", ["B"] = "{{@items.Photo}}" },
        };
        var tpl = TempPath("img_template.xlsx");
        Excel.SaveAs(tpl, tplRows, new ExportOptions { PrintHeader = false });

        var outp = TempPath("img_out.xlsx");
        var value = new Dictionary<string, object?>
        {
            ["logo"] = new ExcelImage(Red),
            ["items"] = new List<PhotoRow>
            {
                new() { Name = "红图", Photo = new ExcelImage(Red) },   // same bytes as logo -> dedup
                new() { Name = "蓝图", Photo = new ExcelImage(Blue) },
            },
        };
        Excel.SaveAsByTemplate(outp, tpl, value);

        using (var zip = ZipFile.OpenRead(outp))
        {
            var media = zip.Entries.Where(e => e.FullName.StartsWith("xl/media/")).ToList();
            Assert.Equal(2, media.Count); // Red shared by logo+row1, Blue separate
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("xl/drawings/drawing"));
            Assert.NotNull(zip.GetEntry("xl/styles.xml"));
        }

        // read anchors back through the package reader
        using var fs = File.OpenRead(outp);
        using var pkg = XlsxPackageReader.Open(fs);
        var images = pkg.ReadImagesFor(new ImportOptions());
        Assert.Equal(3, images.Count);
        Assert.True(images.ContainsKey((0, 0))); // logo at A1 (zero-based)
        Assert.Equal(Red.Length, images[(0, 0)].Data.Length);
        Assert.Equal(Red.Length, images[(1, 1)].Data.Length);  // 红图 shares the logo's media part (dedup)
        Assert.Equal(Blue.Length, images[(2, 1)].Data.Length); // 蓝图 on the second expanded row
    }

    public class PhotoRow { public string Name { get; set; } = ""; public ExcelImage Photo { get; set; } = null!; }

    [Fact]
    public void ImportByTemplate_Uses_Template_Header_Layout()
    {
        // template: title row on 1, headers on 2 (HeaderRowIndex = 1)
        var tplRows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "员工表（模板）", ["B"] = "", ["C"] = "", ["D"] = "", ["E"] = "", ["F"] = "", ["G"] = "" },
            new Dictionary<string, object?> { ["A"] = "工号", ["B"] = "姓名", ["C"] = "部门", ["D"] = "薪资", ["E"] = "入职日期", ["F"] = "在职", ["G"] = "职级" },
        };
        var tpl = TempPath("import_template.xlsx");
        Excel.SaveAs(tpl, tplRows, new ExportOptions { PrintHeader = false });

        // data file follows the same layout
        var dataRows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "员工表（数据）", ["B"] = "", ["C"] = "", ["D"] = "", ["E"] = "", ["F"] = "", ["G"] = "" },
            new Dictionary<string, object?> { ["A"] = "工号", ["B"] = "姓名", ["C"] = "部门", ["D"] = "薪资", ["E"] = "入职日期", ["F"] = "在职", ["G"] = "职级" },
            new Dictionary<string, object?> { ["A"] = 1, ["B"] = "张三", ["C"] = "研发", ["D"] = 8000, ["E"] = new DateTime(2021, 5, 1), ["F"] = true, ["G"] = "Mid" },
            new Dictionary<string, object?> { ["A"] = 2, ["B"] = "李四", ["C"] = "市场", ["D"] = 9000, ["E"] = new DateTime(2022, 6, 1), ["F"] = false, ["G"] = "Senior" },
        };
        var data = TempPath("import_data.xlsx");
        Excel.SaveAs(data, dataRows, new ExportOptions { PrintHeader = false });

        var result = Excel.ImportByTemplate<Employee>(data, tpl, new ImportOptions { HeaderRowIndex = 1 });
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Data.Count);
        Assert.Equal("张三", result.Data[0].Name);
        Assert.Equal(9000m, result.Data[1].Salary);
    }

    [Fact]
    public void Template_Keeps_NonPlaceholder_Cells_And_Other_Sheets()
    {
        // two-sheet template; second sheet untouched content
        var sheet1 = new ExcelSheet("Sheet1", new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "{{Name}}", ["B"] = "固定文本" },
        });
        var sheet2 = new ExcelSheet("Sheet2", new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["A"] = "参考数据" },
        });
        var tpl = TempPath("multi.xlsx");
        Excel.SaveAs(tpl, new List<ExcelSheet> { sheet1, sheet2 }, new ExportOptions { PrintHeader = false });

        var outp = TempPath("multi_out.xlsx");
        Excel.SaveAsByTemplate(outp, tpl, new Dictionary<string, object?> { ["Name"] = "替换值" },
            new TemplateOptions { SheetName = "Sheet1" });

        Assert.Equal(new[] { "Sheet1", "Sheet2" }, Excel.GetSheetNames(outp).ToArray());
        var rows = Excel.Query(outp, useHeaderRow: false).ToList();
        Assert.Equal("替换值", rows[0]["A"]);
        Assert.Equal("固定文本", rows[0]["B"]);
    }
}
