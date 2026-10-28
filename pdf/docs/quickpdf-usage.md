# QuickPdf 使用文档

**QuickPdf** 是面向 .NET 10 的 PDF 报表导出库：把表状数据画成"标题 + 分节表格"的报表 PDF，**中文安全**（从宿主机现找 CJK TrueType 字体整份嵌入，Identity-H 寻址，任何阅读器渲染一致），**只做导出**（不解析 PDF），**零第三方依赖**。本文件同时也是 NuGet 包内 README。

> 与 QuickExcel / QuickWord 各自带一套**同名同形但互不相认**的列特性（`[PdfColumn]` 一族）——POCO 上写的 `[ExcelColumn]` 对 QuickPdf 无效，要两边生效就各写各的。

## 安装

```bash
dotnet add package QuickPdf --prerelease
```

或 csproj 写死版本号：

```xml
<PackageReference Include="QuickPdf" Version="0.1.0-beta.5" />
```

命名空间 `QuickPdf`。预发布系列装不到时先清缓存：`dotnet nuget locals http-cache --clear`。

## 快速上手

```csharp
using QuickPdf;

Pdf.SaveAs("report.pdf", employeeList, new PdfOptions
{
    Title = "员工花名册",
    Landscape = true,          // A4 横向
    FontSizePt = 9,
});

// 多表：每表一节、自动分页、续页重绘表头
Pdf.SaveAs("multi.pdf", new List<PdfSheet> { new("员工", staff), new("字典", dict) });

byte[] bytes = Pdf.GetAsByteArray(employeeList);
await Pdf.SaveAsAsync("report.pdf", employeeList);
```

字体解析失败**先于任何写操作**抛 `QuickPdfException`——不会留下半个文件。

## 1. 入口与数据源

| 方法 | 说明 |
| --- | --- |
| `Pdf.SaveAs(string path, object value, PdfOptions? = null)` | 覆盖写；自动建父目录 |
| `Pdf.SaveAs(Stream stream, object value, PdfOptions? = null)` | 渲染成字节后整份写入流 |
| `Pdf.SaveAsAsync(string path, …)` | 异步版 |
| `Pdf.GetAsByteArray(object value, PdfOptions? = null)` | 内存产物 |

`value` 数据源与 QuickExcel 同构五种：POCO `IEnumerable<T>`、`IEnumerable<IDictionary<string, object>>` / `ExpandoObject`（列取第一行键）、`DataTable`、其他 `IEnumerable` 反射探测；多表传 `IEnumerable<PdfSheet>`（`new PdfSheet("员工", rows)`）。

## 2. 列特性

| 特性 | 作用 |
| --- | --- |
| `[PdfColumn("列名")]` | `Name` 改列名；`Order` 排序；`Width` 列宽（Excel 字符单位 × 6.0pt 换算，未设宽均分剩余、每列下限 36pt）；`Format` 数字格式 |
| `[PdfFormat("N2")]` | 单独设格式 |
| `[PdfColumnWidth(20)]` | 单独设列宽 |
| `[PdfIgnore]` | 排除属性 |

## 3. `PdfOptions`

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `Title` | `null` | 文档标题（按内容宽度自动换行） |
| `PrintHeader` | `true` | 灰底加粗表头行，**每页顶部重复一次** |
| `Landscape` | `false` | true = A4 横向 |
| `MarginPt` | `56.7`（2 cm） | 四边页边距（pt），下限 clamp 到 20 |
| `FontSizePt` | `9` | 单元格字号 |
| `TitleSizePt` | `16` | 标题字号 |
| `FontFile` | `null` | 显式指定 TrueType 字体路径（`.ttf`/`.ttc`）；null 走探测链 |
| `FontDirectory` | `null` | 额外字体目录（平台路径分隔符分开），排在系统目录之前 |

## 4. 字体：怎么找到字、怎么嵌

**解析顺序**：`FontFile` > `FontDirectory` > `%WINDIR%\Fonts` > Linux 常见目录（`/usr/share/fonts`、`/usr/local/share/fonts`、`~/.local/share/fonts`）。

候选优先级：微软雅黑（`msyh`）→ 黑体（`simhei`）→ 宋体（`simsun`）→ Noto Sans CJK → 文泉驿（微米黑/正黑）。每目录探测序：名单直命中 → 递归找同名 → 任意 `*.ttf`/`*.ttc` 兜底。

- **只有 glyf/TrueType 轮廓能嵌入**：CFF 轮廓的 `.otf` 进不了字体流，会被跳过换下一颗（显式 `FontFile` 指定则抛 `QuickPdfException`，不静默）。`.ttc` 集合取 font #0 重建独立字体。
- **整份嵌入**（无子集化）：单文件约 +15 MB 起（~20 MB 的雅黑嵌进去，200 行中文报告 ≈ 14.8 MB、约 210 ms）。子集化是后续项。
- 全部候选都不可用 ⇒ `QuickPdfException`，消息给三条出路（装雅黑/黑体、装 Noto/文泉驿、`FontFile` 指路）。**容器部署**记得装 `fonts-noto-cjk` 一类包，或 `FontFile` 带字。注意字体授权是否允许嵌入分发。

## 5. 版式与文本真实性

- **A4**：纵向 595.28 × 841.89 pt，横向互换；列宽"先尊重显式、再均分、超了缩"。
- **换行**：先按 `\n` 分段；拉丁按词切、CJK 按字切；超宽 token 按码点强断。行高 = 字号 × 1.45，长文本自动加高行。
- **分页**：行底越界封页重开，续页顶部重画表头；多表时每表前发一节加粗小标题。
- **真实文字**：每个码点经 cmap 换字形号绘制——**可选中可复制**，不是截图；ToUnicode CMap 自动生成（复制出来是对的 Unicode）；粗体是描边拟粗（复制不重影）；缺字形回退 `.notdef`。
- PDF 1.4 经典结构（xref/trailer），确定性对象号，适合下游程序化处理。

## 6. 边界与常见问题

- **只导出**：不做 PDF 读回；列模型没有图片概念（`byte[]` 按 base64 文本渲染；图片进 PDF 是后续项）。
- **全量字节模型**：整份 PDF 建在内存再落盘/写流（PDF 要回写 xref 偏移，天然不可纯流式）；超大报表受字节数组上限约束。
- **样式是表格专用**：无页眉页脚、无目录、无页码；只有"标题 + 一节一表"的报表形状。
- **无子集化，体积大**：见 §4；对体积敏感就先等子集化，或挑一款小的 CJK 字体用 `FontFile` 指定。
- **依赖宿主机字体**：无可用 CJK TrueType 的机器直接抛 `QuickPdfException`。

## 相关文档

- [QuickExcel 使用文档](../../excel/docs/quickexcel-usage.md)（输入契约的一般形态）
- [QuickWord 使用文档](../../word/docs/quickword-usage.md)
- [QuickSql（ORM）使用文档](../../sql/docs/quicksql/README.md)

## License

MIT
