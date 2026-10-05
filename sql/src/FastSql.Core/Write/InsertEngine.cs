using System.Globalization;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using FastSql.Aop;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Write;

/// <summary>插入链（§5.4 的写侧、§12.3 的 L3 多值语句 + 客户端分块）。
/// 分两段跑：<b>取值段</b>（列合成 → <c>AuditValue</c> → 契约校验）一次，<b>发射段</b>（分块 → 方言 →
/// <c>CudBuilding</c> → <c>ParameterBinding</c>）按出口形态各一次。分开是因为三个出口（不回填 / 只回填自增键 /
/// 连并发列一起回填）产出的文本不同，
/// 而取值段的副作用（钩子写实体、校验拒绝）必须只发生一次 —— 否则 <c>CreatedAt</c> 会被填两个不同的时间，
/// §12.4 第 4 项那条「审计列刷新次数一致」就没有可比的对象了。</summary>
/// <remarks>值级钩子按<b>构建次数</b>触发而不是按执行次数：同一条语句先 <c>ToSql()</c> 再
/// <c>ExecuteReturnKeysAsync()</c> 会构建两次（两个出口的文本不同），<c>ParameterBinding</c> 因此过两遍。
/// §10.1 硬规则 1 要求钩子幂等，正是为了这种情况；<c>AuditValue</c> 更严——它只可能跑一次。</remarks>
sealed class InsertEngine<T> : IInsert<T> where T : class
{
    readonly FastSqlOrm _orm;
    readonly T[] _rows;

    Preparation? _preparation;
    InsertPlan? _plain;
    InsertPlan? _withKeys;
    InsertPlan? _withGenerated;

    // §12.2 的三格请求。null 不是"没决定"，是"这条出口自己的默认"：ExecuteAffrowsAsync 默认文本层，
    // ExecuteBulkAsync 默认 Auto（判据与理由见 IInsert.WithBulk 的抬头，落在下面的 Decide / ExecuteAffrowsAsync）。
    BulkMethod? _requested;
    FallbackPolicy _fallback = FallbackPolicy.DegradeAndLog;
    int? _rowsPerRoundTrip;

    public InsertEngine(FastSqlOrm orm, T[] rows)
    {
        _orm = orm;
        _rows = rows;
    }

    // ── 离线出口（§3：不连库） ────────────────────────────────────────

    public InsertPlan Explain() => Plan(Backfill.None);

    public InsertPlan ExplainKeys() => Plan(Backfill.Keys);

    /// <summary>「库里给的那些列」= 自增列 ∪ 并发列（§8.2 的回填集）。跟踪器走这一档：并发列的值不进
    /// 列清单、只进 <c>WHERE</c>，插完不读回来，手里的值就永远是空的，下一次保存必假冲突。</summary>
    public InsertPlan ExplainGenerated() => Plan(Backfill.Generated);

    public string ToSql() => Plan(Backfill.None).ToSql();

    public string ToSql(out IReadOnlyList<AdoParameter> parameters)
    {
        var plan = Plan(Backfill.None);

        if (!plan.IsSingleStatement)
            throw new FastSqlException($"分块后没有单一参数表（{plan.Reason}）。取 Explain().Chunks[i].Sql.Parameters。");

        parameters = plan.Chunks[0].Sql.Parameters;

        return plan.Chunks[0].Sql.Text;
    }

    public string ToLogSql() => Plan(Backfill.None).ToLogSql();

    // ── 批量阶梯（§12.2） ────────────────────────────────────────────

    public IInsert<T> WithBulk(BulkMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog, int? rowsPerRoundTrip = null)
    {
        if (rowsPerRoundTrip is { } requested && requested < 1)
            throw new FastSqlException($"rowsPerRoundTrip={requested}：每块至少 1 行。要到阶梯最底那一档请传 {BulkMethod.SingleRow}，不是把这块压到 0。");

        _requested = method;
        _fallback = fallback;
        _rowsPerRoundTrip = rowsPerRoundTrip;

        // 每块行数是发射段的输入，改过它，缓存里那三份文本就过期了。取值段那份不动 ——
        // AuditValue 只许跑一次（§10.1 硬规则 1 与上面抬头的两段之分）。
        _plain = _withKeys = _withGenerated = null;

        return this;
    }

