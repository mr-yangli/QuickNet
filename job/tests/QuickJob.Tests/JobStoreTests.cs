namespace QuickJob.Tests;

using QuickJob;
using System.Collections.Concurrent;

/// <summary>
/// InMemoryJobStore 的合同：claim 的原子独占（CAS 语义下同一 run 只被一方拿走）、
/// lease 的收尾、future run 的到点解锁、历史环的容量截断与读法、定义面 CRUD。
/// 人口全走 IJobStore 显式接口；泵/触发完全靠事件面（RunsPending）。
/// </summary>
public class JobStoreTests
{
    private static IJobStore NewStore(FakeTimeProvider time, int historyCapacity = 2000)
        => new InMemoryJobStore(time, historyCapacity);

    [Fact]
    public async Task Past_due_put_raises_runs_pending_immediately()
    {
        var time = new FakeTimeProvider();
        var store = NewStore(time);
        var raised = 0;
        store.RunsPending += () => Interlocked.Increment(ref raised);

        await store.SaveDefinitionAsync(new StoredDefinition { Name = "a", Contract = "{}" });
        var raisedAfterDef = raised;
        await Task.Yield(); // 存定义不打泵

        await store.PutDueRunAsync(new StoredDueRun
        {
            RunId = Guid.NewGuid(),
            JobName = "a",
            DueAt = time.GetUtcNow() - TimeSpan.FromSeconds(1),
            Origin = JobTrigger.Enqueued,
        });

        Assert.Equal(0, raisedAfterDef);
        Assert.Equal(1, raised); // past-due 立即点灯
    }

    [Fact]
    public async Task Future_run_claims_nothing_until_it_is_due()
    {
        var time = new FakeTimeProvider();
        var store = NewStore(time);

        var run = new StoredDueRun
        {
            RunId = Guid.NewGuid(),
            JobName = "a",
            DueAt = time.GetUtcNow() + TimeSpan.FromMinutes(10),
            Origin = JobTrigger.Scheduled,
        };
        await store.PutDueRunAsync(run);

        Assert.Empty(await store.ClaimDueRunsAsync("inst", 10));
        Assert.Empty(await store.ClaimDueRunsAsync("inst", 10));

        time.Advance(TimeSpan.FromMinutes(11));
        var leases = await store.ClaimDueRunsAsync("inst", 10);
        Assert.Single(leases);
        Assert.Equal(run.RunId, leases[0].Run.RunId);
        await store.FinishLeaseAsync(leases[0], DueRunOutcome.Completed);
    }

    [Fact]
    public async Task Claim_honours_the_batch_size_then_drains()
    {
        var time = new FakeTimeProvider();
        var store = NewStore(time);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            await store.PutDueRunAsync(new StoredDueRun
            {
                RunId = id,
                JobName = "a",
                DueAt = time.GetUtcNow(),
                Origin = JobTrigger.Manual,
            });
        }

        var first = await store.ClaimDueRunsAsync("inst", 3);
        Assert.Equal(3, first.Count);
        var second = await store.ClaimDueRunsAsync("inst", 3);
        Assert.Equal(2, second.Count);
        Assert.Empty(await store.ClaimDueRunsAsync("inst", 3)); // 已声明的人不可再获

