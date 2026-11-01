# QuickNet

基于 **.NET 10** 的五个独立库的同仓集合：**QuickSql**（多数据库 ORM）、**QuickExcel**（.xlsx 导入导出）及其姊妹库 QuickWord / QuickPdf（.docx 与 PDF），以及 **QuickJob**（任务调度）。五库**互相零引用**、各自零第三方依赖（QuickSql 与 QuickJob 仅依赖 Microsoft.Extensions.DependencyInjection.Abstractions），全程流式处理，MIT 协议。

| 库 | NuGet | 一句话 |
| --- | --- | --- |
| **QuickSql** | `QuickSql.Core` + `QuickSql.Providers.*` + `QuickSql.Generators` | LINQ 风格链式 ORM，一套实体覆盖 18 家数据库（含达梦/金仓/虚谷/GBase 等信创库），源生成器零反射物化 |
| **QuickExcel** | `QuickExcel` | 高性能 .xlsx 流式导入导出：DataAnnotations 验证、图片、模板填充 |
| **QuickWord** | `QuickWord` | .docx 表格导出 + 文档读回 + 不改版式填模板 |
| **QuickPdf** | `QuickPdf` | PDF 报表导出：CJK 字体嵌入、续页表头、可选中可复制的真实文本 |
| **QuickJob** | `QuickJob` + `QuickJob.Generators` + `QuickJob.Dashboard` | 定时任务/后台作业调度：cron + interval + 一次性 + 按需四类 job，重试/并发门/重叠档，[QuickJob] 编译期登记，OAuth 登录仪表盘 |

> 全家均为 `-beta` 预发布档：`dotnet add package` 需带 `--prerelease`，或在 `PackageReference` 里写死版本号。

---

## QuickSql — 多数据库 ORM

```bash
dotnet add package QuickSql.Core --prerelease
dotnet add package QuickSql.Providers.SqlServer --prerelease
dotnet add package QuickSql.Generators --prerelease
dotnet add package Microsoft.Data.SqlClient     # 驱动由应用侧自装
```

```csharp
using QuickSql;

var orm = QuickSqlOrm.Create(DbKind.SqlServer,
    "Server=.;Database=app;User Id=sa;Password=***;TrustServerCertificate=True");

await orm.Insert(new Order { CustomerName = "甲", Amount = 12.5m, CreatedAt = DateTime.Now })
         .ExecuteAffrowsAsync();

var page = await orm.Select<Order>()
    .Where(o => o.Amount > 100m)
    .OrderByDescending(o => o.CreatedAt)
    .ToPagedAsync(1, 20);

// 工作单元：改属性 → 一个批次提交（AppDb 是 DbContext 的派生类，见文档 09 篇）
public class AppDb(QuickSqlOrm orm) : QuickSql.DbContext(orm)
{
    public DbSet<Order> Orders => Set<Order>();
}

await using var db = new AppDb(orm);
var order = await db.Orders.Where(x => x.Id == 7).FirstOrDefaultAsync();
order!.Note = "改一下";
await db.SaveChangesAsync();                            // 只写脏列
```

特性一览：

- **18 家数据库**：SQL Server、PostgreSQL、MySQL、Oracle、SQLite、达梦、人大金仓、openGauss、GaussDB、瀚高、GBase 8c/8s、DuckDB、ClickHouse、TiDB、OceanBase、神通、虚谷——换库只动 csproj、一处 `DbKind`、一处连接串，实体与链式代码一字不动
- **查询**：条件/排序（含字符串排序键）/投影/Distinct/分页计数/存在性探针；`Include`/`IncludeMany` 导航与引用导航自动联表；分组聚合；子查询；流式读取
- **写入**：批量分块（按各家参数预算）、自增键回填、SQL Server 装载通道高速层、`InsertOrUpdate` upsert
- **工作单元**：`DbContext`/`DbSet`/`ChangeTracker`/`SaveChanges`（脏列判定、级联新增/摘行/到位、乐观锁）
- **事务**：显式/包段/环境/会话四形态，保存点嵌套
- **过滤与审计**：全局查询过滤器（接口键、mandatory）、软删除（删口自动改写）、AOP 钩子全家（审计/慢日志/文本改写/取消）
- **分库分表**：`[SplitTable]` 时间+哈希分片、`[DataSource]` 具名集群、读写分离与写后读
- **CodeFirst**：结构同步三档（关闭/执行/出 DBA 脚本）、已有表逐列补差
- **工程化**：`ToSql()` 离线产 SQL、AOT 干净注册路径、原生 SQL 逃生舱走同一条执行链

📚 **[QuickSql 文档中心（按功能 16 篇）](sql/docs/quicksql/README.md)** · [设计规格](sql/docs/quicksql-orm-design.md)

## QuickExcel — Excel 导入导出

```bash
dotnet add package QuickExcel --prerelease
```

```csharp
using QuickExcel;

// 导出
Excel.SaveAs("orders.xlsx", orderList, new ExportOptions { BoldHeader = true, FreezeHeaderRow = true });

// 导入 + DataAnnotations 验证（错误合并不短路，行号/列名/单元格引用逐条定位）
ImportResult<Employee> result = Excel.Import<Employee>("in.xlsx");
if (result.HasErrors)
    foreach (var e in result.Errors)
        Console.WriteLine($"第{e.RowNumber}行 {e.CellReference} [{e.Header}] {e.Message}");

// 模板填充：{{标量}} / {{items.Field}} 集合行展开 / {{@img}} 图片占位
Excel.SaveAsByTemplate("report.xlsx", "template.xlsx", new Dictionary<string, object?>
{
    ["Title"] = "季度报告",
    ["items"] = orderList,
    ["logo"]  = ExcelImage.FromFile("logo.png"),
});
```

