# QuickExcel 使用说明

> 面向使用者的一份操作文档：怎么集成、怎么配置、怎么使用。根 `README.md` 是营销向的特性清单与快速上手，本文是逐 API 对齐的操作手册 —— 想知道"为什么是这个形状"读代码注释，想知道"该调哪个参数"读这里。
>
> 本文里出现的每一个 API 名字、每一个默认值都在工作树的 `excel/src/QuickExcel/` 下读到过真实定义；凡属"设计意图而尚未交付"的地方一律写明。

## 0. 这份文档是什么，现在真实的状态是什么

四段：§1 集成、§2 列模型与数据源、§3 导出、§4 导入与验证；§5 图片、§6 模板是两条独立功能线；§7 是边界与坑，§8 是一张 API 总表。

真实状态（三句，句句可核）：

1. **零第三方依赖**。`QuickExcel.csproj` 没有任何 `PackageReference`，只用 BCL（`System.IO.Compression` 打 zip、`XmlReader`/`XmlWriter` 读写 OOXML、`System.ComponentModel.DataAnnotations` 做验证）。姊妹库 QuickWord / QuickPdf 与它**互不引用**——各自内置一套同构的列模型（见 `../../word/docs/quickword-usage.md`、`../../pdf/docs/quickpdf-usage.md`）。
2. **只支持 `.xlsx`（OOXML），全程流式**。所有入口先归一到 `(sheet 名, 列元数据, 行枚举)` 三元组（`Export/SheetBuilder.cs`），写出是 SAX 式逐行、读取是前向 `XmlReader`，内存与行数无关。`ExcelType` 枚举里有 `Csv` 占位，但**今天没有 CSV 实现**（`enum ExcelType { Xlsx, Csv }`，`Csv` 是路线图）。
3. **验证证据**：`excel/tests/QuickExcel.Tests` 26 个单测（roundtrip / 图片 / DISPIMG / 合并单元格 / 验证 / 模板）；产物兼容性经 openpyxl 交叉验证（日期格式、列宽、样式、drawing/media 结构），并经本机 Excel COM 真实打开校验。

## 1. 怎么集成

### 1.1 SDK 与目标框架

| 项 | 值 | 出处 |
| --- | --- | --- |
| TFM | `net10.0` | `excel/Directory.Build.props` |
| 语言版本 | `preview`；`Nullable enable`、`ImplicitUsings enable` | `excel/Directory.Build.props` |
| unsafe | `AllowUnsafeBlocks`（仅本体需要） | `QuickExcel.csproj` |
| XML 文档 | `GenerateDocumentationFile true`，`NoWarn` 抑制 `CS1591` | `QuickExcel.csproj` |
| NuGet 包 | 有：`QuickExcel` —— **安装主路径就靠它**（见 §1.2）。源上（nuget.org）截至 2026-10-15 最新是 `0.1.0-beta.4`，与本仓库 props 声明的那颗逐字相同（版本串在此，作者/许可在仓库根 `Directory.Build.props`；运行期零依赖，csproj 只留 `PackageId`） | `excel/Directory.Build.props` + 根 `Directory.Build.props` |

子树根有独立的 `Directory.Build.props`（上面四颗公共设置上提于此；它再 `<Import>` 仓库根那一棵，那里放着四家共用的作者/许可/仓库地址）与 `Directory.Packages.props`（CPM：测试四件套与基准的 MiniExcel 版本钉在那里；src 零依赖不入账）。与 `sql/` 子树的差异是刻意的：这里**不开** `TreatWarningsAsErrors` 与 trim/AOT 分析器 —— QuickExcel 大量用反射与表达式树，过了那道闸门会当场红；两本账的边界见各自 props 头注。

### 1.2 应用侧怎么装：NuGet 主路径

**装 QuickExcel 只有一条主路径：NuGet。** 包 ID 与工程名逐字相同，运行期零依赖（不拖任何传递包，只用 BCL），装一颗就能用全部功能。csproj 落一行：

```xml
<ItemGroup>
  <PackageReference Include="QuickExcel" Version="0.1.0-beta.4" />
</ItemGroup>
```

或命令行装。注意全家都是 `-beta.N` 预发布档，`dotnet add package` 必须带 `--prerelease`（否则报"找不到稳定版本"），或在 `PackageReference` 里写死版本号：

