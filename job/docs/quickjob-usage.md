# QuickJob 使用文档

**QuickJob** 是面向 .NET 10 的**任务调度库**：cron 定时、间隔拍（interval）、一次性定时（once）、按需作业（on-demand）四类 job 一网打尽，自带逐 job 取数（payload）、重试回报、并发门与重叠档，配**编译期登记**（`[QuickJob]` 特性 → 源生成器）与**OAuth 登录的 Dashboard**。**零第三方依赖**（只引 `Microsoft.Extensions.DependencyInjection.Abstractions`，容器选择归宿主），**全引擎可测**（时钟/库存两个接缝都注入式）。本文件同时也是 NuGet 包内 README。

> QuickNet 家族第五家：与 QuickSql / QuickExcel / QuickWord / QuickPdf 同风格——零依赖、MIT、独立列模型互不相认、源码短平快。

- 包：`QuickJob`（引擎）、`QuickJob.Generators`（分析器 + 源生成器，编译期登记）、`QuickJob.Dashboard`（ASP.NET 面板挂件）
- 语义锚点：一切时间由 `QuickJobOptions.Time`（`TimeProvider`）出发；一切落账走 `QuickJobOptions.Store`（`IJobStore`）。默认配方 = 系统钟 + 内存库存，改一颗就能把整套测试推成确定性。

## 安装

```bash
dotnet add package QuickJob --prerelease
dotnet add package QuickJob.Generators --prerelease   # 只有走 [QuickJob] 特性路才需要
```

命名空间 `QuickJob`。预发布系列装不到时先清缓存：`dotnet nuget locals http-cache --clear`。

## 快速上手

一条线程、两种登记路，产出同一 `JobDefinition`，dashboard/store 面同构：

```csharp
using QuickJob;

// 路 1：[QuickJob] 特性（编译期登记 —— 零反射、AOT 干净）
public sealed class Housekeeping
{
    [QuickJob("daily-roll", Cron = "0 2 * * *",        // 每日 02:00
              MaxRetries = 2, OnOverlap = OverlapPolicy.Skip)]
    public static async Task Roll(CancellationToken ct)
    {
        await DayRoller.RollAsync(ct);
    }
}

// 路 2：DSL 现场挂名
var scheduler = QuickJobScheduler.Create(b =>
{
    b.Interval("beat", TimeSpan.FromSeconds(30),
        ctx => { ctx.Report("flat pulse"); return ValueTask.CompletedTask; });
    b.OnDemand("invoice",
        async ctx =>
        {
            var order = await ctx.PayloadAs<InvoiceOrder>()!;   // payload = UTF-8 JSON
            await Invoicer.IssueAsync(order, ctx.Token);
        },
        o => { o.MaxRetries = 4; o.RetryBackoff = RetryBackoff.Exponential; });
});

await scheduler.StartAsync();          // 生成器登记 + DSL 职责此刻一起入座
scheduler.Stats                        // 计数面（started/completed/failed/retried/skipped/pending/running）
await scheduler.StopAsync();           // StopGrace 10s 内等在飞 run 收在场，逾时主令牌齐切
```

生命周期约束：scheduler **不可重启**——`StopAsync` 之后要再来，请新造一颗（实例即抛 `QuickJobException`）。

## 1. 四种 Job 类型

| Kind | 语义 | DSL | 触发面 |
| --- | --- | --- | --- |
| `Cron` | 5–7 字段 cron（可选秒/年、@daily 别名、命名月周日），按 `TimeZoneId` 壁钟游走 | `b.Cron(name, "0 2 * * *", handler, options)` | `JobTrigger.CronTick` |
| `Interval` | 固定步长（fixed-delay：run 完成后按预定时刻续排，积压不走圈） | `b.Interval(name, TimeSpan, handler, options)` | `JobTrigger.IntervalTick` |
| `Once` | 指定时刻一打 | `b.Once(name, at, handler, options, payload?)` | `JobTrigger.Scheduled` |
| `OnDemand` | 无自动节拍，只接待送 | `b.OnDemand(name, handler, options)` | `Manual` / `Enqueued` / `Retry` |

启动时就能丢进来跑的入口：

