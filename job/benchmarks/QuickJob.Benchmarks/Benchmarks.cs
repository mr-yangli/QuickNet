using BenchmarkDotNet.Attributes;
using NCrontab;
using TickerQ.Utilities;

namespace QuickJob.Benchmarks;

/// <summary>§性能基准的 BDN 用例。<b>与 <see cref="Gates"/> 的分工</b>：门禁给红绿（一条命令几秒、退出码报警），
/// 本文件给"人看的前后对比"（BDN 的显著性、误差条、Alloc Ratio 是 PR 附件该带的数字）——
/// 两边跑的是同一批调用形状，不会漂成两套代码。判据本身不用 BDN 出：几十分钟一场且要交叉验证环境，
/// CI 阻断项等不起。
/// <para><b>派生类不能 <c>sealed</c></b>：BDN 靠生成子类来跑基准，sealed 会在验证阶段整批判成无效
/// 并且只打一行提示，看起来像"跑完了但没数字"（sql 家同一条学费）。方法一律返回同步值：
/// 被 QuickJob 的 ValueTask 返回值只能被 await 一次 —— BDN 重放同一调用时第二个 ValueTask 会炸；
/// 所以这里的异步件都用 <c>.GetAwaiter().GetResult()</c> 收敛成同步形状（与门禁同口径）。</para>
/// <para><b>工具链在 Program.cs 里换成了 InProcessEmit</b>（而非这里贴 job 属性）：本机 SDK 是 net11 preview，
/// BDN 0.15.8（就是最新版）的 SDK 名片校验解不出 preview 名片，NotRecognized → NotImplemented 当场炸；
/// InProcess 不落子项目，那条校验路径整体不存在。代价与收益写在 Program.cs 的注释里。</para></summary>
[MemoryDiagnoser]
public abstract class BenchBase
{
    /// <summary>BDN 一个类只允许一个 <c>[GlobalSetup]</c>：派生类要扩展，override 这一份而不另起（属性在基方法上）。</summary>
    [GlobalSetup]
    public virtual void Setup()
    {
    }
}

/// <summary>门禁第 1 行的 BDN 腿：三档同形表达式上"一次下一次触发"，QuickJob 与 NCrontab（TickerQ 11.5 的
/// cron 引擎本体）两条腿同现场对跑。地基 <see cref="Support.Base"/>（UTC）在 setup 里落进字段 ——
/// 写成属性调用就会把"造一个 DateTimeOffset"计进被测量。<para>TickerQ 侧没有公开的 Next：
/// <c>CronScheduleCache</c> 是 internal，公开面只有校验壳 <c>CronExpression.Parse</c>（那一条腿进
/// <see cref="CronParseBench"/>）。两腿的分配与比值都只报告不判 —— 时间类对的是机器状态，
/// 预算与钉都在门禁行上（0.1.0-beta.1 已按首场实测重钉 32/32/32 B）。</para></summary>
[MemoryDiagnoser]
public class CronNextBench : BenchBase
{
    CronSchedule _quickFive = null!, _quickBusiness = null!, _quickSeconds = null!;

    CrontabSchedule _ncrFive = null!, _ncrBusiness = null!, _ncrSeconds = null!;

    DateTimeOffset _at;

    public override void Setup()
    {
        _quickFive = CronSchedule.Parse("*/5 * * * *");
        _quickBusiness = CronSchedule.Parse("0 9-17 * * 1-5");
        _quickSeconds = CronSchedule.Parse("*/30 * * * * *");

        _ncrFive = Support.ParseNcron("*/5 * * * *");
        _ncrBusiness = Support.ParseNcron("0 9-17 * * 1-5");
        _ncrSeconds = Support.ParseNcron("*/30 * * * * *");

        _at = Support.Base;
    }

    [Benchmark(Baseline = true, Description = "Next: QuickJob（*/5 * * * *）")]
    public DateTimeOffset? QuickJob_Each5() => _quickFive.Next(_at, Support.Zone);

    [Benchmark(Description = "Next: NCrontab（*/5 * * * *）")]
    public DateTime? NCrontab_Each5() => Support.NextNcron(_ncrFive, _at);

    [Benchmark(Description = "Next: QuickJob（0 9-17 * * 1-5）")]
    public DateTimeOffset? QuickJob_Business() => _quickBusiness.Next(_at, Support.Zone);