        var every = first.Concat(second).Select(l => l.Run.RunId).ToHashSet();
        Assert.Equal(ids.ToHashSet(), every);
    }

    [Fact]
    public async Task Claim_is_exclusive_under_concurrency()
    {
        var time = new FakeTimeProvider();
        var store = NewStore(time);
        const int total = 10_000;
        for (var i = 0; i < total; i++)
        {
            await store.PutDueRunAsync(new StoredDueRun
            {
                RunId = Guid.NewGuid(),
                JobName = "churn",
                DueAt = time.GetUtcNow(),
                Origin = JobTrigger.Enqueued,
            });
        }

        const int workers = 8;
        var claimed = new ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var leases = await store.ClaimDueRunsAsync($"w-{Environment.CurrentManagedThreadId}", 64);
                if (leases.Count == 0) return;
                foreach (var lease in leases) claimed.Add(lease.Run.RunId);
            }
        })));

        Assert.Equal(total, claimed.Count);
        Assert.Equal(total, claimed.Distinct().Count()); // 没有 run 被两方同时拿走
        Assert.Empty(await store.ClaimDueRunsAsync("late", 10));
    }

    [Fact]
    public async Task Lease_finish_is_quiet_and_definitions_roundtrip()
    {
        var time = new FakeTimeProvider();
        var store = NewStore(time);

        // 定义面：CRUD + newest-wins
        await store.SaveDefinitionAsync(new StoredDefinition { Name = "d", Contract = "{\"v\":1}" });
        await store.SaveDefinitionAsync(new StoredDefinition { Name = "d", Contract = "{\"v\":2}" });
        await store.SaveDefinitionAsync(new StoredDefinition { Name = "e", Contract = "{\"v\":1}" });
        var defs = await store.LoadDefinitionsAsync();
        Assert.Equal(2, defs.Count);
        Assert.Equal("{\"v\":2}", defs.Single(x => x.Name == "d").Contract);

        await store.DeleteDefinitionAsync("e");
        Assert.Single(await store.LoadDefinitionsAsync());
        await store.DeleteDefinitionAsync("absent"); // 缺席不炸

        // lease 收尾：幂等式的安静删除
        var run = Guid.NewGuid();
        await store.PutDueRunAsync(new StoredDueRun { RunId = run, JobName = "d", DueAt = time.GetUtcNow(), Origin = JobTrigger.Manual });
        var lease = (await store.ClaimDueRunsAsync("inst", 1)).Single();
        await store.FinishLeaseAsync(lease, DueRunOutcome.FailedFinal);
        Assert.Empty(await store.ClaimDueRunsAsync("inst", 1)); // 完更之后绝不回潮
    }

    [Fact]
    public async Task History_reads_newest_first_and_filters_by_job()
    {
        IJobStore store = NewStore(new FakeTimeProvider());
        for (var i = 1; i <= 5; i++)
        {
            await store.AppendExecutionAsync(new ExecutionRecord
            {
                ExecutionId = Guid.NewGuid(),
                JobName = i % 2 == 0 ? "even" : "odd",
                ScheduledAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow.AddSeconds(i),
                Attempt = i,
                Origin = JobTrigger.Manual,
                Outcome = "Completed",
            });
        }

        var page = await store.ReadExecutionsAsync();
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, page.Select(r => r.Attempt).ToArray()); // 后进先出
        Assert.Equal(new[] { 4, 2 }, (await store.ReadExecutionsAsync(new ExecutionQuery { Take = 10, JobName = "even" })).Select(r => r.Attempt).ToArray());
        Assert.Empty(await store.ReadExecutionsAsync(new ExecutionQuery { JobName = "ghost" }));
        Assert.Equal(new[] { 5 }, (await store.ReadExecutionsAsync(new ExecutionQuery { Take = 1 })).Select(r => r.Attempt).ToArray());
    }

    [Fact]
    public async Task History_capacity_keeps_the_newest_ring_only()
    {
        var store = NewStore(new FakeTimeProvider(), historyCapacity: 128);
        for (var i = 1; i <= 300; i++)
        {
            await store.AppendExecutionAsync(new ExecutionRecord
            {
                ExecutionId = Guid.NewGuid(),
                JobName = "history",
                ScheduledAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                Attempt = i,
                Origin = JobTrigger.Manual,
                Outcome = "Completed",
            });
        }

        var rows = await store.ReadExecutionsAsync(new ExecutionQuery { Take = 1000 });
        Assert.Equal(128, rows.Count);
        Assert.Equal(300, rows[0].Attempt);          // 最新在头
        Assert.Equal(173, rows[^1].Attempt);         // 1..172 已滚出
        Assert.Equal(Enumerable.Range(173, 128).Reverse().ToArray(), rows.Select(r => r.Attempt).ToArray());
    }

    [Fact]
    public async Task Trim_releases_records_older_than_the_instant()
    {
        IJobStore store = NewStore(new FakeTimeProvider());
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 4; i++)
        {
            await store.AppendExecutionAsync(new ExecutionRecord
            {
                ExecutionId = Guid.NewGuid(),
                JobName = "trim",
                ScheduledAt = now,
                StartedAt = now,
                FinishedAt = now.AddMinutes(i * 10),
                Attempt = i,
                Origin = JobTrigger.Manual,
                Outcome = "Completed",
            });
        }

        await store.TrimExecutionsAsync(now.AddMinutes(25));
        var rows = await store.ReadExecutionsAsync(new ExecutionQuery { Take = 100, JobName = "trim" });
        // FinishedAt = now+10/+20 分钟的行已过线（< olderThan 被清），now+30/+40 的留下
        Assert.Equal([4, 3], rows.Select(r => r.Attempt).ToArray());
    }
}