    [RequiresUnreferencedCode("判「这一家有没有装载通道」靠一次按类型名的反射查找（IBulkStrategy.ResolveCopyChannel 的抬头）：被裁剪的驱动会让这一颗报出与完整运行时不同的层。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public BulkReport ExplainBulk(BulkMethod? requested = null)
        => Decide(Prepare(), requested ?? _requested ?? BulkMethod.Auto).Report;

    // ── 执行出口 ──────────────────────────────────────────────────────

    /// <summary>文本层出口：<b>认得的请求是 <see cref="BulkMethod.MultiValue"/> 与
    /// <see cref="BulkMethod.SingleRow"/> 那一档</b>（连同 <see cref="WithBulk"/> 那句里的每块行数），
    /// <b>而指向装载通道的请求在这里当场拒，不是"默默当没看见"</b>。
    /// 拒的理由与默认层为什么是文本层是同一条（<c>IInsert.WithBulk</c> 的抬头）：这一颗返回的是一颗
    /// <see cref="int"/>，装不下"走了哪一层、几次往返、为什么"，而装载通道那一档根本不产文本 ——
    /// <see cref="FastSqlAop.CudBuilding"/>（软删改写、影子表）与 <see cref="FastSqlAop.ParameterBinding"/>
    /// 两个改写钩子会集体失效，而调用方只看到一个数字。<b>结构上的另一半收益是这条出口不欠修剪税</b>：
    /// 它不可能走到 <see cref="AdoExecutor.CopyAsync"/>，所以签名上没有
    /// <see cref="RequiresUnreferencedCodeAttribute"/>，写路径上今天已有的每一个调用点都不必改。</summary>
    public async Task<int> ExecuteAffrowsAsync(CancellationToken ct = default)
    {
        if (_requested is BulkMethod.Auto or BulkMethod.CopyChannel)
            throw new FastSqlException(
                $"ExecuteAffrowsAsync 走不了 {_requested}：那一档要的是「走了哪一层、几次往返、为什么」，而它的返回值只是一颗行数。" +
                "装载通道请用 ExecuteBulkAsync；只压每块行数或按请求逐条请用 WithBulk(MultiValue/SingleRow, rowsPerRoundTrip: …)。");

        var plan = Plan(Backfill.None);
        var tag = Tag();
        var affrows = 0;

        foreach (var chunk in plan.Chunks)
            affrows += await _orm.Ado.NonQueryAsync(chunk.Sql, tag, ct).ConfigureAwait(false);

        return affrows;
    }

    [RequiresUnreferencedCode("转调装载通道：驱动自家的批量类不在 DbProviderFactory 的抽象面上，只能按类型名找（接口那一句标注的镜像，IL2046 要求两边一致）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    public async Task<BulkReport> ExecuteBulkAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();
        var decision = Decide(preparation, _requested ?? BulkMethod.Auto);
        var report = decision.Report;
        var tag = Tag();

        // 留痕在动手之前：降级之后那句写还可能失败，而"它退到哪一层"正是排查那一半要用的信息。
        // DegradeSilently 走不到这里（§12.2 那一格给的就是"纯优化路径不必声张"）。
        if (report.Degraded && _fallback == FallbackPolicy.DegradeAndLog && _orm.Aop.BulkDegraded is { } degraded)
            _orm.Aop.Observe(() => degraded(new BulkDegradedEvent(report.Requested, report.Method, report.RowCount, report.Reason)));

        int written;

        if (decision.Channel is { } channel)
        {
            // 分表时一片一次递交（装载通道的目标表是一次一个，混不了两片的行），
            // 所以"几次往返"是逐片取整再相加，而递交次数上限按各片自己的行数算。
            var batches = CopyRequests(preparation);
            var width = 0;
            var trips = 0;

            written = 0;

            foreach (var batch in batches)
            {
                // rowsPerRoundTrip 在装载通道这一档说的是"一次递交几行"（IInsert.WithBulk 的抬头），与文本层同一个旋钮、
                // 同一条只压不抬：这里没有参数预算那道界，所以上限只有这一片的行数本身。
                var slice = _rowsPerRoundTrip is { } cap && cap < batch.Rows.Count ? cap : batch.Rows.Count;

                width = Math.Max(width, slice);
                trips += (batch.Rows.Count + slice - 1) / slice;

                for (var first = 0; first < batch.Rows.Count; first += slice)
                    written += await _orm.Ado
                        .CopyAsync(channel, Slice(batch, first, slice), tag, ct)
                        .ConfigureAwait(false);
            }

            // 通道报回的数与递交的数不等 = 部分写入。少报的数比少一列危险（v0.32 那一判同一条：
            // 绿着交出一个看着合法的结果，调用方拿不到异常），所以这里抛而不是把 report 改小。
            if (written != report.RowCount)
                throw new FastSqlException(
                    $"{_orm.Provider.Kind} 的装载通道报回 {written}/{report.RowCount} 行：这一家的批量入口给不出「进了几行」，" +
                    "不能假设这一批完整落库（§12.1 那句「失败不能定位到行」在这一格里就是整批不可信）。");

            return report with { ChunkSize = width, RoundTrips = trips };
        }

        // 文本层：把决策里的估算换成发射段的实数（块数与每块行数都由 Build 那一份算，两处不许各算一遍）。
        var plan = Plan(Backfill.None);

        report = report with { ChunkSize = plan.RowsPerStatement, RoundTrips = plan.Chunks.Count };

        written = 0;

        foreach (var chunk in plan.Chunks)
            written += await _orm.Ado.NonQueryAsync(chunk.Sql, tag, ct).ConfigureAwait(false);

        return report;
    }

    /// <summary>§12.2 那张阶梯今天能落的三档，由这一颗纯函数选：<b>不建文本、不碰连接、不发钩子</b>
    /// （<see cref="ExplainBulk"/> 与 <see cref="ExecuteBulkAsync"/> 共用它，所以"离线看到的层"与
    /// "执行走的那一层"之间没有第二套判据可漂移）。</summary>
    /// <remarks><b>两次判定之间不许有第二份真相</b>是这里唯一的难点：装载通道那一条分支必须在建文本
    /// <em>之前</em>定下来，否则 <c>CudBuilding</c> 会为一句丢掉的分块语句白跑一遍；而文本层那两档的
    /// 往返次数又是 <c>Build</c> 算出来的（它才知道预算、钩子改过的东西）。做法是让 <see cref="BulkReport"/>
    /// 先带公式的估算、执行时由 <c>Plan</c> 的实数覆写（<see cref="ExecuteBulkAsync"/> 那一句 <c>with</c>），
    /// 而不是让 <c>Decide</c> 去建一份它用不到的文本。</remarks>
    [RequiresUnreferencedCode("调用 IBulkStrategy.ResolveCopyChannel() 那一次按类型名的反射查找：税从这里往上冒到两条公开出口（ExplainBulk / ExecuteBulkAsync）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析装载类需要运行时生成代码。")]
    BulkDecision Decide(Preparation preparation, BulkMethod requested)
    {
        RequireColumns(preparation);

        var provider = _orm.Provider;
        var rows = _rows.Length;

        if (rows == 0)
            return new BulkDecision(new BulkReport(requested, BulkMethod.MultiValue, 0, 0, 0, false,
                "0 行：一层都不必选。不建文本、不取连接，执行直接返回 0。"), null);

        if (requested is BulkMethod.Auto or BulkMethod.CopyChannel)
        {
            var channel = provider.Bulk?.ResolveCopyChannel();
            var refusal = Refusal(provider, channel, preparation.Columns);

            if (refusal is null)
            {
                // 装载通道这一档没有参数预算那道界，所以 _rowsPerRoundTrip 的上限就是这批的行数；
                // 不压时一次递交（执行段用同一组数，两处不许各算一遍）。
                var width = _rowsPerRoundTrip is { } cap && cap < rows ? cap : rows;
                var trips = CopyTrips(preparation, width);

                return new BulkDecision(new BulkReport(requested, BulkMethod.CopyChannel, rows, trips, width, false,
                    $"{rows} 行走 {provider.Kind} 的装载通道（§12.1 的 L1）：不发 SQL 文本、不走参数，" +
                    (preparation.Groups is { Count: > 1 } shardGroups
                        ? $"分 {shardGroups.Count} 张片、共 {trips} 次递交（一片一次递交，跨片不合并）。"
                        : trips == 1 ? "一次递交。" : $"分 {trips} 次递交、每次 {width} 行（块宽是请求压下来的）。") +
                    "通道内部怎么分批是驱动的事，对上层是一次递交一块；改写类钩子（CudBuilding / ParameterBinding）在这一档不适用。"), channel);
            }

            // 请求过装载通道而拿不到：Never 不许退（§12.2 那句"唯一能防住静默逐条的手段"）。
            // Auto 不算降级 —— 它没有承诺过任何一层，取到阶梯上第一档满足条件的层就是它的定义。
            if (requested == BulkMethod.CopyChannel)
            {
                if (_fallback == FallbackPolicy.Never)
                    throw new FastSqlException($"请求了 {provider.Kind} 的装载通道而它不可用：{refusal}。FallbackPolicy.Never 不允许退回文本层。");

                return new BulkDecision(TextReport(preparation, requested, BulkMethod.MultiValue, degraded: true) with
                {
                    Reason = $"{refusal} → 退回 L3 多值语句。{TextReason(preparation, BulkMethod.MultiValue)}",
                }, null);
            }

            return new BulkDecision(TextReport(preparation, requested, BulkMethod.MultiValue, degraded: false), null);
        }

        return new BulkDecision(TextReport(preparation, requested, requested, degraded: false), null);
    }

    /// <summary>这一家能不能整批进装载通道的判据，<b>交回的是话而不是布尔</b>：拒绝的理由要进
    /// <see cref="BulkReport.Reason"/> 给现场读，也要进 <see cref="FallbackPolicy.Never"/> 那句异常，
    /// 两处同一份文本。null = 可以进。</summary>
    static string? Refusal(IDbProvider provider, IBulkCopyChannel? channel, ColumnContract[] columns)
    {
        if (channel is null)
            return $"{provider.Kind} 没有可用的装载通道（IBulkStrategy.ResolveCopyChannel() 交回 null：这一家没接，或这一支驱动里没有装载类）";

        // 一列不合格就整批退回，不是把那一列从通道里省掉：省一列等于那一列的值不进库，那是丢写
        // （IBulkCopyChannel.SupportsColumn 的抬头，与 IChangeGuard.Compares 同一个道理）。
        foreach (var column in columns)
            if (!channel.SupportsColumn(column))
                return $"{column.ColumnName}（CLR {column.ClrType.Name}）不在 {provider.Kind} 装载通道收下的类型里";

        return null;
    }

    BulkReport TextReport(Preparation preparation, BulkMethod requested, BulkMethod method, bool degraded)
        => new(requested, method, _rows.Length, Trips(preparation, method), ChunkRows(preparation, method), degraded, TextReason(preparation, method));

    string TextReason(Preparation preparation, BulkMethod method)
    {
        var provider = _orm.Provider;
        var budget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var rows = _rows.Length;

        if (method == BulkMethod.SingleRow)
            return $"{rows} 行按请求逐条（L5）：{rows} 次往返。语义与批量一致（§12.4 的等价性），代价是往返。";

        var statementRows = ChunkRows(preparation, method);

        return $"{rows} 行 × {preparation.Columns.Length} 列，参数预算 {budget} → 每块 {statementRows} 行（{Trips(preparation, method)} 块），L3 多值语句。" +
               (statementRows < StatementEmitter.RowsPerStatement(budget, preparation.Columns.Length)
                   ? " 每块行数是请求压下来的（WithBulk 的 rowsPerRoundTrip），不是预算算出来的。"
                   : string.Empty);
    }

    /// <summary>每块几行 —— <c>Build</c> 里那一句的同一个公式，两处不许各写一遍算式：都由这一个出口取。</summary>
    /// <remarks><b><see cref="BulkMethod.SingleRow"/> 不是第二套逐条实现，就是把块宽定成 1</b>：
    /// 阶梯最底那一档今天已经在走这条代码路径（回填链上<em>没有</em>独立的逐条循环，块宽为 1 时
    /// 每行拿到的是<em>自己那份</em>经 <c>CudBuilding</c> 改写过的文本，而块与块之间不共享软删后缀），
    /// 所以按请求逐条走的也是同一条：一行一块 = 一次改写 = 一次往返。分岔点在 <c>Build</c> 之下，不在它旁边。</remarks>
    int ChunkRows(Preparation preparation, BulkMethod method)
    {
        if (method == BulkMethod.SingleRow)
            return 1;

        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;

        return Math.Min(StatementEmitter.RowsPerStatement(budget, preparation.Columns.Length), _rowsPerRoundTrip ?? int.MaxValue);
    }

    /// <summary>往返次数。<b>分表时逐片取整再相加</b>：三片各 7 行、块宽 10 是 3 次而不是一次 ——
    /// 一块的 <c>VALUES</c> 只进得了一张表，跨片合并那"半块"没有能落的目标。不分表时那个分支
    /// 就是原来那一句，一个字节都不多分配。</summary>
    int Trips(Preparation preparation, BulkMethod method)
    {
        var width = ChunkRows(preparation, method);

        if (preparation.Groups is not { } groups)
            return (_rows.Length + width - 1) / width;

        var trips = 0;

        foreach (var (_, rows) in groups)
            trips += (rows.Length + width - 1) / width;

        return trips;
    }

    /// <summary>装载通道的递交次数：不分表就是 <c>ceil(总行数 / 块宽)</c>；分表时逐片取整再相加 ——
    /// 三片各 7 行、块宽 10 是 3 次而不是一次，因为一次递交只有一个目标表。
    /// <b><see cref="Decide"/> 的估算与 <see cref="ExecuteBulkAsync"/> 的实数共用这一颗</b>（§12.2 那句
    /// "离线看到的层与执行走的那一层之间没有第二套判据可漂移"）。</summary>
    int CopyTrips(Preparation preparation, int width)
    {
        if (preparation.Groups is not { } groups)
            return (_rows.Length + width - 1) / width;

        var trips = 0;

        foreach (var (_, rows) in groups)
            trips += (rows.Length + width - 1) / width;

        return trips;
    }

    /// <summary>递给装载通道的那一批，<b>分表时一片一份</b>（装载通道的目标表是一次递交一个，
    /// 混不了两片的行）。非分表实体只产出一颗，与不分表的今天逐字同形。</summary>
    List<BulkCopyRequest> CopyRequests(Preparation preparation)
    {
        if (preparation.Groups is not { } groups)
            return [CopyRequestFor(preparation, preparation.Contract.Table, null)];

        var requests = new List<BulkCopyRequest>(groups.Count);

        foreach (var group in groups)
            requests.Add(CopyRequestFor(preparation, group.Table, group.Rows));

        return requests;
    }

    /// <summary><see cref="CopyRequests"/> 的那一份产物：<b>列清单照文本层</b>（自增列与并发列都不进，理由与
    /// <see cref="Prepare"/> 那两条 continue 同一条），每格过 <see cref="Values.IValueBinder"/> 那一扇门。
    /// 目标表名由 <see cref="ISqlDialect.EmitTableName"/> 产，所以 COPY 落的那一张与 <c>INSERT</c> 写的必然是同一张
    /// —— 分表时也是同一张：两者拿的都是 <see cref="Preparation"/> 里那一份已换算好的片名。</summary>
    BulkCopyRequest CopyRequestFor(Preparation preparation, TableInfo table, int[]? ordinals)
    {
        var binder = _orm.Provider.Values;
        var columns = preparation.Columns;
        var count = ordinals?.Length ?? preparation.Cells.Length;
        var payload = new object?[count][];

        for (var row = 0; row < payload.Length; row++)
        {
            var source = preparation.Cells[ordinals is null ? row : ordinals[row]];
            var cells = new object?[columns.Length];

            // 名字在这一趟里是白花的（装载通道不接 DbParameter），但类型归一只长在这颗门上：
            // 另起一条"不带名字的转换"就是第七份 Normalize（§12.1 那句"同一条插入在几家参数化形状不同"的第二个出处）。
            for (var column = 0; column < columns.Length; column++)
                cells[column] = binder.CreateParameter($"c{column}", source[column], columns[column]).Value;

            payload[row] = cells;
        }

        return new BulkCopyRequest(DestinationTable(preparation, table), columns, payload);
    }

    /// <summary>把已算好的那一批切出第 <paramref name="first"/> 行起的 <paramref name="width"/> 行。
    /// <b>切的是行数组，不重走取值与归一</b>：<see cref="CopyRequests"/> 每片只算一次，格子值与列清单在几次递交之间
    /// 是同一份 —— 而 §12.4 第 1 项那句"行序就是入参顺序"要靠这一点才成立。</summary>
    static BulkCopyRequest Slice(BulkCopyRequest batch, int first, int width)
    {
        var count = Math.Min(width, batch.Rows.Count - first);
        var rows = new object?[count][];

        for (var row = 0; row < count; row++)
            rows[row] = batch.Rows[first + row];

        return batch with { Rows = rows };
    }

    string DestinationTable(Preparation preparation, TableInfo table)
    {
        var dialect = _orm.Provider.Dialect;
        var writer = new SqlWriter(new StringBuilder(table.Name.Length + 8), [], dialect.ParameterPrefix);

        dialect.EmitTableName(writer, table);

        return writer.ToSqlString();
    }

    /// <summary>0 列的插入只剩 <c>DEFAULT VALUES</c> 一种写法，而本版本未接入（§12.3）。
    /// 抽成一处是因为它有<em>两个</em>读者：文本层在建语句之前要问，装载通道在选层之前也要问
    /// —— 而"零列的 COPY"恰好是那种能绿着把 0 个字段写进 N 行的形状，不能让它比文本层更晚红。</summary>
    static void RequireColumns(Preparation preparation)
    {
        if (preparation.Columns.Length == 0)
            throw new FastSqlException($"{preparation.Table} 的每一列都不进列清单（自增列或并发列），插入只剩 DEFAULT VALUES 这一种写法，本版本未接入（§12.3）。");
    }

    /// <summary>决策 + 决策依据的那一个通道对象。<see cref="BulkDecision.Channel"/> 只在method == CopyChannel 时非 null：
    /// "能不能用"与"用它"因此是同一次判定的两个产物，执行时不再取第二次 <c>Bulk?.ResolveCopyChannel()</c>（那一取会看到另一个实例）。</summary>
    sealed record BulkDecision(BulkReport Report, IBulkCopyChannel? Channel);


    public async Task<long> ExecuteIdentityAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();
        var keys = preparation.Keys;

        if (_rows.Length != 1)
            throw new FastSqlException($"ExecuteIdentityAsync 只用于单行插入（当前 {_rows.Length} 行）：多行请用 ExecuteReturnKeysAsync 一次拿全。");

        if (keys.Length == 0)
            throw new FastSqlException($"{preparation.Table} 没有自增列，回填不出标识值。主键是客户端生成的话直接用 ExecuteAffrowsAsync。");

        if (keys.Length > 1)
            throw new FastSqlException($"{preparation.Table} 有 {keys.Length} 个自增列，一份 long 装不下：请用 ExecuteReturnKeysAsync 拿实体。");

        // 整数性在这里判，而不是读回来再判：读回来才发现类型不对时，行已经写进库了。
        var member = Nullable.GetUnderlyingType(keys[0].ClrType) ?? keys[0].ClrType;

        if (member != typeof(long) && member != typeof(int) && member != typeof(short) && member != typeof(byte))
            throw new FastSqlException($"自增列 {keys[0].MemberName} 是 {member.Name}，不是整数：ExecuteIdentityAsync 的返回类型是 long，请改用 ExecuteReturnKeysAsync。");

        await ExecuteReturnKeysAsync(ct).ConfigureAwait(false);

        return Convert.ToInt64(preparation.Schema.GetValue(_rows[0], preparation.KeyOrdinals[0]), CultureInfo.InvariantCulture);
    }

    public Task<List<T>> ExecuteReturnKeysAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();

        if (preparation.Keys.Length == 0)
            throw new FastSqlException($"{preparation.Table} 没有自增列：没有键可回填。要受影响行数请用 ExecuteAffrowsAsync。");

        return ReadBackAsync(preparation, preparation.KeyOrdinals, Plan(Backfill.Keys), ct);
    }

    /// <summary>把「值归库管的那几列」（自增列 ∪ 并发列）读回实体 —— <see cref="ExecuteReturnKeysAsync"/> 的超集，
    /// 没有并发列时两档产出逐字相同的文本、走同一次往返。跟踪器的那一档插入走这里（§8.2 的回填集）。</summary>
    public Task ExecuteGeneratedAsync(CancellationToken ct = default)
    {
        var preparation = Prepare();

        if (preparation.GeneratedOrdinals.Length == 0)
            throw new FastSqlException($"{preparation.Table} 既没有自增列也没有并发列：没有任何一列的值归库管，回填那一档无事可做。");

        return ReadBackAsync(preparation, preparation.GeneratedOrdinals, Plan(Backfill.Generated), ct);
    }

    /// <summary>回填的那一段。<paramref name="ordinals"/> 已经按 <c>Returning</c> 的列序排好，
    /// 所以回包的第 <c>column</c> 列直接落进实体的对应槽位，不需要再查一次名字。</summary>
    async Task<List<T>> ReadBackAsync(Preparation preparation, int[] ordinals, InsertPlan plan, CancellationToken ct)
    {
        var expected = ordinals.Length;
        var tag = Tag();

        foreach (var chunk in plan.Chunks)
        {
            var rowOrdinals = chunk.RowOrdinals;
            var first = chunk.FirstRow;
            var backfilled = 0;

            await _orm.Ado.WriteWithResultAsync(chunk.Sql, tag, (reader, index) =>
            {
                if (index == 0 && reader.FieldCount != expected)
                    throw new FastSqlException($"{_orm.Provider.Kind} 的回填结果集有 {reader.FieldCount} 列，期望 {expected} 列：发射层与键策略对不上，不能假设回填可用。");

                // 回包第 index 行 ↔ 这一块的第 index 行。这条映射就是 §12.4 第 1 项的全部内容，
                // 集成测试按 payload 逐行证明它，而不是只证明"拿到了某个键"。
                // 分表时那一块行在入参数组里不连续，所以块自己带着那份下标表；不分表时它是 null，
                // 走的还是 FirstRow + 行号那一句（不多一次数组分配）。
                var entity = _rows[rowOrdinals is null ? first + index : rowOrdinals[index]];

                for (var column = 0; column < expected; column++)
                    preparation.Schema.SetValue(entity, ordinals[column], reader.GetValue(column));

                backfilled++;
            }, ct).ConfigureAwait(false);

            // <b>v0.32 第二格补的那一判</b>：上面那句列数检查只在<em>至少回来一行</em>时才走得到，
            // 而虚谷把这一格踩空了 —— <c>INSERT … RETURNING "ID"</c> 在那台活库上<em>执行成功、行也真进去了</em>，
            // 回包却是 <c>FIELDS=0 ROWS=0</c>（出处 <c>obj/logs/v32-recon-keys-1.log</c> 的 N-06，
            // 多行 VALUES 那一形同红：N-07），于是实体上的自增列留在 <c>default</c>，
            // 而 <c>obj/logs/v32-recon-orm-2.log</c> 的 O-11 把这件事印成了"写侧 OK 回填的 Id=0"。
            // 少一行比少一列更危险：少一列当场红，少几行是<em>绿着把错值交出去</em>，
            // 调用方拿到的是"看着合法"的 0 而不是异常（§10.2 说的"三个月后才现形"那一类）。
            // 这一判不改变任何一家已连过库的产出：四家里的 SQL Server 走 <c>OUTPUT</c>、金仓走 <c>RETURNING</c>，
            // 两家的回包行数本来就等于块行数，而达梦/Oracle 两家在 <c>SupportsBulkKeyRetrieval</c>
            // 那里已经被拆成逐条了（<see cref="Build"/> 的那条分支）—— 而"不改变"这句在本版不由我说，
            // 由 §13.4 那 95 条在两台授权 SQL Server 实例上复跑说了算。
            if (backfilled != chunk.RowCount)
                throw new FastSqlException(
                    $"{_orm.Provider.Kind} 的插入执行成功却只回填了 {backfilled}/{chunk.RowCount} 行：" +
                    "这一家的回填形态没有把键发回客户端，不能假设回填可用。虚谷实测：语句收下、行进去了、结果集是空的。");
        }

        return [.. _rows];
    }

    // ── 构建 ──────────────────────────────────────────────────────────

    InsertPlan Plan(Backfill backfill)
    {
        // 三条分支各自缓存一份，而不是 ref 三元链套一个 switch：编译器不接受嵌套的 ref 条件运算符，
        // 而换成一个按枚举下标取的数组就等于给每条链多一次分配（§12.7 的每查询字节是钉住的数）。
        if (backfill == Backfill.None)
            return _plain ??= Build(Prepare(), backfill);

        return backfill == Backfill.Keys
            ? _withKeys ??= Build(Prepare(), backfill)
            : _withGenerated ??= Build(Prepare(), backfill);
    }

    /// <summary>取值段：列合成 → 钩子填充 → 契约校验 → 行 × 列的实参值。只跑一次。</summary>
    Preparation Prepare()
    {
        if (_preparation is { } done)
            return done;

        var contract = _orm.Schemas.Contract<T>();
        var schema = _orm.Schemas.Schema<T>();
        var validator = _orm.Schemas.Validator<T>();
        var audit = _orm.Aop.AuditValue;

        var columns = new List<ColumnContract>(contract.Columns.Length);
        var ordinals = new List<int>(contract.Columns.Length);
        var keys = new List<ColumnContract>();
        var keyOrdinals = new List<int>();
        // 没有并发列的实体占绝大多数，所以这两张表是懒出来的：先建好再判断等于向每一条批量插入收一笔"为别人设计的列"的钱。
        List<ColumnContract>? tokens = null;
        List<int>? tokenOrdinals = null;

        for (var ordinal = 0; ordinal < contract.Columns.Length; ordinal++)
        {
            var column = contract.Columns[ordinal];

            // 自增列不进列清单（§12.4 第 1 项）：它的值由库决定，写进去只会得到一个"填的数被忽略"的错。
            // 要显式写自增列是 IDENTITY_INSERT 那条路（IKeyStrategy.IdentityInsertToggle），P0 未接。
            if (column.IsIdentity)
            {
                keys.Add(column);
                keyOrdinals.Add(ordinal);

                continue;
            }

            // 并发列同样不进列清单，理由与自增列同一条：那一列的值不归这一侧决定（SQL Server 的 rowversion 由库递增）。
            // 它也不进 AuditValue 与 §10.6 校验 —— 一列不写出去，就没有可校验的值。
            if (column.IsConcurrencyToken)
            {
                (tokens ??= []).Add(column);
                (tokenOrdinals ??= []).Add(ordinal);

                continue;
            }

            columns.Add(column);
            ordinals.Add(ordinal);
        }

        // 回填集 = 自增列 ∪ 并发列，两半的下标表按同一顺序并起来：回包是按位置落进实体的，
        // 所以 Returning 的列序与这张下标表必须是同一把尺子（因此写在一处，不各自再排一遍）。
        // 没有并发列时两半是同一份数组 —— 逐字同文的两个出口不必各自占一格（§8.2 那一档插入因此不加倍）。
        ColumnContract[] keyColumns = [.. keys];
        int[] identityOrdinals = [.. keyOrdinals];

        var traces = new List<HookTrace>();
        var cells = new object?[_rows.Length][];
        var failures = new List<ValidationFailure>();
        var rowFailures = new List<ValidationFailure>();
        var step = 0;

        for (var row = 0; row < _rows.Length; row++)
        {
            var entity = _rows[row] ?? throw new FastSqlException($"第 {row} 行是 null：插入集合里不能有 null 实体。");

            cells[row] = new object?[columns.Count];

            // 顺序是「先填充、后校验」：租户列这类 NOT NULL 列正是靠 AuditValue 补上的，
            // 反过来会把钩子刚填好的值判成缺，或者更糟——把本该由钩子补的列静默放过。
            for (var column = 0; column < columns.Count; column++)
            {
                var ordinal = ordinals[column];
                var value = schema.GetValue(entity, ordinal);

                // 硬规则 2「未订阅即零成本」：不订阅就不构造事件对象。
                if (audit is null)
                {
                    cells[row][column] = value;
                    continue;
                }

                var @event = new AuditValueEvent(entity, columns[column], value, written => schema.SetValue(entity, ordinal, written));

                // 改写类钩子的异常直接向上失败（硬规则 3）：吞掉等于写出一份"填充没发生"的行。
                audit(@event);

                if (@event.WasSet)
                {
                    traces.Add(new HookTrace(step++, "AuditValue", HookCategory.Rewrite, $"{columns[column].MemberName} = {StatementEmitter.Text(value)}", $"{columns[column].MemberName} = {StatementEmitter.Text(@event.CurrentValue)}"));
                    value = @event.CurrentValue;
                }

                cells[row][column] = value;
            }

            if (validator.Mode == ValidationMode.Off)
                continue;

            rowFailures.Clear();

            if (validator.TryValidate(entity, rowFailures))
                continue;

            // 单行时不加行号：那时"哪一行"没有意义，而消息文本已经点了成员与列（§10.6 要的正是定位到行）。
            foreach (var failure in rowFailures)
                failures.Add(_rows.Length == 1 ? failure : failure with { Message = $"[第 {row} 行] {failure.Message}" });
        }

        if (failures.Count > 0)
            throw new ValidationFailedException(
                $"{_rows.Length} 行插入被拒绝：{failures.Count} 项违反库契约（ValidationMode.{validator.Mode}）。整批先校验后执行，一行都没写进库。",
                failures);

        return _preparation = new Preparation(
            contract,
            schema,
            [.. columns],
            [.. ordinals],
            keyColumns,
            identityOrdinals,
            tokens is null ? keyColumns : [.. keyColumns, .. tokens],
            tokenOrdinals is null ? identityOrdinals : [.. identityOrdinals, .. tokenOrdinals],
            cells,
            traces)
        {
            Groups = SplitGroups(contract, schema),
        };
    }

    /// <summary>分表实体把整批按目标片分组（片内保留<em>入参顺序</em>的行下标，片的先后按各片首行第一次
    /// 出现的先后），非分表实体交回 <c>null</c> = "整批同一张表，照旧按连续区间切块"。
    ///
    /// <para><b>算在取值段而不是发射段</b>：<see cref="SplitRouting.Resolve"/> 读的是实体上的分片键值，而
    /// <c>AuditValue</c> 刚在那一段跑过 —— 发射段再读一次，三个出口（各自的文本、各自的构建时机）就可能读到
    /// 钩子改过的与没改过的两份值，于是同一批行在不同出口里进了不同的片。</para>
    ///
    /// <para>分片键为空的那一行在这里当场抛：一条都不写，而不是按 SqlSugar 那一族的"空值当作今天"
    /// 把行静默送进本月那张表（<c>SplitTableService.cs:65,75-76</c> 的读码结论）。</para></summary>
    List<(TableInfo Table, int[] Rows)>? SplitGroups(EntityContract contract, IEntityTypeSchema<T> schema)
    {
        if (contract.Table.Split is not { } split)
            return null;

        var ordinal = Array.IndexOf(contract.Columns, contract.ByMember[split.MemberName]);
        var groups = new List<(TableInfo Table, List<int> Rows)>();
        var index = new Dictionary<TableInfo, int>();

        for (var row = 0; row < _rows.Length; row++)
        {
            var table = SplitRouting.Resolve(contract.Table, split, schema.GetValue(_rows[row], ordinal));

            if (index.TryGetValue(table, out var at))
                groups[at].Rows.Add(row);
            else
            {
                index[table] = groups.Count;
                groups.Add((table, [row]));
            }
        }

        return [.. groups.ConvertAll(g => (g.Table, g.Rows.ToArray()))];
    }

    /// <summary>发射段：按 §12.3 的公式分块，每块一份独立 AST + <see cref="DialectSql"/> + 参数表。</summary>
    InsertPlan Build(Preparation preparation, Backfill backfill)
    {
        var provider = _orm.Provider;
        var dialect = provider.Dialect;
        var columns = preparation.Columns;
        var budget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var traces = new List<HookTrace>(preparation.Traces);
        var step = traces.Count;

        if (_rows.Length == 0)
            return new InsertPlan([], 0, columns.Length, 0, budget, "0 行：不产生任何语句，执行直接返回 0。", traces) { Provider = provider };

        RequireColumns(preparation);

        // 回填集为空就没有那一档：带 Returning 的文本会生成一条「看着像在回填」的普通 INSERT。
        // 执行口自己会拒（ReadBackAsync 之前先查），但离线口是给人审阅用的，读到一条假 OUTPUT 就已经误导了。
        ColumnContract[] returning = backfill switch
        {
            Backfill.None => [],
            Backfill.Keys => Require(preparation.Keys, "没有自增列：回填主键那一档根本没有东西可回。主键由调用方自己生成的话直接用 Explain()/ExecuteAffrowsAsync。"),
            _ => Require(preparation.Generated, "既没有自增列也没有并发列：没有任何一列的值归库管，回填那一档无事可做。"),
        };

        // §12.3：预算先留十分之一的余量（文本里还有表名/列名/OUTPUT），再除以每行参数个数，至少 1 行。
        // 请求把块宽压下来（WithBulk 的 rowsPerRoundTrip）或按请求逐条，都走这同一个出口取数（见 ChunkRows 的抬头）。
        var budgetRows = StatementEmitter.RowsPerStatement(budget, columns.Length);
        var rowsPerStatement = ChunkRows(preparation, _requested ?? BulkMethod.MultiValue);
        var degraded = false;

        // L3 → L5 的那一跳（§12.4 第 1 项）：没有批量 RETURNING/OUTPUT 的库要回填，只能逐条。
        // SQL Server 走不到（Keys.SupportsBulkKeyRetrieval = true），达梦/Oracle 系必然走到。
        if (backfill != Backfill.None && !provider.Keys.SupportsBulkKeyRetrieval)
        {
            rowsPerStatement = 1;
            degraded = true;
        }

        // 逐条之后键落在哪里 —— v0.34 立的那颗口子（§14 卡了两家的那格）。资格收窄到三件同时成立：
        // 这一家没有批量 RETURNING、声明了"语句后回读"、而回填集恰好是一颗自增列。并发列与多颗键
        // 都不在回读句给得出的东西里，它们照旧走 EmitReturning 的拒绝路径，不在这里假装覆盖。
        var postRead = degraded && provider.Keys.SupportsPostStatementKeyRead && returning is [{ IsIdentity: true }];

        var chunks = new List<InsertChunk>((_rows.Length + rowsPerStatement - 1) / rowsPerStatement);

        // 建一段文本：不分表时是"连续的这几行"（ordinals 为 null，回填按 FirstRow + 行号对回去），
        // 分表时是"这一张片的那几行"，它们的下标不连续，所以块要自带那份下标表。
        // 两条路共用同一段发射与钩子代码 —— 差别只在表位给哪张表、哪几行进这一块。
        void EmitChunk(TableInfo table, int[]? ordinals, int first, int count)
        {
            var ast = new InsertStatement
            {
                Table = new TableRef(0, FastSqlOrm.AliasAt(0), table, preparation.Contract),
                Columns = columns,
                Rows = ordinals is null
                    ? RowsOf(preparation.Cells, first, count, columns)
                    : RowsOf(preparation.Cells, ordinals, columns),

                // 回读形不发 RETURNING：键不是这条 INSERT 的结果集，是同一句命令尾巴上那条 SELECT 的。
                Returning = postRead ? [] : returning,
            };

            var writer = new SqlWriter(new StringBuilder(EstimateLength(columns, count)), [], dialect.ParameterPrefix);
            dialect.Emit(in ast, writer);

            if (postRead)
            {
                writer.Append(" ; ");
                provider.Keys.EmitPostStatementKeyRead(writer, returning[0]);
            }

            var emitted = new DialectSql(writer.ToSqlString(), writer.Parameters);
            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "insert", traces, ref step), traces, ref step);

            chunks.Add(new InsertChunk(ordinals is null ? first : ordinals[0], count, ast, sql) { RowOrdinals = ordinals });
        }