| 方法 | 说明 |
| --- | --- |
| `TriggerNowAsync(name, payload?)` | 按需正文；payload 即时序列化为 UTF-8 JSON 存档文本 |
| `EnqueueAsync(name, payload?, at?)` | 后台队列；`at` 给未来即刻到站 |
| `CancelAsync(runId)` | 撤未派工的单（在飞的单走合作令牌） |

handler 都是 `delegate ValueTask JobHandler(JobRunContext ctx)`——任意常见形状（无参 / `CancellationToken` / `ctx` / `ctx+token` / 类型化 payload）都由 `JobHandlerBinder` 重载解析接走，不用手写包装。

## 2. `[QuickJob]` 特性与生成器

```csharp
[QuickJob("drain", Cron = "0 3 * * *", Timeout = "5m", Description = "夜间落账")]
public static Task Drain(CancellationToken ct) { ... }
```

- 特性面可用参数与 `JobOptions` 一一对应（要用字符串时长：`"1500ms"` / `"30s"` / `"5m"` / `"2h"` / `"2d"` / `"00:05:00"`）。方法要求：

- **static**、非泛型容器/泛型方法即可见（public 或 internal 都行）；
- handler 形状取支持族（无参 / token / ctx / ctx+token / 类型化 request）；
- `RequestType = typeof(T)` 开类型化 payload（JSON 反序列化后进 handler）；
- `Cron` 与 `Every` 互斥，只许选一。

生成器在编译期拦这些（运行期只做兜底，语义以运行期为准）：

| 诊断 | 内容 |
| --- | --- |
| `QJ0001` | `[QuickJob]` 方法必须 static |
| `QJ0002` | handler 形状不在支持族 |
| `QJ0003` | Cron 与 Every 互斥 |
| `QJ0004` | 方法或容器对生成的登记代码不可达（private / 泛型容器 / 泛型方法） |
| `QJ0005` | 重复的 job 名 |
| `QJ0006` | 时长文本解析不了 |
| `QJ0007` | Cron 表达式结构不对 |
| `QJ0011` | Every 必须是正时长 |

登记代码不投递反射——生成器吐出一段 `[ModuleInitializer]` 调 `PendingRegistrations.Add(...)` 的类，`StartAsync` 收编入表；坏数据那时便刻炸成带 job 名的 `QuickJobException`。

## 3. 重试

失败（含超时）后按档安排重跑（同一个 `ExecutionId`，`Attempt` 递增），可重试上限走尽才投向 `RunFailed`：

| `JobOptions` | 默认 | 说明 |
| --- | --- | --- |
| `MaxRetries` | `0` | 首跑之后再补几次 attempts |
| `RetryBackoff` | `Exponential` | `Fixed` = 基阶恒定；`Linear` = 基阶 × 序号；`Exponential` = 基阶 × 2^(n-1) |
| `RetryBaseDelay` | `10s` | 基阶延迟 |
| `RetryMaxDelay` | `10min` | 指数/线性生长的上钳 |
| `RetryJitter` | `0.25` | 相对抖动（0–1），延迟 ∈ [1−j, 1+j] × computed |

上手即精密：`Fixed` + `RetryJitter = 0` 成精确 `1s`。

成功面：`RunRetrying`（每次重排一条，带 `Delay`）→ 走尽后 `RunFailed`。计数挂 `Stats.RunsRetried`。重跑的待办落在 store 里（换件盘算见『边界与常俗』）。

## 4. 并发门与重叠

```csharp
b.Interval("ingest", TimeSpan.FromSeconds(5), HandleIngest, o =>
{
    o.MaxConcurrentPerJob = 4;        // 同 job 同时在飞的单（默认 1 = 串行）
    o.OnOverlap = OverlapPolicy.RunAnyway;  // 见下表
});
```

| `OverlapPolicy` | 行为 |
| --- | --- |
| `Skip`（默认） | 门满时丢弃新 slot（`RunSkippedByGate` 事件 + `Stats.RunsSkipped`、历史行 `Skipped:OverlapSkip`） |
| `Queue` | 排进 job 级队（`OverlapQueueLimit`，默认 128；满则同 Skip 丢弃，队列接续次第放门） |
| `RunAnyway` | 无门直跑（业务侧自行保证语义） |

