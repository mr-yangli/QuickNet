using QuickExcel;
using System.ComponentModel.DataAnnotations;

// QuickExcel 示例：导出与导入两条线都走一遍。零第三方依赖、全程流式（内存与行数无关）、只支持 .xlsx。
// 见 docs/quickexcel-usage.md（§3 导出 / §4 导入与验证 / §5 图片 / §6 模板）。
const string PngBase64 =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

var files = new List<string>();

// ── 1. 导出（POCO 单表）：加粗+冻结表头、自定义数字格式、列宽、必填 ──────────────
var staff = new List<Employee>
{
    new() { Id = 1, Name = "张三", Salary = 8000m,  HiredAt = new DateTime(2026, 1, 5) },
    new() { Id = 2, Name = "李四", Salary = 12000m, HiredAt = new DateTime(2026, 2, 9) },
    new() { Id = 3, Name = "王五", Salary = 9500m,  HiredAt = new DateTime(2026, 3, 1) },
};

var xlsx = "emp.xlsx";
Excel.SaveAs(xlsx, staff, new ExportOptions { SheetName = "员工", BoldHeader = true, FreezeHeaderRow = true });
files.Add(xlsx);
Console.WriteLine($"[导出·POCO] {xlsx}（{new FileInfo(xlsx).Length} 字节）");

// ── 2. 导出（多表）：IEnumerable<ExcelSheet>，一表一 Sheet ───────────────────────
var multi = "multi.xlsx";
Excel.SaveAs(multi, new List<ExcelSheet>
{
    new("员工", staff),
    new("字典", new List<Dictionary<string, object?>> { new() { ["键"] = "总计", ["值"] = 29500m } }),
});
files.Add(multi);
Console.WriteLine($"[导出·多表] {multi}：Sheet = {string.Join(" ∶ ", Excel.GetSheetNames(multi))}");

// ── 3. 导出到流 / 异步 / 内存字节（三颗入口同源，都归一到 (sheet, 列, 行枚举)）──
using (var ms = new MemoryStream())
{
    Excel.SaveAs(ms, staff);                       // 写进流
    await Excel.SaveAsAsync("emp-async.xlsx", staff);   // 真异步（useAsync 的 FileStream + await）
    files.Add("emp-async.xlsx");
    var bytes = Excel.GetAsByteArray(staff);       // 内存产物
    Console.WriteLine($"[导出·流/异步/字节] 流 {ms.Length} B ∶ 异步落盘 ∶ 字节数组 {bytes.Length} B");
}

// ── 4. 只读（流式、无验证）：动态字典 与 强类型 Query<T> ─────────────────────────
var dynamic = Excel.Query(xlsx).ToList();                 // 键 = 表头文本
var typed = Excel.Query<Employee>(xlsx).ToList();          // 逐格转成强类型
Console.WriteLine($"[只读] 字典 {dynamic.Count} 行、首行键 = {string.Join(",", dynamic[0].Keys)}");
Console.WriteLine($"[只读] 强类型 {typed.Count} 行，首行 {typed[0].Name} ∶ {typed[0].Salary:0.00} ∶ {typed[0].HiredAt:yyyy-MM-dd}");

// ── 5. 导入 + DataAnnotations 验证（从不因数据错误抛，一次交回 ImportResult）──────
var clean = Excel.Import<Employee>(xlsx);
Console.WriteLine($"[导入·干净] 数据 {clean.Data.Count} 行 ∶ 错误 {clean.Errors.Count} ∶ 总行 {clean.TotalRows} ∶ HasErrors={clean.HasErrors}");

// 造一行脏数据（缺 [Required] 的名字、日期填成乱码文本），演示"转换错与验证错合并、不短路、都进 Errors"。
var dirtyPath = "dirty.xlsx";
Excel.SaveAs(dirtyPath, new List<Dictionary<string, object?>>
{
    new() { ["Id"] = 9, ["Name"] = "", ["Salary"] = "不是数字", ["HiredAt"] = "2026-13-40" },
});
files.Add(dirtyPath);
var dirty = Excel.Import<Employee>(dirtyPath);
Console.WriteLine($"[导入·脏] 错误 {dirty.Errors.Count} 条：" +
    string.Join("；", dirty.Errors.Select(e => $"{e.FieldName}({e.Kind})")));
Console.WriteLine($"        首错单元格定位 = {dirty.Errors.FirstOrDefault()?.CellReference ?? "n/a"}");

// ── 6. 模板导出：单元格文本里写占位符 {{字段}}，SaveAsByTemplate 渲染、其余部件原样保留 ──
//   先用一个"值即占位符"的表造出模板，再拿真实数据渲染（标量替换，保留原生类型与样式）。
var tplPath = "emp-template.xlsx";
Excel.SaveAs(tplPath, new List<Dictionary<string, object?>>
{
    new() { ["工号"] = "{{No}}", ["姓名"] = "{{Name}}", ["城市"] = "{{City}}" },
});
files.Add(tplPath);
var reportPath = "emp-report.xlsx";
Excel.SaveAsByTemplate(reportPath, tplPath, new { No = 7, Name = "赵六", City = "上海" });
files.Add(reportPath);
Console.WriteLine($"[模板导出] {reportPath} 渲染完成（{{{{No}}}}/{{{{Name}}}}/{{{{City}}}} → 7/赵六/上海）");

// ── 7. 图片导出 + 回读：成员类型 ExcelImage → 浮动图片锚；再 Query 回来 ──────────
var pic = "pic.xlsx";
Excel.SaveAs(pic, new List<Shot> { new() { Title = "一张像素", Photo = new ExcelImage(Convert.FromBase64String(PngBase64)) } });
files.Add(pic);
var shot = Excel.Query<Shot>(pic).First();
Console.WriteLine($"[图片] 导出 {new FileInfo(pic).Length} 字节 ∶ 回读 Photo = {shot.Photo?.PixelWidth}×{shot.Photo?.PixelHeight} {shot.Photo?.Format}（{shot.Photo?.Data.Length} 字节）");

foreach (var f in files)
    File.Delete(f);
Console.WriteLine("示例完成（临时产物已清理）。");

// 目标类型要 class + new()；[ExcelFormat]/[ExcelColumn(Width)] 只影响导出，不改表头名（默认取属性名）。
// [ExcelColumn(Bold/Align)] 是逐列样式位：整列（含表头）加粗或水平对齐，落进 xl/styles.xml 的 xf。
class Employee
{
    [ExcelColumn(Width = 8, Align = ExcelAlign.Center)]
    public int Id { get; set; }

    [Required, ExcelColumn(Bold = true)]
    public string? Name { get; set; }

    [ExcelFormat("#,##0.00")]
    public decimal Salary { get; set; }

    [ExcelColumn(Align = ExcelAlign.Right)]
    public DateTime HiredAt { get; set; }
}

// byte[] 标 [ExcelImage] 才当图片，否则按 Base64 文本导出；这里直接用 ExcelImage 成员类型。
// [ExcelImage(Width, Height)] 定的是<em>显示尺寸</em>——测试图是 1×1 像素，不给显示尺寸就小到看不见，
// 给一对尺寸后 Excel/WPS 会把它拉成一块肉眼可见的色块（源字节仍是那张 1×1，回读也拿回 1×1）。
class Shot
{
    public string? Title { get; set; }

    [ExcelImage(Width = 16, Height = 16)]
    public ExcelImage? Photo { get; set; }
}
