using QuickSql.Metadata;
using QuickSql.Providers;
using QuickSql.Query.Model;
using QuickSql.Sql;
using QuickSql.Sql.Ast;

namespace QuickSql.Query;

/// <summary>QueryModel → SelectStatement（§3 的第 2→3 层）。这一层只做版式：
/// 选列展开、别名、AND 折叠、聚合剥序、分页落位。<b>不出现任何库名语法</b>，
/// 方言文本全部留给 <see cref="ISqlDialect"/>。</summary>
public sealed class QueryCompiler(IDbProvider provider)
{
    public IDbProvider Provider { get; } = provider;

    public SelectStatement Compile(QueryModel model)
    {
        // 一座集群一句 SQL：这一道门在分表那道门之前，因为"跨库的连接"要拒的是整句，
        // 而分片剪枝只改根表位的名字——两道的判据不一样，先判那个更根本的。
        EnsureSingleCluster(model);

        // 分表实体在"没选片"与"选了多片"这两种情况下要发的是完全不同的句子，所以先过这一道门。
        // 派生表与覆盖源那两种来路不在这里插手：内层自己会回到这个门口，外层只剩一个表位。
        if (model.Root.Table.Split is not null && model.DerivedFrom is null && model.Source is null)
            return CompileSplit(model);

        // 但"派生表"那一格留一条同判据的门：count 是库自己包的内层（外层只剩一颗 COUNT(*)，UNION ALL 活在内层），
        // 所以 CompileSplit 里那句聚合检查打不到这一份模型。放行它的后果是"CountAsync 抛，而 Take(10).CountAsync() 给数"——
        // 那是实现顺序决定的形状，不是裁决。见 <see cref="CrossShardAggregate"/>。
        if (model.DerivedFrom is not null && model.Root.Table.Split is not null && model.Shards is { Count: > 1 } derivedShards)
            throw CrossShardAggregate(derivedShards.Count);

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

        if (UsesIncludeJoins(model))
        {
            var includeJoins = new List<JoinClause>(model.Includes.Count);
            var includeColumns = new List<Projection>();
            var offset = columns.Count;

            CollectIncludeColumns(model.Includes, model.Joins, includeJoins, includeColumns, ref offset);

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

    /// <summary>Include 的 LEFT JOIN 这一版到底加不加（编译口与物化口的同一个判据）：
    /// 只有"实体投影 + 没拆成第二条查询"才是真加进这一句 SQL。<c>SplitQuery</c> 那条路上导航是主查询之后
    /// 另发的 SELECT，跨集群因此天然成立（那一颗的根表位是子实体自己，它自己去算自己那一座）。</summary>
    static bool UsesIncludeJoins(QueryModel model)
        => model.Includes.Count > 0 && model.Plan is EntityProjection && !model.SplitQuery;

    /// <summary>一句 SELECT 只能说一座集群（§16.4）。判据只看<em>真的进这一句</em>的那些表位：
    /// 根表位与显式 <c>JOIN</c> 的表位（都在 <see cref="QueryModel.Tables"/> 里）、以及在这一版落成
    /// LEFT JOIN 的那些 Include 目标。<b>拒的不是"模型里出现了两座集群"，而是"这一句要两座集群才答得出"</b>——
    /// 后者才是没有出路的那件事：一条跨库的 <c>JOIN</c> 在任何一家都不是能用一句 SQL 表达的东西，
    /// 而"发两条、在内存里拼"要的是一套本家没有的东西（两侧的行序、分页单位、聚合归并都得重定）。
    /// 规格 §16.4① 对"跨集群读"点的正是这一格。</summary>
    /// <remarks>内层的派生表不在这里查：它是同一句 SQL 的一部分，而它自己会回到 <see cref="Compile"/>
    /// 这个门口（内层自己的表位由内层那一趟判）。<see cref="QueryModel.Source"/> 那一段用户自己写的
    /// 文本更无从判起——它连表名都不告诉库，判据在 <c>RawSql</c> 那一格已经说过"这里没有模型"。</remarks>
    static void EnsureSingleCluster(QueryModel model)
    {
        string? cluster = null;
        TableInfo? first = null;

        foreach (var table in model.Tables)
            if (Note(ref cluster, ref first, table.Table))
                throw CrossCluster(first!, table.Table);

        if (!UsesIncludeJoins(model))
            return;

        foreach (var include in FlattenIncludes(model.Includes))
            if (Note(ref cluster, ref first, include.TargetTable.Table))
                throw CrossCluster(first!, include.TargetTable.Table);
    }

    /// <summary>把一座集群记进那一只"已经说过谁"的格子：与已知的那座相同返回 false（继续），
    /// 不同返回 true（调用方去造那句拒绝）。第一座总是 false。<b>名字写成 <c>Note</c> 而不是
    /// <c>CheckCluster</c> 是为了让调用点读起来是"记一笔，记不下就拒"</b>，而不是"这里有一处校验"。</summary>
    static bool Note(ref string? cluster, ref TableInfo? first, TableInfo table)
    {
        var named = DataSourceRouting.ClusterOf(table);

        if (cluster is null)
        {
            cluster = named;
            first = table;
            return false;
        }

        return !string.Equals(cluster, named, StringComparison.Ordinal);
    }

    static IReadOnlyList<IncludeNode> FlattenIncludes(IReadOnlyList<IncludeNode> includes)
    {
        var flattened = new List<IncludeNode>();

        void Walk(IReadOnlyList<IncludeNode> level)
        {
            foreach (var include in level)
            {
                flattened.Add(include);

                if (include.Children.Count > 0)
                    Walk(include.Children);
            }
        }

        Walk(includes);
        return flattened;
    }

    /// <summary>那句拒绝给两条走得通的路：<b>把两张表放进同一座集群</b>（声明在同一处，改一行），
    /// 或<b>让它们各查各的、在内存里缝</b>（集合导航的 <c>IncludeMany</c> 那一族本来就是两条 SELECT，
    /// 跨集群天然成立）。不给"库自己发两条再拼"的第三条路：那等于悄悄改了这条链的语义。</summary>
    static QuickSqlException CrossCluster(TableInfo first, TableInfo other)
        => new($"这一句要同时读集群 {DataSourceRouting.ClusterOf(first)} 的 {first.Name} 与集群 {DataSourceRouting.ClusterOf(other)} 的 {other.Name}：跨集群的连接（JOIN / 落成 LEFT JOIN 的 Include）不是一句 SQL 能说的，规格 §16.4① 没有裁它该怎么拼。" +
               "两条出路：把两个实体声明到同一座集群，或改用内存缝合的那一族（集合导航 Include 走第二条 SELECT，跨集群天然成立）。");

    /// <summary>分表实体的读侧形状（规格 §16.4）：一片一条 SELECT，串成 <c>UNION ALL</c> 挂在外层唯一的表位，
    /// 序与分页只长在外层。
    /// <b>单片时不包这一层</b>：根表位直接换成那张物理表，走原来那条路，于是"这个月只查一张表"的句子
    /// 与一张不分表的表<em>逐字同形</em>——金标基线因此不必为单片另写一份，而"包了一层没用的派生表"
    /// 这种性能税也收不到。</summary>
    SelectStatement CompileSplit(QueryModel model)
    {
        var shards = model.Shards
                     ?? throw new QuickSqlException($"分表实体 {model.Root.Contract?.ClrType.Name} 没选片就要读：请先 SplitTables(起, 止)（时间档）或 SplitTables(桶号…)（哈希档）。本家不给“默认扫全片”的退路，因为现存的分片有几张只有库知道。");

        if (shards.Count == 1)
            return Compile(Shard(model, shards[0]) with { Shards = null });

        // 多片上明确不接的三件事，共同点是"跨片之后要变的是语义，而变法是裁决不是实现"。
        // 聚合那一档尤其要写清：库能把 COUNT(*) 打在 UNION ALL 外面（那是对的），但 AVG 与"分组之后再数有几组"
        // 就不是包一层能救的，而 SqlSugar 那一族的形态是把 GroupBy 原样复制进每一片（每片各分各的组，
        // 键不合并）。本家宁可在这里停住，也不要一份看着像结果的错数。
        if (model.Plan is AggregateProjection || model.GroupBy.Count > 0 || model.Having is not null)
            throw CrossShardAggregate(shards.Count);

        if (model.Joins.Count > 0 || model.Includes.Count > 0 || model.IncludeManys.Count > 0)
            throw new QuickSqlException($"分表实体跨 {shards.Count} 片不做连接与急切加载（JOIN / Include）：每片各连一遍会把“一条主行配几份子行”变成片内的事，而那不是同一次查询。");

        var branches = new SelectStatement[shards.Count];

        for (var i = 0; i < shards.Count; i++)
            branches[i] = Compile(Shard(model, shards[i]) with { Shards = null, Ordering = [], Offset = null, Limit = null });

        return new SelectStatement
        {
            // 外层只端一样东西：* 。片内的选列形状与单片时完全一致（那一份是从同一个模型算出来的），
            // 所以物化那边按位置读的列序不用为跨片重定。
            Columns = [new Projection(new StarExpression(null), null)],
            From = new SourceUnion(model.Root, branches),
            OrderBy = model.StripsOrdering ? [] : [.. model.Ordering],
            Offset = model.Offset is { } offset ? new ParameterValue(offset, null) : null,
            Limit = model.Limit is { } limit ? new ParameterValue(limit, null) : null,
            Distinct = model.Distinct,
        };
    }

    /// <summary>把根表位换成一张具体的分片表。别名<em>不动</em>（还是 <c>t0</c>），所以谓词、选列、序
    /// 那些引用着根表位的节点一个都不用重写——发出来的列前缀照旧成立。</summary>
    static QueryModel Shard(QueryModel model, TableInfo shard)
        => model with { Tables = [model.Root with { Table = shard }, .. model.Tables.Skip(1)] };

    /// <summary>跨片聚合的那一句拒绝，两个入口共用（<see cref="CompileSplit"/> 直投、<see cref="Compile"/>
    /// 那条 count 自己包内层的侧门）。<b>消息要给一条走得通的路</b>：分页要的是"有没有下一页"而不是总数，
    /// 那一档（§5.12 的 <c>SkipTotal</c>）多取一行就够，跨片成立，所以把它的名字写在报错里。</summary>
    static QuickSqlException CrossShardAggregate(int shardCount)
        => new($"分表实体跨 {shardCount} 片不做聚合/分组（COUNT、SUM、GROUP BY、HAVING）：那要先定“每片各算再怎么合”，规格 §5.12 与 §16.4 都还没裁。" +
               "单片不受这一格限制——把范围收到一片就是一句普通的聚合；分页只要“有没有下一页”而不必总数时，传 PagedOptions { CountMethod = CountMethod.SkipTotal }。");

    IReadOnlyList<Projection> Columns(QueryModel model) => model.Plan switch
    {
        EntityProjection entity => EntityColumns(model.Tables[entity.TableIndex]),
        ValueProjection value => [new Projection(value.Node, null)],
        ObjectProjection obj => [.. obj.Slots.Select(slot => new Projection(slot.Node, slot.Name))],
        // GROUP BY 的键必须出现在选列里，否则生成的 SQL 在任何一家都是错的。
        AggregateProjection aggregate when model.GroupBy.Count > 0 =>
            [.. model.GroupBy.Select(key => new Projection(key, null)), new Projection(Aggregate(aggregate), null)],
        AggregateProjection aggregate => [new Projection(Aggregate(aggregate), null)],
        _ => throw new QuickSqlException("未知投影形态：" + model.Plan.GetType().Name),
    };

    static IReadOnlyList<Projection> EntityColumns(TableRef table)
    {
        var contract = table.Contract ?? throw new QuickSqlException($"表位 {table.Alias} 没有实体契约，无法展开选列");

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
    /// 列偏移按遍历顺序递增，物化器按同一顺序读取。
    /// <b>已经作为真 JOIN 在场的那一颗不再追加</b>（<paramref name="joined"/>）：这一根边被 §5.2 的自动联表
    /// 收养过时，那条 <c>LEFT JOIN</c> 已经在 <see cref="QueryModel.Joins"/> 里（它比这一颗活得久 ——
    /// 投影、拆查与 count 内层都带着它），这里再加一条就是把同一张表联第二遍。扩展列照旧发：
    /// 物化按位置读（<see cref="IncludeNode.ColumnOffset"/>），读不出这一列的表位是哪一颗 JOIN 送进来的。</summary>
    static void CollectIncludeColumns(
        IReadOnlyList<IncludeNode> includes,
        IReadOnlyList<JoinClause> joined,
        List<JoinClause> joins,
        List<Projection> columns,
        ref int offset)
    {
        foreach (var include in includes)
        {
            include.ColumnOffset = offset;

            if (!IsJoined(joined, include.TargetTable))
                joins.Add(new JoinClause(JoinKind.Left, new SourceTable(include.TargetTable), include.OnCondition));

            var contract = include.TargetTable.Contract!;

            for (var i = 0; i < contract.Columns.Length; i++)
                columns.Add(new Projection(new ColumnExpression(include.TargetTable, contract.Columns[i]), null));

            offset += contract.Columns.Length;

            if (include.Children.Count > 0)
                CollectIncludeColumns(include.Children, joined, joins, columns, ref offset);
        }
    }

    /// <summary>那颗表位是不是已经在 FROM 里联过。判据取 <see cref="TableRef.Index"/> 而不是实例：
    /// 收养走的是同一颗实例，而分表那条换实例的路（<see cref="Shard"/>）本来就在"有 JOIN 或有 Include 就拒"
    /// 那一格外面。</summary>
    static bool IsJoined(IReadOnlyList<JoinClause> joined, TableRef table)
    {
        foreach (var join in joined)
            if (join.Source.Ref.Index == table.Index)
                return true;

        return false;
    }

    SqlExpression Aggregate(AggregateProjection aggregate)
    {
        // AggregateKind.Any 自存在性探针落地起不再有任何调用方递进来（QueryEngine.AnyAsync 发的是
        // 「SELECT 1 … 取第一行」，不经过聚合映射）。这一格留着 Any→COUNT 的映射只是把枚举语义配齐，
        // 公开枚举面上那颗成员删不删是 §5.12 的裁决，不在实现侧顺手做。
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
            throw new QuickSqlException($"{Provider.Kind} 未登记聚合函数 {Fn.Key(key, 1)}");

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

            // "内层还有一整句"的表源：那份文本也在同一条 StringBuilder 里落地，所以容量要一起算。
            // SourceUnion 是跨片读的那一层（每片一条 SELECT 串起来），一颗分支就是一整句 ——
            // 少了这一段，五片的句子会按一片的容量开，剩下的靠倍增重拷。
            length += from switch
            {
                SourceDerived derived => EstimateSqlLength(derived.Query),
                SourceRawText raw => raw.Sql.Length,
                SourceUnion union => SumBranches(union),
                _ => 0,
            };
        }

        foreach (var join in ast.Joins)
            length += join.Source.Ref.Table.Name.Length + join.Source.Ref.Alias.Length + 48;

        return length + ast.GroupBy.Count * 24 + ast.OrderBy.Count * 24 + (ast.Where is null ? 0 : 64);
    }

    /// <summary>逐支累加，再为每道接缝留 <c> UNION ALL </c> 的 11 个字符与外层那对括号。</summary>
    static int SumBranches(SourceUnion union)
    {
        var total = 2;

        foreach (var branch in union.Branches)
            total += (total == 2 ? 0 : 11) + EstimateSqlLength(branch);

        return total;
    }
}
