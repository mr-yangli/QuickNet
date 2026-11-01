namespace QuickJob.Tests;

using QuickJob;

/// <summary>
/// 执行管线的行为学：假时钟 + 真 worker 池。断言都在结果面上（Completed/Failed/Skipped 计数、
/// 事件序列、payload），不猜内部行进秒数 —— 引擎的 claim/gate/worker 各自异步推进，
/// 测试只等「该发生的都发生了」。
/// </summary>
public class PipelineTests
{
    [Fact]
    public async Task Interval_fires_on_fake_clock_cadence()
    {
        var time = new FakeTimeProvider();
        var seen = new Collector();

        var scheduler = QuickJobScheduler.Create(b => b.Interval("beat", TimeSpan.FromSeconds(20),
            ct => { seen.Increment(); return ValueTask.CompletedTask; }),
            options: new QuickJobOptions { Time = time });
        await scheduler.StartAsync();

        // 步进推进：每拍给引擎真实时钟的间隙把 run 吃掉（默认门=串行+跳重叠，一次大跳积压档会被跳）
        for (var i = 1; i <= 6; i++)
        {
            time.Advance(TimeSpan.FromSeconds(20));
            await WaitUntil(() => seen.Value >= i, TimeSpan.FromSeconds(10));
        }

        var stats = scheduler.Stats;
        Assert.Equal(6, stats.RunsStarted);
        Assert.Equal(6, stats.RunsCompleted);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Grace_window_drops_a_far_late_cron_slot()
    {
        var time = new FakeTimeProvider();
        IJobStore store = new InMemoryJobStore(time);
        var scheduler = QuickJobScheduler.Create(b => b.Cron("tight", "0 * * * *", _ => ValueTask.CompletedTask,
                o => { o.Grace = TimeSpan.FromSeconds(30); o.OnMissedSlot = MissedSlotPolicy.SkipLate; }),
            options: new QuickJobOptions { Time = time, Store = store });
        await scheduler.StartAsync();

        // 启动后预置一条 30 虚拟分钟前的 cron 槽 —— grace 30s 拦不下 30min 的迟到，SkipLate 档应当丢
        var droppedLateRun = Guid.NewGuid();
        await store.PutDueRunAsync(new StoredDueRun
        {
            RunId = droppedLateRun,
            JobName = "tight",
            DueAt = time.GetUtcNow() - TimeSpan.FromMinutes(30),
            Attempt = 1,
            Origin = JobTrigger.CronTick,
        });
        await WaitUntil(() => scheduler.Stats.RunsSkipped >= 1, TimeSpan.FromSeconds(10));

        Assert.Equal(0, scheduler.Stats.RunsStarted);   // 丢弃不进 handler
        Assert.Equal(0, scheduler.Stats.RunsFailed);    // 丢弃也不是失败

        // 丢档的落账是 stats 先行、FinishLease→AppendExecution 垫后的 fire-and-forget 链；
        // 全量并行下这条腿会被挤压，历史面需要轮询追平（10s 预算），断言仍以结果面为准
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var probe = await store.ReadExecutionsAsync(new ExecutionQuery { JobName = "tight" });
            if (probe.Any(r => r.ExecutionId == droppedLateRun && (r.Outcome ?? "").StartsWith("Skipped:Late"))) break;
            await Task.Delay(25);
        }

        var rows = await store.ReadExecutionsAsync(new ExecutionQuery { JobName = "tight" });
        Assert.Contains(rows, r => r.ExecutionId == droppedLateRun && (r.Outcome ?? "").StartsWith("Skipped:Late"));
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Retries_follow_backoff_and_exhaust()
    {
        var time = new FakeTimeProvider();
        var attempts = new Collector();
        var retryGate = new object();
        var retried = new List<RunRetrying>();

        // 单跑主题：OnDemand 一次触发，不掺 interval 拍 —— 数的重试就是这一个 run 的
        // 钟与库存同源：默认 InMemoryJobStore 是真钟，重试 dueAt(fake+10s) 在真钟眼里是过去，
        // 会赶在 attempt1 放门前被 claim → 门 Skip 卡死重试。接缝一致性是家规。
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("flaky",
                ctx => { attempts.Increment(); throw new InvalidOperationException("boom"); },
                o => { o.MaxRetries = 2; o.RetryBackoff = RetryBackoff.Fixed; o.RetryBaseDelay = TimeSpan.FromSeconds(10); o.RetryJitter = 0; o.RetryMaxDelay = TimeSpan.FromSeconds(10); }),
            options: new QuickJobOptions { Time = time, Store = new InMemoryJobStore(time) });
        using var sub = scheduler.Events.Observe(e =>
        {
            if (e is RunRetrying r) { lock (retryGate) retried.Add(r); } // 并发 Publish 下裸 List 不安全
        });
        await scheduler.StartAsync();

        _ = await scheduler.TriggerNowAsync("flaky");
        await WaitUntil(() => attempts.Value >= 1, TimeSpan.FromSeconds(10));
        // Put(584) 先于 stats.Retried++(586) —— 观测到 stats 即知 rerun 已入库、store 摆锤武装到点。
        await WaitUntil(() => scheduler.Stats.RunsRetried >= 1, TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(30)); // 余量档：Advance 不跟 dueAt 同刻对赌——dueAt 在 Put 时刻定格
        await WaitUntil(() => attempts.Value >= 2, TimeSpan.FromSeconds(10));
        await WaitUntil(() => scheduler.Stats.RunsRetried >= 2, TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(30)); // 第二次重试到点后耗尽
        await WaitUntil(() => scheduler.Stats.RunsFailed >= 1, TimeSpan.FromSeconds(10));

        Assert.Equal(3, attempts.Value);       // 1 次正跑 + 2 次重试
        List<RunRetrying> retrySnapshot;
        lock (retryGate) retrySnapshot = [.. retried];
        Assert.Equal(2, retrySnapshot.Count);  // 每次失败排一次重试，第三次耗尽
        Assert.All(retrySnapshot, r => Assert.Equal(TimeSpan.FromSeconds(10), r.Delay));
        var stats = scheduler.Stats;
        Assert.Equal(1, stats.RunsFailed);
        Assert.Equal(2, stats.RunsRetried);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Per_job_gate_serialises_runs()
    {
        var inFlight = 0;
        var peak = 0;
        var finished = 0L;

        var scheduler = QuickJobScheduler.Create(b => b.Interval("serial", TimeSpan.FromSeconds(1),
                ctx =>
                {
                    var now = Interlocked.Increment(ref inFlight);
                    Interlocked.CompareExchange(ref peak, now, now - 1);
                    Thread.Sleep(30);   // 手里活干得慢
                    Interlocked.Decrement(ref inFlight);
                    Interlocked.Increment(ref finished);
                    return ValueTask.CompletedTask;
                },
                o => { o.MaxConcurrentPerJob = 1; o.OnOverlap = OverlapPolicy.Skip; }),
            options: new QuickJobOptions { MaxParallelism = 8 });
        await scheduler.StartAsync();

        await WaitUntil(() => Interlocked.Read(ref finished) >= 5, TimeSpan.FromSeconds(25));
        Assert.Equal(1, Volatile.Read(ref peak)); // gate=1 ⇒ 从来没有两个同 job 同时在跑
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Overlap_queue_runs_slots_back_to_back_in_order()
    {
        var completed = new List<long>();
        var gate = 0;

        var scheduler = QuickJobScheduler.Create(b => b.Interval("ordered", TimeSpan.FromSeconds(1),
                ctx =>
                {
                    if (ctx.Token.IsCancellationRequested) return ValueTask.CompletedTask;
                    var slot = Interlocked.Increment(ref gate);
                    Thread.Sleep(40); // 让下一拍排进 overflow 而不是撞进 worker
                    lock (completed) completed.Add(slot);
                    return ValueTask.CompletedTask;
                },
                o => { o.MaxConcurrentPerJob = 1; o.OnOverlap = OverlapPolicy.Queue; o.OverlapQueueLimit = 16; }),
            options: new QuickJobOptions { MaxParallelism = 8 });
        await scheduler.StartAsync();

        await WaitUntil(() => Volatile.Read(ref gate) >= 4 && completed.Count >= 3, TimeSpan.FromSeconds(30));
        var list = completed.ToList();
        Assert.Equal(list.OrderBy(x => x), list); // queue 档：排队的那几拍按拍的顺序跑
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Explicit_cancel_stops_a_running_attempt()
    {
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("victim", ctx =>
        {
            started.SetResult(ctx.ExecutionId);
            try { Task.Delay(Timeout.Infinite, ctx.Token).Wait(ctx.Token); }
            catch (OperationCanceledException) { cancelled.SetResult(true); throw; }
            return ValueTask.CompletedTask;
        }));
        await scheduler.StartAsync();

        var (accepted, runId) = await scheduler.TriggerNowAsync("victim");
        Assert.True(accepted);
        Assert.True(runId.HasValue);
        var id = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(id, runId.GetValueOrDefault());

        var stop = await scheduler.CancelAsync(id);
        Assert.True(stop);
        var seen = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(seen);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Timeout_cancels_the_attempt_inside_the_handler()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("slow", ctx =>
        {
            try { Task.Delay(Timeout.Infinite, ctx.Token).Wait(ctx.Token); }
            catch (OperationCanceledException) { cancelled.SetResult(true); throw; }
            return ValueTask.CompletedTask;
        }, o => o.Timeout = TimeSpan.FromMilliseconds(400)));
        await scheduler.StartAsync();

        _ = await scheduler.TriggerNowAsync("slow");
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitUntil(() => scheduler.Stats.RunsFailed >= 1, TimeSpan.FromSeconds(10));
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Payload_flows_through_and_deserialises()
    {
        var received = new TaskCompletionSource<OrderPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("cargo", ctx =>
        {
            var payload = ctx.PayloadAs<OrderPayload>();
            received.TrySetResult(payload ?? new OrderPayload());
            return ValueTask.CompletedTask;
        }));
        await scheduler.StartAsync();

        _ = await scheduler.EnqueueAsync("cargo", new OrderPayload { Id = 42, Tags = ["hot", "rush"] });
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(42, payload.Id);
        Assert.Equal(["hot", "rush"], payload.Tags);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Progress_note_from_the_handler_is_visible_on_the_live_view_and_in_history()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("report", async ctx =>
        {
            ctx.Report("halfway there");
            gate.SetResult(true);
            await Task.Delay(200, ctx.Token);
        }));
        await scheduler.StartAsync();

        _ = await scheduler.TriggerNowAsync("report");
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var live = await WaitUntilOrNull(() => scheduler.RunningRuns().Any(r => r.Progress == "halfway there"), TimeSpan.FromSeconds(3));
        Assert.True(live, "progress note should appear on the live run view");

        await WaitUntil(() => scheduler.Stats.RunsCompleted >= 1, TimeSpan.FromSeconds(5));
        var store = scheduler.Options.Store;
        var rows = await store.ReadExecutionsAsync(new ExecutionQuery { JobName = "report" });
        Assert.Contains(rows, r => r.Progress == "halfway there");
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Enqueue_at_future_instant_waits_for_due()
    {
        var time = new FakeTimeProvider();
        var seen = new Collector();
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("later", ct => { seen.Increment(); return ValueTask.CompletedTask; }),
            options: new QuickJobOptions { Time = time });
        await scheduler.StartAsync();

        var (accepted, runId) = await scheduler.EnqueueAsync("later", payload: null, at: time.GetUtcNow() + TimeSpan.FromMinutes(5));
        Assert.True(accepted);
        Assert.True(runId.HasValue);
        Assert.Equal(0, seen.Value); // 未到站一条都不会跑

        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => seen.Value >= 1, TimeSpan.FromSeconds(10));
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Trigger_on_unknown_name_is_rejected()
    {
        var scheduler = QuickJobScheduler.Create();
        await scheduler.StartAsync();

        var (accepted, runId) = await scheduler.TriggerNowAsync("ghost");
        Assert.False(accepted);
        Assert.False(runId.HasValue);
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Once_job_runs_and_leaves_the_registry()
    {
        var time = new FakeTimeProvider();
        var seen = new Collector();
        var scheduler = QuickJobScheduler.Create(b => b.Once("one-shot", time.GetUtcNow() + TimeSpan.FromSeconds(10),
            ct => { seen.Increment(); return ValueTask.CompletedTask; }),
            options: new QuickJobOptions { Time = time });
        await scheduler.StartAsync();

        time.Advance(TimeSpan.FromSeconds(11));
        await WaitUntil(() => seen.Value >= 1, TimeSpan.FromSeconds(10));
        await WaitUntil(() => scheduler.GetJob("one-shot") is null, TimeSpan.FromSeconds(5)); // 离场
        await scheduler.StopAsync();
    }

    private static async Task WaitUntil(Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in time");
            await Task.Delay(15);
        }
    }

    private static async Task<bool> WaitUntilOrNull(Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(10);
        }
        return true;
    }
}

/// <summary>Payload 形状。</summary>
public sealed class OrderPayload
{
    public int Id { get; set; }
    public string[] Tags { get; set; } = [];
}
