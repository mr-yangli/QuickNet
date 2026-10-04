using FastSql.Metadata;
using FastSql.Providers;
using FastSql.Query.Model;
using FastSql.Sql;
using FastSql.Sql.Ast;

namespace FastSql.Query;

/// <summary>QueryModel → SelectStatement（§3 的第 2→3 层）。这一层只做版式：
/// 选列展开、别名、AND 折叠、聚合剥序、分页落位。<b>不出现任何库名语法</b>，
/// 方言文本全部留给 <see cref="ISqlDialect"/>。</summary>
public sealed class QueryCompiler(IDbProvider provider)
{
    public IDbProvider Provider { get; } = provider;

    public SelectStatement Compile(QueryModel model)
    {
        // FROM 位有第三种来路：派生表（先编译内层，再挂成外层的唯一表位）、覆盖源（WithSql 的那一段文本），
        // 以及照旧的根表位。内层走的是同一个 Compile，所以内层的分页、Include、方言无关的剥序全部原样成立。
        var from = model.DerivedFrom is { } inner
            ? new SourceDerived(model.Root, Compile(inner))
            : model.Source ?? new SourceTable(model.Root);

        var where = Fold(model.Predicates);
        IReadOnlyList<OrderExpression> ordering = model.StripsOrdering ? [] : [.. model.Ordering];

        // Include 的 LEFT JOIN 追加在用户显式 JOIN 之后。列也按同一顺序展开：
        // 先根实体列，再按深度优先遍历 Include 树展开每颗子实体的列。
        var allJoins = model.Joins;
        var columns = Columns(model);

        if (model.Includes.Count > 0 && model.Plan is EntityProjection && !model.SplitQuery)
        {
            var includeJoins = new List<JoinClause>(model.Includes.Count);
            var includeColumns = new List<Projection>();
            var offset = columns.Count;

            CollectIncludeColumns(model.Includes, includeJoins, includeColumns, ref offset);

            allJoins = [.. model.Joins, .. includeJoins];
            columns = [.. columns, .. includeColumns];
        }

        return new SelectStatement
        {
            Columns = columns,
            From = from,
            Joins = allJoins,
            Where = where,
            GroupBy = model.GroupBy,
            Having = model.Having,
            OrderBy = ordering,
            Offset = model.Offset is { } offsetVal ? new ParameterValue(offsetVal, null) : null,
            Limit = model.Limit is { } limit ? new ParameterValue(limit, null) : null,
            Distinct = model.Distinct,
        };
    }

    IReadOnlyList<Projection> Columns(QueryModel model) => model.Plan switch
    {
        EntityProjection entity => EntityColumns(model.Tables[entity.TableIndex]),
        ValueProjection value => [new Projection(value.Node, null)],
        ObjectProjection obj => [.. obj.Slots.Select(slot => new Projection(slot.Node, slot.Name))],
        // GROUP BY 的键必须出现在选列里，否则生成的 SQL 在任何一家都是错的。
        AggregateProjection aggregate when model.GroupBy.Count > 0 =>
            [.. model.GroupBy.Select(key => new Projection(key, null)), new Projection(Aggregate(aggregate), null)],
        AggregateProjection aggregate => [new Projection(Aggregate(aggregate), null)],
        _ => throw new FastSqlException("未知投影形态：" + model.Plan.GetType().Name),
    };

    static IReadOnlyList<Projection> EntityColumns(TableRef table)
    {
        var contract = table.Contract ?? throw new FastSqlException($"表位 {table.Alias} 没有实体契约，无法展开选列");

        // 列序 = 契约列序（§10.3）。物化按位置读，所以这里绝不能按名字排序。
        var columns = contract.Columns;
        var projections = new Projection[columns.Length];

        // 手写循环而不是 [.. Select()]：那一串要先造闭包与迭代器、再长出一个没有初值的 List
        // （4→8→16→32 的倍增，每次连同旧内容一起拷），最后ToArray() 再拷一遍 —— 三段都为得到同一个数组。
        // 列数在契约里是已知的，所以一次分配就够。
        for (var i = 0; i < projections.Length; i++)
            projections[i] = new Projection(new ColumnExpression(table, columns[i]), null);

        return projections;
    }