```bash
dotnet add package QuickExcel --prerelease
```

版本口径以源上实读为准：截至 2026-10-15，nuget.org 上最新是 `0.1.0-beta.4`，本仓库 props 声明的就是这一颗——**装包请填源上已有的那颗**，写一颗还没发布的版本还原会失败。另有一条打包纪律：每重打一次包升一颗 beta，同版本号不同内容的两颗包，消费方全局缓存里那颗会压住新打的。

> **辅助路径（只有仓库内开发、贡献者、`tests`∶`samples` 才走）：源码 `ProjectReference`。** `<ProjectReference Include="..\..\excel\src\QuickExcel\QuickExcel.csproj" />`。`QuickExcel.csproj` 的 `InternalsVisibleTo` 只开一颗 `QuickExcel.Tests`；姊妹库 QuickWord / QuickPdf 各内置同构的 `TypeShape`/`SheetBuilder`/`DisplayFormatter` 副本，三库零互相引用——普通应用方请走上面的 NuGet 主路径，看到的都是全 `public` 门面，不必碰这些内部层。

### 1.3 命名空间

公开面全在 `namespace QuickExcel`：入口类 `Excel`、选项 `ExportOptions`/`ImportOptions`/`TemplateOptions`、结果 `ImportResult<T>`/`ImportError`、图片 `ExcelImage`/`ImagePlaceholder`、特性 `ExcelColumnAttribute` 等六个、枚举 `ExcelType`/`StringStorageMode`/`ExcelImageFormat`/`ImportErrorKind`/`ExcelAlign`，以及多表容器 `ExcelSheet`（定义在 `Export/SheetBuilder.cs`）。一行 `using QuickExcel;` 就够。

## 2. 列模型与数据源

`Excel.SaveAs` 先过本库 `SheetBuilder.Build` 归一。QuickWord / QuickPdf 各自带一份同构的 `SheetBuilder`（互不引用），接受的数据源形态完全一致，只是多表容器分别叫 `WordSheet` / `PdfSheet`。理解这一节等于理解三库共同的输入契约。

### 2.1 五种数据源

| 源 | 列怎么定 | 备注 |
| --- | --- | --- |
| POCO `IEnumerable<T>` | 扫 `T` 的属性/字段，按列特性建表 | 首选；见 §2.2 特性 |
| `IEnumerable<IDictionary<string, object>>` | **取第一行的键集合**为列，顺序即键序 | 与 MiniExcel 同约定，各行键要保持一致 |
| `IEnumerable<ExpandoObject>` | 同上（`ExpandoObject` 就是字典） | `elem==typeof(object)` 时探第一行判断 |
| `DataTable`（或 `IEnumerable<DataRow>`） | 按 `DataColumns` 建列，`DBNull`→null | `Export/SheetBuilder.cs:FromDataTable` |
| `IEnumerable<ExcelSheet>` / 单个 `ExcelSheet` | 每个 `ExcelSheet` 一表，各自带源 | 多 Sheet 输出；见 §2.3 |

空字典序列（拿不到键）会抛 `ArgumentException("Cannot infer columns...")` —— 这时改用 POCO 或显式给一行。

### 2.2 六个列特性

全部定义在 `Attributes/`，`AllowMultiple=false`、`Inherited=true`，可打在属性或字段上（`ExcelSheet` 打在类型上）。

| 特性 | 关键成员（默认值） | 作用 |
| --- | --- | --- |
| `ExcelColumnAttribute` | `Name`(=成员名)、`Aliases`(导入接受的额外表头，忽略大小写与空白)、`Order`(1000，小者靠前，同值保持声明序)、`Width`(-1=自动)、`Format`、`Required`(默认 false，与 DataAnnotations `[Required]` **取并集**)、`ColumnIndex`(-1=按名匹配)、`AutoTrim`(true)、`Bold`(false，整列含表头加粗)、`Align`(`ExcelAlign.Default`，水平对齐 Left/Center/Right) | 重命名 / 排序 / 定宽 / 定位 / 别名 / 逐列样式的主开关 |
| `ExcelColumnWidthAttribute` | `Width`（构造参数，Excel 字符单位） | 只设导出列宽 |
| `ExcelFormatAttribute` | `FormatString`（构造参数，如 `"#,##0.00"`、`"yyyy-MM-dd HH:mm"`） | 数字/日期自定义格式，写进 `xl/styles.xml`（自定义 numFmtId 从 164 起排） |
| `ExcelIgnoreAttribute` | 无 | 属性/字段既不导出也不导入 |
| `ExcelImageAttribute` | `Width`(0=原始)、`Height`(0=原始)、`AutoFitRow`(true) | 把成员标成图片列；见 §5 |
| `ExcelSheetAttribute` | `Name`（构造参数） | 类型未显式给表名时的 Sheet 名 |

