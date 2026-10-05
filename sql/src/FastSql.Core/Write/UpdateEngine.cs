using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using FastSql.Aop;
using FastSql.Execution;
using FastSql.Metadata;
using FastSql.Query.Model;
using FastSql.Schema;
using FastSql.Sql;
using FastSql.Sql.Ast;
using FastSql.Translation;

namespace FastSql.Write;

/// <summary>UPDATE 链的落点。<b>与 <see cref="InsertEngine{T}"/> 的关键差别是它没有「取值段」</b>：
/// 插入要往库里放一整行，所以必须先把实体读成行 × 列的实参值（<c>AuditValue</c> 与契约校验都挂在那一段）；
/// 这条链上落到库里的只有 <c>SET</c> 那几列，其余列根本不进语句，
/// 于是「按实体形状校验一遍」在这里是错的（见 <see cref="Build"/> 里那段注释）。
/// 表达式树在这里归约，所以标注与读侧 <see cref="FastSql.Query.QueryEngine"/> 同一套（§5.10）。</summary>
[RequiresUnreferencedCode("lambda 归约与闭包常量求值要反射读成员。")]
[RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
sealed class UpdateEngine<T> : IUpdate<T> where T : class
{
    readonly FastSqlOrm _orm;
    readonly EntityContract _contract;
    readonly TableInfo? _target;
    readonly TableRef _table;
    readonly IReadOnlyList<SetClause> _sets;
    readonly SqlExpression? _where;

    UpdatePlan? _plan;

    public UpdateEngine(FastSqlOrm orm)
        : this(orm, [], null, null)
    {
    }

    UpdateEngine(FastSqlOrm orm, IReadOnlyList<SetClause> sets, SqlExpression? where, TableInfo? target)
    {
        _orm = orm;
        _contract = orm.Schemas.Contract<T>();
        _target = target;

        // 目标的自引用名由方言定（§4 的 WriteTargetAlias）：Oracle 血统不能给写目标起别名，
        // 那一家只能用表名，而 WHERE 里的列前缀必须与它一致 —— 见 ISqlDialect.WriteTargetAlias 的注释。
        // 分表时这里是<em>那一张片</em>（SplitTable 已经把它钉成一片），所以别名与列前缀都跟着片名走 ——
        // 这也是 <see cref="SplitTable(DateTime)"/> 必须放在链首的原因（下面那颗守卫）。
        var table = target ?? _contract.Table;

        _table = new TableRef(0, _orm.Provider.Dialect.WriteTargetAlias(table), table, _contract);
        _sets = sets;
        _where = where;
    }

    // ── 算子 ──────────────────────────────────────────────────────────

    /// <summary>分表实体的 UPDATE 要打的那一片。<b>一次一片，不是范围</b>：这条链上的语句是一条
    /// <c>UPDATE</c>，而一条 UPDATE 只有一个目标表；给范围就要产 N 条并<em>悄悄</em>执行 N 条，
    /// 而这条链的契约写的是「<c>ToSql()</c> 与紧随其后的 <c>ExecuteAffrowsAsync()</c> 是同一条语句」。
    /// 要多片各改一遍就自己按片调 N 次（或者要跨片的整体改写时走 <c>orm.Ado</c> 的原生 SQL 口）。
    /// <b>必须放在链首</b>：SET 与 WHERE 的列引用是翻译时绑到当时的目标上的，而 Oracle 血统那一家
    /// 的自引用名就是表名本身（<c>WriteTargetAlias</c>），先写了条件再换片会让前缀停在模板名上。</summary>
    public IUpdate<T> SplitTable(DateTime at)
        => Single(SplitAt(at));

    /// <summary>分表实体的 UPDATE 要打的那一片（哈希档）。</summary>
    public IUpdate<T> SplitTable(int bucket)
        => Single(SplitRouting.EnumerateBuckets(_contract.Table, Split(), [bucket])[0]);

    UpdateEngine<T> Single(TableInfo target)
    {
        if (_sets.Count > 0 || _where is not null)
            throw new FastSqlException(
                $"{_contract.Table.Name} 的 SplitTable 要放在链式最前面：这条链上已叠加 {_sets.Count} 个 SET/{(_where is null ? 0 : 1)} 个条件，" +
                "它们的列引用仍指向没选片时的那个目标。");

        return new UpdateEngine<T>(_orm, _sets, _where, target);
    }

    /// <summary>这一条链的目标片：已选就用它，没选而实体分了片就在<em>构建之前</em>拒。</summary>
    TableInfo Target()
    {
        if (_target is { } chosen)
            return chosen;

        if (_contract.Table.Split is null)
            return _contract.Table;

        throw new FastSqlException(
            $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），UPDATE 要先说打哪一片：SplitTable(那一片里的一个日期) 或 SplitTable(桶号)。" +
            "一次只给一片，因为一条 UPDATE 只有一个目标表，而这条链的契约是「ToSql() 与紧随其后的执行是同一条语句」。");
    }

    /// <summary>分片事实，只在实体确实分了片时才交出去。<b>取模板而不是取 <see cref="_table"/></b>：
    /// 已经选过片时 <c>_table.Table.Split</c> 是 null（那是一张换算完的物理表），而范围换算要的仍然是那份模板。</summary>
    SplitFacts Split()
        => _contract.Table.Split
           ?? throw new FastSqlException($"{_contract.Table.Name} 没有声明分表（类上没有 [SplitTable]），SplitTable 那一步无事可做。");

    TableInfo SplitAt(DateTime at)
    {
        var split = Split();

        return SplitRouting.EnumerateTime(_contract.Table, split, at, at)[0];
    }

    public IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, TProp value)
    {
        var target = ColumnOf(column.Body);

        Check(target, value);

        return Append(target, new ParameterValue(value, target));
    }

    /// <summary>值那一侧走的是读侧同一个归约器，所以 <c>o =&gt; o.Amount + 1</c> 与
    /// <c>o =&gt; Math.Round(o.Amount, 2)</c> 在这里的翻译结果与在 <c>Where</c> 里完全一致（§5.5 单一真相）。</summary>
    public IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, Expression<Func<T, TProp>> value)
    {
        var target = ColumnOf(column.Body);

        return Append(target, _orm.Expressions.Visit(value.Body, ScopeOf(value)));
    }

    public IUpdate<T> Where(Expression<Func<T, bool>> predicate)
        => new UpdateEngine<T>(_orm, _sets, _where is null
            ? _orm.Expressions.Predicate(predicate, ScopeOf(predicate))
            : And(_where, _orm.Expressions.Predicate(predicate, ScopeOf(predicate))), _target);

    /// <summary>SET 左侧的列。四件事在这里判：<b>是不是列</b>（导航属性与计算成员不进 SET）、
    /// <b>是不是自增列</b>（库里那一列不接受写，除非开 IDENTITY_INSERT，P0 未接）、
    /// <b>是不是并发列</b>（§8.2 的"只比较不书写"在两条写链上同一条尺子：链式这一侧拦下来的理由是
    /// 那一句本来就在库里报错，而报错点离写它的人隔了一次往返）、
    /// <b>是不是已经写过一次</b>（同一列出现两次是纯 bug，而库给的错只说 "specified multiple times"）。</summary>
    UpdateEngine<T> Append(ColumnContract target, SqlExpression value)
    {
        for (var i = 0; i < _sets.Count; i++)
            if (ReferenceEquals(_sets[i].Column, target))
                throw new FastSqlException($"{_contract.Table.Name} 的 {target.ColumnName} 被设置了两次：后一次不会覆盖前一次，" +
                    "库里直接报错。要基于原值算就写成表达式那一档：Set(o => o.Amount, o => o.Amount + 1)。");

        return new UpdateEngine<T>(_orm, [.. _sets, new SetClause(target, value)], _where, _target);
    }

    ColumnContract ColumnOf(Expression body)
    {
        // 值类型列在 Expression<Func<T, TProp>> 里不产生转换节点，但 object 化的写法与枚举底层转换会留下
        // 一层 Convert —— 剥掉它，否则下面那句"必须是列的读法"会对一个合法写法报错。
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } widen)
            body = widen.Operand;

        if (body is not MemberExpression { Expression: ParameterExpression } member)
            throw new FastSqlException($"Set 的第一个参数必须是本实体单个列的读法（形如 o => o.列名），当前写的是 {body}。" +
                "算式放在第二个参数上，条件放在 Where 上。");

        var name = member.Member.Name;

        if (!_contract.ByMember.TryGetValue(name, out var column))
            throw new FastSqlException($"{_contract.Table.Name} 没有名为 {name} 的列：导航属性与未映射的计算成员不能进 SET（§10.2 的映射层不认它）。");

        if (column.IsIdentity)
            throw new FastSqlException($"{_contract.Table.Name} 的自增列 {column.MemberName} 不接受写：那是 IDENTITY_INSERT 那条路（IKeyStrategy.IdentityInsertToggle），本版本未接。");

        if (column.IsConcurrencyToken)
            throw new FastSqlException($"{_contract.Table.Name} 的并发列 {column.MemberName} 只比较、不书写（§8.2）：那一列的值归库给，" +
                "而这一句在库里也执行不过（SQL Server 的 rowversion 不可赋值）。要改版本号得直连库；" +
                "要应用自己维护版本列（<c>int Version</c> 那一派），那是另一格，本版本未接（§14 的 P1 余项）。");

        return column;
    }

    /// <summary>值级的那两把契约尺子（§10.6 的 <c>ContractOnly</c> 档 = 批量/无宿主实体路径唯一能跑的档）。
    /// <b>为什么只有这两把</b>：DataAnnotations 与 Custom 档的规则挂在<em>实体</em>上，而这条链上没有整实体，
    /// 只有一格要进 <c>SET</c> 的值 —— 拿 <c>IEntityValidator.TryValidate</c> 就得先造一只假实体，
    /// 那会把没填的列一起判成"缺"。所以这里直接调 <see cref="ContractRules"/> 那两条，
    /// 消息文本与插入链逐字同源（它也是从那里出来的）。差异记在 §14。</summary>
    void Check(ColumnContract column, object? value)
    {
        var validator = _orm.Schemas.Validator<T>();

        if (validator.Mode == ValidationMode.Off)
            return;

        var failures = new List<ValidationFailure>();

        if (value is null)
        {
            // 可空列上给 NULL 是一种写法（清空这一列），不可空列上它是库里的一个报错，所以在这里拦。
            if (!column.IsNullable)
                ContractRules.Required(column, failures);
        }
        else if (value is string text)
        {
            ContractRules.Length(column, text, failures);
        }

        if (failures.Count == 0)
            return;

        throw new ValidationFailedException(
            $"{_contract.Table.Name} 的 {column.ColumnName} 被拒绝：这一格的值违反库契约（ValidationMode.{validator.Mode}）。语句还没有生成，什么都没写进库。",
            failures);
    }

    Scope ScopeOf(LambdaExpression lambda)
        => Scope.Create(new QueryModel { Tables = [_table], Plan = new EntityProjection(0) }, lambda);

    static SqlExpression And(SqlExpression left, SqlExpression right)
        => new SqlBinaryExpression(BinaryOperator.And, left, right);

    // ── 出口 ──────────────────────────────────────────────────────────

    public UpdatePlan Explain() => _plan ??= Build();

    public string ToSql() => Explain().Sql.Text;

    public string ToSql(out IReadOnlyList<AdoParameter> parameters)
    {
        var sql = Explain().Sql;

        parameters = sql.Parameters;

        return sql.Text;
    }

    public string ToLogSql() => Explain().ToLogSql();

    public async Task<int> ExecuteAffrowsAsync(CancellationToken ct = default)
        => await _orm.Ado.NonQueryAsync(Explain().Sql, Tag(), ct).ConfigureAwait(false);

    /// <summary>构建那条语句。<b>不跑整实体校验</b>，不是漏了：一条 <c>SET [Note] = @p0 WHERE [Id] = @p1</c>
    /// 只碰两列，而实体上其余六列是调用方随手 new 出来的默认值，按整实体校验会把「改一列备注」判成
    /// 「金额越界 + 客户名为空 + 状态非法」。<em>该不该校验由这一列进不进语句决定</em>，所以常量那一档
    /// 的值在 <see cref="Check"/> 里过 §10.6 那两把尺子，而表达式那一档（值在库里算）与 <c>Where</c> 里的
    /// 条件都不过 —— 后者读侧从来不过，同一件事不必在两条链上各判一遍。</summary>
    UpdatePlan Build()
    {
        if (_sets.Count == 0)
            throw new FastSqlException($"{_contract.Table.Name} 的 UPDATE 一个 SET 列都没有：这样的语句没有任何效果。用 .Set(o => o.列, 值) 说要改什么。");

        // 没有 WHERE 的 UPDATE 会把整表改成同一个值，而这条链上没有任何东西能把它拦在库外。
        // 构建期由 Guard.Whereless 硬拦（早 catch 比晚 catch 便宜），运行期由 §10.1 的 CommandTerminating 钩子做第二道闸。
        // 要清全表请显式写 .Where(o => true) —— 它产出同一条语句。
        if (_where is null)
            throw new FastSqlException(Guard.Whereless("UPDATE", _contract.Table.Name));

        // 先问一次目标：分表而没选片时要的是 <see cref="Target"/> 那一句可行动的拒绝，而不是撞上
        // EmitTableName 那道结构性兜底 —— 那一兜说的是"模板没换算成具体分片"，是给内核看的不变量，
        // 不是给调用方看的用法说明。（值不用在这里取：<c>_table</c> 在建引擎时就按同一份目标建好了，
        // 谓词的列引用绑的是那一个实例，换一份反而会让两边指不同的 TableRef。）
        var table = Target();

        var traces = new List<HookTrace>();
        var step = 0;

        // §10.7 约束 1 的写侧那一半：过滤器叠在调用方写的条件<em>之后</em>（与读侧多次 Where 的折叠同一个方向，
        // 于是同一条链在读写两侧看到的括号形状一致）。位置在 Guard.Whereless 之后 —— 全局过滤器不是
        // "你说的条件"，一条一个 Where 都没写的 UPDATE 不能因为册子上有过滤器就获得那道闸门的免死。
        var where = _orm.Filters.ApplyTo(_table, _where);
        var injected = _orm.Filters.CountFor(typeof(T));

        var ast = new UpdateStatement
        {
            Table = _table,
            Sets = _sets,
            Where = where,
        };

        var writer = StatementEmitter.Writer(_orm.Provider.Dialect, StatementEmitter.EstimateLength(_sets.Count, IdentifierChars(ast)));
        _orm.Provider.Dialect.Emit(in ast, writer);

        var emitted = new DialectSql(writer.ToSqlString(), writer.Parameters);
        var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "update", traces, ref step), traces, ref step);

        var reason = $"UPDATE {table.Name}：SET {_sets.Count} 列（{string.Join("、", _sets.Select(s => s.Column.ColumnName))}），" +
            $"参数 {sql.Parameters.Count} 颗。条件按主键以外的列写时注意命中行数 —— 受影响行数等于谓词命中数，不等于改动了值的那几行。" +
            (injected > 0
                ? $" WHERE 里除了 .Where(...) 那几句，还叠着 {injected} 条全局过滤器（§10.7 的 orm.Filters，它由内核加、不出现在这条链的调用里）。"
                : string.Empty);

        // 与 DELETE 同一句话的分岔（见 DeleteEngine.Build）：SET 那一串描述的是 AST，而改写钩子动的正是
        // AST 已成文之后的那一份文本，所以没人改写时这句话照样是最终语句的摘要，有人改写时它就不是了。
        if (sql.Text != emitted.Text)
            reason += " 注意：这一句已被 CudBuilding 改写，最终执行的是 Sql 而不是上面这一段描述的东西（改了几步、每步前后文本见 HookReplay）。";

        return new UpdatePlan(ast, sql, reason, traces) { Provider = _orm.Provider };
    }

    /// <summary>日志与 AOP 上的表名给<em>物理片</em>而不是模板：一次 <c>UpdateTable</c> 只打一张片，
    /// 而看日志的人要的是"改了哪张表"。集群也从同一张片上读（分片的 <c>Source</c> 继承模板，
    /// 所以"分库 + 分表"叠在一起时这一句说的是那座库里的那张片，§16.4 两档共用一条链）。</summary>
    ExecutionTag Tag()
    {
        var table = Target();

        return new ExecutionTag("update", [table.Name], DataSourceRouting.ClusterOf(table));
    }

    static int IdentifierChars(UpdateStatement ast)
        => ast.Table.Table.Name.Length + ast.Table.Alias.Length * 3 + ast.Sets.Sum(s => s.Column.ColumnName.Length) + 128;
}

