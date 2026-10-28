# QuickWord 使用文档

**QuickWord** 是面向 .NET 10 的 Word（`.docx`）库：**表格数据导出 + 文档读回**双向都有，另能在**不改版式**的前提下填别人排好的模板（`{{标签}}` 与图片占位）。**零第三方依赖**（纯 BCL，不牵入 QuickExcel），写侧逐行流式序列化，内存 O(列数)、与行数无关。本文件同时也是 NuGet 包内 README。

> 与 QuickExcel / QuickPdf 各自带一套**同名同形但互不相认**的列特性（`[WordColumn]` 一族）——POCO 上写的 `[ExcelColumn]` 对 QuickWord 无效，要两边生效就各写各的。

## 安装

```bash
dotnet add package QuickWord --prerelease
```

或 csproj 写死版本号：

```xml
<PackageReference Include="QuickWord" Version="0.1.0-beta.5" />
```

命名空间 `QuickWord`，一行 `using QuickWord;` 就够。预发布系列装不到时先清缓存：`dotnet nuget locals http-cache --clear`。

## 快速上手

```csharp
using QuickWord;

// 表格导出
Word.SaveAs("report.docx", employeeList, new WordOptions { Title = "员工花名册" });

// 散文文档：一行一段
Word.SaveLines("notice.docx", File.ReadLines("notice.txt"));

// 读回
var doc = Word.Read("report.docx");
foreach (var p in doc.Paragraphs) Console.WriteLine($"{p.StyleName ?? "-"}\t{p.Text}");
var rows = doc.Tables.First().Query();          // IEnumerable<IDictionary<string, object?>>

// 模板填充（不改版式）
await Word.SaveAsByTemplateAsync(outputStream, "Templates/合同.docx", new Dictionary<string, object>
{
    ["姓名"] = "张萌",
    ["日期"] = DateTime.Now,
    ["签名"] = ImagePlaceholder.FromFile("sign.png", width: 70, height: 30),
});
```

---

## 1. 数据导出（表格）

### 入口

| 方法 | 说明 |
| --- | --- |
| `Word.SaveAs(string path, object value, WordOptions? = null)` | 覆盖写；自动建父目录 |
| `Word.SaveAs(Stream stream, object value, WordOptions? = null)` | 写完整 .docx 进流 |
| `Word.SaveAsAsync(string path, …, CancellationToken = default)` | 异步版 |
| `Word.GetAsByteArray(object value, WordOptions? = null)` | 内存产物 |

### 数据源

`value` 与 QuickExcel 同构的五种：POCO `IEnumerable<T>`、`IEnumerable<IDictionary<string, object>>` / `ExpandoObject`（列取第一行键）、`DataTable`、其他 `IEnumerable` 反射探测。多表 = `IEnumerable<WordSheet>`（`new WordSheet("员工", rows)`），每张表前自动加 `Heading1` 小标题。

### 列特性

| 特性 | 作用 |
| --- | --- |
| `[WordColumn("列名")]` | `Name` 改列名；`Order` 排序；`Width` 列宽（Excel 字符单位，最小 0.5"）；`Format` 数字格式 |
| `[WordFormat("N2")]` | 单独设格式 |
| `[WordColumnWidth(20)]` | 单独设列宽 |
| `[WordIgnore]` | 排除属性 |

### `WordOptions`

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `Title` | `null` | 表格上方的文档大标题（居中 `DocTitle` 样式） |
| `PrintHeader` | `true` | 带底纹、加粗的表头行 |
| `HeaderFill` | `"EEEEEE"` | 表头底纹（RRGGBB） |
| `FontSizePt` | `10.5` | 单元格字号（Word 五号） |
| `BorderColor` | `"999999"` | 表格线颜色；`"000000"` 强边框 |
| `FooterText` | `null` | 页脚文本，每页居中重复（9pt 灰）；`{PAGE}` / `{NUMPAGES}` 渲染成 Word **活页码域**。null = 不发页脚部件 |

值转文本尊重 `[WordFormat]`；单元格内 `\n` 渲染成真实换行（一格多段）；A4 纵向、1 英寸页边距，未设宽的列均分剩余宽度、总和超预算按比例缩。

### 散文文档：`SaveLines`

给人读的文档（通知、试卷、问答记录）用：

```csharp
Word.SaveLines("out.docx", new[] { "第一段", "", "第三段" });   // 空串 = 空段（版面留白）
Word.GetLinesAsByteArray(lines, options);
```

一行一个 `<w:p>`，`WordOptions.Title` 仍是顶部大标题段；读回来就是 §2 的段落模型，写读可往返。

## 2. 文档读回

```csharp
WordDocument doc = Word.Read("in.docx");                 // 不可寻址的流（上传管道）会先整段缓冲
WordDocument doc2 = await Word.ReadAsync("in.docx");
```

`WordReadOptions.IncludeEmptyParagraphs`（默认 `false`）：默认丢掉空白行；要还原原始版面再打开。

**块模型：**

