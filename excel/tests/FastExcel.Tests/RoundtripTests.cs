using FastExcel.Tests.Models;
using System.IO.Compression;
using System.Text;

namespace FastExcel.Tests;

public class RoundtripTests : IDisposable
{
    private readonly string _dir;

    public RoundtripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fastexcel_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    private string TempPath(string name = "book.xlsx") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static List<Employee> Sample() => new()
    {
        new Employee { Id = 1, Name = "张三", Department = "研发", Salary = 12345.67m, HireDate = new DateTime(2021,3,15), Active = true,  Level = Level.Senior, InternalCode = "X1" },
        new Employee { Id = 2, Name = "李四", Department = "市场", Salary = 8000m,       HireDate = new DateTime(2020,1,2),  Active = false, Level = Level.Mid },
        new Employee { Id = 3, Name = "王五 & <特殊> \"字符\"", Department = null, Salary = 999.99m, HireDate = new DateTime(2023,12,31), Active = true, Level = Level.Lead, Note = "换行\n测试" },
    };

    [Fact]
    public void Export_Then_Import_Roundtrips_Values()
    {
        var path = TempPath();
        Excel.SaveAs(path, Sample());

        var result = Excel.Import<Employee>(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Equal(3, result.Data.Count);

        Assert.Equal(1, result.Data[0].Id);
        Assert.Equal("张三", result.Data[0].Name);
        Assert.Equal("研发", result.Data[0].Department);
        Assert.Equal(12345.67m, result.Data[0].Salary);
        Assert.Equal(new DateTime(2021,3,15), result.Data[0].HireDate);
        Assert.True(result.Data[0].Active);
        Assert.Equal(Level.Senior, result.Data[0].Level);

        // special characters & newline preserved
        Assert.Equal("王五 & <特殊> \"字符\"", result.Data[2].Name);
        Assert.Equal("换行\n测试", result.Data[2].Note);

        // ExcelIgnore respected
        Assert.Equal("hidden", result.Data[0].InternalCode); // default kept, not read
    }

    [Fact]
    public void Package_Contains_Expected_Parts()
    {
        var path = TempPath();
        Excel.SaveAs(path, Sample());
        using var zip = ZipFile.OpenRead(path);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("[Content_Types].xml", names);
        Assert.Contains("_rels/.rels", names);
        Assert.Contains("xl/workbook.xml", names);
        Assert.Contains("xl/worksheets/sheet1.xml", names);
        Assert.Contains("xl/styles.xml", names);
    }

    [Fact]
    public void Dynamic_Query_Returns_HeaderKeyed_Rows()
    {
        var path = TempPath();
        Excel.SaveAs(path, Sample());
        var rows = Excel.Query(path, useHeaderRow: true).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("张三", rows[0]["姓名"]);
        Assert.Equal(2, rows.Count(r => (string?)r["部门"] != "研发"));
    }

    [Fact]
    public void Query_Typed_Returns_Objects()
    {
        var path = TempPath();
        Excel.SaveAs(path, Sample());
        var rows = Excel.Query<Employee>(path).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(Level.Lead, rows[2].Level);
    }

    [Fact]
    public void Header_Aliases_Match_On_Import()
    {
        // Export with a renamed header "Dept" via a dictionary, import into Employee.Department (alias).
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["工号"] = 10, ["姓名"] = "赵六", ["Dept"] = "外包", ["薪资"] = 1m, ["入职日期"] = new DateTime(2022,1,1), ["在职"] = true, ["职级"] = "Junior" },
        };
        var path = TempPath();
        Excel.SaveAs(path, rows);
        var result = Excel.Import<Employee>(path);
        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Equal("外包", result.Data[0].Department);
    }
}