/// <summary>DELETE 链的落点。与 UPDATE 共用 <see cref="Where"/> 的翻译路径与同一个无闸门规则（<see cref="Guard.Whereless"/>）。</summary>
[RequiresUnreferencedCode("lambda 归约与闭包常量求值要反射读成员。")]
[RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
sealed class DeleteEngine<T> : IDelete<T> where T : class
{
    readonly FastSqlOrm _orm;
    readonly EntityContract _contract;
    readonly TableInfo? _target;
    readonly TableRef _table;
    readonly SqlExpression? _where;

    DeletePlan? _plan;

    public DeleteEngine(FastSqlOrm orm)
        : this(orm, null, null)
    {
    }

    DeleteEngine(FastSqlOrm orm, SqlExpression? where, TableInfo? target)
    {
        _orm = orm;
        _contract = orm.Schemas.Contract<T>();
        _target = target;

        // 与 UPDATE 同一条方言口径（§4 的 WriteTargetAlias）：Oracle 血统那一家目标的自引用名就是表名，
        // 所以选片必须发生在写条件之前，列前缀才跟得上换掉的那一张 —— 见 <see cref="SplitTable(DateTime)"/>。
        var table = target ?? _contract.Table;

        _table = new TableRef(0, _orm.Provider.Dialect.WriteTargetAlias(table), table, _contract);
        _where = where;
    }

    /// <summary>分表实体的 DELETE 要打的那一片，口径与 <see cref="UpdateEngine{T}.SplitTable(DateTime)"/>
    /// 逐字相同：一次一片（一条 DELETE 只有一个目标表），且必须放在链首。</summary>
    public IDelete<T> SplitTable(DateTime at) => Single(SplitRouting.EnumerateTime(_contract.Table, Split(), at, at)[0]);

    /// <summary>分表实体的 DELETE 要打的那一片（哈希档）。</summary>
    public IDelete<T> SplitTable(int bucket) => Single(SplitRouting.EnumerateBuckets(_contract.Table, Split(), [bucket])[0]);

    DeleteEngine<T> Single(TableInfo target)
    {
        if (_where is not null)
            throw new FastSqlException(
                $"{_contract.Table.Name} 的 SplitTable 要放在链式最前面：这条链上已叠加条件，它们的列引用仍指向没选片时的那个目标。");

        return new DeleteEngine<T>(_orm, _where, target);
    }

    TableInfo Target()
    {
        if (_target is { } chosen)
            return chosen;

        if (_contract.Table.Split is null)
            return _contract.Table;

        throw new FastSqlException(
            $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），DELETE 要先说删哪一片：SplitTable(那一片里的一个日期) 或 SplitTable(桶号)。" +
            "一次只给一片，因为一条 DELETE 只有一个目标表，而这条链的契约是「ToSql() 与紧随其后的执行是同一条语句」。");
    }

    SplitFacts Split()
        => _contract.Table.Split
           ?? throw new FastSqlException($"{_contract.Table.Name} 没有声明分表（类上没有 [SplitTable]），SplitTable 那一步无事可做。");

    public IDelete<T> Where(Expression<Func<T, bool>> predicate)
        => new DeleteEngine<T>(_orm, _where is null
            ? _orm.Expressions.Predicate(predicate, ScopeOf(predicate))
            : new SqlBinaryExpression(BinaryOperator.And, _where, _orm.Expressions.Predicate(predicate, ScopeOf(predicate))), _target);

    public DeletePlan Explain() => _plan ??= Build();

    public string ToSql() => Explain().Sql.Text;

    public string ToSql(out IReadOnlyList<AdoParameter> parameters)
    {
        var sql = Explain().Sql;

        parameters = sql.Parameters;

        return sql.Text;
    }

    public string ToLogSql() => Explain().ToLogSql();

    public async Task<int> ExecuteAffrowsAsync(CancellationToken ct = default)
        => await _orm.Ado.NonQueryAsync(Explain().Sql, Tag(), ct).ConfigureAwait(false);

    DeletePlan Build()
    {
        if (_where is null)
            throw new FastSqlException(Guard.Whereless("DELETE", _contract.Table.Name));

        // 与 UPDATE 同一句：分表而没选片要的是可行动的那一句，不是撞上发射口那道结构性兜底。
        // 目标已经长在 <c>_table</c> 上（谓词的列引用绑的就是那一个实例），这里只取它的名义。
        var table = Target();

        var traces = new List<HookTrace>();
        var step = 0;

        // 与 UPDATE 同一条叠加规则（顺序、位置、以及"过滤器不算调用方说过的条件"三件事都在
        // <c>UpdateEngine.Build</c> 那一段注释里说了，理由一处讲清、两条链共用）。
        var where = _orm.Filters.ApplyTo(_table, _where);
        var injected = _orm.Filters.CountFor(typeof(T));

        var ast = new DeleteStatement
        {
            Table = _table,
            Where = where,
        };

        // 参数位在这里数不出来（它们全在谓词里，一颗还是几百颗要到发射时才知道），所以估 0：
        // 估少只多翻一次 StringBuilder 的倍，不改文本（§12.7 E 的同一笔账）。
        var writer = StatementEmitter.Writer(_orm.Provider.Dialect,
            StatementEmitter.EstimateLength(0, _contract.Table.Name.Length + _table.Alias.Length * 3 + 128));
        _orm.Provider.Dialect.Emit(in ast, writer);

        var emitted = new DialectSql(writer.ToSqlString(), writer.Parameters);
        var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "delete", traces, ref step), traces, ref step);

        // §10.7 的软删改写点（CudBuilding）现在真的会改文本了，所以"物理删除"这句话<em>只在没人改写时成立</em>。
        // 分岔按文本判而不是按订阅判：钩子可以只读不写，也可以改出另一条 DELETE，那句话都得跟着实际那一支说。
        // 写在 Reason 的第一句里，是因为开了软删的项目读到这里必须知道这一段描述还准不准。
        // v0.26 起这一句少了一半出处：全局过滤器已经会叠进上面的 WHERE，所以"这一句什么都没帮你加"不再成立，
        // 而软删那一半仍然没人做（orm.Behaviors.SoftDelete 还没有），于是那一句改口成点名那一口子。
        var reason = $"DELETE FROM {table.Name}：" + (sql.Text == emitted.Text
                ? "物理删除，行不在了（§10.7 的软删要靠 CudBuilding 订阅者把这一句改写成 UPDATE，而 orm.Behaviors.SoftDelete 那一格还没有落地，所以本库自己不会做这个改写；库里那一行归你了）。"
                : "这一句已被 CudBuilding 改写，最终执行的是 Sql 而不是这一格描述的东西（改了几步、每步前后文本见 HookReplay）。")
            + (injected > 0
                ? $" WHERE 里叠着 {injected} 条全局过滤器（§10.7 的 orm.Filters），它们由内核加、不出现在这条链的调用里 —— 命中范围比 .Where(...) 那几句窄。"
                : string.Empty)
            + $"参数 {sql.Parameters.Count} 颗。";

        return new DeletePlan(ast, sql, reason, traces) { Provider = _orm.Provider };
    }

    Scope ScopeOf(LambdaExpression lambda)
        => Scope.Create(new QueryModel { Tables = [_table], Plan = new EntityProjection(0) }, lambda);

    /// <summary>与 UPDATE 同一条口径：日志与 AOP 上给<em>物理片</em>的名字，一次只打一张片，集群也从同一张片读。</summary>
    ExecutionTag Tag()
    {
        var table = Target();

        return new ExecutionTag("delete", [table.Name], DataSourceRouting.ClusterOf(table));
    }
}

/// <summary>两条链共用的那句拒绝。<b>构建期硬拦</b>：没有 WHERE 的 UPDATE/DELETE 在语句还没生成时就抛，
/// 而不是等到执行期由 <c>CommandTerminating</c> 钩子判 —— 早 catch 比晚 catch 便宜，而"忘了写条件"
/// 是最常见的笔误之一。运行期的 <see cref="FastSql.Aop.FastSqlAop.CommandTerminating"/> 是第二道闸，
/// 用于更复杂的规则（租户级禁写、审计拦截等），两道闸不冲突。</summary>
static class Guard
{
    public static string Whereless(string operation, string table)
        => $"{table} 的 {operation} 没有 WHERE：那是整表操作，不是一种可以漏写条件漏出来的默认行为。" +
        $"要真的对全部行做这件事，就把它写成 .Where(o => true) —— 产出的语句一模一样，但这句话是你说的。" +
        "运行期还有第二道闸（§10.1 的 CommandTerminating 钩子），可以做更细的拦截规则。";
}
