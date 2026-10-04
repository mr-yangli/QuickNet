# FastWord 使用说明

> 面向使用者的一份操作文档：怎么集成、怎么用、生成的 .docx 长什么样、读回来的形状是什么。FastWord 导出与读取双向都有（`Word.SaveAs` / `Word.Read`），列模型/数据源契约与 FastExcel **同构但完全独立**（互不引用），输入契约的一般形态见 `../../excel/docs/fastexcel-usage.md` §2，本库自己的特性名看 §1 与 §5。
>
> 本文里出现的每一个 API 名字、每一个默认值都在工作树的 `word/src/FastWord/` 下读到过真实定义。

## 0. 这份文档是什么，现在真实的状态是什么

三段：§1 集成、§2 使用、§3 版式与读取模型；§4 是边界与坑，§5 是一张 API 总表。

真实状态（三句，句句可核）：

1. **写读双向，形状对称**。写：`Word` 静态类四颗导出方法（`SaveAs`/`SaveAsAsync`/`GetAsByteArray`）+ 三颗读取方法（`Read(path)`/`Read(Stream)`/`ReadAsync`），内部分别是 `Internal/DocxWriter.cs` 与 `Internal/DocxReader.cs`。写手先用本库 `Internal/SheetBuilder.cs` 归一数据源（与 FastExcel 同名同形、各自独立的实现），然后**逐行**把 `<w:tr>` 序列化进 `word/document.xml` —— 内存 O(列数)，与行数无关。读手用 `XmlReader` 顺着 `word/document.xml` 走，每个块自己吃掉子树，产出一个块模型（段落 + 表格）。
2. **零依赖**。`FastWord.csproj` 既没有 `ProjectReference` 也没有 `PackageReference`——纯 BCL（`System.IO.Compression` + `System.Xml`）。中文能正常显示是因为它写的是 WordprocessingML（文本 + `w:eastAsia` 字体声明），排版交给打开它的 Word/WPS，不涉字体嵌入（那是 FastPdf 的活）。
3. **验证证据**：`word/tests/FastWord.Tests` 14 个单测 = 5 导出（docx 结构 round-trip / 多表 / 字典源 / `[Word*]` 特性驱动列模型）+ 9 读取（**写→读原样往返**、`Query()` 字典形状、标题与 `Heading1` 样式名、空分隔段开关、`\t`/`\n`/列表标记/域代码跳过、单元格内嵌套表压平、空表头与重名表头去重、非 docx 字节报可读错、不可寻址流缓冲）。此外用本机 Word 16 COM 现造了一份真 .docx（中文标题 + 项目符号 + 软回车 + `&`/`<` + 3×3 表 + 多行格）读回来核对，见 §3.5。

## 1. 怎么集成

| 项 | 值 | 出处 |
| --- | --- | --- |
| TFM | `net10.0`；`LangVersion preview`、`Nullable enable`、`ImplicitUsings enable` | `word/Directory.Build.props` |
| XML 文档 | `GenerateDocumentationFile` | `FastWord.csproj` |
| NuGet 包 | 无（src 零包；CPM `word/Directory.Packages.props` 只钉测试四件套） | `word/Directory.Packages.props` |
| 引用 | 无：`ProjectReference` / `PackageReference` 均无（纯 BCL） | `FastWord.csproj` |

FastWord 自带全套列特性（`[WordColumn]` 一族），不需要、也不会牵入 FastExcel：

```xml
<ProjectReference Include="..\..\word\src\FastWord\FastWord.csproj" />
```

命名空间是 `FastWord`——入口 `Word` 类、选项 `WordOptions`、多表容器 `WordSheet`、列特性 `WordColumn`/`WordFormat`/`WordIgnore`/`WordColumnWidth` 全在其中。注意特性类型与 FastExcel 的 `[Excel*]` 一族**同名同形但互不相认**：POCO 上写的 `[ExcelColumn]` 对 FastWord 无效（会按属性名默认导出），要两边生效就各写各的。

## 2. 使用

### 2.1 写入口方法

