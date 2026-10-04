using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Values;
using FastSql.Sql.Ast;

namespace FastSql.Sql;

/// <summary>AST → 文本的共用步法（§3 第 4 层）。方言只覆写<b>真正分岔</b>的那几处：分页、
/// <c>UPDATE/DELETE</c> 的目标写法、RETURNING 落点、类型名、标识符引号。
/// 之所以住在 Core 而不是各 provider 里：金仓/openGauss/瀚高共享 <c>PostgreSqlBase</c>，
/// 而 provider 之间禁止互引（§2），共用的东西只能往上一层。</summary>
public abstract class DialectBase(IdentifierPolicy identifiers, IValueBinder values, IExpressionRules expressions) : ISqlDialect
{
    /// <summary>报错时要能说出是哪一家，所以每个方言必须报出自己的库种。</summary>
    public abstract DbKind Kind { get; }

    public abstract char ParameterPrefix { get; }

    /// <summary>默认 false = 执行器照旧摘前缀：只有拿到"带前缀那一路有活库见证"的这一家才许翻成 true，
    /// 判据与出处写在 <see cref="ISqlDialect.DriverAcceptsPrefixedParameterNames"/>。</summary>
    public virtual bool DriverAcceptsPrefixedParameterNames => false;

    public virtual bool PrefixUnicodeLiterals => false;

    public virtual string EmptySetPredicate => "1 = 0";

    public virtual string AlwaysTruePredicate => "1 = 1";

    public virtual bool PagingRequiresOrdering => false;

    /// <summary>默认 true：两个已落地的方言族都有 <c>UPDATE … FROM</c>（T-SQL 与 PG 系各用自己的写法），
    /// 所以 §12.5 的合并档对它们是同一份文本形状。取 false 的那一家必须在 <see cref="EmitJoinedSources"/>
    /// 里<em>抛</em>而不是悄悄丢掉那些表源 —— 这一颗只是"要不要走合并那条路"的开关，不是错误豁免。</summary>
    public virtual bool SupportsJoinedWrites => true;

    /// <summary>默认 true：今天四家的驱动各自覆写了保存点三件套，而且各自在那一族的集成册里绿过一次
    /// （判据与逐家出处写在 <see cref="ISqlDialect.SupportsDriverSavepoints"/>，四家的读数是同一批五场）。
    /// <b>这一颗也不是错误豁免</b>：取 false 的那一家必须把下面三口一起覆写，只改开关会得到一处<em>更靠里</em>的抛。</summary>
    public virtual bool SupportsDriverSavepoints => true;

    /// <summary>没有文本保存点的发射口：<see cref="ISqlDialect.SupportsDriverSavepoints"/> 取 true 的那一家
    /// <b>永远走不到这里</b>（执行层在驱动对象上直接调 <c>DbTransaction.Save</c>）。
    /// 走到这里说明有人把那颗开关翻成 false 而没有给出文本 —— 与 <see cref="EmitReturning"/>
    /// 和 <see cref="EmitJoinedSources"/> 那两格同一条纪律：宁可抛，不要悄悄发一句它不认的文本出去，
    /// 也不要悄悄什么都不发（后者会得到一只"能提交、能回滚、其实什么都没做"的保存点事务）。</summary>
    public virtual void EmitSavepoint(SqlWriter writer, string savepointName)
        => throw new FastSqlException($"{Kind} 没有文本保存点的发射口，却被要求创建一只（SupportsDriverSavepoints 取 false 的方言必须覆写 EmitSavepoint，§4）");

    /// <summary>同上一口，<b>必须成对覆写</b>：只覆写创建而不覆写回退，得到的是"内层回滚撤销了外层全部改动"
    /// —— 那正是这一格要避免的那个语义，而它不会在任何一次绿测试里现形。</summary>
    public virtual void EmitRollbackToSavepoint(SqlWriter writer, string savepointName)
        => throw new FastSqlException($"{Kind} 没有文本保存点的回退发射口，却被要求回退到 {savepointName}（SupportsDriverSavepoints 取 false 的方言必须覆写 EmitRollbackToSavepoint，§4）");

    /// <summary>同前两口，第三件。缺它的话红点在<em>内层提交</em>那一句上，而不是在创建那一句上。</summary>
    public virtual void EmitReleaseSavepoint(SqlWriter writer, string savepointName)
        => throw new FastSqlException($"{Kind} 没有文本保存点的释放发射口，却被要求释放 {savepointName}（SupportsDriverSavepoints 取 false 的方言必须覆写 EmitReleaseSavepoint，§4）");

    protected IdentifierPolicy Identifiers { get; } = identifiers;

    protected IValueBinder Values { get; } = values;

    protected IExpressionRules Expressions { get; } = expressions;

    // ── 语句 ──────────────────────────────────────────────────────────

