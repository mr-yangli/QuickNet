using QuickPdf;

// QuickPdf 示例：把数据源画成 PDF 表格页。零第三方依赖；CJK 字体从宿主机现找、TrueType 整份嵌入
// （Identity-H 寻址），所以产物一致但单文件较大。列模型与 QuickExcel 同构、各自独立（互不引用）。
var staff = new List<Employee>
{
    new() { Id = 1, Name = "张三", Salary = 8000m,  HiredAt = new DateTime(2026, 1, 5) },
    new() { Id = 2, Name = "李四", Salary = 12000m, HiredAt = new DateTime(2026, 2, 9) },
    new() { Id = 3, Name = "王五", Salary = 9500m,  HiredAt = new DateTime(2026, 3, 1) },
};

var singlePath = "quickpdf-sample.pdf";

// 单表：options 传 null 走全默认。产物先在内存整体建好再落盘。
Pdf.SaveAs(singlePath, staff);
Console.WriteLine($"导出单表 → {singlePath}（{new FileInfo(singlePath).Length / (1024 * 1024)} MB，含整份嵌入字体）");

// 多表 = PdfSheet 序列：每表一节、自动分页、续表头。
var multiPath = "quickpdf-multi.pdf";
Pdf.SaveAs(multiPath, new List<PdfSheet> { new("员工", staff) });
Console.WriteLine($"导出多表 → {multiPath}（{new FileInfo(multiPath).Length / (1024 * 1024)} MB）");

foreach (var file in new[] { singlePath, multiPath })
    File.Delete(file);

class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Salary { get; set; }
    public DateTime HiredAt { get; set; }
}
