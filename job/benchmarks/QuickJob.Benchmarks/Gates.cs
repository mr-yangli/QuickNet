using System.Text;

namespace QuickJob.Benchmarks;

enum Verdict
{
    /// <summary>达标。</summary>
    Green,

    /// <summary>不达标。<b>红不是事故，是账</b>：这一行要在 docs 的门禁账里有对应的一格，写清为什么红、什么时候能绿。</summary>
    Red,

    /// <summary>这条门禁现在没有可测对象（被门禁的东西还没实现）。不拿「没实现」冒充「达标」。</summary>
    NotJudged,

    /// <summary>判据不在这个进程里 —— 由构建/测试册承担。</summary>
    Delegated,
}

sealed record GateRow(string Metric, string Budget, string Measured, Verdict Verdict, string Note, Pin[]? Pins = null);

/// <summary>钉的方向：<see cref="Worse"/> 是"比基线差"，<see cref="Better"/> 是"比基线好但还没钉下来"，
/// 两者都不改变这一行的红绿 —— 改变的只是退出码里那位回归标记。</summary>
enum Drift
{
    /// <summary>与基线一致（或在容差内）。</summary>
    Aligned,

    /// <summary>比基线好，但基线常量还没跟着改：下一次漂回旧值就报不出来了。</summary>
    Better,

    /// <summary>比基线差。这一位是本次补钉要抓的东西。</summary>
    Worse,
}

/// <summary>一条<b>基线钉</b>。阈值和钉管的是两件事：
/// 阈值问"该不该达标"，钉问"不许比上一次观测更差"。
/// 基线钉是 QuickJob 家自己的闸门 —— 没有它，光把预算判绿等于没有闸门：
/// 一个从 12 B 涨到 20 000 B 的 PR 可以什么都不改就照样过门，
/// 因为预算那一行判的是形状与判据而不是大小，涨的那部分只有钉看得见。
/// <para>QuickJob 首笔的钉一律照 <c>0.1.0-beta.1</c> 的首场 <c>--gate</c> 实测从 0 上册
/// （先把 0 写进常量跑一场，锁定形状判据全绿，再按首场实测重钉并回填来源场）。
/// 字节/计数类是<b>逐字相等</b>的两侧都报（<see cref="OneSided"/> = false）：那些数是确定量，
/// 抖了就值得看一眼，而"变好也没人重钉"会让网眼悄悄留大。<b>时间类只能单向钉</b>
/// （<see cref="OneSided"/> = true），并且 QuickJob 判据的经典形状：预算判达标，钉判「不许变更差」，两件事缺一不可。</para></summary>
sealed record Pin(
    string Shape,
    double Baseline,
    double Actual,
    bool LowerIsBetter = true,
    bool OneSided = false,
    Func<double, string>? Show = null,
    string? Note = null)
{
    /// <summary>把数值写成这一行本来用的样子（字节 "11688"、比值 "84%"），不另起一套格式。</summary>
    public string Text(double value) => Show is null ? Plain(value) : Show(value);

    public Drift Kind
    {
        get
        {
            if (Actual == Baseline) return Drift.Aligned;

            var isBetter = Actual - Baseline < 0 == LowerIsBetter;

            return isBetter && OneSided ? Drift.Aligned : isBetter ? Drift.Better : Drift.Worse;
        }
    }

    /// <summary>偏差的绝对量，给输出用（带符号没有意义 —— 方向已经由 <see cref="Kind"/> 说了）。</summary>
    public string Delta => Text(Math.Abs(Actual - Baseline));

    /// <summary>单向钉只有"塌了/没塌"两态，所以对齐那一格说的是地板而不是"一致"。</summary>
    public string Head => (OneSided ? "地板 " : "钉在 ") + Text(Baseline);

    public string Reading => Kind switch
    {
        Drift.Aligned => OneSided ? "在地板之上，不追高" : "一致",
        Drift.Better => "比基线好 " + Delta + " —— 重钉成新的基线才算跟上，否则网眼留着",
        _ => "比基线差 " + Delta + " ← 回归",
    };

    static string Plain(double value) => value.ToString("0.##");
}

/// <summary>门禁表：<b>预算管达标，钉管「不许变更差」，两件事缺一不可</b> —— 只有预算时，
/// 第 1 行从 12 B 涨到 20 000 B 也仍然只是那几条绿；只有钉时，首场之前没有任何「该不该绿」的口径。
/// <para>退出码 = 红行条数（不达标时非 0，就是 CI 阻断项该有的形状），另有红行<b>比基线差</b>时加
/// <see cref="RegressionFlag"/>。<b>CI 硬断言的只有"这一位没亮"</b> —— 前一半今天可能 flake（时间类行随机器状态翻），
/// 钉住的那一半（字节/计数，确定量）才配当闸门。</para>
/// <para>QuickJob 首笔的预算全是<b>形状判据</b>（往返等值、不炸、不超线性、双收窄后 0 处），
/// 数值上册_every 一颗钉为 0，等的正是首场实测 —— 没有预告的数、更没有从对照库抄来的数：
/// 对照库（TickerQ/NCrontab）给的是同现场的第二把尺，不是预算本身。</para></summary>
static class Gates
{
    /// <summary>内存库存两档（第 6 行两点法的两个点）：8 条起池、64 条大池。
    /// 从 8 到 64 是 8 幅，O(n) 形状的读数也长 8 幅 —— 判据的上限就长在这个形状上。</summary>
    public const int PendingShort = 8, PendingLong = 64;

