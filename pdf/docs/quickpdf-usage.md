# QuickPdf 使用说明

> 面向使用者的一份操作文档：怎么集成、怎么用、字体怎么解析、PDF 长什么样。QuickPdf **只做导出**（不提供 PDF 解析导入），列模型/数据源契约与 QuickExcel **同构但完全独立**（互不引用），输入契约的一般形态见 `../../excel/docs/quickexcel-usage.md` §2，本库自己的特性名看 §1 与 §7。
>
> 本文里出现的每一个 API 名字、每一个默认值都在工作树的 `pdf/src/QuickPdf/` 下读到过真实定义。

## 0. 这份文档是什么，现在真实的状态是什么

三段：§1 集成、§2 使用、§3 字体解析；§4 版式与分页、§5 文件结构；§6 是边界与坑，§7 是一张 API 总表。

真实状态（三句，句句可核）：

1. **单入口 + 整体字体嵌入**。`Pdf` 静态类四颗方法，内部 `PdfTableRenderer.Render`（`Internal/PdfTableRenderer.cs`）把数据源（经本库 `Internal/SheetBuilder.cs` 归一，与 QuickExcel 同名同形、各自独立）画成表格页。CJK 字体从**宿主机现找**、TrueType 表**整份嵌入**（`/FontFile2`，无子集化），以 Identity-H CID 方式寻址字形 —— 任何阅读器渲染一致，代价是单文件约 +15 MB 起。
2. **零依赖**。`QuickPdf.csproj` 既没有 `ProjectReference` 也没有 `PackageReference`；TTF 解析、列模型、CID 映射、ToUnicode、zip 流全是手写的 BCL 代码。
3. **验证证据**：`pdf/tests/QuickPdf.Tests` 6 个单测（xref 逐条对账 / 内容流算符 / 900 行分页续表头 / 横向 MediaBox / `[Pdf*]` 特性经 ToUnicode CMap 对账——重命名、Format 的逗号、Ignore 缺席 / `PdfSheet` 多表分节标题）；产物经 PyMuPDF 校验（结构零告警、Identity-H 字体注册、文本可抽取）并渲染截图核对中文显示。

## 1. 怎么集成

| 项 | 值 | 出处 |
| --- | --- | --- |
| TFM | `net10.0`；`LangVersion preview`、`Nullable enable`、`ImplicitUsings enable` | `pdf/Directory.Build.props` |
| XML 文档 | `GenerateDocumentationFile` | `QuickPdf.csproj` |
| NuGet 包 | 无（src 零包；CPM `pdf/Directory.Packages.props` 只钉测试四件套） | `pdf/Directory.Packages.props` |
| 引用 | 无：`ProjectReference` / `PackageReference` 均无（纯 BCL） | `QuickPdf.csproj` |

```xml
<ProjectReference Include="..\..\pdf\src\QuickPdf\QuickPdf.csproj" />
```

命名空间 `QuickPdf`（入口 `Pdf`、选项 `PdfOptions`、多表容器 `PdfSheet`、异常 `QuickPdfException`；列特性 `PdfColumn`/`PdfFormat`/`PdfIgnore`/`PdfColumnWidth` 全在其中）。特性类型与 QuickExcel 的 `[Excel*]` 一族**同形但互不相认**：POCO 上写的 `[ExcelColumn]` 对 QuickPdf 无效（会按属性名默认导出），要两边生效就各写各的。

## 2. 使用

### 2.1 入口方法

| 方法 | 签名要点 |
| --- | --- |
| `Pdf.SaveAs(string path, object value, PdfOptions? = null)` | `File.WriteAllBytes` 覆盖写；自动建父目录；`value` 为 null 抛 `ArgumentNullException` |
| `Pdf.SaveAs(Stream stream, object value, PdfOptions? = null)` | 先渲染成字节再整份写入流 |
| `Pdf.SaveAsAsync(string path, ...)` | `Task.Run` 包同步版 |
| `Pdf.GetAsByteArray(object value, PdfOptions? = null)` | 内存产物 |

