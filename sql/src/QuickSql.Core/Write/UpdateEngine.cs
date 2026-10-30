using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using QuickSql.Aop;
using QuickSql.Execution;
using QuickSql.Metadata;
using QuickSql.Query.Model;
using QuickSql.Schema;
using QuickSql.Sql;
using QuickSql.Sql.Ast;
using QuickSql.Translation;

namespace QuickSql.Write;

/// <summary>UPDATE 链的落点。<b>与 <see cref="InsertEngine{T}"/> 的关键差别是它没有「取值段」</b>：
/// 插入要往库里放一整行，所以必须先把实体读成行 × 列的实参值（<c>AuditValue</c> 与契约校验都挂在那一段）；
/// 这条链上落到库里的只有 <c>SET</c> 那几列，其余列根本不进语句，
/// 于是「按实体形状校验一遍」在这里是错的（见 <see cref="Build"/> 里那段注释）。
/// 表达式树在这里归约，所以标注与读侧 <see cref="QuickSql.Query.QueryEngine"/> 同一套（§5.10）。</summary>
[RequiresUnreferencedCode("lambda 归约与闭包常量求值要反射读成员。")]
[RequiresDynamicCode("不可约子表达式的退路是编译表达式树。")]
sealed class UpdateEngine<T> : IUpdate<T> where T : class
{
    readonly QuickSqlOrm _orm;
    readonly EntityContract _contract;
    readonly TableInfo? _target;
    readonly TableRef _table;
    readonly IReadOnlyList<SetClause> _sets;
    readonly SqlExpression? _where;

    /// <summary>取值段（<see cref="FromEntity"/> 那一趟 <c>AuditValue</c>）留下的改写痕迹。
    /// 链式点名的那条路永远是空表——<see cref="Set{TProp}(System.Linq.Expressions.Expression{System.Func{T,int}},System.Linq.Expressions.Expression{System.Func{T,int}})"/>
    /// 那些重载不读实体、不过钩子，与 <c>UpdateBulkEngine._valueTraces</c> 同一分工。
    /// 它必须跟着 <see cref="Append"/>/<see cref="Where"/> 的每一次复制走：<see cref="Build"/> 里的步号从它末尾续编（§10.1 硬规则 1）。</summary>
    readonly IReadOnlyList<HookTrace> _valueTraces;

    UpdatePlan? _plan;

    public UpdateEngine(QuickSqlOrm orm)
        : this(orm, [], null, null)
    {
    }

