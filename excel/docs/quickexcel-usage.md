# QuickExcel 使用文档

**QuickExcel** 是一个面向 .NET 10 的高性能 Excel（`.xlsx`）导入导出库：**零第三方依赖**、全程流式读写（内存占用与行数无关）、内置 DataAnnotations 数据验证、单元格图片导入导出与模板填充。本文件同时也是 NuGet 包内 README。

> ① 只支持 `.xlsx`（OOXML），不支持老的 `.xls` 二进制格式与 CSV。
> ② 目标框架 `net10.0`。

## 安装

```bash
dotnet add package QuickExcel --prerelease
```

或在 csproj 中写死版本号（当前为预发布系列，`dotnet add package` 不带 `--prerelease` 会报"找不到稳定版本"）：

```xml
<PackageReference Include="QuickExcel" Version="0.1.0-beta.6" />
```

包零依赖（不牵入任何第三方库），命名空间为 `QuickExcel`，一行 `using QuickExcel;` 就够。刚推送的新版本若安装不到，先清本机 NuGet 缓存：`dotnet nuget locals http-cache --clear`。

## 快速上手

### 导出

```csharp
using QuickExcel;

Excel.SaveAs("orders.xlsx", orderList, new ExportOptions
{
    SheetName = "订单",
    BoldHeader = true,
    FreezeHeaderRow = true,
});

byte[] bytes = Excel.GetAsByteArray(orderList);     // 内存产物（Web 下载用）
await Excel.SaveAsAsync("orders.xlsx", orderList);  // 异步
```

