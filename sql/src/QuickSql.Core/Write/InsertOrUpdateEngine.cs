using System.Text;
using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Write;

/// <summary><see cref="IInsertOrUpdate{T}"/> 链的落点（§5.4 写侧、§12.1 写侧扩展那句"含 MERGE"那一档的入口）。
///
/// <para><b>它是 <see cref="UpdateBulkEngine{T}"/> 的 <c>BuildUpsert</c> 那一档换了个入口</b>：同一份列尺子
/// （写入清单 = 可写列 ++ 主键列）、同两道当场拒（全局过滤器在场、主键由库给值而调用方没给键）、同一段取值段
/// 纪律（<c>AuditValue</c> 只跑一次），连 <c>CudBuilding</c>/<c>ParameterBinding</c> 回放时那一句操作种类
/// 都照旧报 <c>"update"</c>——因为订阅同一句文本的钩子不该因为入口叫 <c>InsertOrUpdate</c> 就看到两种东西。
/// 取值段那个审计种类因此也报 <c>"update"</c>：一条 upsert 的行可能插可能改，这一版把"只在插入时补 CreatedAt"
/// 那一类语义留给能判命中与否的那一天，今天不假装它已经分得开（缺口记 §14）。</para>
///
/// <para><b>发射形态在这一颗里分岔，调用方不选</b>：先问 provider 的 <see cref="IBulkStrategy.UpsertClause"/>
/// 交不交得出句尾——交得出就发 <see cref="InsertStatement"/> 再接那一段（MySQL ∶ PG 血统，那条路
/// <c>UpdateRange(...).WithBulk(Upsert)</c> 已经跑绿，原样复用）；交不出就交给方言的
/// <see cref="ISqlDialect.Emit(UpsertStatement)"/>（SQLite 的 <c>ON CONFLICT</c> 与 <c>MERGE INTO</c> 那一族）。
/// 两处都拿不到时由 <see cref="UpsertStatement"/> 那颗默认抛说"这一家不可达"，各形态在半路接住自己那一份。</para></summary>
sealed class InsertOrUpdateEngine<T> : IInsertOrUpdate<T> where T : class
{
    readonly QuickSqlOrm _orm;

    T[] _rows = [];

    // 契约派生的那几格只依赖 T（不依赖行），构造时就算，与 UpdateBulkEngine 同一条：
    // 没有主键、除了键与库里给值的列以外没有可写列、分表实体——三种现场都在这条链建起来之前红。
    readonly EntityContract _contract;
    readonly IEntityTypeSchema<T> _schema;
    readonly TableRef _table;
    readonly ColumnContract[] _settable;
    readonly int[] _settableOrdinals;
    readonly ColumnContract[] _keys;
    readonly int[] _keyOrdinals;

    bool _atomic = true;

    object?[][]? _cells;
    IReadOnlyList<HookTrace> _valueTraces = [];
    BulkWritePlan? _plan;

    public InsertOrUpdateEngine(QuickSqlOrm orm)
    {
        _orm = orm;
        _contract = orm.Schemas.Contract<T>();
        _schema = orm.Schemas.Schema<T>();

        if (_contract.Table.Split is not null)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），InsertOrUpdate 不接：一批行会散在几张片上，" +
                "而这一档今天没有按片分组的账。要一片一片地 upsert 就选片后逐条走。");