    /// <summary>红行（以及那条会变差的绿行）的基线常量。首笔的纪律是「先以 0 上册，锁形状；首场 --gate 印实测
    /// 后逐颗重钉并回填来源场」（sql 家 v0.20/0.23/0.27 三代同一条先例）—— <b>首场已完</b>：
    /// 0.1.0-beta.1 的 --gate（2026-11-04，本机 Release）15 颗全重钉，来源场逐颗写在各自的 summary 里。
    /// 重钉规则照 sql 家第 8 行：<b>变差要红，变好也要有人把它钉下来</b>，每一次重钉都要在 docs 的门禁账里留下归属。</summary>
    static class Baselines
    {
        /// <summary>第 1 行：三档各「一次 Next 的一次托管分配」。NCrontab 同档腿是 0 分配
        /// （tickerq 家自家对照表里的记录），QuickJob 这三格是<b>首场实测重钉</b>
        /// （0.1.0-beta.1 首场 --gate 2026-11-04：三档同 32 B —— EscapePost 的 tz 参数面上一次
        /// <c>Next</c> 的常数袋。QuickJob≡NCrontab 的同形步进三档 × 3 步逐字相等）。</summary>
        public const double CronNextEach5Bytes = 32;
        public const double CronNextBusinessBytes = 32;
        public const double CronNextSecondsBytes = 32;

        /// <summary>第 2 行：三档各「一次 Parse 的托管分配」（QuickJob 全量解析）。首场实测重钉
        /// （0.1.0-beta.1 首场 --gate 2026-11-04：416/624/456 B —— 三档涨法跟字段面走，营业档表达式长一格
        /// 所以 624。同场对照：TickerQ 壳 288/312/280 B、NCrontab 全量 1704/1776/1992 B —— 三家口径不同，别互搬）。</summary>
        public const double CronParseEach5Bytes = 416;
        public const double CronParseBusinessBytes = 624;
        public const double CronParseSecondsBytes = 456;

        /// <summary>第 3 行：载荷 serialize / deserialize 各一次的托管分配。首场实测重钉
        /// （0.1.0-beta.1 首场 --gate 2026-11-04：serialize 128 B / deserialize 272 B —— 5 字段样张）。</summary>
        public const double PayloadSerializeBytes = 128;
        public const double PayloadDeserializeBytes = 272;

        /// <summary>第 4 行：契约 Write / TryLoad / Snapshot 各一次的托管分配。首场实测重钉
        /// （0.1.0-beta.1 首场 --gate 2026-11-04：1360 / 1032 / 208 B —— 样张契约 510 字符，Snapshot 不走 JSON 最轻）。</summary>
        public const double DefinitionWriteBytes = 1360;
        public const double DefinitionTryLoadBytes = 1032;
        public const double DefinitionSnapshotBytes = 208;

        /// <summary>第 5 行：10 颗登记入收件箱一整批的托管分配（每颗 = ÷10）。首场实测重钉
        /// （0.1.0-beta.1 首场 --gate 2026-11-04：2400 B ≈ 240 B/颗，带 cron 串）。</summary>
        public const double InboxAdd10BatchBytes = 2400;

        /// <summary>第 6 行：put 一条 / take=8 peek 一拍 / claim 4 + 回池 4 一循环，在 64 条池上的托管分配。
        /// 首场实测重钉（0.1.0-beta.1 首场 --gate 2026-11-04：112 / 2168 / 448 B —— peek 的 2168 B 是
        /// OrderBy 全列表 + 8 条装袋的价，8 条池那一腿 600 B 作旁证 —— 两腿差 8→64 之间 12 幅内）。</summary>
        public const double StorePut64Bytes = 112;
        public const double StorePeek64Bytes = 2168;
        public const double StoreClaim64Bytes = 448;
    }

    /// <summary>有红行比基线更差时加到退出码上的那一位。<b>CI 要硬断言的是这一位没亮</b>，
    /// 而不是"退出码等于 4"：前一半（红行数）会随负载翻 —— 时间类行随机器状态翻
    /// （sql 家 28 场里有过 92% 那一次），所以两半的强度不是一回事，
    /// 钉住的那一半（字节/计数，确定量）才配当闸门。
    /// <para>与 Debug 构建的 2 不冲突：那位是"0 条红 + 2"，而这一位只会加在已有红行数之上
    /// （真正的歧义是"恰好 2 条红"，那是这一位设计之前就有的）。</para></summary>
    public const int RegressionFlag = 32;

