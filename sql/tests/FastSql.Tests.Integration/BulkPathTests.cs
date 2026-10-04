using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FastSql.Aop;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Sql;
using FastSql.TestInfra;
using FastSql.Values;
using Xunit;

namespace FastSql.Tests.Integration;

/// <summary>§12.1 的 L1「批量装载通道」在真库上跑一遍。<b>这一册断的全是文本层面看不见的东西</b>：
/// 行进没进去、七列是不是各归其格、事务带不带得动它、回填那一条出口有没有借道、
/// 通道报回的数与递交的数不等时拦不拦得住。
///
/// <para>为什么非得起这一册：<c>BulkLayerTests</c> 能证明"选层的判据与拒绝的话"，但装载通道这一档
/// <em>根本不产 SQL 文本</em> —— 于是它没有黄金样本可对，而它偏偏是"能绿着把整批写歪"的那一种形状：
/// <c>SqlBulkCopy</c> 不给 <c>ColumnMappings</c> 时按<em>序号</em>对齐，而我们递交的是列清单的子集
/// （自增列与并发列都不在里面）。序号对齐会把第 3 个值写进第 3 列，而每一条都返回成功。
/// 那一种错只有逐列读回来比才看得见。</para>
///
/// <para><b>这一档今天只有一家接了</b>，所以每条用例第一句都挂 <see cref="ReadyForCopy"/>：
/// 那不是"本家跑不过"而是"这一档在本家没有对象"，理由写在那一颗里。<b>只插不删</b>与
/// <see cref="WritePathTests"/> 同一套姿势：每条用例带本次运行独占的标签，人工清理按
/// <c>LIKE 'fastsql-bulk/%'</c>。</para>
///
/// <para><b>有一格本册证不了，写在这里而不是假装证过</b>：§12.4 那句"装载通道与逐条语义等价"里
/// "约束与触发器照旧生效"那一半 —— 两颗开关确实是从驱动的枚举上读出来再置上的，但夹具这张表上
/// 既没有 CHECK 也没有触发器，于是它在真库上<em>没有可观测的对象</em>。今天证得到的是
/// "列按名字对齐、类型按 binder 归一"。那一格记在 usage 文档的现状段里。</para></summary>
public sealed class BulkPathTests
{
    static FastSqlOrm Orm => Database.Orm;

    /// <summary>本次运行独占的批次前缀：每条用例在它下面再分一个自己的标签，所以重复跑不会因为
    /// "计数变了"变红（与 <see cref="WritePathTests"/> 同一颗理由）。</summary>
    readonly string Batch = "fastsql-bulk/" + Guid.NewGuid().ToString("N")[..8];

    static readonly DateTime Moment = new(2026, 9, 1, 2, 3, 4, 567);

    string tag(string part) => $"{Batch}/{part}";

    /// <summary>一批行。<b>客户名与标签无关</b>：这样"装载通道写的那一批"与"L3 写的那一批"可以逐列对，
    /// 而区分两批的只有 <c>Note</c> 那一列 —— 名字里嵌标签的话，等价性那一条就成了比两颗不同的值。</summary>
    static Order[] rows(string note, int count)
        => [.. Enumerable.Range(1, count)
               .Select(i => new Order
               {
                   CustomerName = "客户" + i,
                   CustomerId = 7,
                   Amount = i + 0.25m,
                   State = i % 2 == 0 ? OrderState.Paid : OrderState.Cancelled,
                   CreatedAt = Moment,
                   PaidAt = null,
                   Note = note,
               })];

