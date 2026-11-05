namespace QuickJob.Benchmarks;

using System.Globalization;
using NCrontab;
using TickerQ.Utilities;

/// <summary>门禁与基准共用的那批对照件。<b>两家的同形口径</b>：同一条表达式字符串交给两边的解析器
/// （QuickJob 5/6 字段与 NCrontab 5/6 字段-syntax 相同、vixie dom/dow 语义同源），同一条地基时刻
/// 在两边都按 UTC 求解 —— 时区那一维在 QuickJob.Next 里走的是当前 tz 参数（UTC 檔 = no-op 转换），
/// NCrontab 那侧 GetNextOccurrence 的日期派生自同一个 DateTimeOffset。
/// 三档表达式都不放 @别名 与月份名字：TickerQ 11.5 的公开壳不收别名档（它把原串直接解给 NCrontab），
/// 对照只比对两家各自声称的同形面。<b>本类 public 是被逼的</b>：BDN 基准类的公开方法签名里有
/// <see cref="BenchPayload"/>（CS0050），而类型可访问性以包含链为准 —— 放宽外层比抬嵌套类型省事，别再收紧。</summary>
public static class Support
{
    /// <summary>对照现场用 UTC：把"系统时区的机器状态"从对照里摘出去 —— 两家都在同一现场比的是引擎，不是环境。</summary>
    public static TimeZoneInfo Zone => TimeZoneInfo.Utc;

    /// <summary>地基时刻：2026-03-16 12:00 UTC。三档在此处的下一步都非空：分钟档 5 分钟后、
    /// 营业档同日 13:00、秒档 30 秒后。</summary>
    public static DateTimeOffset Base => new(new DateTime(2026, 3, 16, 12, 0, 0, DateTimeKind.Utc));

    /// <summary>同形三档：每 5 分钟 / 工作日营业时段整点 / 每 30 秒（6 字段带秒）。
    /// 第三档对 NCrontab 需要 <see cref="CrontabSchedule.ParseOptions.IncludingSeconds"/>，QuickJob 原生吃 6 字段。</summary>
    public static readonly string[] GateExpressions = ["*/5 * * * *", "0 9-17 * * 1-5", "*/30 * * * * *"];

    public static bool HasSeconds(string expression) => expression.Split(' ').Length >= 6;

    /// <summary>QuickJob 的下一步：严格后（<see cref="CronSchedule.Next"/> 的 inclusive=false 默认）。</summary>
    public static DateTimeOffset? NextQuickJob(CronSchedule schedule, DateTimeOffset from) => schedule.Next(from, Zone);

    /// <summary>NCrontab 的下一步，口径统一为严格后（把 current 推过 1 tick 再问）。NCrontab 3.3 的
    /// <c>GetNextOccurrence(DateTime)</c> 的"含不含当点"没有开关，也没有文档给死 —— 推过 1 tick 让两边
    /// 从同一个严格后起点出发，比猜测库语义要结实。</summary>
    public static DateTime? NextNcron(CrontabSchedule schedule, DateTimeOffset from)
        => schedule.GetNextOccurrence(DateTime.SpecifyKind(from.UtcDateTime.AddTicks(1), DateTimeKind.Utc));

    /// <summary>NCrontab 的解析：6 字段带 <see cref="CrontabSchedule.ParseOptions.IncludingSeconds"/>，5 字段原样。</summary>
    public static CrontabSchedule ParseNcron(string expression)
        => CrontabSchedule.Parse(expression, NcronOptions(expression));

    public static CrontabSchedule.ParseOptions NcronOptions(string expression)
        => HasSeconds(expression)
            ? new CrontabSchedule.ParseOptions { IncludingSeconds = true }
            : new CrontabSchedule.ParseOptions();

    /// <summary>两家各走 <paramref name="steps"/> 步后的序列（口径统一：步步严格后）。</summary>
    public static (List<DateTimeOffset> QuickJob, List<DateTime> Ncron) Walk(
        string expression, DateTimeOffset from, int steps = 3)
    {
        var quickJob = CronSchedule.Parse(expression);
        var ncron = ParseNcron(expression);

        var quickList = new List<DateTimeOffset>(steps);
        var ncronList = new List<DateTime>(steps);

        var atQuick = from;
        var atNcron = from;

        for (var i = 0; i < steps; i++)
        {
            var nextQuick = NextQuickJob(quickJob, atQuick);
            var nextNcron = NextNcron(ncron, atNcron);

            if (nextQuick is not { } q || nextNcron is not { } n) break;

            quickList.Add(q);
            ncronList.Add(n);

            atQuick = q;
            atNcron = q; // 两家共用同一个上一步（QuickJob 那格）为下一问的地基；NCrontab 侧在问的入口再推过 1 tick —— 严格后的口径两处收口在 NextNcron 一处
        }

        return (quickList, ncronList);
    }

