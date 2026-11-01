namespace QuickJob.Tests;

using QuickJob;

/// <summary>调度器生命周期 / 注册表 / DSL / 存档恢复那一条线。</summary>
public class SchedulerLifecycleTests
{
    [Fact]
    public void Create_registers_dsl_shapes_and_snapshots_them()
    {
        var scheduler = QuickJobScheduler.Create(b =>
        {
            b.Cron("nightly", "0 3 * * *", _ => ValueTask.CompletedTask);
            b.Interval("beat", TimeSpan.FromSeconds(30), _ => ValueTask.CompletedTask);
            b.OnDemand("ondemand", _ => ValueTask.CompletedTask);
            b.Once("lunch", new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), _ => ValueTask.CompletedTask);
        });

        var jobs = scheduler.ListJobs();
        Assert.Equal(4, jobs.Count);
        Assert.Contains(jobs, j => j.Name == "nightly" && j.Kind == JobKind.Cron && j.Cron == "0 3 * * *");
        Assert.Contains(jobs, j => j.Name == "beat" && j.Kind == JobKind.Interval && j.Every == TimeSpan.FromSeconds(30));
        Assert.Contains(jobs, j => j.Name == "ondemand" && j.Kind == JobKind.OnDemand);
        Assert.Contains(jobs, j => j.Name == "lunch" && j.Kind == JobKind.Once);
    }

    [Fact]
    public void Dsl_rejects_broken_schedules_at_registration()
    {
        Assert.Throws<QuickJobException>(() =>
            QuickJobScheduler.Create(b => b.Cron("bad", "not a cron", _ => ValueTask.CompletedTask)));

        Assert.Throws<QuickJobException>(() =>
            QuickJobScheduler.Create(b => b.Interval("bad2", TimeSpan.Zero, _ => ValueTask.CompletedTask)));

        Assert.Throws<QuickJobException>(() =>
            QuickJobScheduler.Create(b => b.OnDemand("", _ => ValueTask.CompletedTask)));
    }

    [Fact]
    public void Duplicate_names_replace_the_previous_definition()
    {
        var scheduler = QuickJobScheduler.Create(b =>
        {
            b.Interval("same", TimeSpan.FromMinutes(1), _ => ValueTask.CompletedTask);
            b.Cron("same", "0 12 * * *", _ => ValueTask.CompletedTask);
        });

        var job = scheduler.GetJob("same");
        Assert.NotNull(job);
        Assert.Equal(JobKind.Cron, job.Kind);
        Assert.Single(scheduler.ListJobs());
    }

    [Fact]
    public void Options_flows_into_snapshots()
    {
        var scheduler = QuickJobScheduler.Create(b => b.Cron("k", "0 5 * * *", _ => ValueTask.CompletedTask, o =>
        {
            o.MaxRetries = 2;
            o.MaxConcurrentPerJob = 3;
            o.OnOverlap = OverlapPolicy.Queue;
            o.Priority = JobPriority.High;
            o.Description = "knobbed";
            o.Enabled = false;
        }));

        var job = Assert.IsType<JobDefinition>(scheduler.GetJob("k"));
        Assert.Equal(2, job.Options.MaxRetries);
        Assert.Equal(3, job.Options.MaxConcurrentPerJob);
        Assert.Equal(OverlapPolicy.Queue, job.Options.OnOverlap);
        Assert.Equal(JobPriority.High, job.Options.Priority);
        Assert.Equal("knobbed", job.Options.Description);
        Assert.False(job.Options.Enabled);
    }

    [Fact]
    public async Task Start_then_stop_then_restart_is_refused()
    {
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("x", _ => ValueTask.CompletedTask));
        await scheduler.StartAsync();
        await scheduler.StopAsync();
        await Assert.ThrowsAsync<QuickJobException>(() => scheduler.StartAsync());
    }

    [Fact]
    public async Task Remove_drops_the_definition()
    {
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("gone", _ => ValueTask.CompletedTask));
        await scheduler.StartAsync();

        scheduler.Remove("gone");
        Assert.Null(scheduler.GetJob("gone"));
        Assert.Empty(scheduler.ListJobs());
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Pause_is_a_dashboard_visible_flag_and_runs_apply_it()
    {
        var scheduler = QuickJobScheduler.Create(b => b.Interval("p", TimeSpan.FromSeconds(10), _ => ValueTask.CompletedTask));
        await scheduler.StartAsync();

        scheduler.Pause("p");
        Assert.True(scheduler.GetJob("p")!.Paused);
        scheduler.Resume("p");
        Assert.False(scheduler.GetJob("p")!.Paused);

        Assert.Throws<QuickJobException>(() => scheduler.Pause("no-such"));
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Unresolved_stored_definitions_surface_as_ghosts()
    {
        var time = new FakeTimeProvider();
        IJobStore store = new InMemoryJobStore(time);
        var scheduler = QuickJobScheduler.Create(options: new QuickJobOptions { Store = store, Time = time });

        // 直接给 store 塞一条没有 handler 的定义（Kind 字符串档，带 Options 才算读得动）
        await store.SaveDefinitionAsync(new StoredDefinition
        {
            Name = "ghost",
            Contract = """{"Name":"ghost","Kind":"OnDemand","Options":{"Enabled":true}}""",
        });

        var anomalies = new List<string>();
        scheduler.Options.Diagnostics = anomalies.Add;
        await scheduler.StartAsync();

        var job = scheduler.GetJob("ghost");
        Assert.NotNull(job);
        Assert.Equal("ghost", job!.Name);
        Assert.Contains(anomalies, a => a.Contains("ghost"));
        await scheduler.StopAsync();
    }

    [Fact]
    public void Stats_are_untouched_before_start()
    {
        var scheduler = QuickJobScheduler.Create();
        Assert.Equal(0, scheduler.Stats.PendingRuns);
        Assert.Empty(scheduler.PendingRuns());
        Assert.Empty(scheduler.RunningRuns());
    }

    [Fact]
    public async Task Events_carry_run_lifecycle()
    {
        var gate = new object();
        var seen = new List<QuickJobEvent>();
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("e", ct => { ct.GetHashCode(); return ValueTask.CompletedTask; }));
        void Tap(QuickJobEvent e) { lock (gate) seen.Add(e); } // 并发 Publish 下裸 List.Add 会丢条目
        using var sub = scheduler.Events.Observe(Tap);

        await scheduler.StartAsync();
        _ = await scheduler.TriggerNowAsync("e");
        await WaitUntil(() => seen.Count >= 2, TimeSpan.FromSeconds(10));

        Assert.Contains(seen, e => e is RunStarted s && s.JobName == "e");
        Assert.Contains(seen, e => e is RunCompleted c && c.JobName == "e");
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
}