| 方法 | 签名要点 |
| --- | --- |
| `Word.SaveAs(string path, object value, WordOptions? = null)` | 覆盖写；自动建父目录 |
| `Word.SaveAs(Stream stream, object value, WordOptions? = null)` | 写完整 .docx 进流 |
| `Word.SaveAsAsync(string path, object value, WordOptions? = null, CancellationToken = default)` | `Task.Run` 包同步版 |
| `Word.GetAsByteArray(object value, WordOptions? = null)` | 内存产物 |
| `Word.SaveLines(string path, IEnumerable<string> lines, WordOptions? = null)` | **散文文档**：一行一段，无表格（§2.4） |
| `Word.SaveLines(Stream stream, IEnumerable<string> lines, WordOptions? = null)` | 同上，写进流 |
| `Word.GetLinesAsByteArray(IEnumerable<string> lines, WordOptions? = null)` | 同上，返回字节 |

`value` 接受与 FastExcel 同构的五种数据源；给 `IEnumerable<WordSheet>` 就是**多表 = 多个表格顺排**（见 §3.3）。

```csharp
using FastWord;

Word.SaveAs("report.docx", employeeList, new WordOptions { Title = "员工花名册" });
```

### 2.2 `WordOptions` 逐字段

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `Title` | `null` | 表格上方的文档大标题；null 省略 |
| `PrintHeader` | `true` | 是否写带底纹、加粗的表头行 |
| `HeaderFill` | `"EEEEEE"` | 表头单元格底纹（RRGGBB 十六进制） |
| `FontSizePt` | `10.5`（Word 五号） | 单元格字号；内部转半磅 `sz=2×pt` |
| `BorderColor` | `"999999"` | 表格线颜色；`"000000"` 为强边框 |
| `FooterText` | `null` | 页脚文本，每页居中重复（9 pt 灰）；`{PAGE}` / `{NUMPAGES}` 渲染成 Word 活页码域（打开/打印时由宿主重算）。null = 完全不发页脚部件 |

`Clone()` 浅拷贝，改单字段用。

### 2.3 散文档入口（`SaveLines`）

表状数据用 `SaveAs`，**给人读的文档**（一整张试卷、一份通知、问答记录）用 `SaveLines`：一行一个 `<w:p>`，`WordOptions.Title` 仍是顶部 `DocTitle` 段，空字符串写成空段（版面留白）。不写表格、不写样式表以外的东西，`FooterText` 与 `FontSizePt` 照常生效。读回来正是 §2.4 的段落模型，一家之言可往返（`SaveLines_Writes_Prose_That_Reads_Back_Line_For_Line` 钉住）。

### 2.4 读取入口

| 方法 | 签名要点 |
| --- | --- |
| `Word.Read(string path, WordReadOptions? = null)` | 读磁盘上的 .docx |
| `Word.Read(Stream stream, WordReadOptions? = null)` | 读流；**不可寻址的流（上传管道）会先整段缓冲** |
| `Word.ReadAsync(string path, WordReadOptions? = null, CancellationToken = default)` | `Task.Run` 包同步版 |

```csharp
using FastWord;

var doc = Word.Read("试卷.docx");
foreach (var p in doc.Paragraphs) Console.WriteLine($"{p.StyleName ?? "-"}\t{p.Text}");
var rows = doc.Tables.First().Query();          // 与 FastExcel 同形状：IDictionary<string, object?>
```

`WordReadOptions.IncludeEmptyParagraphs`（默认 `false`）：写手在每张表后补的空段、以及人手排的空白行都不算内容，默认丢掉；要还原原始版面再打开。`Clone()` 同 `WordOptions`。

读不动的包只有一种异常：缺 `PK` 头、缺 `word/document.xml`、zip 半截（central directory 坏）、`document.xml` 的 XML 不完整，全部收敛成 `InvalidDataException`，消息是人能读的一句英文（真实原因在 `InnerException`）——调用方 catch 这一个类型就够，不必认识 `ZipArchiveException`/`XmlException`。

## 3. 版式与读取模型（写出去长什么样、读回来是什么形状）

### 3.1 包结构（固定四+一个部件，页脚可选加一）

`DocxWriter.Write` 用 `ZipArchive`（UTF-8 条目名）写出：