        if (_contract.PrimaryKey.Length == 0)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 没有主键列：InsertOrUpdate 按主键判冲突，ON ∶ 冲突键出不来。" +
                "要么给实体点主键（[PrimaryKey] 或约定名 Id），要么纯插入走 orm.InsertRange。");

        _keys = _contract.PrimaryKey;

        var settable = new List<ColumnContract>(_contract.Columns.Length);
        var setOrdinals = new List<int>(_contract.Columns.Length);
        var keyOrdinals = new List<int>(_keys.Length);

        for (var ordinal = 0; ordinal < _contract.Columns.Length; ordinal++)
        {
            var column = _contract.Columns[ordinal];

            // 主键进冲突判定不进 SET；库里给值的列（自增/计算列）与并发列不接受书写——三条判据与
            // UpdateBulkEngine 构造函数那三道筛逐字同源。
            if (column.IsPrimary || column.ValueFromStore || column.IsConcurrencyToken)
                continue;

            settable.Add(column);
            setOrdinals.Add(ordinal);
        }

        if (settable.Count == 0)
            throw new QuickSqlException($"{_contract.Table.Name} 除了主键与库里给值的那几列没有可写的列：一句 upsert 的 UPDATE 那一支只剩空 SET，那样的语句没有意义。");

        _settable = [.. settable];
        _settableOrdinals = [.. setOrdinals];

        foreach (var key in _keys)
            keyOrdinals.Add(Array.IndexOf(_contract.Columns, key));

        _keyOrdinals = [.. keyOrdinals];

        _table = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(_contract.Table), _contract.Table, _contract);
    }

    public IInsertOrUpdate<T> SetSource(T entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return SetRows([entity]);
    }

    public IInsertOrUpdate<T> SetSource(IEnumerable<T> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        return SetRows(entities as T[] ?? [.. entities]);
    }

    IInsertOrUpdate<T> SetRows(T[] rows)
    {
        _rows = rows;

        // 换一批 = 作废取值段与发射段两份（与 UpdateBulkEngine 只作废发射段那份不同：这里连实体集合都换了，
        // AuditValue 是为这一批新的实体跑的，旧格子没有可比的对象）。
        _cells = null;
        _valueTraces = [];
        _plan = null;

        return this;
    }

    public IInsertOrUpdate<T> WithBatchTransaction(bool atomic = true)
    {
        _atomic = atomic;

        return this;
    }

    public BulkWritePlan Explain() => _plan ??= Build();

    public string ToSql() => Explain().ToSql();

    public string ToLogSql() => Explain().ToLogSql();

    public async Task<int> ExecuteAffrowsAsync(CancellationToken ct = default)
    {
        var plan = _plan ??= Build();
        var tag = Tag();
        var affrows = 0;

        async Task SendAll()
        {
            foreach (var command in plan.Commands)
                affrows += await _orm.Ado.NonQueryAsync(command, tag, ct).ConfigureAwait(false);
        }

        // 判据是语句数而不是命令数（与 UpdateBulkEngine 那一句同源）：一句 upsert 本身就原子，包它只多付两次往返。
        // 第三格（【本笔（ClickHouse 重开·笔一）新增】）：provider 得声明了 DbCapabilities.Transactions 才包——
        // 没声明的那一家（无 DbTransaction 可开，或者驱动根子上不接）走逐句 autocommit 直发，
        // 这一批因此在库里不原子（中途失败已发的语句不回滚；行为诚实条款全文在位注释与用法册）。
        if (BulkAtomicity.ShouldWrap(_atomic, plan.Commands.Count)
            && TransactionCapability.Declared(_orm.Provider)
            && QuickSqlTransaction.For(_orm.Ado) is null)
        {
            using var tran = _orm.BeginTransaction(DataSourceRouting.ClusterOf(_contract.Table));

            await SendAll().ConfigureAwait(false);
            tran.Commit();

            return affrows;
        }

        await SendAll().ConfigureAwait(false);

        return affrows;
    }

    /// <summary>取值段：与 <see cref="UpdateBulkEngine{T}"/> 那份逐字同形（种类报 <c>"update"</c>，主键列不进格子、
    /// 不过钩子——它是冲突判据不是写出去的列，理由见那颗的抬头）。交回行 × 可写列那份格子，改写痕迹记在
    /// <see cref="_valueTraces"/>，发射段的 <c>CudBuilding</c>/<c>ParameterBinding</c> 接着往下编步号。</summary>
    object?[][] Prepare()
    {
        if (_cells is { } done)
            return done;

        var audit = _orm.Aop.AuditValue;
        var cells = new object?[_rows.Length][];
        var traces = new List<HookTrace>();

        for (var row = 0; row < _rows.Length; row++)
        {
            var entity = _rows[row] ?? throw new QuickSqlException($"第 {row} 行是 null：upsert 的集合里不能有 null 实体。");
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

                // 改写类钩子的异常直接向上失败（硬规则 3）：吞掉等于交出一份"填充没发生"的行。
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

    /// <summary>发射段：一条命令一句多值 upsert，块宽按 §12.3 那把尺（每行参数 = 可写列 ++ 主键列）。
    /// 每块先问 <see cref="UpsertClause"/>——交得出句尾就发 <see cref="InsertStatement"/> 再接尾巴（列清单、
    /// 参数表与取号因此与 <c>InsertRange</c> 共用一处真相，本档不另拼一遍 VALUES）；交不出就发
    /// <see cref="UpsertStatement"/> 走方言那颗整语句 emit。两道当场拒（过滤器 ∶ 没给的库给值键）与
    /// <see cref="UpdateBulkEngine{T}"/> 的 <c>BuildUpsert</c> 同一道门。</summary>
    BulkWritePlan Build()
    {
        var dialect = _orm.Provider.Dialect;
        var budget = _orm.Provider.Bulk?.ParameterBudget ?? StatementEmitter.ConservativeParameterBudget;

        if (_rows.Length == 0)
            return new BulkWritePlan([], 0, 0, budget, "0 行：不建文本、不取连接，执行直接返回 0。", []) { Provider = _orm.Provider };

        // 句尾还是 MERGE，由这一家自己表决定：先要句尾（MySQL ∶ PG 血统交得出），拿不到交给方言的整语句 emit。
        var clause = UpsertClause(dialect);

        RefuseUpsertFilters();

        var columns = new ColumnContract[_settable.Length + _keys.Length];
        var ordinals = new int[columns.Length];

        Array.Copy(_settable, columns, _settable.Length);
        Array.Copy(_keys, 0, columns, _settable.Length, _keys.Length);

        for (var column = 0; column < _settable.Length; column++)
            ordinals[column] = _settableOrdinals[column];

        for (var key = 0; key < _keys.Length; key++)
            ordinals[_settable.Length + key] = _keyOrdinals[key];

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
                        // 键位上两种"这颗键定位不到"各一句：null 与并档逐字同判，零是这一档独有的——
                        // 主键由库给值时那个默认形态代表"还没键"，放过去就是插一行没人要的新行。
                        if (value is null)
                            throw new QuickSqlException($"第 {first + row} 行的主键列 {columns[column].ColumnName} 是 null：InsertOrUpdate 按主键判冲突，null 键定位不到任何行。新行请给一颗客户端键，或走 orm.InsertRange。");

                        if (storeGivenKey && StatementEmitter.IsUnassignedKey(value!))
                            throw new QuickSqlException(
                                $"第 {first + row} 行的主键列 {columns[column].ColumnName} 由库给值（自增 ∶ 序列），而它交来的是没给值的那个默认形态：" +
                                $"一句 upsert 的冲突判定出不来，写进去的会是一个新行而不是改了那一行。要么把键给实，要么这种现场走 SaveChanges 或 orm.InsertRange。");
                    }

                    cells[column] = new ParameterValue(value, columns[column]);
                }

                rows[row] = cells;
            }

            var emitted = clause is not null
                ? EmitTailForm(dialect, columns, rows, parameters, clause)
                : EmitUpsertForm(dialect, columns, rows, parameters);

            var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "update", traces, ref step), traces, ref step);

            commands.Add(sql);
        }

        var form = clause is not null ? "句尾（" + _orm.Provider.Kind + " 交的 UpsertClause）" : "整语句（本家方言发的 ON CONFLICT ∶ MERGE）";

        var reason = $"一条命令 = 一句多值 upsert（{form}）：{_rows.Length} 行 × 每行 {columns.Length} 颗参数（写入清单 = 可写 {_settable.Length} 列 + 主键 {_keys.Length} 列），" +
                     $"参数预算 {budget} → 每条命令 {width} 行（{commands.Count} 条命令）。";

        reason += " " + BulkAtomicity.Sentence(_atomic, commands.Count);

        return new BulkWritePlan(commands, _rows.Length, width, budget, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>句尾族：内核发 <see cref="InsertStatement"/>（列清单、VALUES 形状与参数表与 <c>InsertRange</c> 共用一处真相），
    /// provider 只补它后面那一段。与 <see cref="UpdateBulkEngine{T}"/> 的 <c>BuildUpsert</c> 那两句逐字同形。</summary>
    DialectSql EmitTailForm(ISqlDialect dialect, ColumnContract[] columns, IReadOnlyList<IReadOnlyList<SqlExpression?>> rows, List<AdoParameter> parameters, string clause)
    {
        var ast = new InsertStatement { Table = _table, Columns = columns, Rows = rows };

        var writer = new SqlWriter(new StringBuilder(StatementEmitter.EstimateLength(rows.Count * columns.Length, clause.Length)), parameters, dialect.ParameterPrefix);

        dialect.Emit(in ast, writer);
        writer.Append(' ').Append(clause);

        return new DialectSql(writer.ToSqlString(), parameters);
    }

    /// <summary>整语句族（没有句尾槽的一家：SQLite 的 ON CONFLICT、<c>MERGE INTO</c> 那一族）：把键 ∶ 可写的分工
    /// 带进 <see cref="UpsertStatement"/>，方言那颗整语句 emit 发一整句（形态由这一家自己定）。</summary>
    DialectSql EmitUpsertForm(ISqlDialect dialect, ColumnContract[] columns, IReadOnlyList<IReadOnlyList<SqlExpression?>> rows, List<AdoParameter> parameters)
    {
        var ast = new UpsertStatement { Table = _table, Columns = columns, Rows = rows, Keys = _keys, Settable = _settable };

        var writer = new SqlWriter(new StringBuilder(StatementEmitter.EstimateLength(rows.Count * columns.Length, IdentifierChars())), parameters, dialect.ParameterPrefix);

        dialect.Emit(in ast, writer);

        return new DialectSql(writer.ToSqlString(), parameters);
    }

    /// <summary>向这一家要 upsert 的句尾（<see langword="null"/> = 不接，走 MERGE 那条）。三份列名清单都先过方言的
    /// 取号口再交，所以 provider 拿到的已经是这一家的引号与折叠形态——与 <see cref="UpdateBulkEngine{T}"/> 那份同源。</summary>
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

    /// <summary>StringBuilder 的预容量要用到的标识符字符数（§12.7 E 那一格），与 <see cref="UpdateBulkEngine{T}"/> 那份同尺。</summary>
    int IdentifierChars()
    {
        var chars = _contract.Table.Name.Length * 2 + 32;

        foreach (var column in _settable)
            chars += column.ColumnName.Length + 3;

        foreach (var key in _keys)
            chars += key.ColumnName.Length + 5;

        return chars;
    }

    /// <summary>全局过滤器在场时一句 upsert 不可达：<c>INSERT … 冲突子句</c> 与 <c>MERGE … ON</c> 都没有 WHERE
    /// 可以叠过滤器，而冲突那一支改的是"键相同的那一行"——租户、软删那一道闸门在这句文本上无处安放。
    /// 拒在这里而不是放过，后果与 <see cref="UpdateBulkEngine{T}"/> 那份同一条（放过的后果是改宽）。</summary>
    void RefuseUpsertFilters()
    {
        var injected = _orm.Filters.CountFor(typeof(T));

        if (injected > 0)
            throw new QuickSqlException(
                $"{_contract.Table.Name} 上挂着 {injected} 条全局过滤器（§10.7 的 orm.Filters），InsertOrUpdate 不接：那一句没有 WHERE 可以叠过滤器，" +
                "冲突那一支会改到过滤器之外的行。要带过滤器改就走 orm.Update<T>().Set(...).Where(...)，那里叠得出。");
    }

    ExecutionTag Tag() => new("insertOrUpdate", [_contract.Table.Name], DataSourceRouting.ClusterOf(_contract.Table));
}