    /// <summary>深度优先遍历 Include 树，为每个节点生成 LEFT JOIN 与扩展列。
    /// 列偏移按遍历顺序递增，物化器按同一顺序读取。</summary>
    static void CollectIncludeColumns(
        IReadOnlyList<IncludeNode> includes,
        List<JoinClause> joins,
        List<Projection> columns,
        ref int offset)
    {
        foreach (var include in includes)
        {
            include.ColumnOffset = offset;

            joins.Add(new JoinClause(JoinKind.Left, new SourceTable(include.TargetTable), include.OnCondition));

            var contract = include.TargetTable.Contract!;

            for (var i = 0; i < contract.Columns.Length; i++)
                columns.Add(new Projection(new ColumnExpression(include.TargetTable, contract.Columns[i]), null));

            offset += contract.Columns.Length;

            if (include.Children.Count > 0)
                CollectIncludeColumns(include.Children, joins, columns, ref offset);
        }
    }

    SqlExpression Aggregate(AggregateProjection aggregate)
    {
        var countLike = aggregate.Kind is AggregateKind.Count or AggregateKind.LongCount or AggregateKind.Any;

        var key = (countLike, aggregate.Distinct) switch
        {
            (true, true) => Fn.CountDistinct,
            (true, false) => Fn.Count,
            (false, true) => throw new UntranslatableExpressionException(aggregate.Kind.ToString(),
                $"DISTINCT {aggregate.Kind}(x) 暂未接入模板表，请改写为先 DISTINCT 子查询再 {aggregate.Kind}(x)"),
            (false, false) => aggregate.Kind switch
            {
                AggregateKind.Sum => Fn.Sum,
                AggregateKind.Average => Fn.Average,
                AggregateKind.Min => Fn.Min,
                AggregateKind.Max => Fn.Max,
                _ => throw new UntranslatableExpressionException(aggregate.Kind.ToString(), "该聚合当前无 SQL 对应（All 需要 EXISTS 反形）"),
            },
        };

        // 聚合永远是单参（COUNT 的参数是 * 或那一列），所以元数在这里补上。
        if (!Provider.Expressions.TryGetFunction(Fn.Key(key, 1), out var mapping))
            throw new FastSqlException($"{Provider.Kind} 未登记聚合函数 {Fn.Key(key, 1)}");

        // COUNT(*) 而非 COUNT(1)：两家优化器都直接认这个形态。
        var argument = countLike ? new StarExpression(null) : aggregate.Node;

        return new FunctionExpression(mapping.Template, [argument]);
    }

    public static SqlExpression? Fold(IReadOnlyList<SqlExpression> predicates)
    {
        SqlExpression? result = null;

        foreach (var predicate in predicates)
            result = result is null ? predicate : new SqlBinaryExpression(BinaryOperator.And, result, predicate);

        return result;
    }

    /// <summary>给发射缓冲区的一个容量估计。<b>为什么要估</b>：<c>StringBuilder</c> 默认 16 字符起步，
    /// 一条 545 字符的查询要沿 16→32→64→128→256→512 翻五倍，每翻一次连旧内容一起拷一遍 ——
    /// 那笔钱随列数长，所以在 §12.8 第 1 行里它记在"每列开销"而不是"每查询固定税"。
    /// 估多是白给一段缓冲、估少只是再翻一倍，两种都不改 SQL 文本，所以这里宁可取粗。</summary>
    public static int EstimateSqlLength(SelectStatement ast)
    {
        var length = 96;

        foreach (var column in ast.Columns)
            length += column.Node is ColumnExpression { Column.ColumnName.Length: var name, Table.Alias.Length: var alias }
                ? name + alias + 10
                : 32;

        if (ast.From is { } from)
        {
            length += from.Ref.Table.Name.Length + from.Ref.Alias.Length + 40;

            // 两种"内层还有一整句"的表源：那份文本也在同一条 StringBuilder 里落地，所以容量要一起算。
            length += from switch
            {
                SourceDerived derived => EstimateSqlLength(derived.Query),
                SourceRawText raw => raw.Sql.Length,
                _ => 0,
            };
        }

        foreach (var join in ast.Joins)
            length += join.Source.Ref.Table.Name.Length + join.Source.Ref.Alias.Length + 48;

        return length + ast.GroupBy.Count * 24 + ast.OrderBy.Count * 24 + (ast.Where is null ? 0 : 64);
    }
}