    /// <summary>三档起步的序列等值断言：QuickJob 与 NCrontab 的每一步 UTC 时刻必须相等。断言破的不是慢，
    /// 而是「同形对照的地基本身没对齐」—— 门禁行 1 的判据 ① 建立在这上面。</summary>
    public static bool SameWalk(string expression, out string difference)
    {
        var (quick, ncron) = Walk(expression, Base, steps: 3);

        var at = 0;

        for (; at < quick.Count; at++)
        {
            if (at >= ncron.Count) { difference = $"step {at}: QuickJob 有、NCrontab 无（或反之，解析炸了）"; return false; }
            if (quick[at].UtcDateTime != ncron[at])
            {
                difference = $"step {at}: QuickJob {quick[at]:yyyy-MM-dd HH:mm:ss.fff} vs NCrontab {ncron[at]:yyyy-MM-dd HH:mm:ss.fff}";
                return false;
            }
        }

        difference = "";
        return true;
    }

    /// <summary>第 6 行的假钟：GetUtcNow 完全手控、CreateTimer 返回瘫版（泵 Change 换成 no-op ——
    /// InMemoryJobStore 的单只钟摆照常拨，只是不会真的走表），否则基准里会有真计时器在 residues 上撒分配。</summary>
    internal sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new NoopTimer();

        internal sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>第 3/4 行的载荷样张：字段面吃 WebOptions 上有形状差的每一种 —— int/文字/浮点/时刻/枚举
    /// （枚举在 WebOptions 上走 JsonStringEnumConverter，序列化形态是字符串而不是数字）。</summary>
    public sealed record BenchPayload(int Id, string Note, double Rate, DateTimeOffset At, JobTrigger Trigger)
    {
        public static BenchPayload Sample() => new(
            42,
            "invoice sketch",
            3.75,
            new DateTimeOffset(2026, 3, 16, 12, 0, 0, TimeSpan.Zero),
            JobTrigger.Manual);
    }

    /// <summary>第 4 行的定义样张：面覆盖 JobDefinition 全部承载的计算域（Source 也进 —— 那是声明来源那一格）。</summary>
    public static JobDefinition DefinitionSample(string name = "sample.cron.readme") => new()
    {
        Name = name,
        Kind = JobKind.Cron,
        Cron = "02 4 * * sat",
        Options = new JobOptions
        {
            MaxRetries = 3,
            RetryBackoff = RetryBackoff.Linear,
            RetryBaseDelay = TimeSpan.FromSeconds(5),
            RetryMaxDelay = TimeSpan.FromSeconds(30),
            RetryJitter = 0.1,
            MaxConcurrentPerJob = 2,
            OnOverlap = OverlapPolicy.Queue,
            OverlapQueueLimit = 32,
            Priority = JobPriority.High,
            OnMissedSlot = MissedSlotPolicy.CatchUpLatest,
            Timeout = TimeSpan.FromMinutes(2),
            Grace = TimeSpan.FromMinutes(10),
            Description = "benchmark sample definition",
        },
        Paused = false,
        Source = "QuickNet.Benchmarks.Sample.Type.Run",
    };

    /// <summary>第 6 行的既有 pending 池：<paramref name="pending"/> 条都到期（DueAt &lt; now），
    /// 使 claim 一拍就真能拿走 —— put 时 DueAt &le; now 会使 RunsPending 抖一下（无监听者，无害）。
    /// 参数收基类 <see cref="TimeProvider"/> 而不是假钟本体：本方法的可访问性是 public（BDN 的 CS0051 从这里来），
    /// 假钟类型是 internal —— 粒度在"这层只需要读钟"上收口，调用方照样传假钟。</summary>
    public static InMemoryJobStore SeededStore(TimeProvider clock, int pending, int historyCapacity = 512)
    {
        IJobStore store = new InMemoryJobStore(clock, historyCapacity);

        var now = clock.GetUtcNow();

        for (var i = 0; i < pending; i++)
        {
            store.PutDueRunAsync(new StoredDueRun
            {
                RunId = Guid.NewGuid(),
                JobName = $"bench.seed.{i}",
                DueAt = now.AddMilliseconds(-1_000 - i),
                Attempt = 1,
                Origin = JobTrigger.Enqueued,
                Payload = null,
            }).GetAwaiter().GetResult();
        }

        return (InMemoryJobStore) store;
    }

    /// <summary>第 5 行的收件箱登记名：每一轮必须开新名（Known 字典去重真的会炸重名 —— 那是它的职责不是缺陷）。</summary>
    public static string InboxName(int turn, int at)
        => string.Create(CultureInfo.InvariantCulture, $"bench.inbox.{turn}.{at}");
}
