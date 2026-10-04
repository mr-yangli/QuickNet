using System.Globalization;
using System.Text;
using FastSql.Aop;
using FastSql.Execution;
using FastSql.Metadata;
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

    // ── 执行出口 ──────────────────────────────────────────────────────

    public async Task<int> ExecuteAffrowsAsync(CancellationToken ct = default)
    {
        var plan = Plan(Backfill.None);
        var tag = Tag();
        var affrows = 0;

        foreach (var chunk in plan.Chunks)
            affrows += await _orm.Ado.NonQueryAsync(chunk.Sql, tag, ct).ConfigureAwait(false);

        return affrows;
    }

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
            var first = chunk.FirstRow;
            var backfilled = 0;

            await _orm.Ado.WriteWithResultAsync(chunk.Sql, tag, (reader, index) =>
            {
                if (index == 0 && reader.FieldCount != expected)
                    throw new FastSqlException($"{_orm.Provider.Kind} 的回填结果集有 {reader.FieldCount} 列，期望 {expected} 列：发射层与键策略对不上，不能假设回填可用。");

                // 回包第 index 行 ↔ 入参第 first + index 行。这条映射就是 §12.4 第 1 项的全部内容，
                // 集成测试按 payload 逐行证明它，而不是只证明"拿到了某个键"。
                var entity = _rows[first + index];

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
            traces);
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

        if (columns.Length == 0)
            throw new FastSqlException($"{preparation.Table} 的每一列都不进列清单（自增列或并发列），插入只剩 DEFAULT VALUES 这一种写法，本版本未接入（§12.3）。");

        // 回填集为空就没有那一档：带 Returning 的文本会生成一条「看着像在回填」的普通 INSERT。
        // 执行口自己会拒（ReadBackAsync 之前先查），但离线口是给人审阅用的，读到一条假 OUTPUT 就已经误导了。
        ColumnContract[] returning = backfill switch
        {
            Backfill.None => [],
            Backfill.Keys => Require(preparation.Keys, "没有自增列：回填主键那一档根本没有东西可回。主键由调用方自己生成的话直接用 Explain()/ExecuteAffrowsAsync。"),
            _ => Require(preparation.Generated, "既没有自增列也没有并发列：没有任何一列的值归库管，回填那一档无事可做。"),
        };

        // §12.3：预算先留十分之一的余量（文本里还有表名/列名/OUTPUT），再除以每行参数个数，至少 1 行。
        var rowsPerStatement = StatementEmitter.RowsPerStatement(budget, columns.Length);
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

        for (var first = 0; first < _rows.Length; first += rowsPerStatement)
        {
            var count = Math.Min(rowsPerStatement, _rows.Length - first);
            var ast = new InsertStatement
            {
                Table = new TableRef(0, FastSqlOrm.AliasAt(0), preparation.Contract.Table, preparation.Contract),
                Columns = columns,
                Rows = RowsOf(preparation.Cells, first, count, columns),

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

            chunks.Add(new InsertChunk(first, count, ast, sql));
        }

        var reason = degraded
            ? $"{_rows.Length} 行 × {columns.Length} 列：{provider.Kind} 无批量 RETURNING 而这次要回填 → 退到 L5 逐条（{_rows.Length} 块）" +
              (postRead ? "，每块句尾接语句后回读（键在同一句命令里带回，§14 的那颗口子）" : "，而这一家连语句后回读也没有：构建到 EmitReturning 即拒") +
              $"。语义与批量一致（§12.4 的等价性），代价是 {_rows.Length} 次往返。"
            : $"{_rows.Length} 行 × {columns.Length} 列，参数预算 {budget} → 每块 {rowsPerStatement} 行（{chunks.Count} 块），L3 多值语句。";

        if (chunks.Count > 1)
            reason += " 跨块不是原子的（§12.4 第 8 项）：要整批原子就包 §6 的事务。";

        return new InsertPlan(chunks, _rows.Length, columns.Length, rowsPerStatement, budget, reason, traces) { Provider = provider };
    }

    ColumnContract[] Require(ColumnContract[] columns, string whyEmpty)
        => columns.Length > 0 ? columns : throw new FastSqlException($"{Prepare().Table} {whyEmpty}");

    /// <summary>每格都参数化；<c>ParameterBinding</c> 的回放在 <c>StatementEmitter.ApplyBinding</c>，三条写链共用一份。</summary>
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

    /// <summary>只用于 <see cref="StringBuilder"/> 的预容量（§12.7 E）：每颗参数按 <c>@p123, </c> 估 7 个字符。</summary>
    static int EstimateLength(ColumnContract[] columns, int rows)
    {
        var length = 96 + rows * (columns.Length * 7 + 4);

        foreach (var column in columns)
            length += column.ColumnName.Length + 3;

        return length;
    }

    ExecutionTag Tag() => new("insert", [Prepare().Contract.Table.Name]);

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