渲染是**全量字节**模型（PDF 要回写 xref 偏移，天然不可纯流式），但布局计算是逐行流的。

```csharp
using QuickPdf;

Pdf.SaveAs("report.pdf", employeeList, new PdfOptions
{
    Title = "员工花名册",
    Landscape = true,
    FontSizePt = 9,
});

// 多表 = PdfSheet 序列：每表一节、自动分页
Pdf.SaveAs("multi.pdf", new List<PdfSheet> { new("员工", staff), new("字典", dict) });
```

**字体解析失败先于任何写操作抛 `QuickPdfException`** —— 不会留下半个文件。

### 2.2 `PdfOptions` 逐字段

| 字段 | 默认 | 含义 |
| --- | --- | --- |
| `Title` | `null` | 表格上方的文档标题，按内容宽度自动换行；null 省略 |
| `PrintHeader` | `true` | 灰底加粗表头行，且**每页顶部重复一次** |
| `Landscape` | `false` | true=A4 横向（MediaBox `[0 0 841.89 595.28]`） |
| `MarginPt` | `56.7`（2 cm） | 四边页边距，pt；下限 clamp 到 20 |
| `FontSizePt` | `9` | 单元格字号，pt |
| `TitleSizePt` | `16` | 标题字号，pt |
| `FontFile` | `null` | 显式指定 TrueType 字体路径（`.ttf`/`.ttc`）；null 走 §3 探测链 |
| `FontDirectory` | `null` | 额外字体目录（平台路径分隔符分开的目录串），排在系统目录之前探测 |

## 3. 字体解析（本机现场找字）

解析顺序（`Internal/FontResolver.cs`）：**`FontFile` > `FontDirectory` > `%WINDIR%\Fonts` > Linux 常见目录**（`/usr/share/fonts`、`/usr/local/share/fonts`、`~/.local/share/fonts`）。

### 3.1 候选名单（优先文件名，最像样的排前）

| 候选 | 字体 |
| --- | --- |
| `msyh.ttc` / `msyh.ttf` | 微软雅黑（Win8+ UI 黑体） |
| `simhei.ttf` | 黑体 |
| `simsun.ttc` / `simsun.ttf` | 宋体 |
| `NotoSansCJK-Regular.ttc` | Noto Sans CJK |
| `wqy-microhei.ttc` / `wqy-zenhei.ttc` | 文泉驿 |

每颗目录的探测序：名单直命中 → 递归找同名文件 → 任意 `*.ttf`/`*.ttc` 兜底。

### 3.2 只有 glyf/TrueType 能嵌入

每颗候选都**试解析**（`Internal/Ttf.cs:Load`）：要求 `head`/`hhea`/`maxp`/`hmtx`/`cmap`/`glyf` 六表齐备 —— **CFF 轮廓的 `.otf` 进不了 `/FontFile2`，会被静默跳过换下一颗**（显式 `FontFile` 指定的则抛 `QuickPdfException`，不静默）。`.ttc` 集合先抽 **font #0** 重建为独立 sfnt。cmap 走 format 4 / format 12 建码点→字形映射，PostScript 名从 `name` 表读、清洗成合法 PDF 字体名。

全部候选用尽仍无可用字体 ⇒ `QuickPdfException("No embeddable CJK TrueType font found. …")`，消息直接给出三条出路（装雅黑/黑体、装 Noto/文泉驿、或 `FontFile` 指路）。

### 3.3 嵌入方式

字体文件**整份**作为 `/FontFile2` 流写入（`Length1`=原始字节数），zip 式 Flate 压缩用 `Fastest` 档（20 MB 级字体 `SmallestSize` 要几十秒，不值得）。这就是"渲染一致但有体积税"的交易 —— 子集化未做，见 §6。

## 4. 版式与分页

### 4.1 页面与列宽

A4：纵 `595.28 × 841.89` pt，横向互换。内容宽 = 页宽 − 2×`MarginPt`。