    public virtual void Emit(in SelectStatement select, SqlWriter writer)
    {
        // 分页有两种形状：在句尾加子句（T-SQL 的 OFFSET..FETCH、PG 系的 LIMIT/OFFSET），或者把整句
        // <b>包进</b>两层 ROWNUM 过滤（Oracle 系，§9.4 那三套实现里的第三套）。第二种必须占一个槽位而不是
        // 覆写 <see cref="EmitPaging"/>：要包就意味内层那一份不能再有分页尾巴，而 EmitPaging 恰好是在
        // 尾巴那个位置上被调用的 —— 只给它的话，覆写方拿不到"不发尾巴"这件事。
        if (PagingWrapsSelect && (select.Limit is not null || select.Offset is not null))
        {
            EmitWrappedPaging(select, writer);
            return;
        }

        EmitSelectBody(select, writer);

        EmitPaging(writer, select);
    }

    /// <summary>SELECT 的本体：<c>SELECT … FROM … WHERE … GROUP BY … HAVING … ORDER BY</c>，
    /// <b>不含分页</b>。它是"包装式分页"要套进内层的那一段，所以与 <see cref="Emit"/> 分开两口。
    /// 句首那一段分页文本也挂在本方法上（<see cref="EmitPagingQualifier"/>）：本方法是"句首"唯一的出口。</summary>
    protected virtual void EmitSelectBody(in SelectStatement select, SqlWriter writer)
    {
        writer.Append("SELECT ");

        EmitPagingQualifier(writer, select);

        if (select.Distinct)
            writer.Append("DISTINCT ");

        EmitProjections(select.Columns, writer);

        if (select.From is { } from)
        {
            writer.Append(" FROM ");
            EmitSource(from, writer);
        }

        EmitJoins(select.Joins, writer);

        if (select.Where is { } filter)
        {
            writer.Append(" WHERE ");
            EmitExpression(filter, writer);
        }

        if (select.GroupBy.Count > 0)
        {
            writer.Append(" GROUP BY ");
            EmitList(select.GroupBy, writer);
        }

        if (select.Having is { } having)
        {
            writer.Append(" HAVING ");
            EmitExpression(having, writer);
        }

        if (select.OrderBy.Count > 0)
            EmitOrdering(select.OrderBy, writer);
    }

    /// <summary>分页的第三种落点：<b>句首的限定词</b>（Informix 血统的 <c>SELECT FIRST n SKIP m</c>）。
    /// §9.4 那句"三套实现"到这一家变四套，而前两套的槽位都接不住它 ——
    /// <see cref="EmitPaging"/> 长在句尾，<see cref="PagingWrapsSelect"/> 那一档要的是把整句包住，
    /// 限定词那一形两样都不是：它<em>在</em>本句里面、又<em>不</em>包任何一句。
    /// 默认空实现 = 已落地的五家都没有这一档（<see cref="Emit"/> 的尾巴那一步照常走）。
    /// <b>走这一支的方言必须把 <see cref="EmitPaging"/> 收成空体</b>，否则同一份分页在句首与句尾各发一遍 ——
    /// 两口都在同一个类里，改一处的人一定看得到另一处，与 <c>OracleDialect</c> 那一对
    /// （<see cref="PagingWrapsSelect"/> ∶ 空的 <c>EmitPaging</c>）同一种成对纪律。
    /// 参数化的位置在这里也顺带定了：限定词在<b>整句之最前</b>，所以它登记的参数编号比投影与谓词都小，
    /// 而文本上确实也在最前 —— 对位置形占位符（<c>?</c>）那一家，"编号序 = 文本序"这一条就是这么保住的。</summary>
    protected virtual void EmitPagingQualifier(SqlWriter writer, SelectStatement select)
    {
    }

    /// <summary>这一家要不要包装式分页。默认 false —— 两个已落地的方言族都是句尾加子句，
    /// 所以这个开关对它们的产出零影响（黄金样本逐字未动是这一句的见证方式）。</summary>
    protected virtual bool PagingWrapsSelect => false;

    /// <summary>把 <see cref="EmitSelectBody"/> 包进分页的外层。声明要包装却不实现这一口的方言
    /// <b>必须报错</b>而不是悄悄发一条不分页的 SQL —— 那会把"取前 5 行"变成全表扫（§5.8 禁静默退化的同一格）。</summary>
    protected virtual void EmitWrappedPaging(in SelectStatement select, SqlWriter writer)
        => throw new FastSqlException($"{Kind} 声明了包装式分页却没有实现 {nameof(EmitWrappedPaging)}，宁可报错也不能发一条不分页的 SQL");

    /// <summary>分页的<b>形状</b>各家不同（T-SQL 跟在 ORDER BY 之后、PG 用 <c>LIMIT</c>、DM 要三层嵌套的
    /// <c>ROWNUM</c>），所以这里是抽象口。共同的「要不要补占位序」规则在 <see cref="EmitPagingOrdering"/>。
    /// <b>走包装那一支的方言不会到这里</b>：它的分页文本在 <see cref="EmitWrappedPaging"/> 里。</summary>
    protected abstract void EmitPaging(SqlWriter writer, SelectStatement select);