        if (preparation.Groups is null)
        {
            for (var first = 0; first < _rows.Length; first += rowsPerStatement)
                EmitChunk(preparation.Contract.Table, null, first, Math.Min(rowsPerStatement, _rows.Length - first));
        }
        else
        {
            foreach (var (table, rows) in preparation.Groups)
                for (var first = 0; first < rows.Length; first += rowsPerStatement)
                {
                    var count = Math.Min(rowsPerStatement, rows.Length - first);
                    EmitChunk(table, rows[first..(first + count)], 0, count);
                }
        }

        var reason = degraded
            ? $"{_rows.Length} 行 × {columns.Length} 列：{provider.Kind} 无批量 RETURNING 而这次要回填 → 退到 L5 逐条（{_rows.Length} 块）" +
              (postRead ? "，每块句尾接语句后回读（键在同一句命令里带回，§14 的那颗口子）" : "，而这一家连语句后回读也没有：构建到 EmitReturning 即拒") +
              $"。语义与批量一致（§12.4 的等价性），代价是 {_rows.Length} 次往返。"
            : $"{_rows.Length} 行 × {columns.Length} 列，参数预算 {budget} → 每块 {rowsPerStatement} 行（{chunks.Count} 块），L3 多值语句。";

        if (_requested == BulkMethod.SingleRow)
            reason += " 块宽 1 是按请求来的（WithBulk(SingleRow)），不是这一家给不出批量。";
        else if (_rowsPerRoundTrip is { } cap && cap < budgetRows && !degraded)
            reason += $" 块宽 {rowsPerStatement} 是请求压下来的（预算允许 {budgetRows} 行）。";