列宽（`ComputeColumns`）：显式 `Width>0` 按 **Excel 字符单位 × 6.0 pt** 换算；未设宽的均分剩余（每列下限 36 pt）；总和超预算按比例缩 —— 与 QuickWord 同一套"先尊重显式、再均分、超了缩"的策略。

### 4.2 换行与行高

单元格文本按列宽换行（`Wrap`）：**先按 `\n` 分段；拉丁文本按词切 token、CJK 按字切**；单词/超宽 token 撑破列宽时按码点强断。`\r` 吃掉。行高 `FontSizePt × 1.45`，单元格上下留白 `PadY=3`、左右 `PadX=4`，行高 = 最多格数 × 行高 + 2×PadY —— 长文本自动加高行。

### 4.3 分页与续表头

行底越过 `_rowLimit`（页高 − 页边距）就封页重开，**每个续页顶部重画一次灰底表头行**（`0.94 0.94 0.94 rg` 填充 + 加粗文字）。多表时每表前发一节加粗小标题（`FontSizePt+3`，仅 `sheets.Count>1`），表间留 14 pt 空隙。标题段自带换行。

### 4.4 文本真实性

- 每个码点经 cmap 换成字形号，以 16 位 hex 串 `<....>` 配 `Tj` 绘制 —— 是**真实文字，可选中可复制**，不是截图。
- ToUnicode CMap 自动生成（bfchar 每块 ≤100 条），阅读器据此还原 Unicode。
- 粗体是**描边拟粗**（`2 Tr` 填充+描边渲染模式），不伪造第二支字体资源，复制文本不会重影。
- 字体里没有的字形回退 gid 0（.notdef）。

## 5. 文件结构（PDF 1.4，确定性对象号）

`Internal/PdfDoc.cs` 手写经典结构：`%PDF-1.4` 头 → 编号间接对象 → 经典 `xref` 表 + `trailer /Root` + `startxref` + `%%EOF`。对象号布局固定：1 目录、2 页树、3 Type0、4 CIDFontType2、5 FontDescriptor、6 字体文件、7 ToUnicode、8 资源，页与内容流自 9 起。字体走 `/Encoding /Identity-H` + `/CIDToGIDMap /Identity`，`/W` 数组只为**用过的字形**发宽（游程压缩）。这套结构正是单测逐条对账 xref 偏移的依据。

## 6. 边界与坑

- **依赖宿主机字体**：无可用 CJK TrueType 的机器直接抛 `QuickPdfException`。容器里要么装 `fonts-noto-cjk` 一类包，要么 `FontFile` 带字。许可上注意：系统字体整份嵌入进分发型 PDF 前，确认字体授权允许嵌入。
- **无子集化，体积大**：整份嵌入 ~20 MB 雅黑，200 行中文报告 ≈ **210 ms / 14.8 MB**（大头是字体）。按需子集化是明确的后续项，未交付。
- **只导出不解析**：不做 PDF 读回；列模型里没有图片概念，`byte[]` 单元格按 base64 文本渲染（图片进 PDF 是 v2 事项）。
- **全量字节**：`GetAsByteArray`/`SaveAs` 先把整份 PDF 建在内存再落盘，超大报表受字节数组上限约束。
- **样式是表格专用**：无页眉页脚、无目录、无跨页表格列宽自适应，只有"标题 + 一节一表"的报表形状。

## 7. API 总表

| 分类 | 成员 |
| --- | --- |
| 入口 | `Pdf.SaveAs`（path/stream） · `Pdf.SaveAsAsync` · `Pdf.GetAsByteArray` |
| 选项 | `PdfOptions`（`Title`/`PrintHeader`/`Landscape`/`MarginPt`/`FontSizePt`/`TitleSizePt`/`FontFile`/`FontDirectory`） |
| 异常 | `QuickPdfException`（字体缺失、显式字体不可用、文件不可读） |
| 本库自带列模型 | 数据源五选一 · `PdfSheet`（多表分节） · `PdfColumn`/`PdfFormat`/`PdfIgnore`/`PdfColumnWidth` 特性（与 QuickExcel 的 `[Excel*]` 同形，但是独立类型、互不相认） |