    public static int Run()
    {
        var rows = Collect();
        var aligned = 0;
        var improved = 0;
        var regressed = 0;

        foreach (var row in rows)
        {
            Console.WriteLine($"[{Mark(row.Verdict)}] {row.Metric}");
            Console.WriteLine($"       预算  {row.Budget}");
            Console.WriteLine($"       实测  {row.Measured}");

            foreach (var pin in row.Pins ?? [])
            {
                switch (pin.Kind)
                {
                    case Drift.Aligned: aligned++; break;
                    case Drift.Better: improved++; break;
                    default: regressed++; break;
                }

                Console.WriteLine($"       基线  [{Mark(pin.Kind)}] {pin.Shape} {pin.Head}，实测 {pin.Text(pin.Actual)}（{pin.Reading}）"
                    + (pin.Note is null ? "" : " —— " + pin.Note));
            }

            Console.WriteLine($"       说明  {row.Note}");
            Console.WriteLine();
        }

        Console.WriteLine($"QuickJob 门禁：{Count(Verdict.Green)} 绿 / {Count(Verdict.Red)} 红 / "
            + $"{Count(Verdict.NotJudged)} 不判 / {Count(Verdict.Delegated)} 交给构建");

        var red = Count(Verdict.Red);
        var pinned = aligned + improved + regressed;

        Console.WriteLine($"       基线钉：{pinned} 处一致 {aligned}"
            + (improved + regressed == 0
                ? "（红行不会因为变更差而悄悄通过）"
                : $"、比基线好而没重钉 {improved}、回归 {regressed}"
                  + (regressed == 0 ? " —— 前者不阻断，后者单独进退出码" : $" ← 单独进退出码（+{RegressionFlag}）")));

        return red + (regressed == 0 ? 0 : RegressionFlag);

        int Count(Verdict verdict) => rows.Count(r => r.Verdict == verdict);
    }

    static string Mark(Drift drift) => drift switch
    {
        Drift.Aligned => "＝",
        Drift.Better => "↑",
        _ => "↓",
    };

    static string Mark(Verdict verdict) => verdict switch
    {
        Verdict.Green => "绿",
        Verdict.Red => "红",
        Verdict.NotJudged => "不判",
        _ => "构建",
    };

    static List<GateRow> Collect() =>
    [
        CronNext(),
        CronParse(),
        PayloadRoundtrip(),
        DefinitionRoundtrip(),
        InboxAdd10(),
        InMemoryStoreWalk(),
        ReflectionShapes(),
    ];

    // ---------- 第 1 行：Cron 下一次触发 ----------

    /// <summary>§性能门禁第 1 行：三档同形表达式上，「一次下一次触发」的 QuickJob 端价与对照端价。
    /// <para>判据不判快慢 —— <b>判的是同形</b>：QuickJob 的 5/6 字段 cron（vixie dom/dow 语义）与 NCrontab 3.3
    /// 在同一条表达式、同一条地基时刻、同一处 UTC 现场上，往后三步的序列必须逐字相等。
    /// 这一条是最要紧的：注释里写的「语义同源」要找一场真正的步进见证，本行的三档 × 3 步就是那场。
    /// <para>两腿各自一次 Next 的分配照比与 µs 都仅仅报告不判 —— <b>QuickJob 首笔没有立过「下一次触发 0 分配」的预算</b>，
    /// 也没有写过「比 NCrontab 快/慢多少倍」的账。跨行情旅游收不到台对标：NCrontab 是同现场第二把尺，
    /// tickerq 家自家基准也是它的主腿（"TickerQ (NCrontab)" 立场）。</para></summary>
    static GateRow CronNext()
    {
        var walkOk = true;
        var walkDetail = new List<string>();
        var quickBytes = new long[Support.GateExpressions.Length];
        var ncronBytes = new long[Support.GateExpressions.Length];
        var ratios = new (double Value, double Low, double High, double QuickNanos, double NcronNanos)[Support.GateExpressions.Length];

        for (var i = 0; i < Support.GateExpressions.Length; i++)
        {
            var expression = Support.GateExpressions[i];

            var schedule = CronSchedule.Parse(expression);
            var ncron = Support.ParseNcron(expression);

            walkOk &= Support.SameWalk(expression, out var diff);
            walkDetail.Add($"{expression}: {(string.IsNullOrWhiteSpace(diff) ? "逐字相等" : diff)}");

            quickBytes[i] = Measuring.Bytes(() => _ = Support.NextQuickJob(schedule, Support.Base));
            ncronBytes[i] = Measuring.Bytes(() => _ = Support.NextNcron(ncron, Support.Base));

            var (ratio, quickNanos, ncronNanos, low, high) = Measuring.Ratio(
                () => { _ = Support.NextQuickJob(schedule, Support.Base); return Task.CompletedTask; },
                () => { _ = Support.NextNcron(ncron, Support.Base); return Task.CompletedTask; });

            ratios[i] = (ratio, low, high, quickNanos, ncronNanos);
        }

        var quickTotal = quickBytes.Sum();
        var ncronTotal = ncronBytes.Sum();

        var measured = "三档 × 3 步同形步进（QuickJob ⊝ NCrontab）："
            + string.Join("；", walkDetail)
            + $"。一次 Next{Environment.NewLine}       托管分配：QuickJob {ByteRow(quickBytes)}（合计 {quickTotal} B）/"
            + $" NCrontab {ByteRow(ncronBytes)}（合计 {ncronTotal} B）{Environment.NewLine}"
            + "       一次 Next 的中位 µs 与 QuickJob:NCrontab 之比（只报不钉 —— 时间类对的是机器状态）："
            + RatioRow(ratios);

        // 首笔预算只有同形这一条：字节与时间全上册，下一笔把真预算写进来（先立骨架，不预告数）。
        var verdict = walkOk ? Verdict.Green : Verdict.Red;

        const string note = "「同形步进」是这一行的唯一判据，四条都是它的一部分：QuickJob 的 cron 是 vixie 语义"
            + "（dom/dow 双受限抑或 OR），NCrontab 3.3 同源同语义 —— 三档 × 3 步逐字相等是这一句的第一场实测见证，"
            + "不是注释说了算。写字符串上 QuickJob 支持@别名与年份档，NCrontab 3.3 两者不全，所以对照只放两家都接的同形三档"
            + "（@别名的那一档归 tests 册收）；同型对照的口径要一直说白，别把它写成「谁更快」的榜。"
            + "一次 Next 的分配两腿各记 —— NCrontab 是 tickerq 家自家对照表里那条已知 0 分配的腿，"
            + "QuickJob 三档已按首场实测重钉（32 B/档）。µs 只报不钉（时间类对机器状态见头注），"
            + "但同场的比值还在说明串里，别从别处裸搬。三档各在自己的地基上取读：都是 Support.Base 那一次，"
            + "别把不同档的读数互相抵。";

        return new GateRow(
            $"Cron 下一次触发（{Support.GateExpressions.Length} 档同形表达式 × 3 步）",
            "三档步进序列逐字相等（QuickJob ≡ NCrontab，同地基 UTC 现场）；两腿一次 Next 的分配与 µs 各自上册（钉）不判",
            measured,
            walkOk ? Verdict.Green : Verdict.Red,
            note,
            Pins: [
                new Pin("每 5 分钟档一次 Next 的托管分配", Baselines.CronNextEach5Bytes, quickBytes[0]),
                new Pin("营业时段档一次 Next 的托管分配", Baselines.CronNextBusinessBytes, quickBytes[1]),
                new Pin("秒档一次 Next 的托管分配", Baselines.CronNextSecondsBytes, quickBytes[2]),
            ]);
    }