    public virtual void Emit(in InsertStatement insert, SqlWriter writer)
    {
        writer.Append("INSERT INTO ");
        EmitTableName(writer, insert.Table.Table);

        if (insert.ExplicitColumnList)
        {
            writer.Append(" (");
            EmitIdentifiers(insert.Columns, writer);
            writer.Append(')');
        }

        // T-SQL 的 OUTPUT 必须在 VALUES 之前，PG 系的 RETURNING 在语句末尾：同一个位置差就要一个开关。
        // 两侧的空格由调用方补（IKeyStrategy.EmitReturning 只落地那一段，不管上下文）。
        if (ReturningPrecedesValues && insert.Returning.Count > 0)
        {
            writer.Append(' ');
            EmitReturning(writer, insert.Returning);
        }

        writer.Append(" VALUES ");

        for (var row = 0; row < insert.Rows.Count; row++)
        {
            if (row > 0)
                writer.Append(", ");

            var values = insert.Rows[row];

            writer.Append('(');

            for (var column = 0; column < insert.Columns.Count; column++)
            {
                if (column > 0)
                    writer.Append(", ");

                // null 位置走库默认值：多值 VALUES 里只有 DEFAULT 这一个写法能表达「这一行不填这列」。
                if (values[column] is { } value)
                    EmitExpression(value, writer);
                else
                    writer.Append("DEFAULT");
            }

            writer.Append(')');
        }

        if (!ReturningPrecedesValues && insert.Returning.Count > 0)
        {
            writer.Append(' ');
            EmitReturning(writer, insert.Returning);
        }
    }

    protected virtual bool ReturningPrecedesValues => false;

    protected virtual void EmitReturning(SqlWriter writer, IReadOnlyList<ColumnContract> returning)
        => throw new FastSqlException($"{Kind} 未实现 RETURNING/OUTPUT，但请求了主键回填（§4 的 IKeyStrategy）");

    public virtual void Emit(in UpdateStatement update, SqlWriter writer)
    {
        writer.Append("UPDATE ");
        EmitUpdateTarget(writer, update.Table);

        writer.Append(" SET ");

        for (var i = 0; i < update.Sets.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            // SET 左侧一律不带别名前缀：T-SQL 与 PG 在此一致，带上了两家的解析器都不认。
            EmitIdentifier(writer, update.Sets[i].Column.ColumnName);
            writer.Append(" = ");
            EmitExpression(update.Sets[i].Value, writer);
        }

        EmitJoinedSources(writer, " FROM ", update.Joins, out var joined);

        EmitWhere(writer, joined, update.Where);

        // 空格在这里补而不是写进 IKeyStrategy.EmitReturning：那一颗要被 INSERT 与 UPDATE、
        // 被前置的 OUTPUT 与后置的 RETURNING 共用，两侧的间距各不相同。
        // 少一个就得到 `…WHERE @p0 = 1RETURNING "Id"` —— 这一格是第二个方言族接出来才现形的。
        if (!ReturningPrecedesValues && update.Returning.Count > 0)
        {
            writer.Append(' ');
            EmitReturning(writer, update.Returning);
        }
    }

    /// <summary>ANSI 写法：<c>UPDATE [Orders] AS [t0]</c>。T-SQL 不接受目标别名，所以它覆写这三步。</summary>
    protected virtual void EmitUpdateTarget(SqlWriter writer, TableRef table)
    {
        EmitTableName(writer, table.Table);
        writer.Append(" AS ");
        EmitIdentifier(writer, table.Alias);
    }

    public virtual void Emit(in DeleteStatement delete, SqlWriter writer)
    {
        writer.Append("DELETE FROM ");
        EmitDeleteTarget(writer, delete.Table);

        EmitJoinedSources(writer, DeleteJoinKeyword, delete.Joins, out var joined);

        EmitWhere(writer, joined, delete.Where);
    }

    protected virtual void EmitDeleteTarget(SqlWriter writer, TableRef table) => EmitUpdateTarget(writer, table);

    /// <summary>T-SQL 的 <c>DELETE</c> 在目标之后再来一个 <c>FROM</c>，PG 系要 <c>USING</c>。</summary>
    protected virtual string DeleteJoinKeyword => " FROM ";

    /// <summary><c>FROM</c> 里表源与它别名之间的那颗连接词。<b>Oracle 血统没有 <c>AS</c></b>：
    /// 那一家只在<em>列</em>别名上接受 <c>AS</c>，表别名只能是 <c>FROM 表 别名</c> 那种挨着写。
    /// 它不是一处版式偏好而是"这条语句在这一家过不过解析器"，所以留在方言而不是共用层的字面量里。</summary>
    protected virtual string SourceAliasKeyword => " AS ";