全局 `MaxParallelism`（默认 min(32, CPU 数)，钳 4–32）限定 scheduler 级并行席位；`JobPriority.Low/Normal/High` 决定认领次序（High 先于一切）。`scheduler.Pause(name)`/`Resume(name)` 出暂停位（跳 slot 不停牌，历史 `Skipped:Paused`）。

## 5. 超时

`o.Timeout = "3m"` 起合作预算：超点的 `CancellationToken` 走最后一条温和指令（**线程不会被槌杀**，handler 要敬 token）。超时即按失败收场（走重试盘），错误文案 `timeout after …`。

## 6. 时钟、时区与迟到策略

- 时钟只有一颗：`QuickJobOptions.Time`（`TimeProvider`，默认系统钟）。定时（cron/interval 贴钟走带）、once 触发、重试延迟、库存 due 判定都倚仗它——想测就换假钟。
- **接缝一致性家规**：测试档把 `Time` 换成假钟时，务必同时给 `Store = new InMemoryJobStore(time)`——默认库存也拿系统钟判 due，两个钟不同步会导致\"未来\"的档被真钟看老透印，然后被门提前清走。
- cron 按壁钟走（`TimeZoneId`，IANA/Windows id 双吃）——夏令时拨快落过渡瞬间不炸、回拨不重复。

迟到档（宿主挂机/停服后启动补票，`JobTrigger.CronTick`/`Scheduled` 才有）：

| `MissedSlotPolicy` | 行为 |
| --- | --- |
| `RunWithinGrace`（默认） | 迟到 ≤ `Grace`（默认 5min）补跑，否则丢 + 历史行 `Skipped:Late…`，从未来档续命 |
| `SkipLate` | 绝不跑迟档 |
| `CatchUpLatest` | 无论多迟补打一次（不扫史） |

## 7. 事件流

```csharp
using var tap = scheduler.Events.Observe(e =>
{
    if (e is RunRetrying r) ...;    // Delay / NextAttempt
});
```

`sink` 同步回调、异常吞吃、订阅序即推送序。种类：`RunStarted` / `RunCompleted` / `RunFailed` / `RunRetrying` / `RunSkippedByGate` / `SchedulerAnomaly`。

引擎兑现两条强保证（压测实测收编）：

- **同一总序**：扇出整段由单门串行——即便多个 worker 并发发报（如两个 run 的 Start/Complete 同刻），dashboard 环与你的 tap 看到的都是**同一**发布序列，不存在各自视角换序；
- **不交叠**：两次 publish 永不同时把回调送进同一个 sink —— tap 回调即使在跑，其余 worker 的 Publish 只会排队；sink 里裸 `List.Add` 也安全（回调不会并发跑进同一 sink）。

仍要注意的边界：sink 回调是**发报线程同步在跑**的——sink 里做重活会拖慢单个发报 worker（引擎不做异步分发）；且不要在 sink 里同步回驱调度器（如 Stop/Trigger 阻塞等待）。

## 8. 交互面速查

| 成员 | 一句话 |
| --- | --- |
| `Register(def, handler)` | 跑中补挂 job（同步 SaveBootDefinitionAsync + 立刻武装 slot 机） |
| `Remove(name)` | 摘 job（定义 + 存档契约同去，已在飞 run 走自然收音） |
| `Pause` / `Resume` | 泻放键（slot 不等也不丢，被打标的档跳过） |
| `ListJobs()` | 定义面快照 |
| `PendingRuns()` / `RunningRuns()` | 队列面 / 在飞面（dashboard 同源）；**未到站**的档不在这里，在库存的 due 队列（`IJobStore.PeekDueRunsAsync` 可选面） |
| `Stats` | `QuickJobStats`：started / completed / failed / retried / skipped / cancelled / pending / running |
| `Diagnostics` | 出怪异时给一条字符串（如 claim 失败、定义 unreadable） |

监听链（宿主侧推荐壳）：

```csharp
builder.Services.AddQuickJob(
    b => { ... DSL ... },
    o => { o.Time = ...; o.Store = ...; });   // 引擎单例注入
// QuickJob 刻意不引 hosting 抽象 —— 由你自己的 IHostedService 调 StartAsync/StopAsync
```

## 9. Dashboard（QuickJob.Dashboard）

```csharp
builder.Services.AddQuickJobDashboard();          // 依赖同进程的 QuickJob 单例
app.MapQuickJobDashboard();                       // 默认挂在 /quickjob，UI 自带轮询
```