    static string ByteRow(long[] sizes) => string.Join(" / ", sizes.Select(b => $"{N(b)} B"));

    static string NanosRow(double[] nanos) => string.Join(" / ", nanos.Select(ns => $"{N(ns / 1000)} µs"));

    static string RatioRow((double Value, double Low, double High, double QuickNanos, double NcronNanos)[] ratios)
        => string.Join("；", ratios.Select((r, i)
            => $"{N(r.QuickNanos / 1000)} µs vs {N(r.NcronNanos / 1000)} µs（×{N(r.Value)}，全距 {N(r.Low)}~{N(r.High)}）"));

    // ---------- 第 2 行：Cron Parse ----------

    /// <summary>第 2 行：三档同形表达式上，两边「解析一次」的入场价。
    /// <para>三家各量：QuickJob 全量解析（vixie 位掩码，注册一次后就留在 JobRuntime 上）、
    /// TickerQ 公开壳（11.5 的 CronExpression.Parse —— 校验 + LRU 取编译产物，稳态是<b>缓存命中</b>的形状）、
    /// NCrontab 引擎本体（每番全量编译）。三家形状各不相同，费用只各自上册 —— 别拿 TickerQ 的 LRU 当减法要求，
    /// 也别把 QuickJob 的全量解析当缺陷，<b>两家的稳态都在「留存产物」上兑现</b>：QuickJob 存在 JobRuntime，
    /// TickerQ 存在 CronScheduleCache。<b>同形步进已在第 1 行逐字判过</b>，本行不再重判 —— 只判「三档三家都拿到结果不炸」。</para></summary>
    static GateRow CronParse()
    {
        var shapesOk = true;
        var quickBytes = new long[Support.GateExpressions.Length];
        var tickerBytes = new long[Support.GateExpressions.Length];
        var ncronBytes = new long[Support.GateExpressions.Length];
        var quickNanos = new double[Support.GateExpressions.Length];
        var tickerNanos = new double[Support.GateExpressions.Length];
        var ncronNanos = new double[Support.GateExpressions.Length];

        for (var i = 0; i < Support.GateExpressions.Length; i++)
        {
            var expression = Support.GateExpressions[i];

            shapesOk &= QuickJobTemplateHolds(expression);

            quickBytes[i] = Measuring.Bytes(() => _ = CronSchedule.Parse(expression));
            tickerBytes[i] = Measuring.Bytes(() => _ = TickerQ.Utilities.CronExpression.Parse(expression));
            ncronBytes[i] = Measuring.Bytes(() => _ = Support.ParseNcron(expression));

            quickNanos[i] = Measuring.Nanoseconds(() => _ = CronSchedule.Parse(expression));
            tickerNanos[i] = Measuring.Nanoseconds(() => _ = TickerQ.Utilities.CronExpression.Parse(expression));
            ncronNanos[i] = Measuring.Nanoseconds(() => _ = Support.ParseNcron(expression));
        }

        var measured = $"三档 × 三家 Parse 一次的托管分配："
            + $"QuickJob {ByteRow(quickBytes)} / TickerQ(壳,稳态=LRU) {ByteRow(tickerBytes)} / NCrontab {ByteRow(ncronBytes)}；"
            + $"QuickJob 中位 µs {NanosRow(quickNanos)}、TickerQ {NanosRow(tickerNanos)}、NCrontab {NanosRow(ncronNanos)}"
            + "（µs 只报不钉 —— 时间类对的是机器状态）";

        return new GateRow(
            $"Cron 解析一次（{Support.GateExpressions.Length} 档 × {3} 家）",
            "三档三家 Parse都拿到结果不炸；QuickJob 的 Template 归一化保真；三家分配各自上册（钉）不判",
            measured,
            shapesOk ? Verdict.Green : Verdict.Red,
            "TickerQ 公开壳的稳态是 LRU 取编译产物（首遍真编译），NCrontab 是每番全量，QuickJob 是全量位掩码 —— 三家形状不同，"
            + "费用只从本行各自领账。QuickJob 在 Register 那一次就解析并留在 JobRuntime 上，Next 每走一次都是免费的 —— "
            + "别把本行读成「每 tick 一次 parse」的引擎成本（QuickJob 代表那个号也没被运行过）。"
            + "字节已按首场实测重钉（416/624/456 B）—— 它只管回归，不立下笔的预算。",
            Pins: [
                new Pin("每5分钟档一次 QuickJob Parse 的托管分配", Baselines.CronParseEach5Bytes, quickBytes[0]),
                new Pin("营业时段档一次 QuickJob Parse 的托管分配", Baselines.CronParseBusinessBytes, quickBytes[1]),
                new Pin("秒档一次 QuickJob Parse 的托管分配", Baselines.CronParseSecondsBytes, quickBytes[2]),
            ]);
    }

