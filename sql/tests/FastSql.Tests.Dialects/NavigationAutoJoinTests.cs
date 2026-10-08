using FastSql.Query;
using FastSql.TestInfra;
using FastSql.Translation;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>§5.2 的引用导航自动联表：投影/谓词/排序里读 <c>o.Customer.City</c> 这样的一格，库自己按契约里
/// 那根 to-one 边补一条 <c>LEFT JOIN</c>，调用方既不写 <c>Include</c> 也不写 join。
///
/// <para>这一档翻掉了 v0.x 记过的一条裁定（"不做隐式导航 join"），所以这一册钉的都是<em>语义位</em>而不是
/// 实现细节：发的是 LEFT 而不是 INNER（外格为空时主表行必须还在）、ON 用的是 Include 那一份同一个建造口
/// （一条导航全仓库只许有一个答案）、同一根边在一条链上只联一张（lambda 内两处、跨算子两处都算）、
/// 链分出的两个分支各拿各的清单，以及<em>没有</em>这一档的那些场合（集合导航、写侧、显式 join 的 ON）仍然当场拒。</para></summary>
public sealed class NavigationAutoJoinTests
{
    static FastSqlOrm orm() => SqlServer.Orm();

    [Fact]
    public void A_reference_navigation_read_in_a_projection_joins_its_target_table()
    {
        var sql = orm().Select<Order>()
            .Select(o => new { o.Id, City = o.Customer!.City })
            .ToSql();

        Golden.Verify("navigation-auto-join-projection", sql);

        Checks.Has("LEFT JOIN [FastSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Has("SELECT [t0].[Id], [t1].[City] FROM", sql);
    }

    [Fact]
    public void The_join_is_left_rather_than_inner_so_a_blank_edge_keeps_the_row()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.Customer!.City == "上海")
            .ToSql();