- 首页即可观看：`/stats`、`/jobs`、`/runs/pending`、`/runs/running`、`/executions`、`/events`、`/me`；事件窗取环表 200 条。
- 待跑面两条来源：已到站卡在并发门上的（`PendingRuns()`）＋未到站的（库存 due 队列，走 `PeekDueRunsAsync`，行尾标「未到站」）。cron/interval 的下一槽不预置进库存，所以这里只见 定时一次 / once / 入队 / 重试等待 这类真·未来档。「丢弃」对未到站的档当场摘除并记 `Skipped:Cancelled-by-user`；自研库存不实现这两面也无妨 —— 面板只是少这一列。
- 弹窗：点遮罩即关；弹窗开着时外侧整页钉死，滚动条占宽垫回不跳版。
- 鉴权：`DashboardAuthMode` = `Anonymous`（默认）或 `OAuth2`（ClientId/Secret/三腔 endpoint + `UserIsAllowed` 裁员 + 8h 会话）。`ReadOnly` 关掉手动触发面的写入端。
- 静态挂载可换根：`o.PathPrefix = "/ops/jobs"`。
- 任务行「详情」弹定义快照（调度/时区/并发/重叠/优先级/超时/宽限/重试体系）与最近 20 条运行。特性档（`[QuickJob]`）带「声明来源」＝ 命名空间.类.方法（生成器编译期搬进登记），DSL 登记的任务标 DSL。
- 界面偏好记在浏览器本地：语言中英随手切（首访按 `navigator.language`，zh 开头即中文、否则英文兜底），主题浅色/深色（首访看 `prefers-color-scheme`，没定则暗板）。键：`qj.lang`（zh/en）、`qj.theme`（light/dark）；清掉键即回首访引导。

## 10. 性能基准与门禁（QuickJob.Benchmarks）

基准包在 `benchmarks/QuickJob.Benchmarks/`（TFM net11.0 —— 对照组 TickerQ 11.5.x 只发 net11.0，理由写在 `benchmarks/Directory.Build.props` 头注）。两份用场：

```powershell
# 门禁：一条命令、几秒内、给红绿（CI 阻断项；判据走自测，不看 BDN）
dotnet run -c Release --project benchmarks/QuickJob.Benchmarks -- --gate

# 人看的前后对比（BDN 显著性与 Alloc Ratio，PR 附件带）
dotnet run -c Release --project benchmarks/QuickJob.Benchmarks -- --filter *
```

**门禁不听 Debug 的**：Debug 下 `--gate` 返回 2（解释见 Program.cs）——装箱更多、内联更少，红会假红、绿会假绿。

**退出码语义** = 红行数（CI 阻断位），红行里**比基线更差**时再加 `+32`（`RegressionFlag`——那一位是钉闸门的硬断言位：红行数会随机器负载翻，钉住的字节/计数是确定量，才配当闸门）。

**BDN 分支在本机换了工具链**：本机 SDK 是 net11 preview，BDN 0.15.8（NuGet 最新，没有更高可升）的 SDK 名片校验解不出 preview 名片，落子进程那张路当场炸 —— Program.cs 里给 BDN 分支换了 `InProcessEmitToolchain`（不落子项目，那条校验路径整体不存在；InProcess 的预算是整个进程一份，显式给 30 分钟）。代价：28 个方法同进程轮跑、无子进程隔离，`Allocated` 列读数打这份折扣 —— SDK/BDN 出 stable 名片后删 `AddJob` 那一行即回默认，基准代码一行不用动。

**收件箱两条腿的账不同态**：BDN 的 InboxAdd10 腿是**每迭代空箱重建 + 定次 256**（`PendingRegistrations.Reset()` 经 ivt 内部面取用 —— 全局登记只进不出 × BDN 百万次重放会在单腿里撑到 GB，0.1.0-beta.1 首场实账；修复后单腿实测进程峰值 16 MB）。所以 BDN 那格读「每迭代从空箱起手的一批」，门禁行 5 的钉（2400 B）读「累计态窗内一批」—— 同形状不同状态，两边别互搬。