    [Benchmark(Description = "Next: NCrontab（0 9-17 * * 1-5）")]
    public DateTime? NCrontab_Business() => Support.NextNcron(_ncrBusiness, _at);

    [Benchmark(Description = "Next: QuickJob（*/30 * * * * * 带秒）")]
    public DateTimeOffset? QuickJob_Seconds() => _quickSeconds.Next(_at, Support.Zone);

    [Benchmark(Description = "Next: NCrontab（*/30 * * * * * 带秒）")]
    public DateTime? NCrontab_Seconds() => Support.NextNcron(_ncrSeconds, _at);
}

/// <summary>门禁第 2 行的 BDN 腿：三档 × 三家"解析一次"。三家的形状各不相同（<b>不判大小，只同场出数</b>）：
/// QuickJob 全量 vixie 位掩码（Register 一次后就留在 JobRuntime —— 这里量的是入场价不是稳态，注释里说清）；
/// TickerQ 公开壳（11.5 的 <c>CronExpression.Parse</c>，校验 + 5 字段补秒的前缀归一 —— 稳态取编译产物走
/// internal LRU，进入不了公开面）；NCrontab 引擎本体（每番全量编译，6 字段要 IncludingSeconds）。
/// <para>同形步进已在门禁第 1 行逐字判过，这里不重判 —— 三条腿各自量的是各自的入场价。</para></summary>
[MemoryDiagnoser]
public class CronParseBench : BenchBase
{
    // 三档表达式用 const 落死：方法签名里只此一处，别让字段引用把"常量折叠"的桂冠让给解释。
    const string Each5 = "*/5 * * * *";

    const string Business = "0 9-17 * * 1-5";
    const string Seconds = "*/30 * * * * *";

    [Benchmark(Baseline = true, Description = "Parse: QuickJob（*/5 * * * *）")]
    public CronSchedule QuickJob_Each5() => CronSchedule.Parse(Each5);

    [Benchmark(Description = "Parse: TickerQ 壳（*/5 * * * *）")]
    public CronExpression TickerQ_Each5() => CronExpression.Parse(Each5);

    [Benchmark(Description = "Parse: NCrontab（*/5 * * * *）")]
    public CrontabSchedule NCrontab_Each5() => Support.ParseNcron(Each5);

    [Benchmark(Description = "Parse: QuickJob（0 9-17 * * 1-5）")]
    public CronSchedule QuickJob_Business() => CronSchedule.Parse(Business);

    [Benchmark(Description = "Parse: TickerQ 壳（0 9-17 * * 1-5）")]
    public CronExpression TickerQ_Business() => CronExpression.Parse(Business);

    [Benchmark(Description = "Parse: NCrontab（0 9-17 * * 1-5）")]
    public CrontabSchedule NCrontab_Business() => Support.ParseNcron(Business);

    [Benchmark(Description = "Parse: QuickJob（*/30 * * * * * 带秒）")]
    public CronSchedule QuickJob_Seconds() => CronSchedule.Parse(Seconds);

    [Benchmark(Description = "Parse: TickerQ 壳（*/30 * * * * * 带秒）")]
    public CronExpression TickerQ_Seconds() => CronExpression.Parse(Seconds);

    [Benchmark(Description = "Parse: NCrontab（*/30 * * * * * 带秒）")]
    public CrontabSchedule NCrontab_Seconds() => Support.ParseNcron(Seconds);
}

/// <summary>门禁第 3 行的 BDN 腿：载荷 UTF-8 JSON 管道一次的价（5 字段样张：int/文字/浮点/时刻/枚举）。
/// 双向都量：序列化与反序列化是独立的两条腿（后者吃的是 setup 里先序列化好的字节数组 ——
/// 把"先 serialize 再 deserialize"串起来量，第二份的字节就混进第一份里了）。
/// store 文本两腿同场：<c>ToStoreText</c>/FromStoreText 是 JSON bytes ↔ 存档字符串的换柱，
/// 它们各自上册（门禁第 3 行的判据四条在门禁文件里）。</summary>
[MemoryDiagnoser]
public class PayloadBench : BenchBase
{
    Support.BenchPayload _sample = null!;

    byte[] _bytes = null!;

    string _storeText = null!;

    public override void Setup()
    {
        _sample = Support.BenchPayload.Sample();
        _bytes = PayloadJson.Serialize(_sample);
        _storeText = PayloadJson.ToStoreText(_bytes)!;
    }