- SAX 式写出 + 前向读取，内存与行数无关（10 万行导出约 366 ms / 101 MB 分配，对照 MiniExcel 662 ms / 243 MB）
- 图片列（`ExcelImage`）导出为浮动图片、导入按锚点回填，兼容 WPS `DISPIMG`；相同字节自动去重
- 合并单元格导入自动回填（`FillMergedCells`）；多数据源（POCO / 字典 / ExpandoObject / DataTable / 多 Sheet）

📚 [QuickExcel 使用文档](excel/docs/quickexcel-usage.md)

## QuickWord — Word 导出与读回

```bash
dotnet add package QuickWord --prerelease
```

```csharp
using QuickWord;

Word.SaveAs("report.docx", employeeList, new WordOptions { Title = "员工花名册", FooterText = "第 {PAGE} 页" });

var doc = Word.Read("report.docx");                     // 段落 + 表格块模型
var rows = doc.Tables.First().Query();                  // 与 QuickExcel Query 同形状

await Word.SaveAsByTemplateAsync(output, "合同模板.docx", new Dictionary<string, object>
{   // 不改版式：styles/theme 等部件逐字节照抄，run 级字体样式保留
    ["姓名"] = "张萌",
    ["签名"] = ImagePlaceholder.FromFile("sign.png", width: 70, height: 30),
});
```

📚 [QuickWord 使用文档](word/docs/quickword-usage.md)

## QuickPdf — PDF 报表导出

```bash
dotnet add package QuickPdf --prerelease
```

```csharp
using QuickPdf;

Pdf.SaveAs("report.pdf", employeeList, new PdfOptions { Title = "员工花名册", Landscape = true });
Pdf.SaveAs("multi.pdf", new List<PdfSheet> { new("员工", staff), new("字典", dict) });
```

- CJK 安全：宿主机现找雅黑/黑体/Noto/文泉驿，TrueType 整份嵌入（Identity-H），任何阅读器渲染一致
- 每页重复表头、自动换行与分页；文本是真实文字（可选中可复制，ToUnicode CMap 自动生成）
- 只做导出；零依赖，字体解析失败先于任何写操作抛 `QuickPdfException`

📚 [QuickPdf 使用文档](pdf/docs/quickpdf-usage.md)

## QuickJob — 任务调度

```bash
dotnet add package QuickJob --prerelease
dotnet add package QuickJob.Generators --prerelease   # [QuickJob] 特性路需要
```

```csharp
using QuickJob;

// 路 1：[QuickJob] 特性 —— 生成器编译期登记（零反射、AOT 干净）
public sealed class Housekeeping
{
    [QuickJob("daily-roll", Cron = "0 2 * * *",
              MaxRetries = 2, OnOverlap = OverlapPolicy.Skip)]
    public static async Task Roll(CancellationToken ct) => await DayRoller.RollAsync(ct);
}

// 路 2：DSL 现场挂名 —— 两种登记产出同一定义，dashboard/store 同构
var scheduler = QuickJobScheduler.Create(b =>
{
    b.Interval("beat", TimeSpan.FromSeconds(30),
        ctx => { ctx.Report("flat pulse"); return ValueTask.CompletedTask; });
    b.OnDemand("invoice", async ctx =>
    {
        var order = await ctx.PayloadAs<InvoiceOrder>()!;   // payload = UTF-8 JSON
        await Invoicer.IssueAsync(order, ctx.Token);
    }, o => { o.MaxRetries = 4; o.RetryBackoff = RetryBackoff.Exponential; });
});

await scheduler.StartAsync();
var (accepted, runId) = await scheduler.TriggerNowAsync("invoice", new InvoiceOrder(7));
await scheduler.StopAsync();
```

- 四类 job 一家收：cron（5–7 字段、命名月周日、@daily 别名、按时区壁钟游走）/ interval 固定步长 / once 指定时刻 / on-demand 纯接待
- 重试四件套：`MaxRetries` × `Fixed/Linear/Exponential` × 基阶/上钳 × 抖动；同一 ExecutionId、Attempt 递增，走尽才终局
- 并发门按 job 一颗：`MaxConcurrentPerJob` + `Skip/Queue/RunAnyway`（默认串行+跳），外加 scheduler 级 `MaxParallelism` 与三级优先
- 迟到档策略齐活： soaked `RunWithinGrace`（默认 5min）/ `SkipLate` / `CatchUpLatest`，夏令时拨快不炸、回拨不重复
- Dashboard 挂件：`/quickjob` 挂载、环表 200 条事件窗、OAuth 登录档（`DashboardAuthMode.OAuth2`）、`ReadOnly` 关闸
- 全引擎两个接缝：`Time`（TimeProvider，一切定时与重试延迟的出处）+ `Store`（IJobStore，默认内存库存、历史环 2000）——换假钟推测试永确定性

📚 [QuickJob 使用文档](job/docs/quickjob-usage.md)

---

## 开发

```bash
# 全解决方案构建
dotnet build Quick.slnx

# 各库测试
dotnet test excel/tests/QuickExcel.Tests
dotnet test word/tests/QuickWord.Tests
dotnet test pdf/tests/QuickPdf.Tests
dotnet test job/tests/QuickJob.Tests

# QuickSql 测试（离线方言册：构建后直接运行 exe）
dotnet build sql/tests/QuickSql.Tests.Dialects
sql/tests/QuickSql.Tests.Dialects/bin/Debug/net10.0/QuickSql.Tests.Dialects.exe

# QuickSql 集成册（需真库，连接串由环境变量/本地凭据文件给）
# 运行性能基准
dotnet run -c Release --project excel/benchmarks/QuickExcel.Benchmarks
```

## License

MIT