    UpdateEngine(QuickSqlOrm orm, IReadOnlyList<SetClause> sets, SqlExpression? where, TableInfo? target, IReadOnlyList<HookTrace>? valueTraces = null)
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
        _valueTraces = valueTraces ?? [];
    }

    /// <summary>按实体那一行定位的 UPDATE：<c>orm.Update(entity)</c> 的落点。
    /// <para><b>它点的是"哪一行"，不点"改哪几列"</b>，而 <see cref="UpdateEngine{T}"/> 那条链正好相反，
    /// 所以这一格不能并进 <see cref="Set{TProp}(System.Linq.Expressions.Expression{System.Func{T,TProp}},TProp)"/>
    /// 重载去。<b>列尺子在 <see cref="FromEntity"/> 里换了一把</b>：只把<em>带来值</em>（非 null）的可写列写进
    /// <c>SET</c>，那一格交来 null 就是"这一列这次没说"，不进语句。这与 <see cref="UpdateBulkEngine{T}"/>
    /// 的「整行交出去，那一格说的是 NULL 就是 NULL」<em>正对面</em>，是同一批实体的两种意图而不是可以互换的两种写法
    /// （想连 null 一起覆盖就走 <c>UpdateRange</c>；想把某一格显式写成 NULL 就走链式点那一列）。</para>
    /// <para><c>WHERE</c> 是主键合取式（复合键逐列 <c>=</c> 再 <c>AND</c>，与跟踪器 <c>KeyPredicate</c> 同一个形状），
    /// 所以这张表必须有主键。之后叠加的顺序与链式那条逐字相同：调用方再写的 <c>.Where(...)</c> 排在主键之后、
    /// 全局过滤器排在最后，软删在册时 <c>Delete</c> 侧的改写规则不参与这一句。</para></summary>
    internal static UpdateEngine<T> FromEntity(QuickSqlOrm orm, T entity)
    {
        var contract = orm.Schemas.Contract<T>();
        var schema = orm.Schemas.Schema<T>();

        if (contract.PrimaryKey.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有主键列：按实体的 UPDATE 要按主键定位，WHERE 出不来。" +
                $"要么给实体点主键（[PrimaryKey] 或约定名 Id），要么按条件走 orm.Update<{typeof(T).Name}>().Set(...).Where(...)。");

        // 分表实体按<em>这只实体自己的分片键</em>换算那一片 —— 与 InsertEngine 的 SplitGroups 同一颗 Resolve，
        // 于是"这一行进哪张片"在插入与更新上是同一个答案。链式那条的 SplitTable(日期/桶号) 在这里用不上：
        // 选片发生在链首，而这一条链的链首就是这只实体。
        var target = EntityRowLocator.ShardOf(contract, schema, entity);
        var table = target ?? contract.Table;
        var tableRef = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(table), table, contract);

        var audit = orm.Aop.AuditValue;
        var traces = new List<HookTrace>();
        var sets = new List<SetClause>(contract.Columns.Length);

        for (var ordinal = 0; ordinal < contract.Columns.Length; ordinal++)
        {
            var column = contract.Columns[ordinal];

            // 三道筛与 UpdateBulkEngine 构造函数逐字同源：主键进 WHERE 不进 SET，库里给值的列与并发列不接受书写。
            // 应用给值的并发列（AppSetsValue）例外：新值在实体上、由应用生成，必须随整行写出去（WHERE 仍比快照）。
            if (column.IsPrimary || column.ValueFromStore || (column.IsConcurrencyToken && !column.AppSetsValue))
                continue;

            var value = schema.GetValue(entity, ordinal);

            // 取值段与批量链同一趟钩子、同一个种类（"update"）：订阅"补修改时间"的人不该因为调用方选了哪扇门
            // 而少填一格。钩子写完值再判 null —— 钩子把 null 填成有值的那些列因此进得了 SET。
            if (audit is not null)
            {
                var @event = new AuditValueEvent(entity, column, value, "update", written => schema.SetValue(entity, ordinal, written));

                audit(@event);

                if (@event.WasSet)
                {
                    traces.Add(new HookTrace(traces.Count, "AuditValue", HookCategory.Rewrite, $"{column.MemberName} = {StatementEmitter.Text(value)}", $"{column.MemberName} = {StatementEmitter.Text(@event.CurrentValue)}"));
                    value = @event.CurrentValue;
                }
            }

            if (value is null)
                continue;

            Check(orm, contract, column, value);

            sets.Add(new SetClause(column, new ParameterValue(value, column)));
        }

        if (sets.Count == 0)
            throw new QuickSqlException($"{contract.Table.Name} 的这只实体除主键、库里给值的列与并发列以外没有一列带值：" +
                "按实体的 UPDATE 只写带来值的那些列，这一只一列都没说，那一句 SET 出不来。" +
                $"要么把要改的那几列填上，要么显式写 orm.Update<{typeof(T).Name}>().Set(o => o.列, 值)——后者还能把一格真写成 NULL，这一档做不到。");

        return new UpdateEngine<T>(orm, sets, EntityRowLocator.KeyPredicate(contract, schema, entity, tableRef, "按实体的 UPDATE"), target, traces);
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
            throw new QuickSqlException(
                $"{_contract.Table.Name} 的 SplitTable 要放在链式最前面：这条链上已叠加 {_sets.Count} 个 SET/{(_where is null ? 0 : 1)} 个条件，" +
                "它们的列引用仍指向没选片时的那个目标。");

        return new UpdateEngine<T>(_orm, _sets, _where, target, _valueTraces);
    }

    /// <summary>这一条链的目标片：已选就用它，没选而实体分了片就在<em>构建之前</em>拒。</summary>
    TableInfo Target()
    {
        if (_target is { } chosen)
            return chosen;

        if (_contract.Table.Split is null)
            return _contract.Table;

        throw new QuickSqlException(
            $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），UPDATE 要先说打哪一片：SplitTable(那一片里的一个日期) 或 SplitTable(桶号)。" +
            "一次只给一片，因为一条 UPDATE 只有一个目标表，而这条链的契约是「ToSql() 与紧随其后的执行是同一条语句」。");
    }

    /// <summary>分片事实，只在实体确实分了片时才交出去。<b>取模板而不是取 <see cref="_table"/></b>：
    /// 已经选过片时 <c>_table.Table.Split</c> 是 null（那是一张换算完的物理表），而范围换算要的仍然是那份模板。</summary>
    SplitFacts Split()
        => _contract.Table.Split
           ?? throw new QuickSqlException($"{_contract.Table.Name} 没有声明分表（类上没有 [SplitTable]），SplitTable 那一步无事可做。");

    TableInfo SplitAt(DateTime at)
    {
        var split = Split();

        return SplitRouting.EnumerateTime(_contract.Table, split, at, at)[0];
    }

    public IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, TProp value)
    {
        var target = ColumnOf(column.Body);

        Check(_orm, _contract, target, value);

        return Append(target, new ParameterValue(value, target));
    }

    /// <summary>值那一侧走的是读侧同一个归约器，所以 <c>o =&gt; o.Amount + 1</c> 与
    /// <c>o =&gt; Math.Round(o.Amount, 2)</c> 在这里的翻译结果与在 <c>Where</c> 里完全一致（§5.5 单一真相）。</summary>
    public IUpdate<T> Set<TProp>(Expression<Func<T, TProp>> column, Expression<Func<T, TProp>> value)
    {
        var target = ColumnOf(column.Body);
        var node = _orm.Expressions.Visit(value.Body, ScopeOf(value));

        // SET 的值位不收行号：读侧那一族有 <c>WindowGuard.RejectRowNumber</c> 那条统一出路（WithSql ∶ 原生 SQL），
        // 这一格两样都接不上 —— 写侧没有"换个表源"可言，所以这里给的是它自己的那条路。
        // 判在翻译之后：值的形状先照读侧同一条尺子翻（§5.5 单一真相），翻完才知道里面有没有 OVER。
        if (WindowGuard.HasRowNumber(node))
            throw new UntranslatableExpressionException(value.Body.ToString(),
                "SET 的值位里没有行号的落点：UPDATE 的 SET 收不了 OVER（十八家都不认，宁可抛也不要发一句库不认的句子）。" +
                "要按行号写值就先把行读出来、在内存里编号，再走 UpdateRange/SaveChanges 那一条写链。");

        return Append(target, node);
    }

    public IUpdate<T> Where(Expression<Func<T, bool>> predicate)
        => new UpdateEngine<T>(_orm, _sets, _where is null
            ? _orm.Expressions.Predicate(predicate, ScopeOf(predicate))
            : And(_where, _orm.Expressions.Predicate(predicate, ScopeOf(predicate))), _target, _valueTraces);

    /// <summary>SET 左侧的列。四件事在这里判：<b>是不是列</b>（导航属性与计算成员不进 SET）、
    /// <b>是不是自增列</b>（库里那一列不接受写，除非开 IDENTITY_INSERT，P0 未接）、
    /// <b>是不是并发列</b>（§8.2 的"只比较不书写"在两条写链上同一条尺子：链式这一侧拦下来的理由是
    /// 那一句本来就在库里报错，而报错点离写它的人隔了一次往返）、
    /// <b>是不是已经写过一次</b>（同一列出现两次是纯 bug，而库给的错只说 "specified multiple times"）。</summary>
    UpdateEngine<T> Append(ColumnContract target, SqlExpression value)
    {
        for (var i = 0; i < _sets.Count; i++)
            if (ReferenceEquals(_sets[i].Column, target))
                throw new QuickSqlException($"{_contract.Table.Name} 的 {target.ColumnName} 被设置了两次：后一次不会覆盖前一次，" +
                    "库里直接报错。要基于原值算就写成表达式那一档：Set(o => o.Amount, o => o.Amount + 1)。");

        return new UpdateEngine<T>(_orm, [.. _sets, new SetClause(target, value)], _where, _target, _valueTraces);
    }

    ColumnContract ColumnOf(Expression body)
    {
        // 值类型列在 Expression<Func<T, TProp>> 里不产生转换节点，但 object 化的写法与枚举底层转换会留下
        // 一层 Convert —— 剥掉它，否则下面那句"必须是列的读法"会对一个合法写法报错。
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } widen)
            body = widen.Operand;

        if (body is not MemberExpression { Expression: ParameterExpression } member)
            throw new QuickSqlException($"Set 的第一个参数必须是本实体单个列的读法（形如 o => o.列名），当前写的是 {body}。" +
                "算式放在第二个参数上，条件放在 Where 上。");

        var name = member.Member.Name;

        if (!_contract.ByMember.TryGetValue(name, out var column))
            throw new QuickSqlException($"{_contract.Table.Name} 没有名为 {name} 的列：导航属性与未映射的计算成员不能进 SET（§10.2 的映射层不认它）。");

        if (column.IsIdentity)
            throw new QuickSqlException($"{_contract.Table.Name} 的自增列 {column.MemberName} 不接受写：那是 IDENTITY_INSERT 那条路（IKeyStrategy.IdentityInsertToggle），本版本未接。");

        if (column.IsConcurrencyToken && !column.AppSetsValue)
            throw new QuickSqlException($"{_contract.Table.Name} 的并发列 {column.MemberName} 只比较、不书写（§8.2）：那一列的值归库给，" +
                "而这一句在库里也执行不过（SQL Server 的 rowversion 不可赋值）。要改版本号得直连库；" +
                "应用自己给值的并发列（AppSetsValue 那一族）不在这一格，点它写它是合法的。");

        return column;
    }

    /// <summary>值级的那两把契约尺子（§10.6 的 <c>ContractOnly</c> 档 = 批量/无宿主实体路径唯一能跑的档）。
    /// <b>为什么只有这两把</b>：DataAnnotations 与 Custom 档的规则挂在<em>实体</em>上，而这条链上没有整实体，
    /// 只有一格要进 <c>SET</c> 的值 —— 拿 <c>IEntityValidator.TryValidate</c> 就得先造一只假实体，
    /// 那会把没填的列一起判成"缺"。所以这里直接调 <see cref="ContractRules"/> 那两条，
    /// 消息文本与插入链逐字同源（它也是从那里出来的）。差异记在 §14。
    /// <b>静态化的理由是一件事而不是形态</b>：<c>orm.Update(entity)</c> 那一档也在建 <c>SET</c>，
    /// 两扇门量的是同一把尺子，留成两份迟早会在"哪一格算超长"上分家。</summary>
    static void Check(QuickSqlOrm orm, EntityContract contract, ColumnContract column, object? value)
    {
        var validator = orm.Schemas.Validator<T>();

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
            $"{contract.Table.Name} 的 {column.ColumnName} 被拒绝：这一格的值违反库契约（ValidationMode.{validator.Mode}）。语句还没有生成，什么都没写进库。",
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
            throw new QuickSqlException($"{_contract.Table.Name} 的 UPDATE 一个 SET 列都没有：这样的语句没有任何效果。用 .Set(o => o.列, 值) 说要改什么。");

        // 没有 WHERE 的 UPDATE 会把整表改成同一个值，而这条链上没有任何东西能把它拦在库外。
        // 构建期由 Guard.Whereless 硬拦（早 catch 比晚 catch 便宜），运行期由 §10.1 的 CommandTerminating 钩子做第二道闸。
        // 要清全表请显式写 .Where(o => true) —— 它产出同一条语句。
        if (_where is null)
            throw new QuickSqlException(Guard.Whereless("UPDATE", _contract.Table.Name));

        // 先问一次目标：分表而没选片时要的是 <see cref="Target"/> 那一句可行动的拒绝，而不是撞上
        // EmitTableName 那道结构性兜底 —— 那一兜说的是"模板没换算成具体分片"，是给内核看的不变量，
        // 不是给调用方看的用法说明。（值不用在这里取：<c>_table</c> 在建引擎时就按同一份目标建好了，
        // 谓词的列引用绑的是那一个实例，换一份反而会让两边指不同的 TableRef。）
        var table = Target();

        // 取值段那几笔（只有 FromEntity 那一档有）排在最前、步号从 0 起，CudBuilding/ParameterBinding
        // 接着往下编 —— 与 UpdateBulkEngine.Build 同一条回放纪律（§10.1 硬规则 1）。
        var traces = new List<HookTrace>(_valueTraces);
        var step = _valueTraces.Count;

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
    readonly QuickSqlOrm _orm;
    readonly EntityContract _contract;
    readonly TableInfo? _target;
    readonly TableRef _table;
    readonly SqlExpression? _where;

    DeletePlan? _plan;

    public DeleteEngine(QuickSqlOrm orm)
        : this(orm, null, null)
    {
    }

    DeleteEngine(QuickSqlOrm orm, SqlExpression? where, TableInfo? target)
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
            throw new QuickSqlException(
                $"{_contract.Table.Name} 的 SplitTable 要放在链式最前面：这条链上已叠加条件，它们的列引用仍指向没选片时的那个目标。");

        return new DeleteEngine<T>(_orm, _where, target);
    }

    TableInfo Target()
    {
        if (_target is { } chosen)
            return chosen;

        if (_contract.Table.Split is null)
            return _contract.Table;

        throw new QuickSqlException(
            $"{_contract.Table.Name} 是分表实体（{_contract.Table.Split.Kind} 档），DELETE 要先说删哪一片：SplitTable(那一片里的一个日期) 或 SplitTable(桶号)。" +
            "一次只给一片，因为一条 DELETE 只有一个目标表，而这条链的契约是「ToSql() 与紧随其后的执行是同一条语句」。");
    }

    SplitFacts Split()
        => _contract.Table.Split
           ?? throw new QuickSqlException($"{_contract.Table.Name} 没有声明分表（类上没有 [SplitTable]），SplitTable 那一步无事可做。");

    public IDelete<T> Where(Expression<Func<T, bool>> predicate)
        => new DeleteEngine<T>(_orm, _where is null
            ? _orm.Expressions.Predicate(predicate, ScopeOf(predicate))
            : new SqlBinaryExpression(BinaryOperator.And, _where, _orm.Expressions.Predicate(predicate, ScopeOf(predicate))), _target);

    /// <summary>按实体那一行定位的 DELETE：<c>orm.Delete(entity)</c> 的落点。<b>这一格只借主键，不借值</b>——
    /// 实体上的其余列一列都不读（读它们也不会进语句），所以「哪几列是 null」在这条路上没有任何含义，
    /// 与 <see cref="UpdateEngine{T}.FromEntity"/> 那把「null 不进 SET」的尺子是两件事。
    /// <b>复合主键在这里支持</b>：逐列 <c>=</c> 再 <c>AND</c>，与跟踪器摘行、批量链各自主键时同一个形状；
    /// <see cref="DeleteRange{T}"/> 拒它靠的是「IN 清单装不下元组」，那一句在单行上根本不成立。
    /// 软删与全局过滤器那两道闸门由 <see cref="Build"/> 接手，与 <c>.Where(...)</c> 那条路逐字同一条。</summary>
    internal static DeleteEngine<T> FromEntity(QuickSqlOrm orm, T entity)
    {
        var contract = orm.Schemas.Contract<T>();
        var schema = orm.Schemas.Schema<T>();

        if (contract.PrimaryKey.Length == 0)
            throw new QuickSqlException($"{contract.Table.Name} 没有主键列：按实体的 DELETE 要按主键定位，WHERE 出不来。" +
                $"要么给实体点主键（[PrimaryKey] 或约定名 Id），要么按条件走 orm.Delete<{typeof(T).Name}>().Where(...)。");

        var target = EntityRowLocator.ShardOf(contract, schema, entity);
        var table = target ?? contract.Table;
        var tableRef = new TableRef(0, orm.Provider.Dialect.WriteTargetAlias(table), table, contract);

        return new DeleteEngine<T>(orm, EntityRowLocator.KeyPredicate(contract, schema, entity, tableRef, "按实体的 DELETE"), target);
    }

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
            throw new QuickSqlException(Guard.Whereless("DELETE", _contract.Table.Name));

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

        // §10.7 约束 3 的入口子（v0.38 第十七笔）：这张表上在册的软删规则把上面那一棵 DELETE 换成
        // 一句"置位 + 记时间"的 UPDATE，而 WHERE 逐字不动 —— 软删不改命中范围，改的是命中之后的动作。
        // 规则为空时下面这一句就是"没有这件事"，物理删除那条路径与本版之前逐字同形（同一次 EstimateLength 的入参）。
        // 参数位在这里数不出来（它们全在谓词里，一颗还是几百颗要到发射时才知道），所以估 0：
        // 估少只多翻一次 StringBuilder 的倍，不改文本（§12.7 E 的同一笔账）。
        var soft = (_orm.Behaviors.HasAny ? _orm.Behaviors.RuleFor(typeof(T)) : null)
            ?.Rewrite(_contract, _table, where);

        var emitted = StatementEmitter.EmitDelete(
            _orm.Provider.Dialect, ast, soft, _contract.Table.Name.Length + _table.Alias.Length * 3 + 128);

        var sql = StatementEmitter.ApplyBinding(_orm, StatementEmitter.ApplyCud(_orm, emitted, "delete", traces, ref step), traces, ref step);

        // 三分岔：软删在册时说的是"置位"（这才是真正发出去的那一句），否则按最终文本判"物理删除"还是
        // "已被 CudBuilding 改写"。分岔按文本与规则判而不按订阅判：钩子可以只读不写，也可以改出另一条
        // DELETE，那句话都得跟着实际那一支说。写在 Reason 的第一句里，是因为开了软删的项目读到这里
        // 必须知道这一段描述还准不准。
        // v0.26 起这一句少了一半出处：全局过滤器已经会叠进上面的 WHERE，所以"这一句什么都没帮你加"不再成立。
        var reason = soft is null
            ? $"DELETE FROM {table.Name}：" + (sql.Text == emitted.Text
                ? "物理删除，行不在了（本库自己不做这个改写：orm.Behaviors.SoftDelete 在册时这一句会被换成 UPDATE 置位，没在册时它就发 DELETE；要自己改写整句文本，订一颗 CudBuilding）。"
                : "这一句已被 CudBuilding 改写，最终执行的是 Sql 而不是这一格描述的东西（改了几步、每步前后文本见 HookReplay）。")
            : $"{table.Name} 的删除已由本库的 Behaviors.SoftDelete 改写成 UPDATE：SET {string.Join("、", soft.Sets.Select(s => s.Column.ColumnName))}" +
              $"（§10.7 约束 3）。执行的是 Sql 那一格，行还在库里而置位那一格已经写了 true —— 命中 0 行说的是「谓词没圈到行」，" +
              "不是「行已经不在了」，所以这一句不会把「已被删过」报成并发冲突（那一档的判据要先有人裁，见 §14 的 P1 余项）。";

        reason += (injected > 0
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
/// 是最常见的笔误之一。运行期的 <see cref="QuickSql.Aop.QuickSqlAop.CommandTerminating"/> 是第二道闸，
/// 用于更复杂的规则（租户级禁写、审计拦截等），两道闸不冲突。</summary>
static class Guard
{
    public static string Whereless(string operation, string table)
        => $"{table} 的 {operation} 没有 WHERE：那是整表操作，不是一种可以漏写条件漏出来的默认行为。" +
        $"要真的对全部行做这件事，就把它写成 .Where(o => true) —— 产出的语句一模一样，但这句话是你说的。" +
        "运行期还有第二道闸（§10.1 的 CommandTerminating 钩子），可以做更细的拦截规则。";
}

/// <summary>「按实体那一行定位」这一档共用的两件：目标片换算与主键合取式。
/// <b>它们存在的理由是 <c>orm.Update(entity)</c> 与 <c>orm.Delete(entity)</c> 要给出同一个答案</b>——
/// 同一只实体先改后删，两次定位必须落在同一张片、同一个 <c>WHERE</c> 形状上，而两份判据分别长在两条链里
/// 迟早会分家。判据本身不是新东西：分片键那一句抄 <see cref="InsertEngine{T}"/> 的 <c>SplitGroups</c>
/// （插入与更新对"这一行进哪张片"不许有两种说法），合取式那一句抄跟踪器的 <c>KeyPredicate</c>
/// （逐列 <c>=</c> 再 <c>AND</c>，不用行值构造器——<c>(a, b) IN ((…))</c> 的支持面在国产库里参差）。</summary>
static class EntityRowLocator
{
    /// <summary>这只实体该打的那一张片。<see langword="null"/> = 这张表不分片，照模板名走。
    /// 分片键为空由 <see cref="SplitRouting.Resolve"/> 当场抛，不"当作今天"——
    /// 一行数据因为一个空字段静默进错表，那笔账要到月底统计时才看得见。</summary>
    public static TableInfo? ShardOf<T>(EntityContract contract, IEntityTypeSchema<T> schema, T entity) where T : class
    {
        if (contract.Table.Split is not { } split)
            return null;

        var ordinal = Array.IndexOf(contract.Columns, contract.ByMember[split.MemberName]);

        return SplitRouting.Resolve(contract.Table, split, schema.GetValue(entity, ordinal));
    }

    /// <summary>主键合取式，逐列 <c>=</c> 再 <c>AND</c>。<b>键位不过 <c>AuditValue</c></b>：那一列在这条链上是
    /// 定位条件而不是写出去的列，让钩子改它等于允许订阅方把一批语句挪到别的行上（与 <c>UpdateBulkEngine.Prepare</c>
    /// 那条"主键列不进取值段"同一道闸门）。两道空键判据也抄那里：null 定位不到任何行，而库里给值的键交来
    /// CLR 默认形态时同样定位不到——两者都只会安静地报告 0 行。</summary>
    public static SqlExpression KeyPredicate<T>(EntityContract contract, IEntityTypeSchema<T> schema, T entity, TableRef table, string what) where T : class
    {
        SqlExpression? where = null;
        var storeGivenKey = Array.Exists(contract.PrimaryKey, static key => key.ValueFromStore);

        foreach (var key in contract.PrimaryKey)
        {
            var value = schema.GetValue(entity, Array.IndexOf(contract.Columns, key));

            if (value is null)
                throw new QuickSqlException($"{contract.Table.Name} 的主键列 {key.ColumnName} 是 null：{what}按主键定位，null 键定位不到任何行，" +
                    "而那一句会安静地报告 0 行。新行请走 orm.Insert。");

            if (storeGivenKey && StatementEmitter.IsUnassignedKey(value))
                throw new QuickSqlException($"{contract.Table.Name} 的主键列 {key.ColumnName} 由库给值（自增 ∶ 序列），而它交来的是没给值的那个默认形态：" +
                    $"{what}定位不到那一行。要么把键给实，要么这一趟其实是插一行、走 orm.Insert。");

            var equal = new SqlBinaryExpression(BinaryOperator.Equal, new ColumnExpression(table, key), new ParameterValue(value, key));

            where = where is null ? equal : new SqlBinaryExpression(BinaryOperator.And, where, equal);
        }

        return where!;
    }
}