    [Benchmark(Baseline = true, Description = "Payload Serialize（5 字段样张）")]
    public byte[] Serialize() => PayloadJson.Serialize(_sample);

    [Benchmark(Description = "Payload Deserialize（5 字段样张）")]
    public Support.BenchPayload? Deserialize() => PayloadJson.Deserialize<Support.BenchPayload>(_bytes);

    [Benchmark(Description = "Payload ToStoreText（bytes → 存档字符串）")]
    public string? ToStoreText() => PayloadJson.ToStoreText(_bytes);

    [Benchmark(Description = "Payload FromStoreText（存档字符串 → bytes）")]
    public byte[]? FromStoreText() => PayloadJson.FromStoreText(_storeText);
}

/// <summary>门禁第 4 行的 BDN 腿：定义契约 Write → TryLoad 的两腿与 Snapshot 一腿。
/// <b>三腿各自上册</b>：Snapshot 是 JobRuntime/看板每次轮询的读价，Write/TryLoad 是存档 seam 的写/回 ——
/// 把三者串成一场就能互相抵消，那正是本份存在的反面。</summary>
[MemoryDiagnoser]
public class DefinitionBench : BenchBase
{
    JobDefinition _def = null!;

    string _contract = null!;

    public override void Setup()
    {
        _def = Support.DefinitionSample();
        _contract = DefinitionSerialization.Write(_def);
    }

    [Benchmark(Baseline = true, Description = "Definition Write（全字段样张）")]
    public string Write() => DefinitionSerialization.Write(_def);

    [Benchmark(Description = "Definition TryLoad（同一份契约）")]
    public JobDefinition? TryLoad() => DefinitionSerialization.TryLoad(_contract);

    [Benchmark(Description = "Definition Snapshot（ListJobs/GetJob 的读价）")]
    public JobDefinition Snapshot() => _def.Snapshot();
}

/// <summary>门禁第 5 行的 BDN 腿：收件箱 10 颗一批的登记摊销。<see cref="_turn"/> 每次调用开新代：
/// <see cref="PendingRegistrations"/> 是进程全局、Known 字典去重重名会炸 —— 这是它的职责不是缺陷。
/// <para><b>内存纪律（本机首场全跑的实账）</b>：全局只进不出 + BDN 自动 pacing（65 536 op/轮 × ~15 轮
/// ≈ 百万次调用 × 10 颗 ≈ 千万颗 ≈ 3-4 GB Gen2）会当场把进程吃穿，还会把 InProcess 的整进程预算
/// 花完（同名基准两次兜底，一次"takes too long"）。所以这腿自行钉死两颗：① <see cref="FreshBox"/>
/// 每迭代起手清箱（<c>PendingRegistrations.Reset()</c>，ivt 内部面）；② <c>[InvocationCount(256)]</c>
/// 定次 —— 一轮 = 256 × 10 = 2 560 颗 ≈ 1 MB，上限由重放次数给出，不由 pilot 给出。</para>
/// <para><b>账的口径随此改了</b>：BDN 数字是「每迭代从空箱起手」的一批，门禁行 5 的钉是「累计态」
/// 窗内一批（Known 跨窗生长里的事件已摊还 O(1)）—— 同形状不同状态，两边别互相搬数。</para></summary>
[MemoryDiagnoser]
public class InboxBench
{
    /// <summary>进程全局的一代号：Add10 每调用 +1，10 颗新名在其下落名（<see cref="Support.InboxName"/>）。
    /// 跨迭代不 Reset —— 名字去重靠的是 Known（也每轮被清），代号继续走免得两颗不同的名字同名相撞。</summary>
    static int _turn;

    [IterationSetup]
    public void FreshBox() => PendingRegistrations.Reset();

    [Benchmark(Baseline = true, Description = "InboxAdd10（10 颗新名一批；每迭代空箱重建，定次 256）")]
    [InvocationCount(256)]
    public void Add10()
    {
        var turn = Interlocked.Increment(ref _turn);

        for (var i = 0; i < 10; i++)
        {
            PendingRegistrations.Add(new PendingRegistration
            {
                Name = Support.InboxName(turn, i),
                Cron = "*/5 * * * *",
                HandlerFactory = BenchHandler,
            });
        }
    }

    static JobHandler BenchHandler() => static _ => default;
}