`ExcelColumn.Format` 是 `ExcelFormat` 的简写，二者等价。

### 2.3 多 Sheet：`ExcelSheet`

```csharp
public sealed class ExcelSheet
{
    public string Name { get; }   // 空则回退 "Sheet{i}"
    public object Rows { get; }   // 上面任一"单表"数据源
    public ExcelSheet(string name, object rows);
}
```

传给 `SaveAs` 时，第 1 张表若 `Name` 为空用 `Sheet1`、`Sheet2`…… 编号；`XlsxWriter.UniqueSheetName` 再去重防同名。

### 2.4 列元数据缓存

POCO 的属性访问器与构造函数在首行一次性编译成表达式树并缓存（`Internal/Metadata/TypeMetadata.cs`、`ValueResolver`），反射开销只发生在第一次访问某类型。目标类型需 `class + new()`；带参构造走 `Activator` 回退（见 §7）。

## 3. 导出

### 3.1 入口方法

| 方法 | 签名要点 |
| --- | --- |
| `Excel.SaveAs(string path, object value, ExportOptions? = null)` | 覆盖写；自动建父目录 |
| `Excel.SaveAs(Stream stream, object value, ExportOptions? = null)` | 写完整包进流 |
| `Excel.SaveAsAsync(...)`（path / stream 两重载） | 真异步（`useAsync: true` 的 `FileStream` + `await`） |
| `Excel.GetAsByteArray(object value, ExportOptions? = null)` | 内存产物 |

`options` 传 null 即 `new ExportOptions()` 全默认。

### 3.2 `ExportOptions` 逐字段

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `SheetName` | `"Sheet1"` | 单表源且未走 `ExcelSheet` 时的表名 |
| `PrintHeader` | `true` | 是否写表头行 |
| `BoldHeader` | `false` | 表头加粗（走 styles 的 bold 位） |
| `FreezeHeaderRow` | `false` | 冻结首行（`sheetView` 的 pane） |
| `StringStorage` | `Inline` | `Inline`=字符串直接写进单元格（O(1) 内存）；`Shared`=内部化到 `sharedStrings.xml`（重复数据文件更小） |
| `CompressionLevel` | `Fastest` | zip 压缩档；`NoCompression` 最快、`MaximalCompress` 最小 |
| `BufferSize` | `1<<20`（1 MiB） | 写流缓冲 |

`Clone()` 是 `MemberwiseClone` 的浅拷贝，方便改一两个字段而不重建默认。

### 3.3 值怎么序列化

`Internal/OpenXml/ValueSerializer.cs` 把 CLR 值折成"类别 + 不变字符串"。要点：

- 数值全走 `InvariantCulture`（`float`→`G9`、`double`→`G17`、`decimal`→`InvariantCulture`）。
- `DateTime`：零点视为纯日期（`Date` 类别，套用 `NumFmtDateShort`=14），带时分秒走 `Time` 类别（`NumFmtDateTime`=22）；都写成 **OADate 序列数字** + 样式，而非文本。`DateTimeOffset` 转 UTC 后同理，`DateOnly`/`TimeOnly`/`TimeSpan` 也落序列数。
- `Guid`/`Uri`/`Enum`/其它对象 → 文本（`ToString()`）。
- `ExcelImage` → 空单元格 + 走图片通道（§5）。
- 裸 `byte[]`（未标图片语义）→ **Base64 文本**。想当图片导出就标 `[ExcelImage]`。

列宽：`Width>0` 时按 Excel 字符单位写 `<cols>`；负值不写，交给 Excel 自动。

### 3.4 单元格样式（逐列）

导出的样式只有三个维度，`xl/styles.xml` 里按 `(numFmt, bold, align)` 内部化成一个 `xf` 索引，热路径逐格只是一次字典命中（`Internal/OpenXml/Stylesheet.cs`）。可控项：