    /// <summary>本册第一条闸门：装载通道今天只有 SQL Server 一家接。</summary>
    static void ReadyForCopy()
    {
        Database.Ready();

        // §12.1 那一档的判据是 IBulkStrategy.ResolveCopyChannel() 交不回 null。其余几家交回 null，
        // 于是"请求 CopyChannel"在它们身上会整批退 L3 —— 那时无事可判，而断言会绿在文本层上，
        // 把"这一家没有通道"伪装成"通道是通的"。缺口按 §13.4 的纪律带理由 skip，不删用例。
        Assert.SkipWhen(Database.Family != Database.Target.SqlServer,
            $"装载通道（§12.1 的 L1）今天只有 SQL Server 一家接，本跑是 {Database.Family}：" +
            "ResolveCopyChannel() 交回 null，WithBulk(CopyChannel) 会整批退回 L3 多值语句，" +
            "本册判的那件事在这里没有对象（退路本身由离线册 BulkLayerTests 钉住）。");
    }

    static string Millis(string column) => Database.MillisText(column);

    /// <summary>按标签读回写进去的行，按 <c>[Id]</c> 排序。<b>那颗 ORDER BY 是判据的一部分</b>：
    /// 装载通道没有回包可对，"进去的第 n 行是哪一行"只剩行序这一处真相，而自增值的单调性是库里
    /// 还认得的顺序证人。</summary>
    List<Dictionary<string, string>> stored(string note)
        => Database.Rows(
            $"SELECT [Id], [CustomerName], [CustomerId], [Amount], [State], {Millis("[CreatedAt]")} AS CreatedAt, {Millis("[PaidAt]")} AS PaidAt, [Note] " +
            "FROM [dbo].[FastSqlOrder] WHERE [Note] = @note ORDER BY [Id]",
            ("@note", note));

    /// <summary>逐列比一行。<b>五类值各有其塌法</b>（中文看排序规则、毫秒看 1/300 秒、decimal 看标度、
    /// 枚举看有没有折成底层整数、可空列看落的是 <c>DBNull</c> 还是空串），而装载通道这一档连一句 SQL
    /// 都不产，所以这五类只有读回来这一种看法。</summary>
    static void SameContent(Order one, Dictionary<string, string> landed)
    {
        Assert.Equal(one.CustomerName, landed["CustomerName"]);
        Assert.Equal(one.CustomerId, int.Parse(landed["CustomerId"], CultureInfo.InvariantCulture));
        Assert.Equal(one.Amount, decimal.Parse(landed["Amount"], CultureInfo.InvariantCulture));
        Assert.Equal((int) one.State, int.Parse(landed["State"], CultureInfo.InvariantCulture));
        Assert.Equal(one.CreatedAt, DateTime.Parse(landed["CreatedAt"], CultureInfo.InvariantCulture));

        if (one.PaidAt is null)
            Assert.Equal("NULL", landed["PaidAt"]);
        else
            Assert.Equal(one.PaidAt.Value, DateTime.Parse(landed["PaidAt"], CultureInfo.InvariantCulture));
    }

    // ── 落库与等价（§12.4 的等价性） ────────────────────────────────────

    [Fact]
    public async Task A_requested_copy_channel_lands_the_whole_batch_and_reports_the_layer()
    {
        ReadyForCopy();

        var note = tag("land");
        var batch = rows(note, 50);
        var seen = new List<BulkDegradedEvent>();
        var aop = new FastSqlAop();

        aop.BulkDegraded = seen.Add;

        // Never 这一档在真库上必须不咬：它是 §12.2 那句"CI 里跑一遍防静默逐条"的现场，
        // 而"跑一遍"的前提是通道在真驱动集里找得到 —— 离线册只能证找不到时的形状。
        var report = await Database.OrmWith(aop).InsertRange(batch)
            .WithBulk(BulkMethod.CopyChannel, FallbackPolicy.Never)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BulkMethod.CopyChannel, report.Method);
        Assert.Equal(50, report.RowCount);
        Assert.Equal(1, report.RoundTrips);
        Assert.Equal(50, report.ChunkSize);
        Assert.False(report.Degraded);
        Assert.Empty(seen);

        var landed = stored(note);

        Assert.Equal(batch.Length, landed.Count);

        for (var i = 0; i < batch.Length; i++)
            SameContent(batch[i], landed[i]);

