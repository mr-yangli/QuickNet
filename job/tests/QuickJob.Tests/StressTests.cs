namespace QuickJob.Tests;

using QuickJob;
using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// 压力 + 泄漏双档：10 万级 enqueue 吞吐、并发 claim 的统账、注册/摘除的循环洪峰、
/// 站着跑长轮询的驻留面（等待/在跑队列回归零、堆差值有界）、事件订阅与 handler 的
/// 弱引用可回收性。所有时间/内存断言都留了 ≥5 倍的冗余 —— CI 噪声不该把合同打爆。
/// </summary>
[Collection(nameof(StressCollection))]
public class StressTests
{
    // xUnit 同集合里各测试串行跑 —— GC 压力测试互相干净

    [Fact]
    public async Task Enqueue_hundred_thousand_future_runs_at_speed()
    {
        var time = new FakeTimeProvider();
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("load", _ => ValueTask.CompletedTask),
            options: new QuickJobOptions { Time = time });
        await scheduler.StartAsync();

        const int total = 100_000;
        var at = time.GetUtcNow() + TimeSpan.FromMinutes(10); // 未来档：入队期间引擎不把它们拖去跑
        var before = GcClean.LiveHeap();
        var stopwatch = Stopwatch.StartNew();

        var tasks = new Task<(bool, Guid?)>[total];
        var payload = new { n = 1, tag = "stress" };
        for (var i = 0; i < total; i++) tasks[i] = scheduler.EnqueueAsync("load", payload, at);
        await Task.WhenAll(tasks);

        stopwatch.Stop();
        var retained = GcClean.LiveHeap() - before;