- **加粗**：`ExportOptions.BoldHeader=true` 粗化整行表头；`[ExcelColumn(Bold=true)]` 粗化该列（表头与正文都跟）。表头那格的实际粗体位是 `BoldHeader || Bold` 的并。
- **水平对齐**：`[ExcelColumn(Align=ExcelAlign.Left|Center|Right)]`，默认 `Default` 交给 Excel 自己（数字右、文本左）。表头与正文同享该列的 `Align`。
- **数字/日期格式**：`[ExcelFormat]` 或 `ExcelColumn.Format`（见 §3.3），写进 numFmt。

**边界（今天做不到，别误当有）**：样式是**逐列**的，不是逐格——同一列里无法只标红某一行；**填充底色、字体颜色/字号/字体名、边框、垂直对齐、自动换行**都不在公开面上（`BuildXf` 里 `fillId`/`borderId` 恒为 `0`，字体硬编码 Calibri 11 两档）。按语义给错误格标色只能在导入侧靠 `ImportError.CellReference` 回传、由上层自行着色，导出管线本身不产色。

## 4. 导入与验证

两条读路径，取舍只看**要不要验证**：

| | 入口 | 返回 | 验证 | 用途 |
| --- | --- | --- | --- | --- |
| 只读流式 | `Excel.Query` / `Excel.Query<T>` | `IEnumerable<字典>` 或 `IEnumerable<T>` | 无 | 高吞吐、百万行 |
| 读+验证 | `Excel.Import<T>` / `ImportAsync<T>` | `ImportResult<T>` | DataAnnotations + IValidatableObject | 需要错误报告 |

另有 `Excel.GetSheetNames(string path)` 按文档序返回工作表名，用于先探结构。

### 4.1 `Query`

```csharp
// 动态字典：默认把首行当表头，键=表头文本
IEnumerable<IDictionary<string, object?>> rows = Excel.Query(path);
// useHeaderRow: false 时键回退列字母（A/B/C…）
var raw = Excel.Query(path, useHeaderRow: false);
// 强类型：走 ValueConverter 逐格转换，不验证
IEnumerable<Employee> emps = Excel.Query<Employee>(path);
```

流式：`yield return` 逐行产出，文件流在读枚举期间持有。非 `Seekable` 的流传入会被整体拷进 `MemoryStream`（`Excel.cs:EnsureSeekable`）。

### 4.2 `Import<T>` 与 `ImportResult<T>`

`Import<T>` **从不因数据错误抛异常**（除非 `ThrowOnError=true`）：整册读完，一次返回。

```csharp
public sealed class ImportResult<T>
{
    public List<T> Data { get; }                 // 干净行
    public List<ImportError> Errors { get; }     // 每个失败格一条
    public List<string> TemplateErrors { get; }  // 表形问题（如必填列整列缺失）
    public Exception? Exception { get; internal set; } // 整册级失败（坏包/不可读）
    public int TotalRows { get; internal set; }  // 不含表头
    public bool HasErrors => Errors.Count > 0 || TemplateErrors.Count > 0 || Exception is not null;
    public IEnumerable<int> FailedRowNumbers();  // 去重后的失败行号
}
```

一条 `ImportError`：

```csharp
public sealed class ImportError
{
    public int RowNumber; public string FieldName; public string Header;
    public int? ColumnIndex;      // 定位不到时 null
    public string? CellReference; // Excel 风格 "AB12"，取不到时 null
    public string Message;
    public ImportErrorKind Kind;  // Conversion | Validation | MissingColumn
}
```

**关键设计：转换错误与验证错误合并不短路。** 同一行既有毒日期又缺必填，两条都会进 `Errors`，不是一条压一条。这就是相对"首错即抛"库的核心差异，也是 `RowNumber/CellReference/Kind` 三元组存在的意义 —— 让上层能把错误直接标回表格里。

`ImportErrorKind` 三值：`Conversion`（格文本转不成目标 CLR 类型）、`Validation`（DataAnnotations 或 `IValidatableObject` 规则失败）、`MissingColumn`（必填列整列不在表上，模板对不上）。

想退回"首错即抛"：`ThrowOnError=true`，抛 `QuickExcelImportException`，异常携带已收集的全部错误。

