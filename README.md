# Quick

基于 **.NET 10** 的高性能 Excel（.xlsx）导入导出库 **QuickExcel** 及其姊妹库（QuickWord / QuickPdf / QuickSql）的同仓集合。设计参考 [MiniExcel](https://gitee.com/dotnetchina/MiniExcel) 与 [Magicodes.IE](https://gitee.com/magicodes/Magicodes.IE)，**零第三方依赖**，全程流式读写，支持 DataAnnotations 特性验证、单元格图片导入导出与模板导出/导入。仓库按库分子树：**QuickExcel** 在 `excel/`（src / tests / benchmarks），姊妹库 **QuickWord / QuickPdf** 在 `word/`、`pdf/`（各含 src / tests；各自内置同构但独立的列模型，做 Word 表格 / 中文字体嵌入 PDF 导出，与 QuickExcel **零项目引用**——数据源形态一致，列特性则各用各的 `[WordColumn]` / `[PdfColumn]`）；**QuickSql**（原 Fulcrum ORM，合并时整体更名）在 `sql/`。四棵子树各自带独立的 `Directory.Build.props` + `Directory.Packages.props`（CPM 集中包版本），构建配置互不越界；其中 `TreatWarningsAsErrors` 与 trim/AOT 分析器的严格闸门只在 `sql/` 启用，文档三件套刻意保持轻闸门（QuickExcel 大量使用反射与表达式树，警告即错误会当场翻红）。

## 文档

每库一份使用说明（集成 / 配置 / 使用 / 边界，逐 API 对过源码）：

| 库 | 使用说明 |
| --- | --- |
| QuickExcel | [`excel/docs/quickexcel-usage.md`](excel/docs/quickexcel-usage.md) |
| QuickWord | [`word/docs/quickword-usage.md`](word/docs/quickword-usage.md) |
| QuickPdf | [`pdf/docs/quickpdf-usage.md`](pdf/docs/quickpdf-usage.md) |
| QuickSql | [`sql/docs/quicksql-orm-usage.md`](sql/docs/quicksql-orm-usage.md)（+ [设计规格](sql/docs/quicksql-orm-design.md)、[Schema 同步指南](sql/docs/schema-sync-guide.md)） |

## 特性一览

- 🚀 **流式 xlsx 读写**：SAX 式写出 + 前向 `XmlReader` 读取，内存占用与行数无关（10 万行导出约 100 MB 分配、峰值工作集 <300 MB）
- 🔗 **合并单元格导入**：读取时把合并区左上角的值自动回填到区内每一行（`FillMergedCells`，默认开启）；导出不生成合并区
- ✅ **DataAnnotations 验证**：`[Required]` / `[Range]` / `[StringLength]` / `[RegularExpression]` …，并支持 `IValidatableObject` 跨字段规则；一行可同时报出多个定位错误（行号 / 列名 / 单元格引用）
- 🖼️ **图片导入导出**：`ExcelImage` / `byte[]` 列，标准 OOXML drawing 锚定；相同图片字节自动去重；导入时按锚点坐标回填图片，并兼容 WPS 的 `DISPIMG` 单元格图片（`=_xlfn.DISPIMG(...)` + `xl/cellimages.xml`）
- 📄 **模板导出/导入**：`{{Name}}` 标量占位符、`{{items.Field}}` 集合行展开（保留模板样式）、`{{@img}}` 图片占位符；模板其余部件（样式、主题、其他 Sheet、已有 Logo）原样保留
- 🧬 **多数据源导出**：POCO、`IDictionary<string, object>`、`ExpandoObject`、`DataTable`、`IEnumerable<ExcelSheet>` 多 Sheet
- ⚡ **表达式树编译**：属性访问器/构造函数一次性编译并缓存，反射开销仅发生在首行
- 📝 **QuickWord / QuickPdf（同解决方案姊妹库，仅导出）**：各自内置与 QuickExcel 同构的列特性（`[WordColumn]` / `[PdfColumn]` 一族）直接导出 .docx / PDF，三库互相**零项目引用**；PDF 运行时嵌入系统 TrueType 中文字体（微软雅黑 / 黑体 / 宋体 / Noto CJK），任何设备打开渲染一致

## 快速开始

> 以下是速览；每个选项字段的默认值与语义、版式/换行/分页规则等完整口径见 [`excel/docs/quickexcel-usage.md`](excel/docs/quickexcel-usage.md)。

### 导出

```csharp
using QuickExcel;

// POCO 集合
Excel.SaveAs("orders.xlsx", orderList, new ExportOptions
{
    SheetName = "订单",
    BoldHeader = true,
    FreezeHeaderRow = true,
});

// 也支持字典 / ExpandoObject / DataTable / 多 Sheet
Excel.SaveAs("multi.xlsx", new List<ExcelSheet>
{
    new("客户", customerList),
    new("订单", orderList),
});

byte[] bytes = Excel.GetAsByteArray(orderList);   // 内存产物
await Excel.SaveAsAsync("orders.xlsx", orderList); // 真异步
```

### 导入 + 验证

```csharp
public class Employee
{
    [ExcelColumn("工号", Order = 1)]
    [Required(ErrorMessage = "工号必填")]
    public int Id { get; set; }

    [ExcelColumn("姓名", Order = 2, Width = 18)]
    [Required]
    [StringLength(20, ErrorMessage = "姓名不能超过20字")]
    public string Name { get; set; } = "";

    [ExcelColumn("薪资", Order = 4)]
    [Range(0, 1_000_000, ErrorMessage = "薪资范围非法")]
    [ExcelFormat("#,##0.00")]
    public decimal Salary { get; set; }

    [ExcelColumn("部门", Aliases = new[] { "Dept", "所属部门" })] // 表头别名
    public string? Department { get; set; }

    [ExcelIgnore]
    public string InternalCode { get; set; } = "hidden";
}

ImportResult<Employee> result = Excel.Import<Employee>("in.xlsx");
if (result.HasErrors)
    foreach (var e in result.Errors)
        Console.WriteLine($"第{e.RowNumber}行 {e.CellReference} [{e.Header}] {e.Message} ({e.Kind})");
```

- 转换错误与验证错误**合并不短路**：同一行的坏日期 + 缺失必填会一起报告
- `ThrowOnError = true` 改为首错即抛 `QuickExcelImportException`（携带全部已收集错误）
- 🔗 **合并单元格读取**（默认开启）：OOXML 只在合并区左上角存一次值，`Excel.Import`/`Query` 读取时会把该值"回填"到区内每一行，因此分组表头、纵向合并的类别列都能逐行拿到值。关闭用 `new ImportOptions { FillMergedCells = false }`（非合并文件可省掉扫描，纯性能考量）
- 缺失的必填列报告在 `TemplateErrors`
- 只读不验证的高吞吐场景用 `Excel.Query<T>()` / `Excel.Query()`（动态字典，流式逐行）

### 图片

```csharp
public class Photo
{
    [ExcelColumn("名称")] public string Name { get; set; } = "";

    [ExcelColumn("图片")][ExcelImage(Width = 120)]  // 显式尺寸；默认用图片原始像素并自动抬高行高
    public ExcelImage? Pic { get; set; }             // 也支持 byte[] / ImagePlaceholder
}

Excel.SaveAs("photos.xlsx", photos);                  // 导出为浮动图片锚定到单元格
var back = Excel.Import<Photo>("photos.xlsx");        // 导入按 (行,列) 锚点回填 ExcelImage
                                                     // 相同字节只写一份 xl/media 部件（自动去重）
```

### 模板导出

模板就是一个普通 xlsx，单元格文本中写占位符：

| 语法 | 含义 |
|---|---|
| `{{Title}}` | 标量替换（支持 `{{a.b}}` 嵌套属性；整格单占位符时数字/布尔保留原生类型与模板样式） |
| `{{items.Name}}` | 行展开：`items` 解析为集合时，该模板行按每个元素复制一份（样式保留；空集合删除该行） |
| `{{@logo}}` | 图片占位符：值为 `ExcelImage` / `byte[]` / `ImagePlaceholder` 时锚定浮动图片 |

```csharp
Excel.SaveAsByTemplate("report.xlsx", "template.xlsx", new Dictionary<string, object?>
{
    ["Title"] = "季度报告",
    ["When"]  = DateTime.Now,          // 文本替换用 TemplateOptions.DateTimeFormat
    ["items"] = orderList,             // List<T> / 匿名类型 / 字典均可
    ["logo"]  = ExcelImage.FromFile("logo.png"),
});

// POCO 作为数据根同样有效
Excel.SaveAsByTemplate("report.xlsx", "template.xlsx", new Report { Title = "...", items = list });
```

### 模板导入

数据文件带标题行/备注行等前置内容时，以模板声明的表头布局为准：

```csharp
// 模板第 2 行（HeaderRowIndex = 1）是"工号/姓名/薪资…"表头
var result = Excel.ImportByTemplate<Employee>("data.xlsx", "template.xlsx",
    new ImportOptions { HeaderRowIndex = 1 });
```

### Word / PDF 导出（QuickWord / QuickPdf）

引用 `QuickWord` / `QuickPdf` 包（与 QuickExcel 同解决方案、互不引用，三库都是零依赖）。数据源形态与 `Excel.SaveAs` 完全一致（POCO / 字典 / DataTable / 多表），列特性是各库自己的同名一套：`[WordColumn(Name, Width)]` / `[WordFormat]` / `[WordIgnore]`、`[PdfColumn(Name, Width)]` / `[PdfFormat]` / `[PdfIgnore]`（同一 POCO 想要三边都生效就叠写三套特性；只写 `[ExcelColumn]` 时 Word/PDF 按属性名默认导出）：

```csharp
using QuickWord;
using QuickPdf;

// Word：数据流式写入 .docx 表格（内存 O(列数)，10 万行约 40 ms/10 KB 级写出缓冲）
Word.SaveAs("report.docx", employeeList, new WordOptions { Title = "员工花名册" });

// PDF：自动查找并整体嵌入系统 CJK TrueType 字体（Identity-H），任何阅读器渲染一致
Pdf.SaveAs("report.pdf", employeeList, new PdfOptions
{
    Title = "员工花名册",
    Landscape = true,        // A4 横向
    FontSizePt = 9,
    // FontFile = @"C:\Windows\Fonts\msyh.ttc",  // 或指定字体；找不到可用字体抛 QuickPdfException
});

// 多表 = PdfSheet 序列，每表一节、自动分页并在每页重复表头
Pdf.SaveAs("multi.pdf", new List<PdfSheet> { new("员工", staff), new("字典", dict) });
```

- 中文按字断行、英文按词断行；长文本自动换行加高行，跨页自动续表头
- PDF 文本为真实可复制文字（ToUnicode 映射），非截图
- 200 行中文字典序 PDF 约 **210 ms / 14.8 MB**（大头是 20 MB 雅黑整体嵌入；后续可按需做子集化）
- 完整用法：[`word/docs/quickword-usage.md`](word/docs/quickword-usage.md) · [`pdf/docs/quickpdf-usage.md`](pdf/docs/quickpdf-usage.md)（字体解析链、列宽换算、PDF 结构逐节可核）

## 性能（100,000 行 × 7 列，i 家用小钢炮，Release）

| 场景 | QuickExcel | MiniExcel | 说明 |
|---|---|---|---|
| 导出 | **366 ms / 101 MB alloc** | 662 ms / 243 MB | 产物大小相近（~7 MB） |
| 流式类型化导入 | **600 ms / 143 MB** | 1,522 ms / 483 MB | `Query<T>` |
| 带验证实体导入 | 10 万行全部通过 | — | `Import<T>` |
| 模板渲染 10 万行 | ~1.5 s | — | DOM 路径（模板体量小，可接受） |

复现：`dotnet run -c Release --project excel/benchmarks/QuickExcel.Benchmarks`

## 与参考项目的差异

| | MiniExcel | Magicodes.IE | **QuickExcel** |
|---|---|---|---|
| 依赖 | 无 | EPPlus/NPOI 等 | **无** |
| 验证 | 无 | DataAnnotations | DataAnnotations + IValidatableObject，**不短路、可定位** |
| 图片 | 导出模板占位 | 模板图片导出 | **标准 drawing 导出 + 按锚点导入 + 字节去重** |
| 模板 | `{{}}` / 集合展开 | 模板导出 | `{{}}` / 集合展开 / **`{{@img}}` 图片占位** / 部件保真 |
| API 风格 | `ExcelStreaming` 扩展 | 接口注入 | 静态门面 `Excel.*` + 选项对象 |

## 已知限制

- 仅支持 `.xlsx`（OOXML），不支持 `.xls` 二进制与 CSV（CSV 可按需扩展）
- 模板渲染中：sheetData 之后的部件（mergeCells / dataValidation / hyperlink 等）原样保留，**集合行展开导致的行偏移不会平移其行引用**（模板若把合并区放在展开区下方会错位）；模板中已有的公式/shared formula 不做重算
- 字典导出以**第一行的键集合**为列（与 MiniExcel 相同，行键请保持一致）
- 目标类型需 `class + new()`；带参构造用 `Activator` 回退
- QuickWord 双向（`Word.SaveAs` 导出表格、`Word.Read` 读回段落与表格块模型）；QuickPdf **只做导出**（不提供 PDF 解析导入）；PDF 依赖宿主机的 CJK TrueType 字体（可 `FontFile` 显式指定），且为保渲染一致采用**字体整体嵌入**（无子集化），单文件约 +15 MB

## 开发

```bash
dotnet build Quick.slnx                # 全解决方案构建（文档三件套 + QuickSql 全家，0 警告 0 错误）
dotnet test excel/tests/QuickExcel.Tests   # Excel 26 个单测（roundtrip / 图片 / DISPIMG / 合并单元格 / 验证 / 模板）
dotnet test word/tests/QuickWord.Tests     # Word 3 个单测（docx 结构 / 多 Sheet / 字典源）
dotnet test pdf/tests/QuickPdf.Tests       # PDF 4 个单测（xref 对账 / 内容流算符 / 分页续表头 / 横向）
dotnet run -c Release --project excel/benchmarks/QuickExcel.Benchmarks

# QuickSql（详见 sql/docs/quicksql-orm-usage.md）：xunit.v3/MTP 在本机 SDK 下不能走 dotnet test，
# 与原仓库一致，用 build 后直接跑测试 exe（或 sql/scripts/ 下的门闸脚本）：
dotnet build sql/tests/QuickSql.Tests.Dialects && sql/tests/QuickSql.Tests.Dialects/bin/Debug/net10.0/QuickSql.Tests.Dialects.exe        # 方言离线册（679 条，黄金样本比对）
$env:QUICKSQL_TEST_PROVIDER='sqlite'; dotnet build sql/tests/QuickSql.Tests.Integration && sql/tests/QuickSql.Tests.Integration/bin/Debug/net10.0/QuickSql.Tests.Integration.exe   # SQLite 真库册（本机临时目录自建库）
```

产物兼容性经 openpyxl 交叉验证（日期格式、列宽、样式、drawing/media 结构）；docx 经本机 Word COM 真实打开校验；PDF 经 PyMuPDF 校验（结构零告警、Identity-H 字体注册、文本可抽取）并渲染截图核对中文显示。

## License

MIT