    static bool QuickJobTemplateHolds(string expression)
    {
        var schedule = CronSchedule.Parse(expression);

        return schedule.Template == expression; // 三档本身已是规整形态（无别名无多余空格），Template 应原位保真
    }

    // ---------- 第 3 行：PayloadJson 往返 ----------

    /// <summary>第 3 行：载荷 UTF-8 JSON 管道 (serialize/deserialize) 一次的价，连同四条形状判据。
    /// <para>判据全为形状：往返等值（record 含枚举）、枚举在存档态是字符串（WebOptions 上 JsonStringEnumConverter）、
    /// store 文本双向退化（ToStoreText → FromStoreText 是同一字节串）、null 双向退化（empty []/null）——
    /// 这四条任何一条自己围不拢就红，线守着的就是「这条管道是不是还像它注释说的那样」。</para></summary>
    static GateRow PayloadRoundtrip()
    {
        var sample = Support.BenchPayload.Sample();
        var bytes = PayloadJson.Serialize(sample);

        var loaded = PayloadJson.Deserialize<Support.BenchPayload>(bytes);
        var roundtripOk = loaded == sample;
        var enumTextOk = Encoding.UTF8.GetString(bytes).Contains("\"Manual\"", StringComparison.Ordinal);
        var storeTextOk = PayloadJson.FromStoreText(PayloadJson.ToStoreText(bytes)) is { } rebuilt
                          && PayloadJson.Deserialize<Support.BenchPayload>(rebuilt) == sample;
        var nullOk = PayloadJson.Serialize(null).Length == 0 && PayloadJson.Deserialize<Support.BenchPayload>(null) is null;

        var serializeBytes = Measuring.Bytes(() => _ = PayloadJson.Serialize(sample));
        var deserializeBytes = Measuring.Bytes(() => _ = PayloadJson.Deserialize<Support.BenchPayload>(bytes));

        var shapeOk = roundtripOk && enumTextOk && storeTextOk && nullOk;

        var measured = $"载荷 {sizeof(int) + 16 + sizeof(double) + 16 + 4 + 16} 字节级样张（int/文字/浮点/时刻/枚举 5 字段）"
            + $":往返等值 {Yes(roundtripOk)}、枚举成字符串存档 {Yes(enumTextOk)}、store 文本同渠同值 {Yes(storeTextOk)}、"
            + $"null 双向退化 {Yes(nullOk)}；serialize {N(serializeBytes)} B/次、deserialize {N(deserializeBytes)} B/次";

        return new GateRow(
            "PayloadJson 往返（BenchPayload 5 字段）",
            "往返等值 + 枚举字符串存档 + store 文本同渠同值 + null 双向退化；两腿分配各自上册（钉）不判",
            measured,
            shapeOk ? Verdict.Green : Verdict.Red,
            "四条判据各守一格承诺：往返等值是「typed-request 链上 payload 只序列化两次而不走第三次」那句的前提；"
            + "枚举字符串档是「存档态里枚举保真排版」那条设计的存在；store 文本同渠是「JSON 文本即 store 格式」那句 —— "
            + "FromStoreText 真造一条新 bytes 串（serialize 到 UTF-8 bytes 的第二步），这一双它的价要记进批账。"
            + "分配两腿已按首场实测重钉（serialize 128 B / deserialize 272 B）—— 只管回归，不立下笔的预算。",
            Pins: [
                new Pin("serialize 一次的托管分配", Baselines.PayloadSerializeBytes, serializeBytes),
                new Pin("deserialize 一次的托管分配", Baselines.PayloadDeserializeBytes, deserializeBytes),
            ]);
    }

    static string Yes(bool ok) => ok ? "✓" : "✗（红）";

    // ---------- 第 4 行：DefinitionSerialization 往返 ----------

