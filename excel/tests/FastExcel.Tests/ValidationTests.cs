using FastExcel.Import;
using FastExcel.Tests.Models;

namespace FastExcel.Tests;

public class ValidationTests : IDisposable
{
    private readonly string _dir;
    public ValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fastexcel_val_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }
    private string TempPath(string n = "v.xlsx") => Path.Combine(_dir, n);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // Build a sheet with deliberate problems, then import + validate.
    private string MakeMessySheet()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["工号"] = 1, ["姓名"] = "正常", ["部门"] = "A", ["薪资"] = 100, ["入职日期"] = new DateTime(2020,1,1), ["在职"] = true, ["职级"] = "Mid" },
            new Dictionary<string, object?> { ["工号"] = 2, ["姓名"] = new string('超', 25), ["部门"] = "B", ["薪资"] = -5, ["入职日期"] = new DateTime(2020,1,1), ["在职"] = false, ["职级"] = "Senior" }, // name too long + salary < range
            new Dictionary<string, object?> { ["工号"] = 0, ["姓名"] = "", ["部门"] = "C", ["薪资"] = 50, ["入职日期"] = "not-a-date", ["在职"] = true, ["职级"] = "Junior" }, // missing required name + bad date
        };
        var p = TempPath();
        Excel.SaveAs(p, rows);
        return p;
    }

    [Fact]
    public void Collects_Multiple_Located_Errords_Per_Row()
    {
        var path = MakeMessySheet();
        var result = Excel.Import<Employee>(path);

        Assert.True(result.HasErrors);
        Assert.Single(result.Data); // only the clean row survives

        // Row 3 (header is row 1): name too long + salary range
        Assert.Contains(result.Errors, e => e.RowNumber == 3 && e.FieldName == nameof(Employee.Name) && e.Kind == ImportErrorKind.Validation);
        Assert.Contains(result.Errors, e => e.RowNumber == 3 && e.FieldName == nameof(Employee.Salary) && e.Message.Contains("薪资"));

        // Row 4: required name missing (Validation) AND bad date (Conversion) both reported
        Assert.Contains(result.Errors, e => e.RowNumber == 4 && e.FieldName == nameof(Employee.Name) && e.Kind == ImportErrorKind.Validation);
        Assert.Contains(result.Errors, e => e.RowNumber == 4 && e.FieldName == nameof(Employee.HireDate) && e.Kind == ImportErrorKind.Conversion);

        // errors carry a located cell reference
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrEmpty(e.CellReference)));
    }

    [Fact]
    public void IValidatableObject_CrossField_Rule_Fires()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Sku"] = "X1", ["Qty"] = 5, ["UnitPrice"] = 0, ["IsGift"] = false }, // non-gift price<=0 -> invalid
            new Dictionary<string, object?> { ["Sku"] = "X2", ["Qty"] = 1, ["UnitPrice"] = 0, ["IsGift"] = true },  // gift allowed
        };
        var p = TempPath("orders.xlsx");
        Excel.SaveAs(p, rows);
        var result = Excel.Import<OrderLine>(p);

        Assert.Single(result.Data);
        Assert.Equal("X2", result.Data[0].Sku);
        Assert.Contains(result.Errors, e => e.RowNumber == 2 && e.FieldName == nameof(OrderLine.UnitPrice) && e.Message.Contains("赠品"));
    }

    [Fact]
    public void ThrowOnError_Raises_Exception_With_Errors()
    {
        var path = MakeMessySheet();
        var ex = Assert.Throws<FastExcelImportException>(() =>
            Excel.Import<Employee>(path, new ImportOptions { ThrowOnError = true }));
        Assert.NotEmpty(ex.Errors);
    }

    [Fact]
    public void IncludeInvalidRows_In_Data_When_Option_On()
    {
        var path = MakeMessySheet();
        var result = Excel.Import<Employee>(path, new ImportOptions { IncludeInvalidRowsInData = true });
        Assert.Equal(3, result.Data.Count); // all rows present, invalid ones partially populated
        Assert.True(result.HasErrors);
    }
}
