using System.Diagnostics.CodeAnalysis;
using System.Text;
using FastSql.Aop;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Write;

/// <summary><c>UpdateRange</c> 链的落点：按主键整行更新。与 <see cref="UpdateEngine{T}"/> 的关键差别
/// 在两侧各让了一步——那条链由调用方点名改哪几列、一条语句；这一条列清单由契约给、一次往返可以并几条语句。
/// 表达式树一个都没有（列来自 <see cref="EntityContract"/>、值来自编译好的读写委托），所以入口零 RUC/RDC 税，
/// 与 <see cref="InsertEngine{T}"/> 同档。</summary>
sealed class UpdateBulkEngine<T> : IUpdateBulk<T> where T : class
{
    readonly FastSqlOrm _orm;
    readonly T[] _rows;
    readonly EntityContract _contract;
    readonly IEntityTypeSchema<T> _schema;
    readonly TableRef _table;
    readonly ColumnContract[] _settable;
    readonly int[] _settableOrdinals;
    readonly ColumnContract[] _keys;
    readonly int[] _keyOrdinals;

    BulkWriteMethod _requested = BulkWriteMethod.Auto;
    FallbackPolicy _fallback = FallbackPolicy.DegradeAndLog;
    bool _atomic = true;

    BulkWritePlan? _plan;

    /// <summary>取值段的格子：每行 × 每个<em>可写</em>列一颗，已经是 <c>AuditValue</c> 之后的值。
    /// null = 那一段还没跑过。<b>主键列不在里面</b>——它是用来定位那一行的，不是被写出去的列，理由见 <see cref="Prepare"/>。</summary>
    object?[][]? _cells;

    /// <summary>取值段那几笔改写痕迹，发射段的 <c>traces</c> 以它打头（§10.1 硬规则 1：钩子按顺序、步号连续才回放得起来）。
    /// 没人订阅时是空表。</summary>
    IReadOnlyList<HookTrace> _valueTraces = [];

    public UpdateBulkEngine(FastSqlOrm orm, T[] rows)
    {
        _orm = orm;
        _rows = rows;
        _contract = orm.Schemas.Contract<T>();
        _schema = orm.Schemas.Schema<T>();

        if (_contract.Table.Split is not null)
            throw new FastSqlException(
                $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），UpdateRange 不接：一批行会散在几张片上，" +
                "而这一档今天没有按片分组的账。要批量改一片就选片后逐条走 orm.Update<T>().SplitTable(那一片)。");

        if (_contract.PrimaryKey.Length == 0)
            throw new FastSqlException(
                $"{_contract.Table.Name} 没有主键列：UpdateRange 按主键定位，WHERE 出不来。" +
                "要么给实体点主键（[PrimaryKey] 或约定名 Id），要么按条件走 orm.Update<T>().Set(...).Where(...)。");

        _keys = _contract.PrimaryKey;

        var settable = new List<ColumnContract>(_contract.Columns.Length);
        var setOrdinals = new List<int>(_contract.Columns.Length);
        var keyOrdinals = new List<int>(_keys.Length);

        for (var ordinal = 0; ordinal < _contract.Columns.Length; ordinal++)
        {
            var column = _contract.Columns[ordinal];

            // 主键进 WHERE 不进 SET；库里给值的列（自增/计算列）与并发列不接受书写——三条判据与
            // UpdateEngine.ColumnOf 逐字同源，那里拦调用方点名的列，这里拦契约整行的列。
            if (column.IsPrimary || column.ValueFromStore || column.IsConcurrencyToken)
                continue;

            settable.Add(column);
            setOrdinals.Add(ordinal);
        }

        if (settable.Count == 0)
            throw new FastSqlException($"{_contract.Table.Name} 除了主键与库里给值的那几列没有可写的列：整行更新只剩 SET 空串，那样的语句没有意义。");

        _settable = [.. settable];
        _settableOrdinals = [.. setOrdinals];

        foreach (var key in _keys)
            keyOrdinals.Add(Array.IndexOf(_contract.Columns, key));

        _keyOrdinals = [.. keyOrdinals];

        _table = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
    }