        // INNER 会让同一句在"外键为 NULL"与"外键指向已删的行"两种数据上少行，而读导航读出来的是 NULL ——
        // 与 Include 交回 null 同一件事，所以这一格发不出 INNER。
        Checks.Has("LEFT JOIN", sql);
        Checks.Absent("INNER JOIN", sql);
        Checks.Has("WHERE [t1].[City] = @p0", sql);
    }

    [Fact]
    public void A_reference_navigation_read_in_an_order_key_joins_it_too()
    {
        var sql = orm().Select<Order>()
            .OrderBy(o => o.Customer!.City)
            .ToSql();

        Checks.Has("LEFT JOIN [FastSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Has("ORDER BY [t1].[City] ASC", sql);
    }

    [Fact]
    public void A_two_level_read_lands_two_joins_each_keyed_to_its_own_host()
    {
        var sql = orm().Select<SelfRef>()
            .Where(s => s.Manager!.Manager!.ManagerId == 3)
            .ToSql();

        // 第二颗的 ON 挂在 t1 而不是 t0 上：链是从宿主那一格一级一级推出来的，写错一级就是另一条边。
        Checks.Has("LEFT JOIN [FastSqlSelfRef] AS [t1] ON [t0].[ManagerId] = [t1].[Id]", sql);
        Checks.Has("LEFT JOIN [FastSqlSelfRef] AS [t2] ON [t1].[ManagerId] = [t2].[Id]", sql);
        Checks.Has("WHERE [t2].[ManagerId] = @p0", sql);
    }

    [Fact]
    public void Reading_the_same_edge_twice_in_one_lambda_joins_it_once()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.Customer!.City == "上海" && o.Customer!.Name == "x")
            .ToSql();

        Assert.Equal(1, Checks.Occurrences(sql, "LEFT JOIN"));
        Checks.Has("WHERE [t1].[City] = @p0 AND [t1].[Name] = @p1", sql);
    }

    [Fact]
    public void An_edge_joined_by_an_earlier_operator_is_reused_by_the_later_one()
    {
        var sql = orm().Select<Order>()
            .Where(o => o.Customer!.City == "上海")
            .Select(o => new { o.Id, City = o.Customer!.City })
            .ToSql();

        // 没有链级那份清单的话，这里出的是第二张 [t2] —— 两处要一起改，这一条钉的就是那第二半。
        Assert.Equal(1, Checks.Occurrences(sql, "LEFT JOIN"));
        Checks.Has("SELECT [t0].[Id], [t1].[City] FROM [FastSqlOrder] AS [t0] LEFT JOIN [FastSqlCustomer] AS [t1]", sql);
        Checks.Has("WHERE [t1].[City] = @p0", sql);
    }

    [Fact]
    public void Grouping_and_having_over_a_navigation_key_join_before_the_group()
    {
        var sql = orm().Select<Order>()
            .GroupBy(o => o.Customer!.City)
            .Having(g => g.Count() > 1)
            .Select(g => new { g.Key, N = g.Count() })
            .ToSql();

        Checks.Has("LEFT JOIN [FastSqlCustomer] AS [t1] ON [t0].[CustomerId] = [t1].[Id]", sql);
        Checks.Has("GROUP BY [t1].[City] HAVING COUNT(*) > @p0", sql);
    }

    [Fact]
    public void The_auto_join_is_visible_in_the_explain_model_text()
    {
        var text = orm().Select<Order>().Where(o => o.Customer!.City == "上海").Explain().ModelText;

        // 它进的是模型的 Joins，不是编译期才补的那一层：所以"这一句联了谁"在 Explain 里就读得出来（§5.8 的可判性）。
        Checks.Has("TABLE t1 = dbo.FastSqlCustomer AS t1", text);
        Checks.Has("JOIN Left FastSqlCustomer AS t1 ON (t0.CustomerId = t1.Id)", text);
    }

    [Fact]
    public void A_paged_query_over_a_navigation_order_key_pages_after_the_join()
    {
        var sql = orm().Select<Order>()
            .OrderBy(o => o.Customer!.City)
            .Skip(10).Take(5)
            .ToSql();

        Checks.Has("LEFT JOIN", sql);
        Checks.Has("ORDER BY [t1].[City] ASC OFFSET @p0 ROWS FETCH NEXT @p1 ROWS ONLY", sql);
    }

    [Fact]
    public void A_navigation_read_inside_a_captured_subquery_joins_inside_that_subquery()
    {
        var customerIds = orm().Select<Order>()
            .Where(o => o.Customer!.City == "上海")
            .Select(o => o.CustomerId);

        var sql = orm().Select<Customer>().Where(c => customerIds.Contains(c.Id)).ToSql();

        // 联表进的是内层那一句的 FROM。落在外层的话外层引用一张它 FROM 里没有的表，而这条链的
        // StableKey 与 Explain 都会跟着外层走 —— 内层自成一个 QueryModel，账就记在自己头上。
        // 别名不写死号：子查询那一段的表位号由内层自己的基数推出来，它进第几个槽位不是这一条要钉的事。
        var at = sql.IndexOf("IN (SELECT", StringComparison.Ordinal);

        Assert.True(at > 0, $"这句里没有那个子查询：{sql}");

        var inner = sql[at..];

        Checks.Has("LEFT JOIN [FastSqlCustomer]", inner);
        Checks.Has("WHERE", inner);

        // 外层那半句一个字没多：FROM 只有一张表，没有 JOIN。
        Checks.Absent("JOIN", sql[..at]);
    }

    [Fact]
    public void A_navigation_read_on_a_guid_keyed_edge_uses_that_edges_own_columns()
    {
        var sql = orm().Select<Milestone>()
            .Select(m => new { m.Title, ProjectName = m.Project!.Name })
            .ToSql();

        // 外键列名与目标主键列名都来自契约，而不是"属性名 + Id"那句猜测：Milestone.ProjectId → Project.Id 是约定命中的形状。
        Checks.Has("LEFT JOIN [FastSqlProject] AS [t1] ON [t0].[ProjectId] = [t1].[Id]", sql);
    }

    // ── 没有这一档的场合：仍然当场拒，而且消息要说清是哪一档 ─────────────

    [Fact]
    public void A_collection_navigation_still_has_no_auto_join()
    {
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().Select(o => new { o.Id, Count = o.Items!.Count }).ToSql());

        Checks.Has("集合导航", failure.Message);
        Checks.Has("已经会自动联表", failure.Message);

        // 消息把两档分开了：说"引用导航那一档已经能自动联"不能顺手把集合也带进去 ——
        // 一条 LEFT JOIN 明细就把每行主表复制成明细行数，页码、行数与聚合一起改语义。
        Checks.Has("页码、行数与聚合", failure.Message);
    }

    [Fact]
    public void A_navigation_used_whole_as_a_value_is_still_refused()
    {
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>().Where(o => (object)o.Customer! == null).ToSql());

        Checks.Has("t0.Customer 不是列", failure.Message);
        Checks.Has("再往下写一格", failure.Message);
    }

    [Fact]
    public void A_navigation_read_inside_an_explicit_join_condition_is_refused()
    {
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Select<Order>()
                .LeftJoin<Customer>((o, c) => o.Customer!.City == c.City)
                .ToSql());

        // ON 的翻译在联表那一步自己那一颗 lambda 里完成，那时右表还没进 FROM：
        // 在这一格补的 LEFT JOIN 会把 ON 压在一张还不存在的表上。拒的是时机，不是想法。
        Checks.Has("无法翻译表达式", failure.Message);
    }

    [Fact]
    public void The_write_side_does_not_gain_a_join_from_a_navigation_read()
    {
        var failure = Checks.Raised<UntranslatableExpressionException>(
            () => orm().Update<Order>()
                .Set(o => o.Amount, 1m)
                .Where(o => o.Customer!.City == "上海")
                .ToSql());

        // UPDATE 没有 Joins 这一格可落：句子里凭空多一张表是另一件事（要改哪些行由谁决定），不该由读法顺带定。
        Checks.Has("无法翻译表达式", failure.Message);
    }

    [Fact]
    public void Two_branches_off_one_query_each_carry_only_their_own_join()
    {
        var root = orm().Select<Order>();

        var joined = root.Where(o => o.Customer!.City == "A").ToSql();
        var plain = root.Where(o => o.CustomerName == "B").ToSql();

        // 清单是链级那份不可变的量，不是挂在引擎上的字典：分支各拿各的，共用那份不互相污染。
        Assert.Equal(1, Checks.Occurrences(joined, "LEFT JOIN"));
        Checks.Absent("LEFT JOIN", plain);
    }

    [Fact]
    public void An_operator_that_only_loosens_a_chain_flag_keeps_the_joined_edge()
    {
        var ignored = orm().Select<Order>()
            .Where(o => o.Customer!.City == "A")
            .IgnoreQueryFilter<Order>()
            .Where(o => o.Customer!.Name == "B")
            .ToSql();

        var untracked = orm().Select<Order>()
            .Where(o => o.Customer!.City == "A")
            .AsNoTracking()
            .Where(o => o.Customer!.Name == "B")
            .ToSql();

        // 这两颗算子<em>不改模型</em>，只松开链上的一颗标记位，所以它们各自新建引擎时必须把手里那份
        // 已联清单一起递过去。漏掉的后果不是报错而是多一张表：第二颗 lambda 认不出"这根边已经联过"，
        // 于是同一根边联出第二张 Customer，而第一张还挂在 FROM 里 —— 结果一样，句子更贵。
        Assert.Equal(1, Checks.Occurrences(ignored, "LEFT JOIN"));
        Assert.Equal(1, Checks.Occurrences(untracked, "LEFT JOIN"));
        Checks.Has("WHERE [t1].[City] = @p0 AND [t1].[Name] = @p1", ignored);
    }

    [Fact]
    public void An_included_edge_and_a_navigation_read_are_two_joins_not_a_reused_one()
    {
        var sql = orm().Select<Order>()
            .Include(o => o.Customer)
            .Where(o => o.Customer!.City == "上海")
            .ToSql();

        // 已裁的形状：两张而不是复用 Include 那一颗。理由是 Include 的 LEFT JOIN 只在"实体投影且没拆查"
        // 才真的进这一句（QueryCompiler.UsesIncludeJoins），按它复用会造出一句引用不存在的表的话。
        // 多一张表在 MySQL/SQL Server 上都不改变结果，这一条是贵的而不是错的。
        Assert.Equal(2, Checks.Occurrences(sql, "LEFT JOIN"));
        Checks.Has("[t1].[Remark]", sql);
        Checks.Has("WHERE [t2].[City] = @p0", sql);
    }
}