**必填列判定也是并集。** 整列缺失才记 `TemplateErrors`，口径是 `ExcelColumn.Required || [Required]` —— 有 `[ExcelColumn]` 却没写 `Required` 时不能把 DataAnnotations 的 `[Required]` 吃掉（属性默认值是 `false`，无法与"未指定"区分，所以只能取并集；要显式免除就在 DTO 上别加 `[Required]`）。

**空行按行判，不按册判。** 无值行（模板为画边框预留的 `<c s="…"/>` 样式空格）跳过、不计 `TotalRows`；只有该行确有浮动图片锚点才算数据行 —— 图片是浮在格上的对象，不能因为册子里有图就把整册空行都当数据读进来。

### 4.3 类型转换能吃什么

`Internal/ValueConverter.cs` 容忍 Nullable、enum、GUID、Excel 日期序列。文本到目标类型：整型族走各 `Parse`（decimal 用 **`CurrentCulture`**，其余用 `InvariantCulture`）、`bool` 用宽松 `ParseBool`、`Guid`/`Uri` 直解、`DateTime` 先按序列数再按多种文本格式试、`TimeSpan` 支持"日.小数"序列。转换失败不崩，折成一条 `Conversion` 错误。

### 4.4 `ImportOptions` 逐字段

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `SheetName` | `null` | 读哪张表；null=第一张 |
| `HeaderRowIndex` | `0` | 表头所在 0-based 行；`-1`=自动探测首个非空行 |
| `MatchHeadersByName` | `true` | false 时按 `ExcelColumn.ColumnIndex` 位置匹配，忽略表头文本 |
| `ThrowOnError` | `false` | 首错即抛（收集进异常），否则回填 `Errors` |
| `MaxRows` | `0` | 读满 N 数据行即停；0=不限 |
| `TrimStrings` | `true` | 验证前对字符串格去首尾空白（与成员级 `AutoTrim` 配合） |
| `IncludeInvalidRowsInData` | `false` | true 时失败行也（部分填充地）进 `Data` |
| `FillMergedCells` | `true` | 🔗 合并单元格回填，见下 |

**合并单元格**：OOXML 只把合并块的值存一份在左上角格。`FillMergedCells=true`（默认）在行流过时把该值灌回块内其余空格，于是分组表头、纵向合并的类别列都能逐行取到。关掉可省一次合并扫描（`Internal/OpenXml/MergeCellScanner.cs`），纯性能考量，非合并文件关掉无副作用。

## 5. 图片

### 5.1 `ExcelImage`

`ExcelImage` 包原始字节 + 探测出的格式 + 像素尺寸，供写手建正确尺寸的 drawing 锚。构造/工厂：`new ExcelImage(byte[])`、`FromFile`/`FromFileAsync`、`FromStream`/`FromStreamAsync`，`byte[]` 可隐式转。

格式靠**魔数嗅探**（`DetectFormat`）：PNG / JPEG / GIF / BMP / EMF / WMF / TIFF，识别不出发 `Unknown`（扩展名 `bin`、类型 `application/octet-stream`）。尺寸从头部读（PNG IHDR、GIF LSD、BMP DIB、JPEG 走 marker 链找 SOFn），失败回退 `(0,0)`，由 `ExcelImageAttribute` 显式尺寸兜底。

### 5.2 导出

成员类型是 `ExcelImage`（或 `byte[]` 且标 `[ExcelImage]`）时，值不落单元格文本，而是经 `DrawingWriter` 写一个浮动图片锚到该格；相同字节经 `MediaCollector` 内容哈希**自动去重**（`xl/media` 只存一份）。`[ExcelImage(Width, Height)]` 定显示尺寸，`AutoFitRow=true`（默认）抬高行高/列宽让图片放得下。

### 5.3 导入

`Import<T>`/`Query<T>` 按 `(行,列)` 锚点把图片字节回填到 `ExcelImage` 成员。**WPS 兼容**：WPS 的"单元格内图片"是 `=_xlfn.DISPIMG("ID_x",1)` 公式 + `xl/cellimages.xml`（ID→media 映射），`CellImageReader` 两侧都读，因此 WPS 产的单元格图片也能导回（`Internal/OpenXml/CellImageReader.cs`）。

## 6. 模板

### 6.1 三种占位符

模板就是一个普通 xlsx，单元格文本里写占位符（`Internal/OpenXml/TemplateEngine.cs` 渲染）：