    /// <summary>第 4 行：存档契约 Write → TryLoad 的回环与 Snapshot 的存档投影，连同它的分配价。
    /// <para>判据全为形状：Write → TryLoad 全字段逐位等（含 Source）、坏契约与「缺 options 的旧档」都要 null
    /// （<b>「读不得」必须明说读不得，不冒充读得过</b>）、Snapshot 全字段逐位等 —— 那是 JobRuntime 与 dashboard
    /// 看板共同的「定义快照单张」那份契约本身。</para></summary>
    static GateRow DefinitionRoundtrip()
    {
        var def = Support.DefinitionSample();

        var contract = DefinitionSerialization.Write(def);
        var loaded = DefinitionSerialization.TryLoad(contract);
        var sameOk = SameDefinition(loaded, def);

        var junkOk = DefinitionSerialization.TryLoad("not a json") is null;
        var slimOk = DefinitionSerialization.TryLoad("{}") is null;

        var snapOk = SameDefinition(def.Snapshot(), def);

        var writeBytes = Measuring.Bytes(() => _ = DefinitionSerialization.Write(def));
        var loadBytes = Measuring.Bytes(() => _ = DefinitionSerialization.TryLoad(contract));
        var snapshotBytes = Measuring.Bytes(() => _ = def.Snapshot());

        var shapeOk = sameOk && junkOk && slimOk && snapOk;

        var measured = $"契约 len {N(contract.Length)} 字符:Write→TryLoad 全字段等 {Yes(sameOk)}、"
            + $"坏契约 null {Yes(junkOk)}、缺 options null {Yes(slimOk)}、Snapshot 全字段等 {Yes(snapOk)}；"
            + $"Write {N(writeBytes)} B/次、TryLoad {N(loadBytes)} B/次、Snapshot {N(snapshotBytes)} B/次";

        return new GateRow(
            "DefinitionSerialization 往返 + Snapshot",
            "Write→TryLoad 全字段等；坏契约/旧档 lack 返 null；Snapshot 全字段等；三腿分配各自上册（钉）不判",
            measured,
            shapeOk ? Verdict.Green : Verdict.Red,
            "契约是 store seam 的 StoredDefinition.Contract —— 写进存档体里「唯一传播 schedule + options」那条路。"
            + "判据 ② 的「缺 options 的旧档」也算档病：读得的 definition 若没有 Options，这一档在 boot 里就是"
            + "Anomaly 的形状 —— 本行它必须 null，不多补偿。 Snapshot 是 ListJobs/GetJob 的价，"
            + "别换成随 dashboard render —— 每次轮询的看板线上这是大家一处。三腿字节全上册（首场已重钉：1360/1032/208 B）",
            Pins: [
                new Pin("Write 一次的托管分配", Baselines.DefinitionWriteBytes, writeBytes),
                new Pin("TryLoad 一次的托管分配", Baselines.DefinitionTryLoadBytes, loadBytes),
                new Pin("Snapshot 一次的托管分配", Baselines.DefinitionSnapshotBytes, snapshotBytes),
            ]);
    }

    static bool SameDefinition(JobDefinition? loaded, JobDefinition def)
    {
        if (loaded is null) return false;

        var a = loaded.Options;
        var b = def.Options;

        return loaded.Name == def.Name
               && loaded.Kind == def.Kind
               && loaded.Cron == def.Cron
               && loaded.Every == def.Every
               && loaded.RunAt == def.RunAt
               && loaded.Paused == def.Paused
               && loaded.Source == def.Source
               && a.TimeZoneId == b.TimeZoneId
               && a.MaxRetries == b.MaxRetries
               && a.RetryBackoff == b.RetryBackoff
               && a.RetryBaseDelay == b.RetryBaseDelay
               && a.RetryMaxDelay == b.RetryMaxDelay
               && a.RetryJitter == b.RetryJitter
               && a.MaxConcurrentPerJob == b.MaxConcurrentPerJob
               && a.OnOverlap == b.OnOverlap
               && a.OverlapQueueLimit == b.OverlapQueueLimit
               && a.Priority == b.Priority
               && a.OnMissedSlot == b.OnMissedSlot
               && a.Grace == b.Grace
               && a.Timeout == b.Timeout
               && a.Description == b.Description
               && a.Enabled == b.Enabled;
    }

    // ---------- 第 5 行：登记摊销 ----------

    /// <summary>第 5 行：生成器送进收件箱的那批登记，10 颗一整批的入库摊销。
    /// <para>窗内循环每轮开 10 个新名（<see cref="Support.InboxName"/>）—— Known 字典是进程内终身去重，
    /// 重名当场炸，所以基准不能复用名字。代价是 Known/Inbox 在跨轮里单调生长 —— 计数是「累计态下」的摊销，
    /// 与「清空箱重放 10 颗」是两档读法，别互相搬数。
    /// <para>重名的形状判据独立做一次：同一颗名字 Add 两次，第二次必须 QuickJobException ——
    /// 这是「duplicate job name 编译期就报」那句话在 runtime 侧的收尾。</para></summary>
    static int _inboxTurn;