| 部件 | 内容 |
| --- | --- |
| `[Content_Types].xml` | 声明 document.xml + styles.xml 两颗 Override（有页脚时再加 footer1.xml 一颗） |
| `_rels/.rels` | 根关系到 `word/document.xml` |
| `word/_rels/document.xml.rels` | document 到 styles 的关系（有页脚时 rId2 → footer1.xml） |
| `word/styles.xml` | docDefaults + `Normal`/`Heading1`/`DocTitle` 三颗样式 |
| `word/footer1.xml` | 仅当 `FooterText` 非空；单段居中，`{PAGE}`/`{NUMPAGES}` 为 fldChar 域（begin→instrText→separate→缓存值→end） |
| `word/document.xml` | 正文，**流式**逐行写；`sectPr` 首位挂 `<w:footerReference w:type="default" r:id="rId2"/>`（仅当有页脚） |

`docDefaults` 的东亚字体声明为 `w:eastAsia="宋体"`、西文 `Calibri`，`lang` 钉 `zh-CN`；`Heading1` 是 `b + sz 28`，`DocTitle` 是居中 `b + sz 36`。

### 3.2 页面与列宽

A4 纵向、1 英寸页边距：`pgSz w=11906 h=16838`、`pgMar 1440`，可用内容宽 `ContentWidth = 11906 - 2×1440 = 9026` 缇（twips）。列最小宽 `MinColWidth = 720`（0.5"）。

列宽换算在 `ComputeWidths`：

- 列有显式 `Width>0`（Excel 字符单位）→ `max(720, round(Width×105 + 75))` 缇（即 Excel 字符宽 → 像素 `7w+5` → 像素×15 缇）。
- 未设宽的列均分剩余预算，每列至少 `720`。
- 总和超预算 → 按 `budget/total` 比例缩。表格 `tblLayout=fixed`，`tblW` 取各列宽之和。

### 3.3 标题与多表

- 有 `Title` → 一段 `DocTitle` 样式、居中。
- 多张表（`sheets.Count > 1`）→ 每张表前一段 `Heading1`，内容是 `sheet.Name`；单表不额外发标题段。
- 每张表后补一段空 `<w:p/>`（Word 要求表间/表后要有段落分隔）。

### 3.4 单元格文本与换行

值经本库 `Internal/DisplayFormatter.Of(v, formatString)` 转文本（尊重 `[WordFormat]`，回退不变文化默认）。表头文本取列的 `HeaderName`。

**`\n` 渲染成真实换行**：一格按 `\n` 拆成多段 `<w:p>`（每段前距 40/后距 40/`line=240 auto`），`\r` 被吃掉。XML 文本全部经本库 `Internal/XmlUtility.Escape` 转义（`&`/`<`/`>` 等安全往返）。表头单元格内的空行会补一个空格 run 防止塌陷。

### 3.5 读回来的块模型（`DocxReader`）

`word/document.xml` 用前向 `XmlReader` 走一遍，不建 DOM。正文里遇到的每颗 `w:p` / `w:tbl` 各自吃掉整棵子树，所以**单元格里的段落、嵌套表、文本框都不会漏进正文**（文本框的文字并进所属段落/单元格的文本里）。

| 成员 | 含义 |
| --- | --- |
| `WordDocument.Blocks` | 正文顺序（`WordBlockKind.Paragraph` / `.Table`） |
| `WordDocument.Paragraphs` / `.Tables` | 同序过滤视图 |
| `WordParagraph.Text` | `w:t` 文本拼接；`w:tab` → `\t`，`w:br`/`w:cr` → `\n`；`w:instrText`（域代码）与 `w:delText`（修订删除）不算文本 |
| `WordParagraph.StyleName` | 原样 `w:pStyle/w:val`，无样式则 null |
| `WordParagraph.IsListItem` | 带 `w:numPr` 即 true（项目符号/编号） |
| `WordTable.Rows` | 逐行逐格文本；格内多段用 `\n` 连接 —— 正是 §3.4 的逆运算，写→读原样往返 |
| `WordTable.Query(useHeaderRow = true)` | `IEnumerable<IDictionary<string, object?>>`，与 FastExcel `Query` 同形状；空表头/重名表头自动补 `ColumnN` / `名称(2)` |

真机核对（Word 16 COM 现造的 .docx，含中文标题、项目符号两条、软回车一段、`&`/`<`、3×3 表、格内多行）读回 6 个块：标题段 `StyleName="1"`、两条 `IsListItem=true` 且 `StyleName="a9"`、软回车变成 `第一行\n第二行（软回车）`、表格 3 行 3 列且第 3 列是 `多行A\nmultiline B`。