**对照组口径**：同代社区调度库 TickerQ 11.5.1（公开壳 `CronExpression.Parse` —— 校验 + 归一，稳态走 internal LRU）与 NCrontab 3.3.0（TickerQ.Utilities 引的 cron 引擎本体；tickerq 家自家基准也以 "TickerQ (NCrontab)" 立场出数）。三档同形表达式 `*/5 * * * *` / `0 9-17 * * 1-5`（营业档）/ `*/30 * * * * *`（带秒），同地基 `2026-03-16 12:00 UTC` —— **QuickJob ≡ NCrontab 的三档 × 3 步步进逐字相等**是行 1 的第一判据（对照只放两家都接的同形面；@别名档归 tests 册，因 TickerQ 壳不收）。别把对照读成「谁更快」的榜——三家形状不同（QuickJob 全量位掩码且 Register 一次后 Next 免费；TickerQ 稳态在 LRU；NCrontab 每番全量），费用各自领账。

**七行预算**（判据性质写在括号里）：

| # | 门禁行 | 判据 |
|---|--------|------|
| 1 | Cron 下一次触发 | 同形步进逐字相等（QuickJob≡NCrontab；分配/µs 只报不判） |
| 2 | Cron 解析一次 | 三档三家都拿到结果不炸 + QuickJob Template 保真 |
| 3 | PayloadJson 往返 | 往返等值 + 枚举字符串档 + store 文本同渠 + null 双向退化 |
| 4 | DefinitionSerialization | Write→TryLoad 全字段等 + 坏契约/缺 options 返 null + Snapshot 全字段等 |
| 5 | 收件箱登记摊销 | 10 颗全收 + 重名二次 Add 即 QuickJobException |
| 6 | InMemoryJobStore 三腿 | take=8 输出 8 条且有序；三腿 8→64 增量 ≤ 12 幅（O(n²) 会爆） |
| 7 | 反射字样 0 处 | `src/QuickJob/**` 八形状全 0（零反射承诺 —— 新增即红） |

**首场实测（0.1.0-beta.1，2026-11-04，本机 Release —— 15 颗基线钉的来源场）**：

| 钉 | 首场值 |
|----|--------|
| 一次 Next（三档） | 32 / 32 / 32 B（NCrontab 同场 0 B） |
| 一次 Parse：QuickJob | 416 / 624 / 456 B（TickerQ 壳 288/312/280；NCrontab 1704/1776/1992） |
| Payload serialize / deserialize | 128 / 272 B（5 字段样张） |
| Definition Write / TryLoad / Snapshot | 1360 / 1032 / 208 B（契约 510 字符样张） |
| 收件箱 10 颗一批 | 2400 B（≈240 B/颗，带 cron 串） |
| Store 64 条池：put / peek take=8 / claim4+回池4 | 112 / 2168 / 448 B（8 条池旁证：同腿 112/600/448 —— 8→64 在 12 幅内） |

**重钉纪律**（照 sql 家三代同一条先例：v0.20/0.23/0.27）：阈值与钉管两件事 —— 阈值问「该不该达标」，钉问「不许比上一次观测更差」。**变差要红，变好也要有人把它钉下来**：读数比钉好时行不红但报「比基线好 —— 重钉成新的基线才算跟上，否则网眼留着」；每一次重钉在常量 summary 与本节的账里各留一格来源。时间类（µs）只报不钉 —— 对的是机器状态，不是代码。

## 边界与常俗

- **只做调度**：不做业务重放、不做死信线、不做日历表 —— 见重试 + 你自己的 handler。
- **单机内存起步**：默认 `InMemoryJobStore`（跑即进程，重启回零）；`IJobStore` 缝为节点协作开（Redis/数据库盘），换包即上游。
- **断言面稳定性**：测试里若把 `Time` 换成假钟，`Store` 要同钟注入（见 §6 家规），再配合 `WaitUntil` 真钟轮询的边界，即可拿全确定性与真钟鲁棒两手。
- **AOT 干净**：生成路径无反射；handler 形状在编译期就投递到 `JobHandlerBinder`。

## 文档

- [QuickSql（ORM）使用文档](../../sql/docs/quicksql/README.md)
- [QuickExcel 使用文档](../../excel/docs/quickexcel-usage.md)
- [QuickWord 使用文档](../../word/docs/quickword-usage.md)
- [QuickPdf 使用文档](../../pdf/docs/quickpdf-usage.md)

## License

MIT