`value` 支持五种数据源（见[支持的数据源](#支持的数据源)）；传 `List<ExcelSheet>` 即多 Sheet 导出。

### 导入 + 验证

```csharp
using QuickExcel;
using System.ComponentModel.DataAnnotations;

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

    [ExcelColumn("部门", Aliases = new[] { "Dept", "所属部门" })]  // 表头别名
    public string? Department { get; set; }

    [ExcelIgnore]
    public string InternalCode { get; set; } = "hidden";
}

ImportResult<Employee> result = Excel.Import<Employee>("in.xlsx");
if (result.HasErrors)
    foreach (var e in result.Errors)
        Console.WriteLine($"第{e.RowNumber}行 {e.CellReference} [{e.Header}] {e.Message} ({e.Kind})");
```

验证基于 `System.ComponentModel.DataAnnotations`（`[Required]` / `[StringLength]` / `[Range]` / `[RegularExpression]` 等一切 `ValidationAttribute` 子类都生效），并支持 `IValidatableObject` 跨字段规则。**转换错误与验证错误合并不短路**：同一行的坏日期 + 缺失必填会一起报告，每条错误带行号、列名、单元格引用（如 `C12`）。

### 模板填充

```csharp
Excel.SaveAsByTemplate("report.xlsx", "template.xlsx", new Dictionary<string, object?>
{
    ["Title"] = "季度报告",
    ["When"]  = DateTime.Now,
    ["items"] = orderList,                             // {{items.*}} 集合行展开
    ["logo"]  = ExcelImage.FromFile("logo.png"),       // {{@logo}} 图片占位
});
```

| 占位符 | 含义 |
|---|---|
| `{{Title}}` | 标量替换（独占一格时保留模板样式与原生类型） |
| `{{items.Name}}` | 集合行展开，逐元素克隆模板行（样式保留） |
| `{{@logo}}` | 图片占位符（`ExcelImage` / `byte[]` / `ImagePlaceholder`） |

---

## 导出

### 入口方法

| 方法 | 说明 |
| --- | --- |
| `Excel.SaveAs(string path, object value, ExportOptions? = null)` | 覆盖写文件；自动创建父目录 |
| `Excel.SaveAs(Stream stream, object value, ExportOptions? = null)` | 写入流 |
| `Excel.SaveAsAsync(string path, …)` / `SaveAsAsync(Stream, …)` | 异步版 |
| `Excel.GetAsByteArray(object value, ExportOptions? = null)` | 返回 `byte[]` |
| `Excel.GetSheetNames(string path)` | 列出工作表名 |

### 支持的数据源

`value` 参数为 `object`，支持：

| 数据源 | 列的来源 |
| --- | --- |
| `IEnumerable<T>`（POCO） | 公共可读属性/字段，按 `[ExcelColumn(Order)]` 排序 |
| `List<ExcelSheet>` / 单个 `ExcelSheet` | 多 Sheet；`new ExcelSheet("订单", rows)`，重名自动去重 |
| `IEnumerable<IDictionary<string, object>>` / `ExpandoObject` | **第一行的键集合**为列（空集合无法推断列，抛 `ArgumentException`） |
| `DataTable` | `DataColumns`（`DBNull` → 空单元格） |
| 其他任意 `IEnumerable` | 反射探测元素类型 |

### `ExportOptions`

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `SheetName` | `"Sheet1"` | 工作表名 |
| `PrintHeader` | `true` | 是否写表头行 |
| `BoldHeader` | `false` | 表头加粗 |
| `FreezeHeaderRow` | `false` | 冻结首行 |
| `StringStorage` | `Inline` | 字符串存储模式 |
| `CompressionLevel` | `Fastest` | zip 压缩档位 |
| `BufferSize` | 1 MiB | 写出缓冲 |

### 列特性（导出）

| 特性 | 作用 |
| --- | --- |
| `[ExcelColumn("列名")]` | 改列名；`Order` 排序（小的在前）；`Width` 列宽（Excel 字符单位）；`Format` 数字格式；`Bold` 整列加粗；`Align` 对齐（`Left/Center/Right`）；`Aliases` 导入时的表头别名；`Required` 必填 |
| `[ExcelColumnWidth(20)]` | 单独设列宽（优先级高于 `ExcelColumn.Width`） |
| `[ExcelFormat("#,##0.00")]` | 单独设数字格式（写入 numFmt，Excel 中真实生效） |
| `[ExcelIgnore]` | 排除该属性 |
| `[ExcelImage]` / `[ExcelImage(Width, Height)]` | 该列按图片导出（`ExcelImage` 类型成员可省略此特性） |

样式能力只有三维度：**加粗**（表头/整列）、**对齐**、**数字格式**，外加列宽与冻结首行。没有填充色、字体颜色、边框、斑马纹——样式是逐列的，不是逐单元格的。

### 值的序列化规则

| CLR 类型 | 单元格内容 |
| --- | --- |
| `null` / 空串 | 空单元格 |
| `bool` | 原生布尔（1/0） |
| 整数 / `decimal` / `float` / `double` | 数字（不变文化格式） |
| `DateTime` / `DateOnly` / `TimeOnly` / `TimeSpan` | Excel 日期序列值 + 内置格式（午夜值按日期、其余按日期时间） |
| `Guid` / `Uri` / 枚举（写名）/ 其他对象 | 文本 |
| `byte[]` | **Base64 文本**——要内嵌为图片请标 `[ExcelImage]` 或改用 `ExcelImage` 类型 |
| `ExcelImage` | 空单元格 + 浮动图片 |

## 导入与验证

### 两条读路径

| 方法 | 用途 |
| --- | --- |
| `Excel.Query(path, useHeaderRow = true, ImportOptions?)` | **流式动态行**：`IEnumerable<IDictionary<string, object?>>`，不验证、不建类型；`useHeaderRow=false` 时键为列字母 `"A"/"B"/…` |
| `Excel.Query<T>(path, ImportOptions?)` | **流式强类型**：懒枚举，边读边转；转换失败该成员落默认值、不报错 |
| `Excel.Import<T>(path, ImportOptions?)` | **完整导入 + DataAnnotations 验证**：返回 `ImportResult<T>` |
| `Excel.ImportAsync<T>(…)` | 异步版 |
| `Excel.ImportByTemplate<T>(dataPath, templatePath, …)` | 表头以模板文件为准（取模板 `HeaderRowIndex` 行） |

`Query` / `Query<T>` 是同步 `IEnumerable`（前向流式），适合大文件逐行处理；`Import<T>` 一次性读全并验证。

### `ImportOptions`

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `SheetName` | `null` | null = 第一个工作表 |
| `HeaderRowIndex` | `0` | 表头行号（0 基）；`-1` 自动探测第一个非空行 |
| `MatchHeadersByName` | `true` | 按表头名匹配列；否则按位置 |
| `ThrowOnError` | `false` | true = 首错即抛 `QuickExcelImportException`（在 `QuickExcel.Import` 命名空间，带 `Errors` 清单） |
| `MaxRows` | `0` | 最大数据行数，0 = 不限 |
| `TrimStrings` | `true` | 字符串去首尾空白 |
| `IncludeInvalidRowsInData` | `false` | true = 无效行也进 `Data`（配合错误清单定位） |
| `FillMergedCells` | `true` | 合并区左上角的值回填到区内每一行（导出不生成合并区） |

### `ImportResult<T>`

| 成员 | 说明 |
| --- | --- |
| `Data` | `List<T>`，有效行（默认不含无效行） |
| `Errors` | `List<ImportError>`：`RowNumber`（1 基工作表行号）、`FieldName`（属性名）、`Header`（表头名）、`ColumnIndex?`、`CellReference?`（如 `C12`）、`Message`、`Kind`（`Conversion` / `Validation`） |
| `TemplateErrors` | `List<string>`：表头级问题（如必填列整个缺失——这类错误不属于任何一行） |
| `Exception` | 整体性异常（文件损坏等） |
| `TotalRows` | 数据行总数（不含表头） |
| `HasErrors` | `Errors` / `TemplateErrors` / `Exception` 任一非空 |
| `FailedRowNumbers()` | 出错行号（去重升序） |

### 类型转换规则（导入）

- 整数：不变文化解析，容忍 `"1,234"` 与 `"1234.0"`；布尔文本 `true/1/yes/y` 与 `false/0/no/n`（大小写不敏感）
- `DateTime`：数字 → OADate；否则先 `TryParse`（当前文化 → 不变文化），再尝试常见格式（`yyyy-MM-dd`、`yyyy/MM/dd`、`yyyy-MM-dd HH:mm:ss` 等）；`DateOnly` / `DateTimeOffset` 由同一结果派生
- 枚举：按名大小写不敏感解析；`Guid` / `Uri` 直接解析
- 图片目标（`ExcelImage` / `byte[]`）：接受图片对象、字节数组或 Base64 文本
- 失败 → `Conversion` 错误（`Import<T>` 报告；`Query<T>` 落默认值）

## 图片

```csharp
public class Photo
{
    [ExcelColumn("名称")] public string Name { get; set; } = "";

    [ExcelColumn("图片")]
    [ExcelImage(Width = 120)]              // 显式像素尺寸（缺省用图片原生尺寸）
    public ExcelImage? Pic { get; set; }   // 也支持 byte[]（需标 [ExcelImage]）
}

Excel.SaveAs("photos.xlsx", photos);           // 导出：浮动图片锚定到单元格，相同字节自动去重
var back = Excel.Import<Photo>("photos.xlsx"); // 导入：按锚点坐标回填 ExcelImage
```

- `ExcelImage`：`FromFile` / `FromFileAsync` / `FromStream` / `FromStreamAsync`，支持 `byte[]` 隐式转换；格式按魔数识别（PNG/JPEG/GIF/BMP/EMF/WMF/TIFF），尺寸从头解析，识别不出时用 `[ExcelImage(Width, Height)]`
- 导入兼容 WPS 的 `=DISPIMG()` 单元格图片（读取 `xl/cellimages.xml`）
- `AutoFitRow = true`（默认）自动加高图片所在行

## 模板

### 入口方法

| 方法 | 说明 |
| --- | --- |
| `Excel.SaveAsByTemplate(outputPath, templatePath, value, TemplateOptions?)` | 磁盘进出 |
| `Excel.SaveAsByTemplate(Stream output, Stream template, …)` | 流进出（模板流不可寻址会先缓冲） |
| `Excel.SaveAsByTemplate(Stream output, string templatePath, …)` | 模板来自文件，产物写流 |
| `Excel.SaveAsByTemplateAsync(…)`（路径版 / 流版） | 异步版 |
| `Excel.GetAsByteArrayByTemplate(templatePath, value, TemplateOptions?)` | 内存产物 |
| `Excel.ImportByTemplate<T>(dataPath, templatePath, ImportOptions?)` | 表头取自模板 |

`value`：`IDictionary<string, object?>`（含 `ExpandoObject`，键大小写不敏感）或任意对象（取 public 实例属性）。

### `TemplateOptions`

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `SheetName` | `null` | null = 第一个工作表 |
| `DateTimeFormat` | `"yyyy-MM-dd HH:mm:ss"` | 日期转文本格式；特殊值 `"serial"` 写 Excel 原生日期序列（保留真日期类型） |
| `DateOnlyFormat` | `"yyyy-MM-dd"` | `DateOnly` 转文本格式 |

### 语法细节

- `{{items.Field}}` 集合展开：**连续的模板行绑定同一集合名**构成一个块，逐元素克隆（样式保留）；空集合时整块行移除；一行里出现两个不同集合名抛 `QuickExcelTemplateException`
- `{{@img}}` 图片：锚定为浮动图片、原单元格清空；模板里已有的图片/Logo 会保留（合并而非替换）
- 无法解析的占位符输出**空单元格**；整格独占占位符保留模板样式与原生类型，与文字混排时按内联字符串处理

## 性能

100,000 行 × 7 列（家用 x64，Release）：

| 场景 | QuickExcel | MiniExcel |
|---|---|---|
| 导出 | **366 ms / 101 MB 分配** | 662 ms / 243 MB |
| 流式类型化导入（`Query<T>`） | **600 ms / 143 MB** | 1,522 ms / 483 MB |

复现：`dotnet run -c Release --project excel/benchmarks/QuickExcel.Benchmarks`

## 边界与常见问题

- **只支持 `.xlsx`**；老的 `.xls` 与 CSV 不支持（`ExcelType.Csv` 是占位，尚无实现）
- **模板展开的行偏移不会平移行引用**：`mergeCells` / `dataValidation` / `hyperlink` 等部件里引用行号的定义原样保留，展开区下方的合并区等可能错位
- 字典/`ExpandoObject` 导出以**第一行的键**为列
- 目标类型需 `class + new()`；带参构造用 `Activator` 回退
- `byte[]` 默认按 Base64 文本导出，不是图片
- 样式没有颜色/边框/逐格样式；需要复杂排版先出数据再用 Excel/WPS 调
- `Query<T>` 静默容忍转换失败（落默认值）；需要错误定位请用 `Import<T>`

## 相关文档

- [QuickSql（ORM）使用文档](../../sql/docs/quicksql/README.md)
- [QuickWord 使用文档](../../word/docs/quickword-usage.md)
- [QuickPdf 使用文档](../../pdf/docs/quickpdf-usage.md)

## License

MIT