| 语法 | 含义 |
| --- | --- |
| `{{Name}}` | 标量替换，支持 `{{a.b}}` 嵌套属性。整格仅此一个占位符且值是数字/布尔时**保留原生类型与模板样式** |
| `{{items.Field}}` | 行展开：`items` 解析为集合时，模板行按每个元素复制一份（样式保留；空集合删除该行） |
| `{{@field}}` | 图片占位：值是 `ExcelImage`/`byte[]`/`ImagePlaceholder` 时锚定浮动图片 |

模板其余部件（样式、主题、其他 Sheet、已有 Logo）原样保留。

### 6.2 `TemplateOptions`

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `SheetName` | `null` | 渲染模板里哪张表；null=第一张 |
| `DateTimeFormat` | `"yyyy-MM-dd HH:mm:ss"` | 文本替换 `DateTime` 的格式；特殊值 `"serial"` 改写 OADate 数并保留模板日期样式 |
| `DateOnlyFormat` | `"yyyy-MM-dd"` | 同上，`DateOnly` |

### 6.3 入口方法

```csharp
Excel.SaveAsByTemplate(outputPath, templatePath, value, options);   // value: 字典或 POCO
Excel.SaveAsByTemplate(outputStream, templateStream, value, options); // 模板流须可 seek
Excel.SaveAsByTemplate(outputStream, templatePath, value, options);
await Excel.SaveAsByTemplateAsync(...);                              // 两重载
byte[] b = Excel.GetAsByteArrayByTemplate(templatePath, value, options);
```

模板渲染走 DOM 路径（模板体量小，可接受）：10 万行约 1.5 s（README 性能表）。

### 6.4 模板导入

数据文件带标题行/备注行等前置内容时，以模板声明的表头布局为准：

```csharp
// 模板第 2 行（HeaderRowIndex=1）是"工号/姓名/薪资…"表头
var result = Excel.ImportByTemplate<Employee>("data.xlsx", "template.xlsx",
    new ImportOptions { HeaderRowIndex = 1 });
```

表头从模板表读、用来映射数据文件的列，因此数据文件本身可以不带或乱序其表头行。

## 7. 边界与坑

- **仅 `.xlsx`**：不支持 `.xls` 二进制；`ExcelType.Csv` 是占位，无实现。
- **模板行偏移不平移引用**：集合行展开后，`sheetData` 之后的部件（`mergeCells`/`dataValidation`/`hyperlink`…）原样保留，**展开导致的行号变化不会平移这些部件里的行引用**。若模板把合并区放在展开区下方，会错位。模板里已有的公式/shared formula 不重算。
- **字典导出以第一行键集合为列**：各行键要一致（与 MiniExcel 同）。
- **目标类型需 `class + new()`**：带参构造走 `Activator` 回退。
- **`Import<T>` 读枚举期间持有文件流**：惰性 `Query` 要尽快消费完或及时 dispose。
- **不可 seek 的流**读入时整体缓冲进 `MemoryStream`（大流注意）。
- 导出不生成合并区（只在导入侧消化合并区）。
- 样式仅逐列的加粗 + 水平对齐 + numFmt（§3.4）；逐格样式、填充色、字体颜色/字号、边框、垂直对齐都不支持。

## 8. API 总表

| 分类 | 成员 |
| --- | --- |
| 导出 | `Excel.SaveAs`（path/stream）· `SaveAsAsync` · `GetAsByteArray` |
| 只读 | `Excel.Query` · `Query<T>` · `GetSheetNames` |
| 导入+验证 | `Excel.Import<T>` · `ImportAsync<T>` · `ImportByTemplate<T>` |
| 模板 | `Excel.SaveAsByTemplate`（3 重载） · `SaveAsByTemplateAsync` · `GetAsByteArrayByTemplate` |
| 选项 | `ExportOptions` · `ImportOptions` · `TemplateOptions` |
| 结果 | `ImportResult<T>` · `ImportError` · `ImportErrorKind` |
| 数据源 | `ExcelSheet` |
| 图片 | `ExcelImage` · `ImagePlaceholder` · `ExcelImageFormat` |
| 特性 | `ExcelColumn` · `ExcelColumnWidth` · `ExcelFormat` · `ExcelIgnore` · `ExcelImage` · `ExcelSheet` |
| 枚举/异常 | `ExcelType` · `StringStorageMode` · `ExcelAlign` · `QuickExcelTemplateException` |