    static GateRow InboxAdd10()
    {
        const int batch = 10;

        var batchBytes = Measuring.Bytes(() =>
        {
            var turn = Interlocked.Increment(ref _inboxTurn);

            for (var i = 0; i < batch; i++)
            {
                PendingRegistrations.Add(new PendingRegistration
                {
                    Name = Support.InboxName(turn, i),
                    Cron = "*/5 * * * *",
                    HandlerFactory = BenchHandler,
                });
            }

            return Task.CompletedTask;
        });

        // 形状判据：重名真的炸。单独一手一档，把「Known 去重不只是口号」压进程照判毙—— 不在 measuring 里跑。
        var duplicatedOk = false;
        try
        {
            var turn = Interlocked.Increment(ref _inboxTurn);
            var name = Support.InboxName(turn, 0);

            PendingRegistrations.Add(new PendingRegistration { Name = name, HandlerFactory = BenchHandler });
            PendingRegistrations.Add(new PendingRegistration { Name = name, HandlerFactory = BenchHandler });
        }
        catch (QuickJobException)
        {
            duplicatedOk = true;
        }

        var measured = $"一批 {batch} 颗（带 cron）{N(batchBytes)} B —— {N((double) batchBytes / batch)} B/颗；"
            + $"重名二次 Add 抛 QuickJobException {Yes(duplicatedOk)}";

        return new GateRow(
            $"收件箱登记摊销（{batch} 颗一批）",
            $"批内 {batch} 颗全收且不放任重名；重名二次 Add 即 QuickJobException；批分配上册（钉）不判",
            measured,
            duplicatedOk ? Verdict.Green : Verdict.Red,
            "窗内一轮 10 颗都要开新名：Known 字典跨轮长着 —— 计数是「累计态」的一批，与「清空箱重放 10 颗」不是一回事，"
            + "两者别互相搬数。重名形状判据只开一次新名、二压同一位 —— 不选「一整批重名」是因为批内第一颗就会把"
            + "窗内逐颗 Add 炸翻，量出来的是 throw 而不是入库。批字节给了 ÷10 的每颗 —— 那只是除法，不是独立自由度。",
            Pins: [
                new Pin($"{batch} 颗一整批入库的托管分配", Baselines.InboxAdd10BatchBytes, batchBytes),
            ]);
    }

    static JobHandler BenchHandler() => static _ => default;

    // ---------- 第 6 行：内存库存的两档 ----------

    /// <summary>第 6 行：InMemoryJobStore 三腿摆在两条池上（8 条 / 64 条），问一句"池大 8 幅，这条腿长多少"。
    /// <para>三腿的形状：<b>put 一条</b>（含 ReschedulePump 的 Min 扫描）、<b>take=8 peek 一拍</b>
    /// （OrderBy 全列表 —— O(n log n) 每拍，这是形状的账），<b>claim 4 + finish 4 + put 回池 4 一循环</b>（池恒伸不出）。
    /// 判据写两条：peek 档 take=8 的读数在两池分别 &le; 一拍 &le; 有序8条；「池大不该使得腿按超过线性长」 ——
    /// 8→64 是 8 幅，判据给上界 12 幅（1.5 马力），O(n²) 会当场爆，线性不误伤。</para></summary>
    static GateRow InMemoryStoreWalk()
    {
        var (putShort, _) = LegPut(PendingShort);
        var (putLong, _) = LegPut(PendingLong);

        var (peekShort, peekTakeCount, peekSortedOk) = LegPeek(PendingShort);
        var (peekLong, _, _) = LegPeek(PendingLong);

        var (claimShort, claimShortCount) = LegClaim(PendingShort);
        var (claimLong, claimLongCount) = LegClaim(PendingLong);

        // 池大 8 幅的同时腿不该长更多 —— 上界照 vixie 时代的取法给 12（8 × 1.5 马力），O(n²) 会当场爆。
        var claimOk = claimShortCount == 4 && claimLongCount == 4;
        var slopeOk = peekSortedOk && peekTakeCount == 8
            && claimLong <= claimShort * 12 && putLong <= putShort * 12 && peekLong <= peekShort * 12;

        var shapeOk = claimOk && slopeOk;

        var measured = $"put 一次（at={PendingShort} 池 {N(putShort)} B / at={PendingLong} 池 {N(putLong)} B）、"
            + $"peek take=8（{N(peekShort)} / {N(peekLong)} B，条数 {peekTakeCount} 排序检查 {Yes(peekSortedOk)}）、"
            + $"claim 4 + 回池 4 一循环（{N(claimShort)} / {N(claimLong)} B），两档 claim 成功 {Yes(claimOk)}";

        return new GateRow(
            $"InMemoryJobStore 三腿 walk（{PendingShort} → {PendingLong} 池）",
            "peek 一拍 take=8 输出 8 条、DueAt 排序；put 与 claim 4+回池 4 成功；三腿 8→64 的增量 ≤ 12 幅（O(n²) 会爆）；三腿分配各自上册（钉）不判",
            measured,
            shapeOk ? Verdict.Green : Verdict.Red,
            "claim 那条腿读的是「claim 出 4 + finish + put 回池 4」一起的价 —— 让池在窗内不退（claim 25+9 轮共 34 拍，"
            + "池若是纯 claim 会退到空）。它比 put 那条要重，预算差是 4 条 put 的价 —— 减出纯 claim 时用"
            + "「claim 腿 − 4 × put 腿」同池同档算，不跨档。peek 的 OrderBy 是全列表扫 —— 大池看板线上每拍一次"
            + "都是这个形状。由假钟/假 Timer（Support.FakeTimeProvider）垫底 —— 泵的 Change 不实时走表，"
            + "ReschedulePump 照常拨，读数才不会跟着真计时器走。",
            Pins: [
                new Pin($"{PendingLong} 条池上 put 一次的托管分配", Baselines.StorePut64Bytes, putLong),
                new Pin($"{PendingLong} 条池上 take=8 peek 一拍的托管分配", Baselines.StorePeek64Bytes, peekLong),
                new Pin($"{PendingLong} 条池上 claim 4 + 回池 4 一循环的托管分配", Baselines.StoreClaim64Bytes, claimLong),
            ]);
    }