    /// <summary>目标表之外的那些表源落成一段子句。<paramref name="keyword"/> 自带前后空格，
    /// 因为它的形状本身就是分岔点之一（T-SQL 在 DELETE 后要再来一个 <c>FROM</c>，PG 系要 <c>USING</c>）。
    /// <paramref name="folded"/> 是<b>被这次发射消化掉、要并入 <c>WHERE</c> 的条件</b>：
    /// 共用写法把条件留在 <c>ON</c> 上，于是返回 null；PG 系的 <c>UPDATE … FROM</c> / <c>DELETE … USING</c>
    /// 只吃逗号分隔的表源（连着目标表的 <c>JOIN</c> 在那两家都不合法），条件必须挪进 <c>WHERE</c> ——
    /// 那对 <c>INNER JOIN</c> 等价、对外连接不等价，所以挪的那一家遇到非 INNER 要抛而不是折。</summary>
    protected virtual void EmitJoinedSources(SqlWriter writer, string keyword, IReadOnlyList<JoinClause> joins, out SqlExpression? folded)
    {
        folded = null;

        if (joins.Count == 0)
            return;

        writer.Append(keyword);
        EmitJoins(joins, writer);
    }

    /// <summary><c>WHERE</c> 只有一个出口：连接条件在前、调用方的谓词在后。合并语句里这就是
    /// 「先按主键对上，再问值变没变」的阅读顺序，而反过来的话同一条语句的两种写法会比不出差异。</summary>
    void EmitWhere(SqlWriter writer, SqlExpression? joined, SqlExpression? filter)
    {
        if (joined is null && filter is null)
            return;

        writer.Append(" WHERE ");

        if (joined is null)
            EmitExpression(filter!, writer);
        else if (filter is null)
            EmitExpression(joined, writer);
        else
            EmitExpression(new SqlBinaryExpression(BinaryOperator.And, joined, filter), writer);
    }

    // ── 版式 ──────────────────────────────────────────────────────────

    protected void EmitProjections(IReadOnlyList<Projection> columns, SqlWriter writer)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            var projection = columns[i];

            EmitExpression(projection.Node, writer);