        var rate = total / stopwatch.Elapsed.TotalSeconds;
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60),
            $"enqueue throughput collapsed: {total} in {stopwatch.Elapsed} ({rate:F0}/s)");
        Assert.True(retained < 128L * 1024 * 1024,
            $"enqueue retained {(retained / 1024.0 / 1024):F1} MB for {total} runs");

        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Concurrent_claiming_never_double_books_a_run()
    {
        var time = new FakeTimeProvider();
        IJobStore store = new InMemoryJobStore(time);
        const int total = 6_400;
        for (var i = 0; i < total; i++)
        {
            await store.PutDueRunAsync(new StoredDueRun
            {
                RunId = Guid.NewGuid(),
                JobName = "race",
                DueAt = time.GetUtcNow(), // store 的钟就是这颗假钟 —— 用它的 now 才是已到期
                Origin = JobTrigger.Enqueued,
            });
        }

        var claimed = new ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var leases = await store.ClaimDueRunsAsync("carrier", 64);
                if (leases.Count == 0) return;
                foreach (var lease in leases) claimed.Add(lease.Run.RunId);
            }
        })));

        Assert.Equal(total, claimed.Distinct().Count()); // 声明面零重叠
    }

    [Fact]
    public async Task Register_remove_churn_does_not_leak()
    {
        var time = new FakeTimeProvider();
        var scheduler = QuickJobScheduler.Create(options: new QuickJobOptions { Time = time });
        await scheduler.StartAsync();

        var before = GcClean.LiveHeap();
        for (var i = 0; i < 20_000; i++)
        {
            var name = $"churn-{i}";
            scheduler.Register(new JobDefinition { Name = name, Kind = JobKind.OnDemand, Options = new JobOptions() },
                ctx => ValueTask.CompletedTask);
            scheduler.Remove(name);
        }
        var delta = GcClean.LiveHeap() - before;

        Assert.True(delta < 32L * 1024 * 1024, $"20k register/remove cycles retained {(delta / 1024.0 / 1024):F1} MB");
        Assert.Empty(scheduler.ListJobs());
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Long_interval_run_keeps_queues_empty_and_memory_bounded()
    {
        var time = new FakeTimeProvider();
        IJobStore store = new InMemoryJobStore(time); // 钟与库存同源 —— 接缝一致性是家规
        var scheduler = QuickJobScheduler.Create(b => b.Interval("steady", TimeSpan.FromSeconds(1),
            _ => ValueTask.CompletedTask, o => o.OnOverlap = OverlapPolicy.RunAnyway),
            options: new QuickJobOptions { Time = time, Store = store, MaxParallelism = 8 });
        await scheduler.StartAsync();

        time.Advance(TimeSpan.FromMinutes(10)); // 第一轮 600 拍（假钟不推进就永远不出 slot）
        await WaitUntil(() => scheduler.Stats.RunsCompleted >= 600, TimeSpan.FromSeconds(30));
        var warm = scheduler.Stats.RunsCompleted;
        var before = GcClean.LiveHeap();

        time.Advance(TimeSpan.FromMinutes(10)); // 第二轮再 600 拍 —— RunAnyway 档，重叠槽不挨跳
        await WaitUntil(() => scheduler.Stats.RunsCompleted >= warm + 600, TimeSpan.FromSeconds(30));

        Assert.Equal(warm + 600, scheduler.Stats.RunsCompleted);
        Assert.Equal(0, scheduler.Stats.PendingRuns);
        Assert.Equal(0, scheduler.Stats.RunningRuns);
        Assert.Empty(scheduler.PendingRuns());
        Assert.Empty(scheduler.RunningRuns());

        var delta = GcClean.LiveHeap() - before;
        Assert.True(delta < 16L * 1024 * 1024, $"steady 600-tick run retained {(delta / 1024.0 / 1024):F1} MB");

        await scheduler.StopAsync();
    }

    [Fact]
    public void Unsubscribed_event_sink_is_collectable()
    {
        // 建立、取证（订阅中=活）、退订都发生在 helper 帧里；helper 一退，整条引用链随帧消亡。
        // Windows 的 GC 保守扫栈会把**活动帧**里的陈旧 spill 槽当根（dotnet-dump !gcroot 坐实：
        // 调用方帧 rbp-88 的陈旧 Unsubscriber 槽正是钉子），所以最后的收集必须站在
        // helper 帧之后的更深层调用里做 —— 该栈区已被覆盖，陈旧指针不再存在。
        var (aliveWhileSubscribed, sinkWeak) = ProbeSubscription();
        Assert.True(aliveWhileSubscribed); // 订阅中：Unsubscriber→委托→sink 链还连着

        Assert.False(CollectAndCheck(sinkWeak)); // 退订+出帧后：引擎清了订阅表，链断人散
    }

    /// <summary>干净帧内的收集取证：本帧与下层调用链从没碰过义务链引用，保守栈扫无从捡漏。</summary>
    private static bool CollectAndCheck(WeakReference weak)
    {
        GcClean.Collect();
        return weak.IsAlive;
    }

    /// <summary>帧内：订阅 → 收集取证 → 退订；只回证据（bool + 弱引用），不带任何义务链引用。</summary>
    private static (bool AliveWhileSubscribed, WeakReference SinkWeak) ProbeSubscription()
    {
        var events = new QuickJobEvents();
        var sink = new Sink();
        var sinkWeak = new WeakReference(sink);
        var subscription = events.Observe(sink.OnEvent);

        GcClean.Collect();
        var aliveWhileSubscribed = sinkWeak.IsAlive;

        subscription.Dispose();
        subscription = null;
        sink = null;
        return (aliveWhileSubscribed, sinkWeak);
    }

    [Fact]
    public async Task Removed_handler_can_be_collected_after_stop()
    {
        // 注册/Remove/Stop 全在 helper 帧里跑，只有 WeakReference 出帧 —— 复用 Unsubscribed 的
        // 取证形态：Windows 保守栈扫会把活动帧里的陈旧 spill 槽当根（dotnet-dump !gcroot 坐实），
        // 所以建立与取证必须拆帧。
        var hostWeak = await RemoveAndStopAsync();
        Assert.False(CollectAndCheck(hostWeak));
    }

    private static async Task<WeakReference> RemoveAndStopAsync()
    {
        var host = new HandlerHost();
        var weak = new WeakReference(host);
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("leaky", host.Run));
        await scheduler.StartAsync();
        scheduler.Remove("leaky");   // 注册表先放手
        await scheduler.StopAsync(); // 停机拆管线
        host = null;
        return weak;
    }

    [Fact]
    public async Task Pending_runs_carrying_payload_do_not_accumulate_past_the_window()
    {
        var time = new FakeTimeProvider();
        var scheduler = QuickJobScheduler.Create(
            b => b.OnDemand("queue", _ => ValueTask.CompletedTask, o => { o.MaxConcurrentPerJob = 4; o.OnOverlap = OverlapPolicy.RunAnyway; }),
            options: new QuickJobOptions { Time = time, MaxParallelism = 8 });
        await scheduler.StartAsync();

        var before = GcClean.LiveHeap();
        for (var round = 0; round < 20; round++)
        {
            for (var i = 0; i < 500; i++)
                _ = await scheduler.EnqueueAsync("queue", new { r = round, i }, time.GetUtcNow());
            await WaitUntil(() => scheduler.Stats.RunsCompleted >= (round + 1) * 500, TimeSpan.FromSeconds(20));
        }

        Assert.Equal(10_000, scheduler.Stats.RunsCompleted);
        Assert.Equal(0, scheduler.Stats.PendingRuns);
        Assert.Equal(0, scheduler.Stats.RunningRuns);
        Assert.Empty(scheduler.PendingRuns());

        var delta = GcClean.LiveHeap() - before;
        Assert.True(delta < 48L * 1024 * 1024, $"10k enqueued+ran runs retained {(delta / 1024.0 / 1024):F1} MB");

        await scheduler.StopAsync();
    }

    [Fact]
    public async Task History_cap_keeps_append_cost_flat()
    {
        IJobStore store = new InMemoryJobStore(new FakeTimeProvider(), historyCapacity: 128);
        var record = new ExecutionRecord
        {
            ExecutionId = Guid.NewGuid(),
            JobName = "flat",
            ScheduledAt = DateTimeOffset.UtcNow,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            Attempt = 1,
            Origin = JobTrigger.Manual,
            Outcome = "Completed",
        };

        // 预热到封环：cap 顶住之后每追加一条只从队头裁一条 —— 洪峰后仍要瞬时完成
        for (var i = 0; i < 1_000; i++)
            await store.AppendExecutionAsync(record);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++) await store.AppendExecutionAsync(record);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"history append collapsed: 10k in {stopwatch.Elapsed}");
        Assert.Equal(128, (await store.ReadExecutionsAsync(new ExecutionQuery { Take = 1_000 })).Count);
    }

    private static async Task WaitUntil(Func<bool> done, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    private sealed class Sink
    {
        public void OnEvent(QuickJobEvent _) { /* 活得明明白白，走得无影无踪 */ }
    }

    private sealed class HandlerHost
    {
        public ValueTask Run(JobRunContext ctx) => ValueTask.CompletedTask;
    }
}

/// <summary>StressTests 独占集合：与其它测试类隔离，GC 压力不混账。</summary>
[CollectionDefinition(nameof(StressCollection), DisableParallelization = true)]
#pragma warning disable xUnit1013 // 该档位为定义装点
public class StressCollection;
#pragma warning restore xUnit1013