    static (long Bytes, bool Ok) LegPut(int pending)
    {
        var clock = NewClock();
        IJobStore store = Support.SeededStore(clock, pending);

        var bytes = Measuring.Bytes(() => store.PutDueRunAsync(FreshRun(clock)).GetAwaiter().GetResult());

        // 测窗之外再放一条：put 通道真的走得通（真不通的话测窗那一路早就炸了，这句是在把"最后都在读"写实）。
        store.PutDueRunAsync(FreshRun(clock)).GetAwaiter().GetResult();

        return (bytes, true);
    }

    static (long Bytes, int TakeCount, bool SortedOk) LegPeek(int pending)
    {
        var clock = NewClock();
        IJobStore store = Support.SeededStore(clock, pending);

        var snapshot = store.PeekDueRunsAsync(8).GetAwaiter().GetResult();

        var sorted = snapshot is { Count: 8 } && snapshot.Zip(snapshot.Skip(1)).All(pair => pair.First.DueAt <= pair.Second.DueAt);

        var bytes = Measuring.Bytes(() => _ = store.PeekDueRunsAsync(8).GetAwaiter().GetResult());

        return (bytes, snapshot.Count, sorted);
    }

    static (long Bytes, int Claimed) LegClaim(int pending)
    {
        var clock = NewClock();
        IJobStore store = Support.SeededStore(clock, pending);

        var leases = store.ClaimDueRunsAsync("bench", 4).GetAwaiter().GetResult();

        // 外环先做完一次 claim（不回池）瞧瞧满不满 —— 那次不动测窗；
        // 量窗内「claim 4 + finish 4 + put 回池 4」，池大小保持不变。
        var bytes = Measuring.Bytes(() =>
        {
            var batch = store.ClaimDueRunsAsync("bench", 4).GetAwaiter().GetResult();

            foreach (var lease in batch)
            {
                store.FinishLeaseAsync(lease, DueRunOutcome.Completed).GetAwaiter().GetResult();
                store.PutDueRunAsync(lease.Run).GetAwaiter().GetResult();
            }

            return Task.CompletedTask;
        });

        return (bytes, leases.Count);
    }

    static Support.FakeTimeProvider NewClock() => new() { Now = Support.Base };

    static StoredDueRun FreshRun(Support.FakeTimeProvider clock) => new()
    {
        RunId = Guid.NewGuid(),
        JobName = "bench.store.walk",
        DueAt = clock.Now,
        Attempt = 1,
        Origin = JobTrigger.Enqueued,
        Payload = null,
    };

    // ---------- 第 7 行：热路径的反射字样 ----------

    /// <summary>第 7 行：QuickJob 的「零反射、AOT 干净」承诺在引擎本体的落点证明 ——
    /// <c>src/QuickJob/**\/*.cs</c> 的手写代码里，这批反射形状<b>几处也不可出现（各有各的主）</b>。
    /// 两条收窄同 sql 家第一代：整行注释不入账、命令前缘不是标识符字符的命中才算（给 ValueConverter 同样的毯护）。</summary>
    static GateRow ReflectionShapes()
    {
        (string Shape, int Budget)[] shapes =
        [
            ("Convert.", 0),
            ("MakeGenericMethod", 0),
            ("PropertyInfo", 0),
            ("Activator.CreateInstance", 0),
            ("MethodInfo", 0),
            ("dynamic", 0),
            ("GetType()", 0),
            ("GetProperties", 0),
        ];

        var files = Repo.Sources("QuickJob").ToArray();

        var drifted = new List<string>();
        var detail = new List<string>();

        foreach (var (shape, budget) in shapes)
        {
            var actual = files.Sum(f => Repo.Count(f, shape));

            detail.Add($"{shape}={actual}");

            if (actual != budget)
                drifted.Add($"{shape} 预算 {budget} → 实测 {actual}");
        }

        return new GateRow(
            "Convert.* / MethodInfo / dynamic 出现在 src/QuickJob/**（引擎本体）",
            "全预算 0（QuickJob 零反射的资格承诺 —— 新增即红）",
            string.Join("、", detail) + $"（扫描 {files.Length} 个手写文件，obj/bin 与整行注释不入账）",
            drifted.Count == 0 ? Verdict.Green : Verdict.Red,
            drifted.Count == 0
                ? "零反射不是彩头而是构造：对 [QuickJob] 方法的绑定走编译期生成、handler 由工厂委托直接装，"
                + "引擎本体把手写在 src/QuickJob。扫描域只这一条 —— src/QuickJob.Generators 与 src/QuickJob.Dashboard"
                + " 各有各的反射资格，不入此域。首场实测为 0 —— 这一条在这次立档，此后新增即红"
                + "（要么抹掉，要么在 docs 的门禁账里立格说明为什么正当）。"
                : "反射进了引擎本体：" + string.Join("；", drifted)
                + "。要么抹掉（QuickJob 的承诺是零反射 —— AOT 干净从这一句来），"
                + "要么在 docs 的门禁账里立格说明为什么正当 —— 首场实测为 0，新增是档不是常态。",
            Pins: null);
    }

    // ---------- 报 号助手 ----------

    static string N(double value) => Math.Round(value, Math.Abs(value) >= 100 ? 0 : Math.Abs(value) >= 10 ? 1 : 2).ToString("0.##");
}
