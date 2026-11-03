namespace QuickJob.Tests;

using QuickJob;
using QuickJob.Dashboard;
using System.Text.Json;

/// <summary>
/// Dashboard 的冒烟档：EventRing 的容量滚动/取序/接线幂等、选项的规范化、页面字符串的完整性。
/// 不起 Kestrel —— 端点装配靠编译期把门，数据面靠环与快照的合同兜住。
/// </summary>
public class DashboardSmokeTests
{
    [Fact]
    public void Ring_starts_empty_and_is_idempotent_to_wire()
    {
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("e", _ => ValueTask.CompletedTask));
        var ring = new EventRing();

        Assert.Empty(ring.Recent(10));

        ring.Wire(scheduler);
        ring.Wire(scheduler);
        Assert.True(ring.Wired);
        Assert.Empty(ring.Recent(10));
    }

    [Fact]
    public async Task Ring_keeps_the_newest_200_newest_first()
    {
        var scheduler = QuickJobScheduler.Create(
            b => b.OnDemand("e", _ => ValueTask.CompletedTask, o => o.OnOverlap = OverlapPolicy.RunAnyway));
        var ring = new EventRing();
        var gate = new object();
        var all = new List<QuickJobEvent>();
        void Tap(QuickJobEvent e) { lock (gate) all.Add(e); } // 扇出被 Publish 单门串行；锁只是余量
        // 总序由引擎保证：QuickJobEvents.Publish 的扇出整段串行（stress 实测并发发布的相邻
        // 事件在两个各自持锁的 sink 间会换序），tap 与 dashboard 环看到同一发布序列。
        ring.Wire(scheduler);
        using var tap = scheduler.Events.Observe(Tap);
        await scheduler.StartAsync();

        // 105 次触发 × (RunStarted + RunCompleted) = 210 条 > 200 容量：最旧 10 条滚出
        for (var i = 0; i < 105; i++) _ = await scheduler.TriggerNowAsync("e");

        await WaitUntil(() => scheduler.Stats.RunsStarted >= 105 && scheduler.Stats.RunsCompleted >= 105,
            TimeSpan.FromSeconds(30)); // stats 是权威计次面，等全部 run 收场（此后不再有发布）

        // 等待条件 = 终局形态撮合：tap 尾与环头整段镜像对齐才摘定 —— 对第 210 条的在途发布
        // 也不再对赌（stats++ 可先于 Publish 落账，等条数会读到滞后快照，镜像断言偶发错位）
        await WaitUntil(() =>
        {
            var recent = ring.Recent(1000);
            List<QuickJobEvent> snapshot;
            lock (gate) snapshot = [.. all];
            if (snapshot.Count < 210 || recent.Count != 200) return false;
            for (var i = 0; i < 200; i++)
                if (!ReferenceEquals(snapshot[^(i + 1)], recent[i])) return false;
            return true;
        }, TimeSpan.FromSeconds(10));

        var recent = ring.Recent(1000); // take 钳到容量
        Assert.Equal(200, recent.Count);
        // 环里保留的正是最后 200 条发布事件，且倒序呈现（最新在头）—— 与埋点序列镜像对齐
        List<QuickJobEvent> snapshot;
        lock (gate) snapshot = [.. all];
        for (var i = 0; i < recent.Count; i++)
            Assert.Same(snapshot[^(i + 1)], recent[i]);

        Assert.Equal(50, ring.Recent(50).Count);
        await scheduler.StopAsync();
    }

    [Fact]
    public void Mount_root_normalises_the_prefix()
    {
        Assert.Equal("/quickjob", new QuickJobDashboardOptions().MountRoot);
        Assert.Equal("/quickjob", new QuickJobDashboardOptions { PathPrefix = "quickjob/" }.MountRoot);
        Assert.Equal("/ops/jobs", new QuickJobDashboardOptions { PathPrefix = "/ops/jobs/" }.MountRoot);
        Assert.Equal("/quickjob/auth/callback", new QuickJobDashboardOptions().CallbackPath);
        Assert.Equal(DashboardAuthMode.Anonymous, new QuickJobDashboardOptions().AuthMode);
        Assert.False(new QuickJobDashboardOptions().ReadOnly);
    }

    [Fact]
    public void Page_carries_the_skeleton_and_the_polling_surface()
    {
        var html = DashboardAssets.Html;
        Assert.Contains("QuickJob Dashboard", html);
        Assert.Contains("/stats", html);
        Assert.Contains("/jobs", html);
        Assert.Contains("/runs/pending", html);
        Assert.Contains("/runs/running", html);
        Assert.Contains("/executions", html);
        Assert.Contains("/events", html);
        Assert.Contains("/me", html);
        Assert.Contains("data", html); // 连接徽标的落点
    }

    [Fact]
    public async Task Event_face_keeps_the_derived_fields_when_typed_as_object()
    {
        var scheduler = QuickJobScheduler.Create(b => b.OnDemand("probe", _ => ValueTask.CompletedTask));
        var ring = new EventRing();
        ring.Wire(scheduler);
        await scheduler.StartAsync();
        await scheduler.TriggerNowAsync("probe");
        await WaitUntil(() => scheduler.Stats.RunsCompleted >= 1, TimeSpan.FromSeconds(20));
        await scheduler.StopAsync();

        // 端点把元素抬成 object 再序列化：按 IReadOnlyList<QuickJobEvent> 的基类声明走，STJ 只写
        // Kind/At，JobName/Error 全丢 —— 面板事件流就只剩一排 "?"。这里钉住抬型这一手。
        var json = JsonSerializer.Serialize(ring.Recent(10).Select(static e => (object)e).ToList());
        Assert.Contains("probe", json);
        Assert.Contains("JobName", json);
        Assert.Contains("ExecutionId", json);
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