    public IUpdateBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog)
    {
        _requested = method;
        _fallback = fallback;
        _plan = null;

        // 只作废发射段那份：换档改的是"几条语句、怎么并"，而取值段（AuditValue）的契约是
        // §10.1 硬规则 1 那一半——同一批实体只许问一次。与 InsertEngine.WithBulk 同一条分寸。
        return this;
    }

    public IUpdateBulk<T> WithBatchTransaction(bool atomic = true)
    {
        _atomic = atomic;

        return this;
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public BulkWritePlan Explain()
    {
        var (method, _) = Decide(_requested);

        return _plan ??= Build(method);
    }

    public BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null) => Decide(requested ?? _requested).Report;

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public async Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default)
    {
        var (method, report) = Decide(_requested);

        if (_rows.Length == 0)
            return report;

        var plan = Build(method);

        // 决策里的估数换成发射段的实数（条数由 Build 算，两处不许各算一遍），与 InsertEngine 同一条覆写。
        report = report with { ChunkSize = plan.RowsPerCommand, RoundTrips = plan.Commands.Count };

        var tag = Tag();

        async Task SendAll()
        {
            foreach (var command in plan.Commands)
                await _orm.Ado.NonQueryAsync(command, tag, ct).ConfigureAwait(false);
        }

        // 一次提交那一档（WithBatchTransaction 的默认）。判据是<em>语句数</em>而不是命令数：并档把 2000 句并成
        // 一条命令，服务器侧仍然一句一提交，而每次提交 flush 一次 redo——远端实例上 2000 行整行更新量到
        // 6386 ms ∶ 263 ms，差距全部来自提交次数【实测·obj/logs/fscompare-C.txt S3 的四支诊断档】。
        // UpdateRange 的语句数：并档一行一句、逐条档一行一句，两档在这一点上同形；而 upsert 档<em>一句命令
        // 就是一句</em>（多值 VALUES 是一句），所以那一档的语句数是命令数。判错了就是该包的没包、不必包的白包。
        var statements = method == BulkWriteMethod.Upsert ? plan.Commands.Count : _rows.Length;

        if (BulkAtomicity.ShouldWrap(_atomic, statements) && FastSqlTransaction.For(_orm.Ado) is null)
        {
            using var tran = _orm.BeginTransaction(DataSourceRouting.ClusterOf(_contract.Table));

            await SendAll().ConfigureAwait(false);
            tran.Commit();

            return report with { UsedTransaction = true };
        }

        await SendAll().ConfigureAwait(false);

        return report;
    }

    /// <summary>档位判定：纯函数，不建文本、不碰连接（判据与 <c>InsertEngine.Decide</c> 同一个形状）。
    /// Auto 拿不到 Merged <b>不算降级</b>——它没承诺过任何一档；点名 Merged 而拿不到才算，拿不到时
    /// Never 抛、其余退逐条并带降级标记。<b>参数预算在这里不参与</b>：逐条那条路每颗参数都各走各的语句，
    /// 预算管的是并档时一条命令塞几行，退到逐条就没有"塞"这件事了。</summary>
    (BulkWriteMethod Method, BulkWriteReport Report) Decide(BulkWriteMethod requested)
    {
        var provider = _orm.Provider;
        var rows = _rows.Length;

        if (rows == 0)
            return (BulkWriteMethod.Merged, new BulkWriteReport(requested, BulkWriteMethod.Merged, 0, 0, 0, false,
                "0 行：一档都不必选。不建文本、不取连接，执行直接返回 0。"));

        var supports = provider.Bulk?.SupportsMergedStatements == true;

        // SingleRow 是点名最底档；Merged 点名而拿不到 → 按 fallback 三档办；Auto 拿不到 → 取逐条、不算降级。
        if (requested == BulkWriteMethod.SingleRow)
            return Textless(BulkWriteMethod.SingleRow, requested, degraded: false,
                $"{rows} 行按请求逐条（一行一条语句、一次往返）。语义与并档一致（§12.4 的等价性），代价是 {rows} 次往返。");

        // Upsert 只在被点名时参与：Auto 的阶梯永远不取它（语义差不是速度差，判据见 IWriteBulk.cs 那颗枚举）。
        if (requested == BulkWriteMethod.Upsert)
        {
            var clause = UpsertClause(provider.Dialect);

            if (clause is null)
            {
                if (_fallback == FallbackPolicy.Never)
                    throw new FastSqlException($"请求了 {provider.Kind} 的一句 upsert 档而它没交 UpsertClause：退路被 FallbackPolicy.Never 封死。");

                // 退到"这一家的最佳改档"：数字（往返与块宽）由那一家自己那条阶梯算，这里不另算一遍，
                // 只把 Requested 换回被点名的那一颗并把降级记上——语义跟着退，那句话必须让现场看见。
                var best = supports ? BulkWriteMethod.Merged : BulkWriteMethod.SingleRow;
                var (method, report) = Decide(best);

                return (method, report with
                {
                    Requested = requested,
                    Degraded = true,
                    Reason = $"{provider.Kind} 没接一句 upsert 的句尾（UpsertClause 交回 null）→ 退到这一家的最佳改档 {method}：" +
                             $"{report.Reason} 这一退换掉的是语义不只是速度：库里没有的那一行不会再插进去。",
                });
            }

            var upsertBudget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
            var upsertWidth = StatementEmitter.RowsPerStatement(upsertBudget, _settable.Length + _keys.Length);
            var upsertTrips = (rows + upsertWidth - 1) / upsertWidth;

            return (BulkWriteMethod.Upsert, new BulkWriteReport(requested, BulkWriteMethod.Upsert, rows, upsertTrips, upsertWidth, false,
                $"一句 upsert（{provider.Kind} 交的句尾）：{rows} 行 × 每行 {_settable.Length + _keys.Length} 颗参数" +
                $"（写入清单 = 可写 {_settable.Length} 列 + 主键 {_keys.Length} 列），参数预算 {upsertBudget} → " +
                $"每条命令 {upsertWidth} 行（{upsertTrips} 条命令），一条命令 = 一句 = 一次往返。" +
                $"库里没有的那一行会插进去（与 Merged 档的语义差）。{BulkAtomicity.Sentence(_atomic, upsertTrips)}"));
        }

        if (requested == BulkWriteMethod.Merged && !supports)
        {
            if (_fallback == FallbackPolicy.Never)
                throw new FastSqlException($"请求了 {provider.Kind} 的多语句合并档而它没声明 SupportsMergedStatements：退路被 FallbackPolicy.Never 封死。");

            return Textless(BulkWriteMethod.SingleRow, requested, degraded: true,
                $"{provider.Kind} 没有多语句批的读数（SupportsMergedStatements=false）→ 退回逐条：{rows} 次往返。");
        }

        if (!supports)
            return Textless(BulkWriteMethod.SingleRow, requested, degraded: false,
                $"{rows} 行走逐条：{provider.Kind} 今天没有多语句批的读数，Auto 取阶梯上第一个满足条件的档（§12.1 写侧扩展）。");

        var budget = provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var width = StatementEmitter.RowsPerStatement(budget, _settable.Length + _keys.Length);
        var trips = (rows + width - 1) / width;

        return (BulkWriteMethod.Merged, new BulkWriteReport(requested, BulkWriteMethod.Merged, rows, trips, width, false,
            $"{rows} 行 × 每行 {_settable.Length + _keys.Length} 颗参数（SET {_settable.Length} 列 + WHERE {_keys.Length} 列），" +
            $"参数预算 {budget} → 每条命令 {width} 行（{trips} 条命令）。一条命令 = 一次往返；{BulkAtomicity.Sentence(_atomic, rows)}"));
    }

    (BulkWriteMethod Method, BulkWriteReport Report) Textless(BulkWriteMethod method, BulkWriteMethod requested, bool degraded, string reason)
        => (method, new BulkWriteReport(requested, method, _rows.Length, _rows.Length, 1, degraded, reason));

    /// <summary>取值段：把每行的可写列读成格子，逐格问一次 <see cref="FastSqlAop.AuditValue"/>
    /// （种类是 <c>"update"</c>），只跑一次——两份文本与一次执行共用同一份格子。与
    /// <see cref="InsertEngine{T}"/> 的两段之分同一条理由，也共用同一条纪律：
    /// <list type="bullet">
    /// <item><b>跑一次</b>（§10.1 硬规则 1 的那一半）：这个钩子会写实体，同一只实体问两遍就盖两遍，
    /// 而 <c>Explain()</c> 与 <c>ExecuteBulkAsync()</c> 是两条各自建文本的路——所以格子缓存在场就不重算，
    /// <c>WithBulk</c> 换档也不重算。</item>
    /// <item><b>主键列不进这一格</b>：那一列在这条链上是 <c>WHERE</c> 的判据，不是写出去的列。让它过钩子
    /// 等于允许订阅方把定位条件改掉——"补一个修改时间"的钩子顺手把一批 UPDATE 挪到别的行上，
    /// 那是这一格必须结构上写不出来的东西。自增/库里给值与并发列本来就不在 <c>_settable</c> 里（构造函数那三道筛），
    /// 与 <see cref="InsertEngine{T}"/> 跳过它们是同一条判据。</item>
    /// <item><b>未订阅即零成本</b>：不构造事件对象，格子就是读出来的原值。仍然建格子这件事与插入链同价——
    /// 那条链在没有钩子时也建，为的是"同一批实体的两种文本读的必须是同一份数"（调用方在 <c>Explain()</c>
    /// 之后改了实体，两份文本就不该不一样）。</item>
    /// </list>
    /// 交回的是行 × 可写列那一份格子；改写痕迹记在 <see cref="_valueTraces"/>，步骤号从 0 起，
    /// 发射段的 <c>CudBuilding</c>/<c>ParameterBinding</c> 接着往下编。</summary>
    object?[][] Prepare()
    {
        if (_cells is { } done)
            return done;

        var audit = _orm.Aop.AuditValue;
        var cells = new object?[_rows.Length][];
        var traces = new List<HookTrace>();

        for (var row = 0; row < _rows.Length; row++)
        {
            var entity = _rows[row] ?? throw new FastSqlException($"第 {row} 行是 null：批量更新的集合里不能有 null 实体。");
            var writable = new object?[_settable.Length];

            for (var column = 0; column < _settable.Length; column++)
            {
                var ordinal = _settableOrdinals[column];
                var value = _schema.GetValue(entity, ordinal);

                if (audit is null)
                {
                    writable[column] = value;

                    continue;
                }

                var @event = new AuditValueEvent(entity, _settable[column], value, "update", written => _schema.SetValue(entity, ordinal, written));

                // 改写类钩子的异常直接向上失败（硬规则 3）：吞掉等于交出一份"填充没发生"的 SET 清单。
                audit(@event);

                if (@event.WasSet)
                {
                    traces.Add(new HookTrace(traces.Count, "AuditValue", HookCategory.Rewrite, $"{_settable[column].MemberName} = {StatementEmitter.Text(value)}", $"{_settable[column].MemberName} = {StatementEmitter.Text(@event.CurrentValue)}"));
                    value = @event.CurrentValue;
                }

                writable[column] = value;
            }

            cells[row] = writable;
        }

        _cells = cells;
        _valueTraces = traces;

        return cells;
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Build(BulkWriteMethod method)
    {
        if (method == BulkWriteMethod.Upsert)
            return BuildUpsert();

        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var width = method == BulkWriteMethod.SingleRow ? 1 : StatementEmitter.RowsPerStatement(budget, _settable.Length + _keys.Length);

        var values = Prepare();
        var traces = new List<HookTrace>(_valueTraces.Count);
        traces.AddRange(_valueTraces);

        var step = traces.Count;
        var commands = new List<DialectSql>((_rows.Length + width - 1) / width);

        for (var first = 0; first < _rows.Length; first += width)
        {
            var count = Math.Min(width, _rows.Length - first);

            // 一条命令一份共享参数表：SqlWriter 的编号长在参数表上（Fork 那句注释），
            // 所以并进来的每条语句拿到的名字天然不重号，执行层按名落地时不撞车。
            var parameters = new List<AdoParameter>(count * (_settable.Length + _keys.Length));
            var text = new StringBuilder(StatementEmitter.EstimateLength(count * (_settable.Length + _keys.Length), IdentifierChars()));

            for (var row = first; row < first + count; row++)
            {
                // 写入位取取值段那份（AuditValue 之后的格子），键位仍然直接读实体：那几列是定位条件，不过钩子。
                var entity = _rows[row];
                var writable = values[row];

                var sets = new SetClause[_settable.Length];

                for (var column = 0; column < _settable.Length; column++)
                    sets[column] = new SetClause(_settable[column], new ParameterValue(writable[column], _settable[column]));

                SqlExpression? where = null;

                for (var key = 0; key < _keys.Length; key++)
                {
                    var value = _schema.GetValue(entity, _keyOrdinals[key]);

                    if (value is null)
                        throw new FastSqlException($"第 {row} 行的主键列 {_keys[key].ColumnName} 是 null：UpdateRange 按主键定位，null 键定位不到任何行。新行请用 InsertRange。");

                    var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, _keys[key]), new ParameterValue(value, _keys[key]));
                    where = where is null ? equal : new SqlBinaryExpression(BinaryOperator.And, where, equal);
                }

                var ast = new UpdateStatement { Table = _table, Sets = sets, Where = _orm.Filters.ApplyTo(_table, where!) };

                var writer = new SqlWriter(new StringBuilder(256), parameters, dialect.ParameterPrefix);
                dialect.Emit(in ast, writer);

                if (text.Length > 0)
                    text.Append(" ; ");

                text.Append(writer.ToSqlString());
            }

            var emitted = new DialectSql(text.ToString(), parameters);
            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "update", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var reason = method == BulkWriteMethod.SingleRow
            ? $"{_rows.Length} 行逐条：{_rows.Length} 条命令、每条一行。"
            : $"{_rows.Length} 行 × 每行 {_settable.Length + _keys.Length} 颗参数（SET {_settable.Length} 列 + WHERE {_keys.Length} 列），参数预算 {budget} → 每条命令 {width} 行（{commands.Count} 条命令）。";

        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            reason += $" 每条语句的 WHERE 里除了主键还叠着 {injected} 条全局过滤器（§10.7 的 orm.Filters）。";

        reason += " " + BulkAtomicity.Sentence(_atomic, _rows.Length);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>StringBuilder 的预容量要用到的标识符字符数（§12.7 E 那一格）。两种形状共用一把尺：
    /// 并档每行一句 UPDATE，upsert 档一条命令一句多值 INSERT 加一段句尾——句尾的长度由 <see cref="BuildUpsert"/>
    /// 自己补，因为它才是那一段的产出方。</summary>
    int IdentifierChars()
    {
        var chars = _contract.Table.Name.Length * 2 + 32;

        foreach (var column in _settable)
            chars += column.ColumnName.Length + 3;

        foreach (var key in _keys)
            chars += key.ColumnName.Length + 5;

        return chars;
    }

    /// <summary>Upsert 档的文本：<b>一条命令一句 <c>INSERT … VALUES (…),(…) 冲突子句</c></b>，
    /// 块宽仍按 §12.3 那把尺（每行参数 = 可写列 + 主键列，与并档同数）。
    /// <para>发的是内核那一份 <see cref="InsertStatement"/> 走 <see cref="ISqlDialect.Emit(InsertStatement)"/>
    /// 的同一条出路，句尾由这一家的 <see cref="IBulkStrategy.UpsertClause"/> 补——列清单、参数表与取号因此
    /// 与 <c>InsertRange</c> 共用一处真相，本档不另拼一遍 VALUES。</para>
    /// <para><b>两道拒收在这一层，与并档那句"null 键定位不到"同一道门</b>：全局过滤器在场（那一句没有 WHERE
    /// 可以叠）；主键由库给值而这一行的键仍是 CLR 默认值（那种键的冲突判定出不来，写进去的是新行而不是改那一行）。
    /// 两道都在建文本时判，所以 <see cref="ExplainBulk"/> 那颗纯档位口不交这笔税，口径与过滤器那一句同族。</para>
    /// <para>钩子回放按 <c>"update"</c> 那一种：这一条链挂在 <c>UpdateRange</c> 上，订阅 <c>CudBuilding</c> 的
    /// 软删改写与影子表按操作种类分派（§10.1），档位不是种类。</para></summary>
    [RequiresUnreferencedCode("全局过滤器的在场判定要问 orm.Filters 挂着多少条（同 Explain 那句）。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan BuildUpsert()
    {
        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;

        var clause = UpsertClause(dialect)
                     ?? throw new FastSqlException($"{_orm.Provider.Kind} 没交 UpsertClause：一句 upsert 档在这一家不可达，而计划已经定在这一档。");

        RefuseUpsertFilters();

        var columns = new ColumnContract[_settable.Length + _keys.Length];
        var ordinals = new int[columns.Length];
        var quoted = new string[columns.Length];

        Array.Copy(_settable, columns, _settable.Length);
        Array.Copy(_keys, 0, columns, _settable.Length, _keys.Length);

        for (var column = 0; column < _settable.Length; column++)
        {
            ordinals[column] = _settableOrdinals[column];
            quoted[column] = Quoted(dialect, _settable[column].ColumnName);
        }

        for (var key = 0; key < _keys.Length; key++)
        {
            ordinals[_settable.Length + key] = _keyOrdinals[key];
            quoted[_settable.Length + key] = Quoted(dialect, _keys[key].ColumnName);
        }

        var storeGivenKey = Array.Exists(_keys, static key => key.ValueFromStore);
        var width = StatementEmitter.RowsPerStatement(budget, columns.Length);

        var values = Prepare();
        var traces = new List<HookTrace>(_valueTraces.Count);
        traces.AddRange(_valueTraces);

        var step = traces.Count;
        var commands = new List<DialectSql>((_rows.Length + width - 1) / width);

        for (var first = 0; first < _rows.Length; first += width)
        {
            var count = Math.Min(width, _rows.Length - first);
            var parameters = new List<AdoParameter>(count * columns.Length);
            var rows = new IReadOnlyList<SqlExpression?>[count];

            for (var row = 0; row < count; row++)
            {
                var entity = _rows[first + row];
                var writable = values[first + row];
                var cells = new SqlExpression?[columns.Length];

                for (var column = 0; column < columns.Length; column++)
                {
                    // 与并档同一分岔：写入位在取值段那份里（AuditValue 之后），键位是冲突判据，不过钩子。
                    var value = column < _settable.Length ? writable[column] : _schema.GetValue(entity, ordinals[column]);

                    if (column >= _settable.Length)
                    {
                        // 键位上的两种"这颗键定位不到"各一句：null 与并档逐字同判，而零是这一档独有的——
                        // 主键由库给值时那个默认形态代表"还没键"，放过去就是插一行没人要的新行。
                        if (value is null)
                            throw new FastSqlException($"第 {first + row} 行的主键列 {columns[column].ColumnName} 是 null：UpdateRange 按主键定位，null 键定位不到任何行。新行请用 InsertRange。");

                        if (storeGivenKey && IsUnassignedKey(value!))
                            throw new FastSqlException(
                                $"第 {first + row} 行的主键列 {columns[column].ColumnName} 由库给值（自增 ∶ 序列），而它交来的是没给值的那个默认形态：" +
                                $"一句 upsert 的冲突判定出不来，写进去的会是一个新行而不是改了那一行。要么把键给实，要么这种现场走 SaveChanges 或 orm.InsertRange。");
                    }

                    // 写入位上的 null 不转成库默认值：这一条链的契约与 Merged 档逐字相同——整行交出去，
                    // 那一格说的是 NULL 就是 NULL（这与 InsertRange 的 null→DEFAULT 正对面，不是同一件事）。
                    cells[column] = new ParameterValue(value, columns[column]);
                }

                rows[row] = cells;
            }

            var ast = new InsertStatement { Table = _table, Columns = columns, Rows = rows };

            var writer = new SqlWriter(
                new StringBuilder(StatementEmitter.EstimateLength(count * columns.Length, IdentifierChars()) + clause.Length),
                parameters, dialect.ParameterPrefix);

            dialect.Emit(in ast, writer);
            writer.Append(' ').Append(clause);

            var emitted = new DialectSql(writer.ToSqlString(), parameters);
            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "update", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var reason = $"一条命令 = 一句多值 upsert：{_rows.Length} 行 × 每行 {columns.Length} 颗参数（写入清单 = 可写 {_settable.Length} 列 + 主键 {_keys.Length} 列），" +
                     $"参数预算 {budget} → 每条命令 {width} 行（{commands.Count} 条命令）。句尾出自 {_orm.Provider.Kind}。";

        // 与并档正对面的一句话：这一档一句就是一句，所以语句数是命令数而不是行数（包事务的判据同源）。
        reason += " " + BulkAtomicity.Sentence(_atomic, commands.Count);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>向这一家要 upsert 的句尾（<see langword="null"/> = 不接）。三份列名清单都先过方言的取号口再交，
    /// 所以 provider 拿到的已经是这一家的引号与折叠形态，它自己不再猜。</summary>
    string? UpsertClause(ISqlDialect dialect)
    {
        var strategy = _orm.Provider.Bulk;

        if (strategy is null)
            return null;

        var settable = new string[_settable.Length];
        var keys = new string[_keys.Length];

        for (var column = 0; column < _settable.Length; column++)
            settable[column] = Quoted(dialect, _settable[column].ColumnName);

        for (var key = 0; key < _keys.Length; key++)
            keys[key] = Quoted(dialect, _keys[key].ColumnName);

        return strategy.UpsertClause(new UpsertColumns([.. settable, .. keys], settable, keys, name => Quoted(dialect, name)));
    }

    static string Quoted(ISqlDialect dialect, string identifier)
    {
        var writer = StatementEmitter.Writer(dialect, identifier.Length + 4);
        dialect.EmitIdentifier(writer, identifier);

        return writer.ToSqlString();
    }

    /// <summary>"这一颗键等于没给"的判据：数值零、<see cref="Guid.Empty"/> 与空串三种。只在主键由库给值时才问它
    /// （非自增主键的 0 是一颗合法键，那种现场不拒）。日期与枚举一类的键落不进这三种，一律放行。</summary>
    static bool IsUnassignedKey(object value) => value switch
    {
        Guid guid => guid == Guid.Empty,
        string text => text.Length == 0,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float =>
            Convert.ToDouble(value) == 0d,
        _ => false,
    };

    /// <summary>全局过滤器在场时一句 upsert 不可达：<c>INSERT … 冲突子句</c> 里没有 WHERE 可以叠过滤器，
    /// 而冲突那一支改的是"键相同的那一行"——租户、软删那一道闸门在这句文本上无处安放。
    /// 拒在这里而不是放过，是因为放过的后果是改宽（越过滤器的行被改），与 <c>SaveChanges</c> 同一批行的判定不一致。</summary>
    void RefuseUpsertFilters()
    {
        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            throw new FastSqlException(
                $"{_contract.Table.Name} 上挂着 {injected} 条全局过滤器（§10.7 的 orm.Filters），一句 upsert 档不接：那一句没有 WHERE 可以叠过滤器，" +
                "冲突那一支会改到过滤器之外的行。要带过滤器批量改就走 orm.UpdateRange 的 Merged 或 SingleRow 档，那里每条语句的 WHERE 都叠得出。");
    }


    ExecutionTag Tag() => new("update", [_contract.Table.Name], DataSourceRouting.ClusterOf(_contract.Table));
}

/// <summary><c>DeleteRange</c> 链的落点：按单列主键删整批。<b>Merged 档是一句 <c>WHERE 键 IN (…)</c> 的
/// 单条语句</b>，不是多语句批——所以这一条链的 Auto 在所有 provider 上都直接取 Merged，
/// 不读 <see cref="IBulkStrategy.SupportsMergedStatements"/>（那颗位只为 UpdateRange 的分号批存在）。
/// 复合主键当场拒并指路：IN 装不下元组，逐对 OR 展开把参数预算的账绕开了，两害都不如指路。</summary>
sealed class DeleteBulkEngine<T> : IDeleteBulk<T> where T : class
{
    readonly FastSqlOrm _orm;
    readonly T[] _rows;
    readonly EntityContract _contract;
    readonly IEntityTypeSchema<T> _schema;
    readonly TableRef _table;
    readonly ColumnContract _key;
    readonly int _keyOrdinal;

    BulkWriteMethod _requested = BulkWriteMethod.Auto;
    FallbackPolicy _fallback = FallbackPolicy.DegradeAndLog;
    bool _atomic = true;

    /// <summary>软删那一句写进「时间」列的值，<b>一颗属于这一批</b>：切成几句 IN 都是同一个删除时刻，
    /// 而 <c>Explain()</c> 与紧随的执行在这条链上是两次构建，所以它不能挂在 <c>Build</c> 的局部上。
    /// 没有注册软删时它永远是 null（不读时钟、不占一次时间调用）。<c>WithBulk</c> 清 <c>_plan</c> 而不清它。</summary>
    DateTime? _deletedAt;

    BulkWritePlan? _plan;

    public DeleteBulkEngine(FastSqlOrm orm, T[] rows)
    {
        _orm = orm;
        _rows = rows;
        _contract = orm.Schemas.Contract<T>();
        _schema = orm.Schemas.Schema<T>();

        if (_contract.Table.Split is not null)
            throw new FastSqlException(
                $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），DeleteRange 不接：一批行会散在几张片上。" +
                "要批量删一片就选片后走 orm.Delete<T>().SplitTable(那一片).Where(...)。");

        if (_contract.PrimaryKey.Length == 0)
            throw new FastSqlException(
                $"{_contract.Table.Name} 没有主键列：DeleteRange 按主键定位，WHERE 出不来。" +
                "按条件删走 orm.Delete<T>().Where(...)，或先给实体点主键。");

        if (_contract.PrimaryKey.Length > 1)
            throw new FastSqlException(
                $"{_contract.Table.Name} 的主键是 {_contract.PrimaryKey.Length} 列复合键：IN 清单装不下元组，逐对 OR 展开又绕开参数预算的账。" +
                "请逐行走 orm.Delete<T>().Where(复合键等式)，或经 SaveChanges 摘行。");

        _key = _contract.PrimaryKey[0];
        _keyOrdinal = Array.IndexOf(_contract.Columns, _key);
        _table = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
    }

    public IDeleteBulk<T> WithBulk(BulkWriteMethod method, FallbackPolicy fallback = FallbackPolicy.DegradeAndLog)
    {
        _requested = method;
        _fallback = fallback;
        _plan = null;

        return this;
    }

    public IDeleteBulk<T> WithBatchTransaction(bool atomic = true)
    {
        _atomic = atomic;

        return this;
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public BulkWritePlan Explain() => _plan ??= Build(Decide(_requested).Method);

    public BulkWriteReport ExplainBulk(BulkWriteMethod? requested = null) => Decide(requested ?? _requested).Report;

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    public async Task<BulkWriteReport> ExecuteBulkAsync(CancellationToken ct = default)
    {
        var (method, report) = Decide(_requested);

        if (_rows.Length == 0)
            return report;

        var plan = Build(method);

        report = report with { ChunkSize = plan.RowsPerCommand, RoundTrips = plan.Commands.Count };

        var tag = Tag();

        async Task SendAll()
        {
            foreach (var command in plan.Commands)
                await _orm.Ado.NonQueryAsync(command, tag, ct).ConfigureAwait(false);
        }

        // 这条链上的语句数就是命令数：Merged 档一条命令一句 IN 清单，SingleRow 档一条命令一句等值。
        // 所以"包不包"在这里几乎永远是假——一句 IN 删两万颗键本身就原子，包它只多付两次往返。
        // 真会包的只有键数超出一句的预算、这批被切成两句以上那一种（SQL Server 2100 颗那句的宽度最小）。
        if (BulkAtomicity.ShouldWrap(_atomic, plan.Commands.Count) && FastSqlTransaction.For(_orm.Ado) is null)
        {
            using var tran = _orm.BeginTransaction(DataSourceRouting.ClusterOf(_contract.Table));

            await SendAll().ConfigureAwait(false);
            tran.Commit();

            return report with { UsedTransaction = true };
        }

        await SendAll().ConfigureAwait(false);

        return report;
    }

    (BulkWriteMethod Method, BulkWriteReport Report) Decide(BulkWriteMethod requested)
    {
        // 一句 upsert 在删除链上没有意义：那一句的"缺键就插"那一支要写的正是被叫删的那一行。
        // 拒在选层之前，所以 ExplainBulk 与执行两条出口都撞得到这一句。
        if (requested == BulkWriteMethod.Upsert)
            throw new FastSqlException(
                $"{_contract.Table.Name} 的 DeleteRange 不接 Upsert 档：那一句是 INSERT … 冲突子句，缺键的那一支写进去的是一个新行，" +
                "而这一条链要的恰好是没有那一行。删整批键用 Merged 档（一句 IN 清单），逐条用 SingleRow 档。");

        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var rows = _rows.Length;
        var method = requested == BulkWriteMethod.SingleRow ? BulkWriteMethod.SingleRow : BulkWriteMethod.Merged;
        var width = method == BulkWriteMethod.SingleRow ? 1 : StatementEmitter.RowsPerStatement(budget, 1);
        var trips = (rows + width - 1) / width;

        if (rows == 0)
            return (method, new BulkWriteReport(requested, method, 0, 0, 0, false,
                "0 行：一档都不必选。不建文本、不取连接，执行直接返回 0。"));

        var reason = method == BulkWriteMethod.SingleRow
            ? $"{rows} 行按请求逐条删：{rows} 条语句、{rows} 次往返。语义与批量一致（§12.4 的等价性），代价是往返。"
            : $"{rows} 颗键、参数预算 {budget} → 每条 {width} 颗（{trips} 条命令）。一句 IN 清单是单条语句，" +
              "不欠多语句能力，所以这一档在每家都可达（§12.1 写侧扩展）。" + BulkAtomicity.Sentence(_atomic, trips);

        return (method, new BulkWriteReport(requested, method, rows, trips, width, false, reason));
    }

    [RequiresUnreferencedCode("全局过滤器的叠加（Filters.ApplyTo）走闭包常量求值，被裁剪的产物上谓词翻译会换一条退路。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：谓词翻译的退路是编译表达式树。")]
    BulkWritePlan Build(BulkWriteMethod method)
    {
        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;
        var width = method == BulkWriteMethod.SingleRow ? 1 : StatementEmitter.RowsPerStatement(budget, 1);

        var traces = new List<HookTrace>();
        var step = 0;
        var commands = new List<DialectSql>((_rows.Length + width - 1) / width);

        // 软删（§10.7 约束 3）在这一档同样生效，而<b>一颗时间要跨过两件事保持不变</b>：一次 DeleteRange 会被切成
        // 若干句 IN（各读一次时钟就把同一批行记成几个删除时间），而 <c>Explain()</c> 与紧随的执行在这条链上是
        // 两次构建（这条档上没有<c>UpdateRange</c> 那一份共享的取值网格）。所以那颗时钟挂在引擎上而不是
        // 挂在 <c>Build</c> 里：<c>WithBulk</c> 清 <c>_plan</c> 不清它，换档重build 也还是同一个删除时刻。
        var soft = _orm.Behaviors.HasAny ? _orm.Behaviors.RuleFor(typeof(T)) : null;
        DateTime? stamp = null;

        if (soft is not null)
            stamp = _deletedAt ??= soft.Stamp();

        for (var first = 0; first < _rows.Length; first += width)
        {
            var count = Math.Min(width, _rows.Length - first);

            SqlExpression keys;

            if (method == BulkWriteMethod.SingleRow)
            {
                keys = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(_table, _key),
                    new ParameterValue(ReadKey(first), _key));
            }
            else
            {
                // IN 清单：一颗键值一行，值在参数表里逐颗登记（发射段按 Parameters 顺序落地）。
                var values = new SqlExpression[count];

                for (var row = 0; row < count; row++)
                    values[row] = new ParameterValue(ReadKey(first + row), _key);

                keys = new InExpression(new ColumnExpression(_table, _key), values);
            }

            var where = _orm.Filters.ApplyTo(_table, keys);
            var ast = new DeleteStatement { Table = _table, Where = where };

            var emitted = StatementEmitter.EmitDelete(dialect, ast, soft?.Rewrite(_contract, _table, where, stamp),
                _contract.Table.Name.Length + _table.Alias.Length * 3 + 128);

            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "delete", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var reason = method == BulkWriteMethod.SingleRow
            ? $"{_rows.Length} 行逐条删：{_rows.Length} 条语句、每条一颗 {_key.ColumnName}。"
            : $"{_rows.Length} 颗 {_key.ColumnName}、参数预算 {budget} → 每条 {width} 颗（{commands.Count} 条 IN 语句）。";

        if (soft is not null)
            reason += $" 这一批已被 Behaviors.SoftDelete 改写成 UPDATE 置位（{soft.SetSummary(_contract)}，同一批共用一颗时间）：行没有不在，只是那一格写了 true。";

        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            reason += $" 每条语句的 WHERE 里叠着 {injected} 条全局过滤器（§10.7 的 orm.Filters）——命中范围比键清单窄。";

        // 这条链上的语句数就是命令数：一句 IN 清单是一条命令、SingleRow 档一句一命令，两档同形。
        reason += " " + BulkAtomicity.Sentence(_atomic, commands.Count);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>读一颗键值。<b>null 键当场抛而不是从清单里悄悄省掉</b>：省一颗等于那一行没删而调用方
    /// 以为删了，与 <c>IBulkCopyChannel.SupportsColumn</c> 那句"省一列等于丢写"同一个道理。</summary>
    object ReadKey(int row)
    {
        var entity = _rows[row] ?? throw new FastSqlException($"第 {row} 行是 null：批量删除的集合里不能有 null 实体。");
        var value = _schema.GetValue(entity, _keyOrdinal);

        if (value is null)
            throw new FastSqlException($"第 {row} 行的主键列 {_key.ColumnName} 是 null：按主键删定位不到任何行。");

        return value;
    }

    ExecutionTag Tag() => new("delete", [_contract.Table.Name], DataSourceRouting.ClusterOf(_contract.Table));
}

/// <summary>"这一批提交几次"那一格的话术，两条链共用一个形状（离线册按这句话断言，不许两处各写一遍）。
///
/// <para><b>这一格的默认值是 2026-10-07 改判的</b>，原稿写的是"跨命令不是原子的：要整批原子就包 §6 的事务"。
/// 改判的根据不是原子性而是速度：并档把往返并了而没把<em>提交</em>并了，InnoDB 每次提交 flush 一次 redo，
/// 而这条链只在数据多的时候被调用——同一台远端实例、同一批 2000 行整行更新，
/// <c>6386 ms ∶ 263 ms</c>，裸 ADO 同一形状的对照组是 <c>7419 ∶ 920</c>，
/// 差距全部来自提交次数【实测·<c>obj/logs/fscompare-C.txt</c> S3 那四支诊断档】。</para>
///
/// <para><b>判据是语句数而不是命令数</b>：一条语句本身就原子，包它只多付两次往返，所以那一格在这种时候闭嘴
/// （<c>DeleteRange</c> 的 Merged 档两万颗键也才一句 IN，正是这一格）。</para></summary>
static class BulkAtomicity
{
    /// <summary>要不要自己开一条事务。两格判据，缺一格就不包：<b>调用方没关那颗开关</b>
    /// （<c>WithBatchTransaction(false)</c> 那条退路是给"要用半批已落地做断点续传"的人留的，不是暗示的路）、
    /// 而<b>计划里有两句以上语句</b>（一句本身就原子，包它只多付两次往返）。第三格在执行段：
    /// 环境事务在场时提交归开它的那个人，这一批已经是别人那一批的一部分（§8.2 那一条同族）。</summary>
    public static bool ShouldWrap(bool atomic, int statements) => atomic && statements > 1;

    public static string Sentence(bool atomic, int statements)
        => atomic
            ? statements > 1
                ? $"整批 {statements} 句一次提交：执行段在没有环境事务在场时自开一条事务（§12.4 第 8 项今天的默认，WithBatchTransaction(false) 可关）。"
                : "一条语句本身就原子，不必再包事务。"
            : "每条语句自己提交，半批可以在库里落地（WithBatchTransaction(false)）：要整批原子就自己包 §6 的事务。";
}