        // 自增列没进那一批：它进不去（库里是 IDENTITY），而"没进"要由库给的键来背书。
        // 这一句同时是"列按名字对齐"的第二颗证人 —— 按序号对齐时第一格会拿 CustomerName 去撞 int 列。
        Assert.All(landed, one => Assert.True(int.Parse(one["Id"], CultureInfo.InvariantCulture) > 0, "库里那一行没有自增键"));
        Assert.Equal(landed.Count, landed.Select(one => one["Id"]).Distinct().Count());
    }

    [Fact]
    public async Task What_the_channel_writes_is_what_the_text_layer_writes()
    {
        ReadyForCopy();

        var viaCopy = tag("via-copy");
        var viaText = tag("via-text");
        var expected = rows(viaCopy, 12);

        await Orm.InsertRange(expected).WithBulk(BulkMethod.CopyChannel)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken);

        // 同一份内容、同一个条数，从 §5.4 那条默认走文本层的出口再写一遍。
        await Orm.InsertRange(rows(viaText, 12)).ExecuteAffrowsAsync(TestContext.Current.CancellationToken);

        var copied = stored(viaCopy);
        var typed = stored(viaText);

        Assert.Equal(12, copied.Count);
        Assert.Equal(copied.Count, typed.Count);

        // 中文、毫秒、decimal 标度、枚举、NULL 五类在同一张表上并排读：装载通道与 L3 之间
        // 任何一格不同都在这里现形。两句同样错也算不同 —— 所以内容本身也要对得上入参。
        for (var i = 0; i < copied.Count; i++)
        {
            Assert.Equal(typed[i]["CustomerName"], copied[i]["CustomerName"]);
            Assert.Equal(typed[i]["CustomerId"], copied[i]["CustomerId"]);
            Assert.Equal(typed[i]["Amount"], copied[i]["Amount"]);
            Assert.Equal(typed[i]["State"], copied[i]["State"]);
            Assert.Equal(typed[i]["CreatedAt"], copied[i]["CreatedAt"]);
            Assert.Equal(typed[i]["PaidAt"], copied[i]["PaidAt"]);

            SameContent(expected[i], copied[i]);
        }
    }

    [Fact]
    public async Task A_client_generated_key_goes_inside_the_batch_because_it_is_a_written_column()
    {
        ReadyForCopy();

        // 自增列不进列清单、客户端给的键进 —— 这一格在文本层由黄金样本钉过，在装载通道这一档
        // 只有真库能给答案：Guid 落 uniqueidentifier 时驱动不翻字节序、不文本化。
        var label = tag("token");
        var ids = Enumerable.Range(1, 3).Select(_ => Guid.NewGuid()).ToArray();
        var batch = ids.Select((id, i) => new SessionToken { Id = id, Value = $"{label}/{i}", ExpiresAt = Moment }).ToArray();

        var report = await Orm.InsertRange(batch).WithBulk(BulkMethod.CopyChannel)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BulkMethod.CopyChannel, report.Method);
        Assert.Equal(3, report.RowCount);

        var landed = Database.Rows(
            $"SELECT [Id], [Value], {Millis("[ExpiresAt]")} AS ExpiresAt FROM [dbo].[FastSqlSessionToken] WHERE [Value] LIKE @label ORDER BY [Value]",
            ("@label", label + "/%"));

        Assert.Equal(3, landed.Count);
        Assert.Equal(ids.Select(id => id.ToString("D")).OrderBy(text => text), landed.Select(one => one["Id"]).OrderBy(text => text));
        Assert.All(landed, one => Assert.Equal(Moment, DateTime.Parse(one["ExpiresAt"], CultureInfo.InvariantCulture)));
    }

    // ── 连接与事务（§3 的第 5 层、§6 的三形态） ─────────────────────────

    [Fact]
    public async Task The_copy_channel_rides_the_transaction_and_rolls_back_with_it()
    {
        ReadyForCopy();

        var committed = tag("tran-commit");

        var tran = Orm.BeginTransaction();

        var report = await Orm.InsertRange(rows(committed, 4)).WithBulk(BulkMethod.CopyChannel)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, report.RowCount);

        // 同一条连接才看得见未提交的自己：CopyAsync 若从工厂另借一条连接，这一句就是 0，
        // 而那正是"事务是假的"的形状（§6 (3) 的自动 enlist 在这里要有同样的读法）。
        Assert.Equal(4, await Orm.Select<Order>()
            .Where(o => o.Note == committed)
            .CountAsync(TestContext.Current.CancellationToken));

        tran.Commit();

        Assert.Equal(4, stored(committed).Count);

        var rolled = tag("tran-rollback");

        using (var inner = Orm.BeginTransaction())
        {
            await Orm.InsertRange(rows(rolled, 3)).WithBulk(BulkMethod.CopyChannel)
                .ExecuteBulkAsync(TestContext.Current.CancellationToken);

            inner.Rollback();
        }

        // 回滚必须由库里没有这三行来背书：只断"没报错"的话，通道自己提交掉了也照样绿。
        Assert.Empty(stored(rolled));
    }

    // ── 回填那一档不借道（§12.1 第三行） ───────────────────────────────

    [Fact]
    public async Task A_backfill_exit_never_borrows_the_channel_even_when_one_is_offered()
    {
        ReadyForCopy();

        var (orm, spy) = Spied();

        var backfilled = await orm.InsertRange(rows(tag("backfill"), 5)).WithBulk(BulkMethod.CopyChannel)
            .ExecuteReturnKeysAsync(TestContext.Current.CancellationToken);

        // 键回填回来了 ⇒ 走的是带 OUTPUT 的文本层；而替身一次都没被叫到 ⇒ 不是"两条都跑了"。
        Assert.Equal(5, backfilled.Count);
        Assert.All(backfilled, one => Assert.True(one.Id > 0, "有实体的主键没被回填，还是 0"));
        Assert.Equal(0, spy.Deliveries);

        // 同一条请求在批量口那边确实用上了通道：拒的是回填口借道，不是请求本身被丢掉。
        var (again, second) = Spied();

        var report = await again.InsertRange(rows(tag("bulk"), 5)).WithBulk(BulkMethod.CopyChannel)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BulkMethod.CopyChannel, report.Method);
        Assert.Equal(1, second.Deliveries);
    }

    // ── 递给通道的 payload（内核那一扇 binder 的门） ────────────────────

    [Fact]
    public async Task The_payload_handed_to_the_channel_is_the_kernels_normalized_one_and_in_row_order()
    {
        ReadyForCopy();

        var note = tag("payload");
        var batch = rows(note, 6);
        var (orm, spy) = Spied();

        var insert = orm.InsertRange(batch).WithBulk(BulkMethod.CopyChannel);

        // 文本层那一份先取一次：它不进执行，但它是"COPY 落的那张表与 INSERT 写的那张表是同一张"的对照组。
        var text = insert.Explain().Chunks[0].Sql.Text;

        await insert.ExecuteBulkAsync(TestContext.Current.CancellationToken);

        var given = spy.Latest;

        // 目标表名逐字等于文本层的 INSERT 目标（§9.2 那句"建出来的表与查的表是同一张"的第三个读者）。
        Assert.Equal(text[(text.IndexOf("INSERT INTO ", StringComparison.Ordinal) + "INSERT INTO ".Length)..text.IndexOf(" (", StringComparison.Ordinal)],
            given.DestinationTable);

        // 列清单照文本层：七列，自增列不在里面。
        Assert.Equal(7, given.Columns.Count);
        Assert.DoesNotContain(given.Columns, column => column.MemberName is "Id");

        // 行序就是入参顺序：装载通道没有回包可对，这是唯一一处"进去的第 n 行是哪一行"的真相。
        for (var i = 0; i < batch.Length; i++)
            Assert.Equal(batch[i].CustomerName, (string?) given.Rows[i][0]);

        // 值已经是驱动侧的形态：枚举折成底层整数、null 是 DBNull —— 而这两件事都是内核的 binder 干的
        // （通道不许自己再猜一遍类型，否则"同一条插入在几家参数化形状不同"就有第二个出处）。
        Assert.Equal((int) batch[0].State, given.Rows[0][3]);
        Assert.Equal(DBNull.Value, given.Rows[0][5]);

        // 替身是把写转发给真通道的：所以这一批是真落库的，上面那几句形状断言不是拿没执行的数据做的。
        Assert.Equal(6, stored(note).Count);
    }

    // ── 块宽与部分写入（§12.3 的旋钮、§12.1 那句"失败不能定位到行"） ─────

    [Fact]
    public async Task A_batch_split_into_deliveries_keeps_row_order_across_the_boundaries()
    {
        ReadyForCopy();

        var note = tag("chunked");
        var batch = rows(note, 5);
        var (orm, spy) = Spied();

        var report = await orm.InsertRange(batch).WithBulk(BulkMethod.CopyChannel, rowsPerRoundTrip: 2)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, report.RoundTrips);
        Assert.Equal(2, report.ChunkSize);
        Assert.Equal(3, spy.Deliveries);

        // 三次递交的宽度与内容：2 + 2 + 1，拼起来正好是入参序列，一块都不许重叠或漏。
        Assert.Equal(new[] { 2, 2, 1 }, spy.Batches.Select(one => one.Rows.Count).ToArray());
        Assert.Equal(batch.Select(one => one.CustomerName).ToArray(),
            spy.Batches.SelectMany(one => one.Rows).Select(cells => (string?) cells[0]).ToArray());

        // 库里读回来同样按入参序：块边界处列没串、每行落回自己那一格。
        var landed = stored(note);

        Assert.Equal(batch.Length, landed.Count);

        for (var i = 0; i < batch.Length; i++)
            SameContent(batch[i], landed[i]);
    }

    [Fact]
    public async Task A_channel_that_under_reports_its_rows_fails_loudly_instead_of_returning_a_plausible_number()
    {
        ReadyForCopy();

        var note = tag("under-report");
        var (orm, spy) = Spied();

        // 通道少报一行：那一行其实进去了（写是真的），但"进了几行"这一格从此不可信。
        // 少报的数比少一列危险 —— 调用方拿不到异常，只会拿到一个看着合法的数（v0.32 那一判同一条）。
        spy.ReportFewerBy = 1;

        var failure = await Assert.ThrowsAsync<FastSqlException>(
            async () => await orm.InsertRange(rows(note, 5)).WithBulk(BulkMethod.CopyChannel)
                .ExecuteBulkAsync(TestContext.Current.CancellationToken));

        Assert.Contains("报回 4/5 行", failure.Message, StringComparison.Ordinal);
        Assert.Contains("不能假设这一批完整落库", failure.Message, StringComparison.Ordinal);

        // 当场把真相读回来：抛了异常并没有撤销那五行（这一档不包事务，跨次递交不原子）。
        // 这一句是写给读用例的人的：那道闸门拦下的是"悄悄交出一个假数"，不是"回滚掉那一批"。
        Assert.Equal(5, stored(note).Count);
    }

    // ── 耗时：只报数，不判（§13.4 那句"不要把一次现场读数钉成断言"） ────

    [Fact]
    public async Task The_wall_clock_gap_is_printed_not_asserted()
    {
        ReadyForCopy();

        const int Count = 2_000;

        var viaCopy = tag("clock-copy");
        var viaText = tag("clock-text");

        var copy = await Measured(async () => await Orm.InsertRange(rows(viaCopy, Count)).WithBulk(BulkMethod.CopyChannel)
            .ExecuteBulkAsync(TestContext.Current.CancellationToken));

        var text = await Measured(async () => await Orm.InsertRange(rows(viaText, Count))
            .ExecuteAffrowsAsync(TestContext.Current.CancellationToken));

        // 两次都必须真写完：否则"快"完全可以是"没写"。
        Assert.Equal(Count, stored(viaCopy).Count);
        Assert.Equal(Count, stored(viaText).Count);

        // 读数进日志，不进判据：这台机器的往返时间随负载变，把比例钉成断言只会造出一颗定期翻红的用例。
        Console.WriteLine($"[bulk-timing] rows={Count} copy_ms={copy.TotalMilliseconds:F0} l3_ms={text.TotalMilliseconds:F0}");
    }

    static async Task<TimeSpan> Measured(Func<Task> work)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        await work();

        return System.Diagnostics.Stopwatch.GetElapsedTime(started);
    }

    // ── 替身 ──────────────────────────────────────────────────────────

    /// <summary>包一层真通道的<b>转发</b>替身：记递交进来的那一批，写仍然是真通道在写。
    /// <see cref="ReportFewerBy"/> 用来量部分写入那道闸 —— 真通道不会少报，而闸门要判的正是少报时拦不拦。</summary>
    sealed class SpiedCopyChannel(IBulkCopyChannel inner) : IBulkCopyChannel
    {
        public List<BulkCopyRequest> Batches { get; } = [];

        public int Deliveries => Batches.Count;

        public BulkCopyRequest Latest => Batches[^1];

        public int ReportFewerBy { get; set; }

        public bool SupportsColumn(ColumnContract column) => inner.SupportsColumn(column);

        [RequiresUnreferencedCode("接口那一句的镜像（IL2046 要求两边一致）。")]
        [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条。")]
        public async Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct)
        {
            Batches.Add(request);

            return await inner.WriteAsync(target, request, ct).ConfigureAwait(false) - ReportFewerBy;
        }
    }

    /// <summary>只把 <see cref="IBulkStrategy.ResolveCopyChannel"/> 换成替身的转发包装：方言、binder、
    /// 参数预算、连接工厂全部照抄内层，所以这一跑发出的语句与任何其他集成用例发出的逐字相同
    /// （与 <see cref="LowBudgetProvider"/>、<c>NoBulkKeysProvider</c> 同一个手法）。</summary>
    sealed class SpiedCopyProvider(IDbProvider inner, SpiedCopyChannel channel) : IDbProvider
    {
        public DbKind Kind => inner.Kind;

        public DbCapabilities Capabilities => inner.Capabilities;

        public IConnectorFactory Connectors => inner.Connectors;

        public ISqlDialect Dialect => inner.Dialect;

        public ITypeMap Types => inner.Types;

        public IValueBinder Values => inner.Values;

        public IExpressionRules Expressions => inner.Expressions;

        public ICodeFirstDialect CodeFirst => inner.CodeFirst;

        public ISchemaReader Schema => inner.Schema;

        public IKeyStrategy Keys => inner.Keys;

        public IConcurrencyStrategy Concurrency => inner.Concurrency;

        public IChangeGuard ChangeGuard => inner.ChangeGuard;

        public IBulkStrategy? Bulk => inner.Bulk is { } bulk ? new SwappedChannel(bulk, channel) : null;

        sealed class SwappedChannel(IBulkStrategy inner, IBulkCopyChannel channel) : IBulkStrategy
        {
            public int ParameterBudget => inner.ParameterBudget;

            public string Describe => inner.Describe + "（本用例把装载通道换成记录用的替身）";

            public IBulkCopyChannel? ResolveCopyChannel() => channel;
        }
    }

    /// <summary>组装一台"通道被替身记着"的 ORM。<b>包的是缝给的 provider</b>，不是用例自己点的一家 ——
    /// 写落在哪、读落在哪，两句必须与 <see cref="Database.Rows"/> 那一边一致（v0.32 第二格立的那条缝）。</summary>
    static (FastSqlOrm Orm, SpiedCopyChannel Spy) Spied()
    {
        var inner = Database.Provider;
        var channel = new SpiedCopyChannel(inner.Bulk!.ResolveCopyChannel()!);

        return (Database.OrmWith(new FastSqlAop(), new SpiedCopyProvider(inner, channel)), channel);
    }
}