| 成员 | 含义 |
| --- | --- |
| `doc.Blocks` | 正文顺序（`WordBlockKind.Paragraph` / `.Table`） |
| `doc.Paragraphs` / `doc.Tables` | 同序过滤视图 |
| `WordParagraph.Text` | 文本拼接（`w:tab`→`\t`、`w:br`→`\n`；域代码与修订删除不算文本） |
| `WordParagraph.StyleName` | 原样样式 id；无样式 null |
| `WordParagraph.IsListItem` | 项目符号/编号段落 |
| `WordTable.Rows` | 逐行逐格文本；格内多段用 `\n` 连接（写侧 `\n` 的逆运算，写读原样往返） |
| `WordTable.Query(useHeaderRow = true)` | `IEnumerable<IDictionary<string, object?>>`，与 QuickExcel `Query` 同形状；空表头/重名表头自动补 `ColumnN` / `名称(2)` |

**两条硬约束**：

- **样式 id 不是契约**：真实 Word 文档的样式 id 是本地化生成的（`1`、`a9` 这类），只有 QuickWord 自己写出的文件才保证 `DocTitle`/`Heading1`。判定自制文档要看文本内容，别看 `StyleName`。
- 坏文件只有一种异常：缺 `PK` 头、缺 `document.xml`、zip 半截、XML 不完整，全部收敛成 `InvalidDataException`（真实原因在 `InnerException`）。

## 3. 模板填充（不改版式）

给一份**别人排好的 .docx**（正文写 `{{姓名}}`、签字位写 `{{签名}}`）填值出稿：

| 方法 | 说明 |
| --- | --- |
| `Word.SaveAsByTemplate(outputPath, templatePath, value, TemplateOptions?)` | 磁盘进出 |
| `Word.SaveAsByTemplate(Stream output, string templatePath, …)` | 模板来自文件，产物写流 |
| `Word.SaveAsByTemplate(Stream output, Stream template, …)` | 模板也是流（内部先缓冲） |
| `Word.SaveAsByTemplateAsync(…)`（路径版 / 流版） | 异步版 |
| `Word.GetAsByteArrayByTemplate(templatePath, value, TemplateOptions?)` | 内存产物 |

`value`：`IDictionary<string, object>`（含 `ExpandoObject`）、任意非泛型 `IDictionary`、或任意对象（取 public 实例属性）。

### 语法

| 占位符 | 含义 |
| --- | --- |
| `{{姓名}}` | 换成值；落回它所属的 run，字体字号下划线**原样保留** |
| `{{签名}}` | 值给 `ImagePlaceholder`（或 `WordImage` / `byte[]` 隐式转）→ 原位内联图；不给尺寸用图片原生像素尺寸 |

- **标签匹配粒度是一段之内**：Word 把 `{{标签}}` 拆成多个 run（还插拼写检查节点）也能正常缝合；跨段落的标签不认。
- **未提供的键保留原样**：`{{Phone}}` 没给值就原样出现在成品里（宁可让人看见，不静默吞掉正文）。
- 值转文本：`null`→空串；`DateTime`/`DateTimeOffset` 按 `TemplateOptions.DateTimeFormat`（默认 `yyyy-MM-dd HH:mm:ss`）、`DateOnly` 按 `DateOnlyFormat`（默认 `yyyy-MM-dd`）；其余 `IFormattable` 按当前文化。要中文日期在业务层拼好字符串再传。
- **只碰 `document.xml` 与页眉页脚**：`styles.xml`/`theme`/`fontTable`/`docProps` 等其余部件**逐字节照抄**——手工排版的法律文书不会跳版式。
- 打包避让：媒体号、关系号、图形 id 都从模板已有的最大号往后数，不覆盖模板自带图；同一张图（字节相同）填多位置只存一份；`[Content_Types].xml` 只补缺的扩展名声明。
- 幂等：产物再当模板渲染一遍是空操作。

### 模板的边界

- **只有替换，没有控制流**：没有行循环、条件段、区块复制。要按数据行数长出来的表格用 `SaveAs` 现排。
- 键名精确匹配（`{{ Name }}` 两侧空格被吃掉、等价 `{{Name}}`；大小写不同不填）。
- 批注、脚注/尾注正文里的标签不填。
- 图片只有内联（inline），不做浮于文字上方（anchor）；只嗅 PNG/JPEG/GIF/BMP 四种魔数。

## 4. 边界与常见问题

- **只吃 `.docx`（OOXML）**：老的 `.doc` 二进制解析不了，抛 `InvalidDataException`。
- **版式不进模型**：字体、颜色、对齐、分页都不读，只留文本 + 样式 id + 是否列表项 + 表格结构。
- **合并格不展开**：`gridSpan`/`vMerge` 不补齐，带合并的表每行格数不等（`Query()` 缺失位置为 null）。
- **嵌套表压平成文本**：格内的表按"行 `\n`、格 `\t`"并进单元格文本，不会出现在 `doc.Tables`。
- **文本框并入所在块**；页眉页脚、脚注尾注、批注不在读取模型里。
- **表格导出不内嵌图片**：`SaveAs` 那条线没有图片概念（`byte[]` 按 base64 文本渲染）；图片入口在模板侧。
- **底纹/边框是固定档**：没有逐格样式或斑马条；跨页不重复表头；样式集只有三颗段落样式。

## 相关文档

- [QuickExcel 使用文档](../../excel/docs/quickexcel-usage.md)（输入契约的一般形态）
- [QuickPdf 使用文档](../../pdf/docs/quickpdf-usage.md)
- [QuickSql（ORM）使用文档](../../sql/docs/quicksql/README.md)

## License

MIT
