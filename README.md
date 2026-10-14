# Quick

基于 **.NET 10** 的高性能 Excel（.xlsx）导入导出库 **QuickExcel** 及其姊妹库（QuickWord / QuickPdf / QuickSql）的同仓集合。设计参考 [MiniExcel](https://gitee.com/dotnetchina/MiniExcel) 与 [Magicodes.IE](https://gitee.com/magicodes/Magicodes.IE)，**零第三方依赖**，全程流式读写，支持 DataAnnotations 特性验证、单元格图片导入导出与模板导出/导入。

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
- 🖼️ **图片导入导出**：`ExcelImage` / `byte[]` 列，标准 OOXML drawing 锚定；相同图片字节自动去重；导入时按锚点坐标回填图片，并兼容 WPS 的 `DISPIMG` 单元格图片
- 📄 **模板导出/导入**：`{{Name}}` 标量占位符、`{{items.Field}}` 集合行展开（保留模板样式）、`{{@img}}` 图片占位符
- 🧬 **多数据源导出**：POCO、`IDictionary<string, object>`、`ExpandoObject`、`DataTable`、`IEnumerable<ExcelSheet>` 多 Sheet
- ⚡ **表达式树编译**：属性访问器/构造函数一次性编译并缓存，反射开销仅发生在首行
- 📝 **QuickWord / QuickPdf（同解决方案姊妹库，仅导出）**：各自内置与 QuickExcel 同构的列特性直接导出 .docx / PDF，三库互相**零项目引用**

## 快速开始

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

// 多数据源支持
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
- `ThrowOnError = true` 改为首错即抛 `QuickExcelImportException`

### 图片

```csharp
public class Photo
{
    [ExcelColumn("名称")] public string Name { get; set; } = "";

    [ExcelColumn("图片")][ExcelImage(Width = 120)]  // 显式尺寸
    public ExcelImage? Pic { get; set; }             // 也支持 byte[] / ImagePlaceholder
}

Excel.SaveAs("photos.xlsx", photos);                  // 导出为浮动图片锚定到单元格
var back = Excel.Import<Photo>("photos.xlsx");        // 导入按锚点回填 ExcelImage
```

### 模板导出

```csharp
Excel.SaveAsByTemplate("report.xlsx", "template.xlsx", new Dictionary<string, object?>
{
    ["Title"] = "季度报告",
    ["When"]  = DateTime.Now,
    ["items"] = orderList,
    ["logo"]  = ExcelImage.FromFile("logo.png"),
});
```

占位符语法：
| 语法 | 含义 |
|---|---|
| `{{Title}}` | 标量替换 |
| `{{items.Name}}` | 行展开 |
| `{{@logo}}` | 图片占位符 |

### Word / PDF 导出（QuickWord / QuickPdf）

```csharp
using QuickWord;
using QuickPdf;

// Word 导出
Word.SaveAs("report.docx", employeeList, new WordOptions { Title = "员工花名册" });

// PDF 导出（自动嵌入 CJK 字体）
Pdf.SaveAs("report.pdf", employeeList, new PdfOptions
{
    Title = "员工花名册",
    Landscape = true,        // A4 横向
    FontSizePt = 9,
});

// 多表 PDF
Pdf.SaveAs("multi.pdf", new List<PdfSheet> { new("员工", staff), new("字典", dict) });
```

## 性能（100,000 行 × 7 列，i 家用小钢炮，Release）

| 场景 | QuickExcel | MiniExcel | 说明 |
|---|---|---|---|
| 导出 | **366 ms / 101 MB alloc** | 662 ms / 243 MB | 产物大小相近（~7 MB） |
| 流式类型化导入 | **600 ms / 143 MB** | 1,522 ms / 483 MB | `Query<T>` |
| 模板渲染 10 万行 | ~1.5 s | — | DOM 路径 |

复现：`dotnet run -c Release --project excel/benchmarks/QuickExcel.Benchmarks`

## 与参考项目的差异

| | MiniExcel | Magicodes.IE | **QuickExcel** |
|---|---|---|---|
| 依赖 | 无 | EPPlus/NPOI 等 | **无** |
| 验证 | 无 | DataAnnotations | DataAnnotations + IValidatableObject，**不短路、可定位** |
| 图片 | 导出模板占位 | 模板图片导出 | **标准 drawing 导出 + 按锚点导入 + 字节去重** |
| 模板 | `{{}}` / 集合展开 | 模板导出 | `{{}}` / 集合展开 / **`{{@img}}` 图片占位** |

## 已知限制

- 仅支持 `.xlsx`（OOXML），不支持 `.xls` 二进制与 CSV
- 模板渲染中：sheetData 之后的部件（mergeCells / dataValidation / hyperlink 等）原样保留，**集合行展开导致的行偏移不会平移其行引用**
- 字典导出以**第一行的键集合**为列
- 目标类型需 `class + new()`；带参构造用 `Activator` 回退
- QuickWord 双向（导出 + 读回）；QuickPdf **只做导出**；PDF 依赖宿主机的 CJK TrueType 字体

## 开发

```bash
# 全解决方案构建
dotnet build Quick.slnx

# Excel 测试
dotnet test excel/tests/QuickExcel.Tests

# Word 测试
dotnet test word/tests/QuickWord.Tests

# PDF 测试
dotnet test pdf/tests/QuickPdf.Tests

# 运行性能基准
dotnet run -c Release --project excel/benchmarks/QuickExcel.Benchmarks
```

QuickSql 测试（需构建后直接运行 exe）：
```bash
dotnet build sql/tests/QuickSql.Tests.Dialects
sql/tests/QuickSql.Tests.Dialects/bin/Debug/net10.0/QuickSql.Tests.Dialects.exe
```

## License

MIT