        if (preparation.Groups is { Count: > 1 } shards)
            reason += $" 这批行分布在 {shards.Count} 张片上：块按片切，跨片不共用一条语句（一条 INSERT 只有一个目标表）。";

        if (chunks.Count > 1)
            reason += " 跨块不是原子的（§12.4 第 8 项）：要整批原子就包 §6 的事务。";

        return new InsertPlan(chunks, _rows.Length, columns.Length, rowsPerStatement, budget, reason, traces) { Provider = provider };
    }

    ColumnContract[] Require(ColumnContract[] columns, string whyEmpty)
        => columns.Length > 0 ? columns : throw new FastSqlException($"{Prepare().Table} {whyEmpty}");

    /// <summary>每格都参数化；<c>ParameterBinding</c> 的回放在 <c>StatementEmitter.ApplyBinding</c>，三条写链共用一份。
    /// <b>不分表走这一颗，一块不给它多分配一个下标数组</b>（§12.7 钉的是每次插入的字节数与分配数，
    /// 为分表那一条路把连续区间物化成 <c>int[]</c> 是让整批调用方替它付钱）。</summary>
    static IReadOnlyList<IReadOnlyList<SqlExpression?>> RowsOf(object?[][] cells, int first, int count, ColumnContract[] columns)
    {
        var rows = new IReadOnlyList<SqlExpression?>[count];

        for (var row = 0; row < count; row++)
        {
            var source = cells[first + row];
            var values = new SqlExpression?[columns.Length];

            // 每一格都是参数：这一版不产生 DEFAULT 格（那要走"部分列插入"，见 §16 的 IgnoreColumns）。
            // 值为 null 也照样参数化成 NULL，而不是让那一格落 DEFAULT —— 有默认值约束的列上这两者不同。
            for (var column = 0; column < columns.Length; column++)
                values[column] = new ParameterValue(source[column], columns[column]);

            rows[row] = values;
        }

        return rows;
    }

    /// <summary>分表那一块：行在入参数组里不连续（按月切表时相邻两行可以隔三个月），所以这里按
    /// 那一份下标表取行，其余与上面那颗逐字相同。两棵的<em>内层</em>只有一份参数化形状可言，
    /// 是因为它总共就三行 —— 抽成一颗共享方法要为每次分块多走一层调用与一个数组。</summary>
    static IReadOnlyList<IReadOnlyList<SqlExpression?>> RowsOf(object?[][] cells, int[] ordinals, ColumnContract[] columns)
    {
        var rows = new IReadOnlyList<SqlExpression?>[ordinals.Length];

        for (var row = 0; row < rows.Length; row++)
        {
            var source = cells[ordinals[row]];
            var values = new SqlExpression?[columns.Length];

            for (var column = 0; column < columns.Length; column++)
                values[column] = new ParameterValue(source[column], columns[column]);

            rows[row] = values;
        }

        return rows;
    }

    /// <summary>只用于 <see cref="StringBuilder"/> 的预容量（§12.7 E）：每颗参数按 <c>@p123, </c> 估 7 个字符。</summary>
    static int EstimateLength(ColumnContract[] columns, int rows)
    {
        var length = 96 + rows * (columns.Length * 7 + 4);

        foreach (var column in columns)
            length += column.ColumnName.Length + 3;

        return length;
    }

    /// <summary>集群从契约的表位算（§16.4）：写侧永远是主库那一格，所以这里只带名字，不带角色 ——
    /// 那个结论由 <c>AdoExecutor.RentAsync</c> 一处下，两处各下一遍就会漂移。</summary>
    ExecutionTag Tag()
    {
        var table = Prepare().Contract.Table;

        return new ExecutionTag("insert", [table.Name], DataSourceRouting.ClusterOf(table));
    }

    /// <summary>取值段的产物。<see cref="Cells"/> 已经是 <c>AuditValue</c> 之后的值，
    /// 所以三个出口共用它 = 同一份数据、几种文本。</summary>
    sealed record Preparation(
        EntityContract Contract,
        IEntityTypeSchema<T> Schema,
        ColumnContract[] Columns,
        int[] Ordinals,
        ColumnContract[] Keys,
        int[] KeyOrdinals,
        ColumnContract[] Generated,
        int[] GeneratedOrdinals,
        object?[][] Cells,
        IReadOnlyList<HookTrace> Traces)
    {
        /// <summary>分表实体的目标片分组，null = 不分表（整批同一张表，切块照旧走连续区间）。
        /// 片的<em>内容</em>是那几行在入参数组里的下标，所以一个块装的行可以不连续，而回填仍按
        /// <see cref="InsertChunk.RowOrdinals"/> 对得回去。<b>在取值段一次算完</b>：发射段、装载通道、
        /// 往返次数估算三处都要分组，而 <see cref="SplitRouting.Resolve"/> 读的是实体上已被
        /// <c>AuditValue</c> 填过的分片键值 —— 三处各读各的就会给出三份可能不同的分片答案。</summary>
        public IReadOnlyList<(TableInfo Table, int[] Rows)>? Groups { get; init; }

        public string Table => Contract.Table.Name;
    }

    /// <summary>发射段要不要带 <c>Returning</c>，以及带哪几列。三档而不是布尔：并发列（§8.2）落地后，
    /// 「回填主键」（公开口那一档）与「回填库里给的那些列」（跟踪器那一档）是两件事，
    /// 而只有后者在 <c>OUTPUT</c> 里多出一列 —— 让前者跟着变就把黄金样本白改了一遍。</summary>
    enum Backfill
    {
        None,

        /// <summary>只有自增列。没有并发列时与 <see cref="Generated"/> 逐字同文。</summary>
        Keys,

        /// <summary>自增列 ∪ 并发列。</summary>
        Generated,
    }
}