**由此得一条硬约束**：真实 Word 文档的样式 id 是本地化生成的（`1`、`2`、`a9` 这类），**不等于** FastWord 写手发出的 `DocTitle`/`Heading1`。所以拿 FastWord 读用户自制的 Word 文件时，判定要看文本本身（`题型：`、`A.`、`答案：`），别把 `StyleName` 当契约；只有自家写手的产物才保证那三颗样式 id。



## 4. 边界与坑

读取侧的边界（`Word.Read`）：

- **只吃 OOXML**：`.docx` 才是有效输入。老的 `.doc`（二进制 OLE）解析不了，读时按 zip 头挡掉并抛 `InvalidDataException`；调用方要按“文件不是 .docx”报给用户。
- **版式不进模型**：字体、颜色、对齐、缩进、分页都不读，只留文本 + 样式 id + 是否列表项 + 表格结构。要还原排版得自己后处理。
- **合并格不展开**：`gridSpan`/`vMerge` 不补齐占位，所以带合并的表每行格数会不等（`Rows[r].Count` 参差不齐），宽表用 `Query()` 时缺失位置为 null。真试卷表头常有合并，判定逻辑要接受变窄的行。
- **嵌套表压平成文本**：格内的表按“行用 `\n`、格用 `\t`”并进单元格文本（`Read_Nested_Table_Inside_A_Cell_Is_Flattened_And_Stays_Out_Of_The_Body` 钉住这个形状），不会成为 `WordDocument.Tables` 的一员。
- **文本框并入所在块**：`w:txbxContent` 里的文字一律拼进它所属的那一段（或那张表）的文本，不作为独立块出现。
- **只读正文那一个部件**：页眉页脚、脚注尾注、批注都不在 `WordDocument` 里（写手发的 `{PAGE}` 页脚域，读手看不见）。
- **样式 id 不是契约**：见 §3.5 末段——真实 Word 给的是 `1`/`a9` 这种本地化 id。
- **空段默认不进模型**：`IncludeEmptyParagraphs=false` 时空白行消失；往返比较时要显式打开。

导出侧的边界：

- **不导出图片**：FastWord 列模型里没有图片概念——`byte[]` 单元格按 base64 文本渲染。图片导出到 docx 是 v2 事项（读取侧同样只会拿到文字，图片没有 alt 之类可依赖）。
- **不做分页续表头**：分页交给 Word 引擎；跨页重复表头是 FastPdf 的能力，Word 侧没有（表头行也没标 `tblHeader`，跨页时不重绘）。页脚已有（`FooterText`），页眉没有。
- **底纹/边框是固定档**：`tblBorders` 六向 `single sz=4`、`HeaderFill` 单色，没有逐格样式或斑马条。
- **样式集很薄**：只发 docDefaults + 三颗段落样式，正文无字符级样式表；要精细排版得自己后处理。

## 5. API 总表

| 分类 | 成员 |
| --- | --- |
| 入口（写表） | `Word.SaveAs`（path/stream） · `Word.SaveAsAsync` · `Word.GetAsByteArray` |
| 入口（写散文） | `Word.SaveLines`（path/stream） · `Word.GetLinesAsByteArray` |
| 入口（读） | `Word.Read`（path/stream） · `Word.ReadAsync` |
| 选项（写） | `WordOptions`（`Title`/`PrintHeader`/`HeaderFill`/`FontSizePt`/`BorderColor`/`FooterText`） |
| 选项（读） | `WordReadOptions`（`IncludeEmptyParagraphs`） |
| 读取模型 | `WordDocument`（`Blocks`/`Paragraphs`/`Tables`） · `WordBlock`+`WordBlockKind` · `WordParagraph`（`Text`/`StyleName`/`IsListItem`） · `WordTable`（`Rows`/`ColumnCount`/`Query`） |
| 本库自带列模型 | 数据源五选一 · `WordSheet`（多表） · `WordColumn`/`WordFormat`/`WordIgnore`/`WordColumnWidth` 特性（与 FastExcel 的 `[Excel*]` 同形，但是独立类型、互不相认） |