/// <summary>属性声明侧的灵活时长文本：<see cref="DurationText"/>（internal，ivt 开了门）一次 Parse 的价。
/// <para>两条腿各守一条路径："00:05:00" 这类 TimeSpan 文本走 BCL <c>TimeSpan.TryParse</c> 的快道；
/// "5m" 这类后缀形式走首字符扫描 + 后缀换算（DSL 与 <c>[QuickJob]</c> 特性上最常写的形状）。
/// 没有第三腿（"不合法文本"的形状与 <c>QuickJobException</c> 一起走 throw 的路，
/// 是 tests 册的事、不该在基准里再走一遍）。</para></summary>
[MemoryDiagnoser]
public class DurationBench
{
    [Benchmark(Baseline = true, Description = "DurationText Parse（TimeSpan 文本 00:05:00）")]
    public TimeSpan Parse_TimeSpanText() => DurationText.Parse("00:05:00", "bench");

    [Benchmark(Description = "DurationText Parse（后缀 5m）")]
    public TimeSpan Parse_SuffixMinutes() => DurationText.Parse("5m", "bench");
}

/// <summary>门禁第 6 行的 BDN 腿：InMemoryJobStore 的<b>只读腿与恒池走转腿</b>，64 条种子池、假钟
/// <see cref="Support.FakeTimeProvider"/>（泵的 Change 是 noop —— 读数不跟真计时器走）。
/// <para><b>Peek</b> 只读池不动（OrderBy 全列表 —— O(n log n) 每拍，形状的账）。<b>ClaimChurn4</b>
/// 是 claim 4 + finish 4 + put 回池 4 的一循环：池恒 64 —— claim 侧的分配是这循环的一部分，
/// 它与门禁第 6 行的 claim 腿同形状；纯 put 腿进 <see cref="StorePutBench"/>（池不能让涨，理由在那边）。</para></summary>
[MemoryDiagnoser]
public class StoreWalkBench : BenchBase
{
    const int Pool = 64;

    Support.FakeTimeProvider _clock = null!;

    IJobStore _store = null!;

    public override void Setup()
    {
        _clock = new() { Now = Support.Base };
        _store = Support.SeededStore(_clock, Pool);
    }

    [Benchmark(Baseline = true, Description = "Store Peek take=8（64 条池，排序拍）")]
    public IReadOnlyList<StoredDueRun> Peek8() => _store.PeekDueRunsAsync(8).GetAwaiter().GetResult();

    [Benchmark(Description = "Store ClaimChurn4（claim 4 + finish 4 + put 回池 4，池恒 64）")]
    public void ClaimChurn4()
    {
        var batch = _store.ClaimDueRunsAsync("bench", 4).GetAwaiter().GetResult();

        foreach (var lease in batch)
        {
            _store.FinishLeaseAsync(lease, DueRunOutcome.Completed).GetAwaiter().GetResult();
            _store.PutDueRunAsync(lease.Run).GetAwaiter().GetResult();
        }
    }
}

/// <summary>门禁第 6 行的 put 腿的 BDN 面：<b>put 是唯一让池涨的腿</b>（每调用 +1 条）—— 一场 BDN 数千次
/// 调用后池从 64 涨到千余条，每次 put 里的 Min 扫描 O(n) 会把中位数拖成一条缓慢上坡的斜线，
/// 那样的数字不能读。所以本类用 <c>[IterationSetup]</c> 每轮重播 64 条种子（池恒 64）：
/// 种子代价在测量窗口之外（BDN 的 setup 时间不算进 invocations），唯一渗进读数的是"重播前的堆状态"——
/// 与门禁的 put 腿同形状（FreshRun 在测窗里造，与真实用法一致：put 的调用方总是带着一条新 run）。</summary>
[MemoryDiagnoser]
public class StorePutBench
{
    const int Pool = 64;

    Support.FakeTimeProvider _clock = null!;

    IJobStore _store = null!;

    [IterationSetup]
    public void Reset()
    {
        _clock = new() { Now = Support.Base };
        _store = Support.SeededStore(_clock, Pool);
    }

    [Benchmark(Baseline = true, Description = "Store Put 1（64 条池，每轮重播种子）")]
    public void Put1()
    {
        _store.PutDueRunAsync(new StoredDueRun
        {
            RunId = Guid.NewGuid(),
            JobName = "bench.store.put",
            DueAt = _clock.Now,
            Attempt = 1,
            Origin = JobTrigger.Enqueued,
            Payload = null,
        }).GetAwaiter().GetResult();
    }
}
