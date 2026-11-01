using QuickJob;

// QuickJob 示例：一砖两路的任务登记（类型声明统一放文件尾 —— top-level 语法要求）。
//   · [QuickJob] 特性 —— 生成器在编译期把静态方法登记进 PendingRegistrations，StartAsync 时收编
//   · DSL —— scheduler.Create(b => ...) 现场挂名，两者产出同一 JobDefinition：dashboard/store 同构
// 零第三方依赖（仅 DI 抽象）；引擎语义：默认串行门（MaxConcurrentPerJob=1 + Skip），可按 job 改。

var scheduler = QuickJobScheduler.Create(b =>
{
    b.Interval("collector", TimeSpan.FromSeconds(2),
        ctx => { ctx.Report("collecting…"); return ValueTask.CompletedTask; },
        o => { o.MaxConcurrentPerJob = 2; o.OnOverlap = OverlapPolicy.RunAnyway; }); // 并行容忍档
    b.OnDemand("audit", ctx => ValueTask.CompletedTask);                             // 纯按需，等 EnqueueAsync
});
using var tap = scheduler.Events.Observe(e =>
{
    if (e is RunRetrying r) Console.WriteLine($"[event] {_n(e.Kind)} {r.JobName} → 第 {r.NextAttempt} 次，{r.Delay.TotalSeconds:F0}s 后");
    else if (e is RunFailed f) Console.WriteLine($"[event] {_n(e.Kind)} {f.JobName}（终局：{f.Error.Split('\n')[0]}）");
    else if (e is RunSkippedByGate s) Console.WriteLine($"[event] {_n(e.Kind)} {s.JobName}（{s.Reason}）");
});

await scheduler.StartAsync();  // 生成器登记 + DSL 职责此刻一起入座

// 手动触发：特性登记的 invoice 走 TriggerNowAsync，payload 以 UTF-8 JSON 过存档文本
var (accepted, runId) = await scheduler.TriggerNowAsync("invoice", new InvoiceRequest(7, "集成"));
Console.WriteLine($"手动触发 invoice: accepted={accepted} runId={runId?.ToString()[..8]}");

// 按需排队：EnqueueAsync 挂未来档（不传 at = 立即到站，由 worker 认领）
await scheduler.EnqueueAsync("audit", new { day = DateTime.Today, source = "sample" });
Console.WriteLine($"Enqueue audit 入队（待 worker 认领）");

await Task.Delay(5500); // 让 1s 心跳打几拍、invoice 的 Fixed 1s 重试跑完 2 次后终局

var stats = scheduler.Stats;
Console.WriteLine($"--- stats: started={stats.RunsStarted} completed={stats.RunsCompleted} skipped={stats.RunsSkipped} retried={stats.RunsRetried} failed={stats.RunsFailed} pending={stats.PendingRuns} running={stats.RunningRuns}");
await scheduler.StopAsync();

static string _n(QuickJobEventKind kind) => kind switch
{
    QuickJobEventKind.RunRetrying => "retrying",
    QuickJobEventKind.RunFailed => "failed",
    QuickJobEventKind.RunSkippedByGate => "skipped",
    _ => kind.ToString(),
};

// -------------------------------------------------------------- [QuickJob] 特性路

/// <summary>生成器会把下面两个静态方法登成两个 job；StartAsync 时与 DSL 面一起进表。</summary>
public sealed class Housekeeping
{
    [QuickJob("heartbeat", Every = "1s", Description = "低频心跳拍，演示间隔档")]
    public static void Heartbeat()
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] heartbeat ▶ interval 拍");
    }

    [QuickJob("invoice", Every = "30s", RequestType = typeof(InvoiceRequest),
        MaxRetries = 2, RetryBackoff = RetryBackoff.Fixed, RetryBaseDelay = "1s", RetryJitter = 0,
        OnOverlap = OverlapPolicy.RunAnyway, Description = "带类型化 payload 与重试的演示档")]
    public static async Task HandleInvoice(InvoiceRequest? msg, CancellationToken ct)
    {
        await Task.Delay(30, ct); // 让重试好演示：慢半拍然后炸
        throw new InvalidOperationException($"invoice {msg?.Id} 首跑必炸（演示重试）");
    }
}

public sealed record InvoiceRequest(int Id, string Customer);