            // 只有计算列才需要别名：直接列的名字对物化没有意义（列序才是真相，§10.3），
            // 多打一个 AS 只会把黄金样本搞脏。
            if (projection.Alias is { Length: > 0 } alias && projection.Node is not ColumnExpression)
            {
                writer.Append(" AS ");
                EmitIdentifier(writer, alias);
            }
        }
    }

    protected void EmitJoins(IReadOnlyList<JoinClause> joins, SqlWriter writer)
    {
        foreach (var join in joins)
        {
            writer.Append(' ');
            writer.Append(join.Kind switch
            {
                JoinKind.Inner => "INNER JOIN ",
                JoinKind.Left => "LEFT JOIN ",
                JoinKind.Right => "RIGHT JOIN ",
                JoinKind.Full => "FULL JOIN ",
                JoinKind.Cross => "CROSS JOIN ",
                _ => throw new FastSqlException($"{Kind} 未知连接类型 {join.Kind}"),
            });

            EmitSource(join.Source, writer);

            if (join.On is { } on)
            {
                writer.Append(" ON ");
                EmitExpression(on, writer);
            }
            else if (join.Kind != JoinKind.Cross)
            {
                // 无 ON 的非交叉连接在哪家都是语法错误或笛卡尔积，不能悄悄放行。
                throw new FastSqlException($"{join.Kind} JOIN {join.Source.Ref.Alias} 缺少 ON 条件");
            }
        }
    }

    protected void EmitSource(TableSource source, SqlWriter writer)
    {
        switch (source)
        {
            case SourceTable table:
                EmitTableName(writer, table.Ref.Table);
                break;

            case SourceSubquery subquery:
                writer.Append('(');
                Emit(subquery.Query, writer);
                writer.Append(") ");
                break;

            case SourceFunction function:
                writer.Append(function.Name).Append('(');

                for (var i = 0; i < function.Arguments.Count; i++)
                {
                    if (i > 0)
                        writer.Append(", ");

                    EmitExpression(function.Arguments[i], writer);
                }

                writer.Append(')');
                break;

            case SourceRows rowSet:
                writer.Append("(VALUES ");

                for (var row = 0; row < rowSet.Rows.Count; row++)
                {
                    if (row > 0)
                        writer.Append(", ");

                    writer.Append('(');
                    EmitList(rowSet.Rows[row], writer);
                    writer.Append(')');
                }

                writer.Append(')');
                break;

            default:
                throw new FastSqlException($"{Kind} 未知表源形态 {source.GetType().Name}");
        }

        writer.Append(SourceAliasKeyword);
        EmitIdentifier(writer, source.Ref.Alias);

        // 行集的列名只有这一处来源：库不看 VALUES 里的参数叫什么，只看这份清单，
        // 而连接条件与 SET 右侧都按名字引它 —— 清单与 <see cref="SourceRows.Rows"/> 不同序就是静默串列。
        if (source is SourceRows rows)
        {
            writer.Append(" (");
            EmitIdentifiers(rows.Columns, writer);
            writer.Append(')');
        }
    }

    /// <summary>子句自带前导空格：<c>Emit</c> 的各个环节（FROM/JOIN/WHERE…）都不以空格收尾，
    /// 少打一个就会得到 <c>[t0]ORDER BY</c> 这种「看着像 SQL、解析器不认」的文本。</summary>
    void EmitOrdering(IReadOnlyList<OrderExpression> ordering, SqlWriter writer)
    {
        writer.Append(" ORDER BY ");

        for (var i = 0; i < ordering.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitExpression(ordering[i].Node, writer);
            writer.Append(ordering[i].Descending ? " DESC" : " ASC");
        }
    }

    /// <summary>§9.4：<c>Skip/Take</c> 无 <c>OrderBy</c> 时，抛错或补主键序由上游策略决定；
    /// 走到这里说明策略是 <c>Allow</c>，那么要求 ORDER BY 的库必须补一个占位序，否则文本直接语法错。</summary>
    protected void EmitPagingOrdering(SqlWriter writer, SelectStatement select)
    {
        if (select.OrderBy.Count > 0 || !select.RequireOrderingForPaging || !PagingRequiresOrdering)
            return;

        writer.Append(" ORDER BY ");
        EmitOrderingPlaceholder(writer);
    }

    protected virtual void EmitOrderingPlaceholder(SqlWriter writer) => writer.Append("(SELECT NULL)");

    protected void EmitList(IReadOnlyList<SqlExpression> nodes, SqlWriter writer)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitExpression(nodes[i], writer);
        }
    }

    void EmitIdentifiers(IReadOnlyList<ColumnContract> columns, SqlWriter writer)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitIdentifier(writer, columns[i].ColumnName);
        }
    }

    void EmitInValues(IReadOnlyList<SqlExpression> values, SqlWriter writer)
    {
        writer.Append('(');

        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
                writer.Append(", ");

            EmitExpression(values[i], writer);
        }

        writer.Append(')');
    }

    // ── 表达式 ────────────────────────────────────────────────────────

    /// <summary>发射前的最后一道方言改写机会：把 AST 节点换成另一颗再交给 switch。
    /// 默认原样返回；<b>唯一一颗覆写者是 <c>SqliteDialect</c></b>——那一家把比较表达式里的
    /// <see cref="ParameterValue"/>（decimal 值）包一层 <c>CAST(@p AS NUMERIC)</c>，
    /// 因为本家把定点存成 TEXT 而 SUM/AVG 返回 INTEGER，存储类排序 INTEGER &lt; TEXT 会让比较静默失败。
    /// 之所以是"发射前"而不是"翻译期"：CAST 是 SQL 文本层的事，AST 在翻译期应当保持 provider 无关。</summary>
    protected virtual SqlExpression PrepareForEmission(SqlExpression node) => node;

    protected virtual void EmitExpression(SqlExpression node, SqlWriter writer)
    {
        node = PrepareForEmission(node);

        switch (node)
        {
            case ColumnExpression column:
                EmitColumn(column, writer);
                break;

            case ParameterValue parameter:
                EmitParameter(parameter, writer);
                break;

            case InlineLiteral literal:
                writer.Append(literal.Sql);
                break;

            case SqlBinaryExpression binary:
                EmitBinary(binary, writer);
                break;

            case SqlUnaryExpression unary:
                EmitUnary(unary, writer);
                break;

            case FunctionExpression function:
                EmitFunction(function, writer);
                break;

            case CastExpression cast:
                writer.Append("CAST(");
                EmitExpression(cast.Operand, writer);
                writer.Append(" AS ").Append(cast.StoreType).Append(')');
                break;

            case InExpression inExpr:
                EmitExpression(inExpr.Target, writer);
                writer.Append(inExpr.Negated ? " NOT IN " : " IN ");

                if (inExpr.Subquery is { } sub)
                {
                    writer.Append('(');
                    Emit(sub, writer);
                    writer.Append(')');
                }
                else
                {
                    EmitInValues(inExpr.Values!, writer);
                }

                break;

            case ExistsExpression exists:
                writer.Append(exists.Negated ? "NOT EXISTS (" : "EXISTS (");
                Emit(exists.Query, writer);
                writer.Append(')');
                break;

            case ScalarSubqueryExpression scalar:
                writer.Append('(');
                Emit(scalar.Query, writer);
                writer.Append(')');
                break;

            case CaseExpression @case:
                EmitCase(@case, writer);
                break;

            case RawExpression raw:
                writer.Append(raw.Sql);
                break;

            case StarExpression star:
                if (star.Table is { } table)
                {
                    EmitIdentifier(writer, table.Alias);
                    writer.Append('.');
                }

                writer.Append('*');
                break;

            default:
                throw new FastSqlException($"{Kind} 未知表达式节点 {node.GetType().Name}");
        }
    }

    protected virtual void EmitColumn(ColumnExpression column, SqlWriter writer)
    {
        EmitIdentifier(writer, column.Table.Alias);
        writer.Append('.');
        EmitIdentifier(writer, column.Column.ColumnName);
    }

    /// <summary>参数化的唯一收口：名字由 writer 发号，形状由 <see cref="IValueBinder"/> 决定（§4）。
    /// 谁在这里自己 <c>AddParameter</c>，谁就会造出「文本里有 @p3、集合里没有 p3」这种只能执行期炸的 SQL。</summary>
    protected virtual void EmitParameter(ParameterValue parameter, SqlWriter writer)
        => writer.AppendParameter(Values.CreateParameter(writer.NextParameterName(), parameter.Value, parameter.Hint));

    void EmitCase(CaseExpression @case, SqlWriter writer)
    {
        writer.Append("CASE");

        if (@case.Operand is { } operand)
        {
            writer.Append(' ');
            EmitExpression(operand, writer);
        }

        foreach (var when in @case.Whens)
        {
            writer.Append(" WHEN ");
            EmitExpression(when.When, writer);
            writer.Append(" THEN ");
            EmitExpression(when.Then, writer);
        }

        if (@case.Else is { } @else)
        {
            writer.Append(" ELSE ");
            EmitExpression(@else, writer);
        }

        writer.Append(" END");
    }

    void EmitBinary(SqlBinaryExpression binary, SqlWriter writer)
    {
        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            EmitLogical(binary, writer);
            return;
        }

        if (binary.Operator == BinaryOperator.Like)
        {
            EmitOperand(binary.Left, binary.Operator, writer);
            writer.Append(" LIKE ");
            EmitOperand(binary.Right, binary.Operator, writer, isRight: true);

            // 转义符由翻译器烘进模式串（§5.7），这里必须把它一起写出来，否则 '\' 变成普通字符。
            writer.Append(" ESCAPE '").Append(Expressions.LikeEscapeCharacter).Append('\'');
            return;
        }

        EmitOperand(binary.Left, binary.Operator, writer);
        writer.Append(' ').Append(OperatorText(binary.Operator)).Append(' ');
        EmitOperand(binary.Right, binary.Operator, writer, isRight: true);
    }

    /// <summary>同种连接词不加括号（翻译器已把 AND/OR 折成右倾扁平链，§13.2），只在混嵌时加：
    /// 这样黄金样本读起来跟人手写的一样。</summary>
    void EmitLogical(SqlBinaryExpression binary, SqlWriter writer)
    {
        EmitLogicalChild(binary.Left, binary.Operator, writer);

        writer.Append(binary.Operator == BinaryOperator.And ? " AND " : " OR ");

        EmitLogicalChild(binary.Right, binary.Operator, writer);
    }

    void EmitLogicalChild(SqlExpression child, BinaryOperator parent, SqlWriter writer)
    {
        var needsParenthesis = child is SqlBinaryExpression nested
                               && nested.Operator is BinaryOperator.And or BinaryOperator.Or
                               && nested.Operator != parent;

        if (needsParenthesis)
            writer.Append('(');

        EmitExpression(child, writer);

        if (needsParenthesis)
            writer.Append(')');
    }

    void EmitOperand(SqlExpression operand, BinaryOperator parent, SqlWriter writer, bool isRight = false)
    {
        if (operand is SqlBinaryExpression nested && NeedsParenthesis(nested.Operator, parent, isRight))
        {
            writer.Append('(');
            EmitExpression(nested, writer);
            writer.Append(')');
            return;
        }

        EmitExpression(operand, writer);
    }

    /// <summary>优先级更低才加括号；同级只有右操作数需要，因为 SQL 全部左结合，
    /// 而减法/除法不满足结合律（<c>a-(b-c)</c> 不加括号就变成 <c>a-b-c</c>）。</summary>
    static bool NeedsParenthesis(BinaryOperator inner, BinaryOperator outer, bool isRight)
        => Precedence(inner) < Precedence(outer)
           || (isRight && Precedence(inner) == Precedence(outer)
                          && outer is BinaryOperator.Subtract or BinaryOperator.Divide or BinaryOperator.Modulo);

    /// <summary>优先级更低才加括号；同级只有右操作数需要，因为 SQL 全部左结合，
    /// 而减法/除法不满足结合律（<c>a-(b-c)</c> 不加括号就变成 <c>a-b-c</c>）。
    /// <b>数字越大越紧</b>，所以整张表是从 <c>AND/OR</c> 往上排的。</summary>
    static int Precedence(BinaryOperator op) => op switch
    {
        BinaryOperator.And or BinaryOperator.Or => 1,
        // <c>IS DISTINCT FROM</c> 排在比较符<b>之下</b>（更松）：<c>a = b IS DISTINCT FROM c</c> 读作
        // <c>(a = b) IS DISTINCT FROM c</c>。出处是 PG 手册 4.1.6 的 Table 4.2（<c>&lt; &gt; = &lt;= &gt;= &lt;&gt;</c>
        // 那一行在 <c>IS ISNULL NOTNULL</c> 之上，表是"由高到低"排的），文法侧对应
        // <c>distinct_expr: a_expr_compare IS NOT DISTINCT FROM a_expr_compare</c> 那条产生式 —— 左右操作数
        // 都只吃到比较符那一层。<b>9.5 之前这一档是反的</b>（手册 4.1.6 末尾那条 Note：9.5 起才改，
        // "IS tests used to have higher priority"），而 §9.1 记的金仓 V8 内核是 9.2 一线，
        // 于是它落在旧规则那一侧的可能性是<em>真的</em> —— 这一档两家可能不同形，未实测，记在 §14。
        // 今天它谁都碰不到：合并语句的守卫把 <c>IS DISTINCT FROM</c> 直接挂在 <c>AND/OR</c> 下面，
        // 左右操作数都是列与参数，所以改这个数不动任何一份黄金样本（跑整套离线套件已核）。
        BinaryOperator.IsDistinctFrom => 2,
        BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan
            or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.Like => 3,
        BinaryOperator.BitwiseOr => 4,
        BinaryOperator.BitwiseAnd => 5,
        BinaryOperator.Add or BinaryOperator.Subtract => 6,
        _ => 7,
    };

    static string OperatorText(BinaryOperator op) => op switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.IsDistinctFrom => "IS DISTINCT FROM",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.BitwiseOr => "|",
        _ => throw new FastSqlException("逻辑运算符不该走到 OperatorText：" + op),
    };

    void EmitUnary(SqlUnaryExpression unary, SqlWriter writer)
    {
        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                writer.Append("NOT ");
                EmitBooleanOperand(unary.Operand, writer);
                break;

            case UnaryOperator.Negate:
                writer.Append('-');
                EmitExpression(unary.Operand, writer);
                break;

            case UnaryOperator.IsNull:
                EmitComparisonOperand(unary.Operand, writer);
                writer.Append(" IS NULL");
                break;

            case UnaryOperator.IsNotNull:
                EmitComparisonOperand(unary.Operand, writer);
                writer.Append(" IS NOT NULL");
                break;

            case UnaryOperator.IsTrue:
                EmitComparisonOperand(unary.Operand, writer);
                EmitBooleanEquality(writer, value: true);
                break;

            case UnaryOperator.IsFalse:
                EmitComparisonOperand(unary.Operand, writer);
                EmitBooleanEquality(writer, value: false);
                break;
        }
    }

    /// <summary>PG 系有 <c>IS TRUE</c>，T-SQL 与 Oracle 系没有，只能比 <c>= 1</c>/<c>= 0</c>；
    /// 可空列上 <c>= 0</c> 匹配不到 NULL，这是 §10.5 那类事故的发生地，所以覆写点留在这里。</summary>
    protected virtual void EmitBooleanEquality(SqlWriter writer, bool value)
        => writer.Append(value ? " = 1" : " = 0");

    void EmitBooleanOperand(SqlExpression operand, SqlWriter writer)
    {
        var needsParenthesis = operand is SqlBinaryExpression nested
                               && nested.Operator is BinaryOperator.And or BinaryOperator.Or;

        if (needsParenthesis)
            writer.Append('(');

        EmitExpression(operand, writer);

        if (needsParenthesis)
            writer.Append(')');
    }

    void EmitComparisonOperand(SqlExpression operand, SqlWriter writer)
    {
        if (operand is SqlBinaryExpression { Operator: BinaryOperator.And or BinaryOperator.Or })
        {
            writer.Append('(');
            EmitExpression(operand, writer);
            writer.Append(')');
            return;
        }

        EmitExpression(operand, writer);
    }

    /// <summary>在一个私有文本上渲染子表达式，再回填到父 writer。参数进同一张表，编号也自然连续
    /// （编号长在那张表上，见 <see cref="SqlWriter.NextParameterName"/>）——两条分岔共用一份参数集合，
    /// 这是 <see cref="SqlWriter.Fork"/> 存在的唯一理由。</summary>
    string Sub(SqlWriter writer, SqlExpression node)
    {
        var forked = writer.Fork();

        EmitExpression(node, forked);

        return forked.ToString();
    }

    /// <summary>手写循环而不是 LINQ：闭包会捕获 <see cref="SqlWriter"/>，而它是 ref struct，捕获即编译错误。</summary>
    string[] RenderArguments(IReadOnlyList<SqlExpression> arguments, SqlWriter writer)
    {
        var rendered = new string[arguments.Count];

        for (var i = 0; i < rendered.Length; i++)
            rendered[i] = Sub(writer, arguments[i]);

        return rendered;
    }

    /// <summary>函数模板 → 文本。<b>位置形占位符那一家在这里多一道核对</b>，而这一道核对是这一格第一次成为必需：
    /// <see cref="RenderArguments"/> 按<em>实参</em>次序把参数登记进那张共享表，<c>string.Format</c> 再按
    /// <em>模板</em>次序把渲染好的片段放进去 —— 两个次序一旦不一致，带名字的方言无感（<c>@p3</c> 跟着文本走，
    /// 驱动按名字绑），位置形那一家是<em>静默错配</em>（<c>?</c> 按第几颗绑，而文本里的第几颗与参数表里的
    /// 第几颗不再是一回事）。所以这一支既不许实参重复出现，也不许编号倒序 ——
    /// 两种都是名字式方言天天在用而位置式用不了的形状：<c>T-SQL</c> 的
    /// <c>CHARINDEX({1}, {0})</c> 是倒序，<c>SUBSTRING({0}, {1} + 1, LEN({0}))</c> 是重复。
    /// <b>抛而不是发</b>：错配的代价是值写到隔壁列上，那是 §1.2 原则 4 的反面，也是 §5.8 那句"绝不静默"
    /// 在本格唯一可能兑现的形式 —— 这一格没有第二次机会被人在评审里看见，它只在批量的第 3 千行上红。
    /// 判据取 <see cref="ParameterPrefix"/> 那颗字符而不新立一位布尔：位置形<em>就是</em> <c>?</c>，
    /// 再挂一颗开关只给"两者各说一句话"留了位置。</summary>
    protected virtual void EmitFunction(FunctionExpression function, SqlWriter writer)
    {
        if (ParameterPrefix == '?' && !PlaceholdersAscendOnce(function.Template, function.Arguments.Count))
            throw new FastSqlException(
                $"{Kind} 的参数占位符是位置形 '?'，而函数模板 {function.Template} 的实参编号不是按 0,1,2… 各出现一次："
                + "文本里第几颗 '?' 与参数表里第几颗会错位，绑定的后果是值写到隔壁列上。"
                + "位置式方言要改的是本家 IExpressionRules 里的那一条模板（每个实参恰好出现一次、且按序），"
                + "不是把值内联成字面量绕过去（§12.7 的共享计划与 §5.8 的内联纪律同一格）。");

        writer.Append(Fn.Apply(function.Template, RenderArguments(function.Arguments, writer)));
    }

    /// <summary>扫模板里的 <c>{n}</c>：必须恰好是 <c>0,1,2,…,argumentCount-1</c> 且各出现一次。
    /// <c>{{</c> 与 <c>}}</c> 是 <c>string.Format</c> 的转义花括号，跳过；本仓库的模板里没有带格式说明符的写法，
    /// 所以遇到非数字的花括号不当作实参引用。</summary>
    static bool PlaceholdersAscendOnce(string template, int argumentCount)
    {
        var expected = 0;

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
                continue;

            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                i++;
                continue;
            }

            var at = i + 1;

            while (at < template.Length && char.IsAsciiDigit(template[at]))
                at++;

            if (at == i + 1)
                continue;

            if (int.Parse(template.AsSpan(i + 1, at - i - 1)) != expected)
                return false;

            expected++;
            i = at - 1;
        }

        return expected == argumentCount;
    }

    // ── 标识符与类型 ──────────────────────────────────────────────────

    public virtual void EmitIdentifier(SqlWriter writer, string identifier) => writer.AppendIdentifier(Identifiers, identifier);

    /// <summary>默认给写语句的目标沿用 <c>t0</c> —— 与 <see cref="EmitUpdateTarget"/> 那句"ANSI 写法"是同一件事的两半：
    /// 目标起了别名，引用就得用那个别名。Oracle 血统两半一起换（不能起别名 ⇒ 前缀只能用表名本身），
    /// 所以这一口与 <see cref="EmitUpdateTarget"/> 是成对覆写的，见 <see cref="ISqlDialect.WriteTargetAlias"/>。</summary>
    public virtual string WriteTargetAlias(TableInfo table) => FastSqlOrm.AliasAt(0);

    public virtual void EmitTableName(SqlWriter writer, TableInfo table)
    {
        if (table.Schema is { Length: > 0 } schema)
        {
            EmitIdentifier(writer, schema);
            writer.Append('.');
        }

        EmitIdentifier(writer, table.Name);
    }

    public abstract string GetStoreType(ColumnContract column);
}
