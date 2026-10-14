using QuickWord;

// QuickWord 示例：流式写 .docx（表格 / 散文），再读回成块模型。零第三方依赖。
// 列模型与 QuickExcel 同构但完全独立（互不引用）：这里用普通属性，QuickWord 按属性名建列。
var tablePath = "quickword-table.docx";
var notePath = "quickword-note.docx";

var staff = new List<Employee>
{
    new() { Id = 1, Name = "张三", Salary = 8000m,  HiredAt = new DateTime(2026, 1, 5) },
    new() { Id = 2, Name = "李四", Salary = 12000m, HiredAt = new DateTime(2026, 2, 9) },
    new() { Id = 3, Name = "王五", Salary = 9500m,  HiredAt = new DateTime(2026, 3, 1) },
};

// 写表：Title 落成顶部 DocTitle 段，其余逐行 <w:tr> 流式写入，内存 O(列数)、与行数无关。
Word.SaveAs(tablePath, staff, new WordOptions { Title = "员工花名册" });
Console.WriteLine($"导出表格 → {tablePath}（{new FileInfo(tablePath).Length} 字节）");

// 读回：块模型（段落 + 表格）。这里只看段落——标题那一段就在其中，逐段读样式名与文本。
var paras = Word.Read(tablePath).Paragraphs.ToList();
Console.WriteLine($"读回 {paras.Count} 段；首段[{paras.FirstOrDefault()?.StyleName ?? "-"}] {paras.FirstOrDefault()?.Text}");

// 散文：给人读的通知，一行一段、不写表格。读回来正是段落模型，一家之言可往返。
Word.SaveLines(notePath, new[] { "本周例会改到周五。", "请各组周五前提交月报。" }, new WordOptions { Title = "通知" });
var note = Word.Read(notePath).Paragraphs.Count();
Console.WriteLine($"SaveLines 往返 → {notePath}（{new FileInfo(notePath).Length} 字节）；读回 {note} 段");

// 模板填充：不改版式地往别人排好的 .docx 里填 {{标签}}。这里先用 SaveLines 造一份"含占位符"的模板，
// 再拿值渲染——引擎只改写 word/document.xml、其余部件逐字节照抄；一段内缝合被 Word 拆开的标签，
// 未提供的键原样保留。值给 ImagePlaceholder（这里由一张 1×1 PNG 的字节隐式转）就在标签处发内联图。
const string PngBase64 =
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
var tplPath = "quickword-template.docx";
var reportPath = "quickword-report.docx";

Word.SaveLines(tplPath, new[] { "编号：{{No}}", "经办：{{Name}}", "日期：{{Date}}", "签名：{{Sign}}", "未填：{{Missing}}" });
ImagePlaceholder sign = Convert.FromBase64String(PngBase64);   // byte[] 隐式转 ImagePlaceholder；不给尺寸用原生像素
var values = new Dictionary<string, object>
{
    ["No"] = 7,
    ["Name"] = "赵六",
    ["Date"] = new DateTime(2026, 10, 7, 9, 0, 0),
    ["Sign"] = sign,               // {{Sign}} → 内联图；没给 Sign 尺寸则用图片原生像素
    // 故意不放 Missing → {{Missing}} 原样留在成品里（宁可让人看见，不静默吞正文）
};
Word.SaveAsByTemplate(reportPath, tplPath, values, new TemplateOptions { DateTimeFormat = "yyyy年MM月dd日" });
var filled = string.Join(" ∥ ", Word.Read(reportPath).Paragraphs.Select(p => p.Text).Where(t => t.Length > 0));
Console.WriteLine($"模板填充 → {reportPath}（{new FileInfo(reportPath).Length} 字节）；填后正文：{filled}");

foreach (var file in new[] { tablePath, notePath, tplPath, reportPath })
    File.Delete(file);

class Employee
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Salary { get; set; }
    public DateTime HiredAt { get; set; }
}
