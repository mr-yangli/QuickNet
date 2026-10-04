using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>§10.7 全局过滤器的第一半（v0.26）：<c>orm.Filters</c> 注册、读侧与两条写链的注入、
/// <c>IgnoreQueryFilters()</c> 与 <c>mandatory</c>、以及"过滤器表达式必须与用户 lambda 同一个翻译器"
/// 那一条在注册期的兑现。
///
/// <para>与其余离线套件同一分工：黄金样本钉版式，结构断言钉"凭什么长这样"，全程
/// <see cref="SqlServer.NoDriverFactory"/>。这里没有一条用例连库 —— 过滤器是<em>文本层</em>的事，
/// 而"这一条语句在库里筛掉了什么行"归集成套件（§13.4）。</para>
///
/// <para><b>每条用例自己造一只 orm</b>：注册表挂在 orm 实例上（一个实例一份，见 <c>FilterRegistry</c>），
/// 所以这一组里没有跨用例的可变全局状态，也就不会有"跑顺序变了结果就变"那一类红。
/// 反过来说，这里也就<em>测不到</em>"两个 orm 共用一份册子"那种 bug —— 那条路今天走不通，
/// 因为册子不是静态的。</para>
///
/// <para><b>这一组不覆盖的东西</b>：<c>SaveChanges</c> 与批量写（§10.7 约束 1 剩下的那一半，
/// 判据要先定"被过滤器挡住的行算不算一次并发冲突"）、<c>Behaviors.SoftDelete</c> 的软删改写
/// （约束 3，机制在 v0.18 已就位、缺入口子）、联表链上的 <c>IgnoreQueryFilters()</c>（arity≥2 没有那个口子）。
/// 逐条欠账写在 §10.7 的落地记录与 §14 的对应格里。</para></summary>
public sealed class FilterTests
{
    static FastSqlOrm orm() => SqlServer.Orm();

    /// <summary>在册一条软删过滤器的 orm。<b>靶子是 <see cref="Ticket"/> 而不是 <see cref="Order"/></b>：
    /// <c>Order</c> 的列序已被几十份黄金样本钉住，而这一组要判的是"文本里多出来的到底那一格"。</summary>
    static FastSqlOrm softDeleted()
    {
        var fresh = orm();

        fresh.Filters.Add<Ticket>(t => !t.Deleted);

        return fresh;
    }

    // ── 读侧：贴在哪一段 ─────────────────────────────────────────────

    [Fact]
    public void A_registered_filter_lands_in_the_where_of_the_select()
    {
        var sql = softDeleted().Select<Ticket>().ToSql();

        Golden.Verify("filter-select-soft-delete", sql);

        // 「没写 WHERE 的链式查询也会带上过滤器」是这一档存在的全部理由：漏筛的人是调用方，
        // 而 §10.7 要的是"默认值就是安全的"。bool 列在 T-SQL 侧被补成 = 1 再取反（§5.7 的布尔形状）。
        Checks.Has("FROM [FastSqlTicket] AS [t0] WHERE NOT [t0].[Deleted] = 1", sql);
    }

    [Fact]
    public void A_filter_registered_for_another_entity_does_not_touch_this_table()
    {
        var target = orm();

        target.Filters.Add<Order>(o => o.Amount > 1m);

        // 匹配规则只有一句（键 IsAssignableFrom 表位的实体），所以另一张表上在册的东西在这里一个字都不该出现。
        // 这一条同时钉住「未注册时不造对象」那个短路的<em>结果</em>：贴不上就等于没贴。
        Assert.Equal(orm().Select<Ticket>().ToSql(), target.Select<Ticket>().ToSql());
    }

    [Fact]
    public void Two_filters_fold_in_registration_order_after_the_callers_condition()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted).Add<Ticket>(t => t.TenantId == 3);

        var sql = target.Select<Ticket>().Where(t => t.Title == "标题").ToSql();

        Golden.Verify("filter-two-registrations", sql);

        // 顺序 = 用户条件 → 注册 1 → 注册 2，与读侧多次 Where 的折叠方向一致（两侧都往末尾追加），
        // 而同一连接词不补括号（§13.2 的"读起来跟人手写的一样"）。
        Checks.Has("WHERE [t0].[Title] = @p0 AND NOT [t0].[Deleted] = 1 AND [t0].[TenantId] = @p1", sql);
    }

    [Fact]
    public void Every_read_exit_carries_the_filter()
    {
        var query = softDeleted().Select<Ticket>().OrderBy(t => t.Id).Skip(2).Take(3);

        var sql = query.ToSql();

        Golden.Verify("filter-paging", sql);

        // 分页是形状改写（TOP / OFFSET / ROWNUM / 嵌套一层），最容易被"改写时只搬了原句的前半"漏掉条件。
        Checks.Has("WHERE NOT [t0].[Deleted] = 1 ORDER BY [t0].[Id] ASC OFFSET", sql);

        // 内联那份给 DBA 看的文本走的是同一个出口，所以它也得带上；区别只是值被写成了字面量。
        Checks.Has("WHERE NOT [t0].[Deleted] = 1", softDeleted().Select<Ticket>().ToLogSql());

        // Explain 的模型文本是"这一条链到底是什么"的那个自述（§10.1 硬规则 1 的读侧形态）：
        // 过滤器不在调用里出现，所以它必须在那里被点名，否则一个人读不出自己的链被加过东西。
        Checks.Has("WHERE NOT (t0.Deleted = TRUE)", softDeleted().Select<Ticket>().Explain().ModelText);
    }

    // ── 联表：贴进 ON 还是 WHERE ─────────────────────────────────────

    [Fact]
    public void A_filter_on_the_left_joined_table_goes_into_the_on_clause()
    {
        var sql = softDeleted().Select<Order>()
            .LeftJoin<Ticket>((o, t) => o.Id == t.TenantId)
            .Where((o, t) => o.Amount > 10m)
            .Select((o, t) => new { o.Id, t.Title })
            .ToSql();

        Golden.Verify("filter-left-join-on", sql);

        Checks.Has("LEFT JOIN [FastSqlTicket] AS [t1] ON [t0].[Id] = [t1].[TenantId] AND NOT [t1].[Deleted] = 1", sql);

        // WHERE 里只剩调用方自己写的那一句。这不是版式偏好：把右表的过滤器写进 WHERE，
        // 未匹配的补 NULL 行会被它一起筛掉（NULL 上任何比较都是 UNKNOWN），左联就静默退化成内联 ——
        // 抹掉的正是 §13.6 那条「左联补 NULL 行」用例存在的全部意义。
        Checks.Has("WHERE [t0].[Amount] > @p0", sql);
        Checks.Absent("[t1].[Deleted]", sql[sql.IndexOf(" WHERE ", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Two_filters_on_the_same_left_joined_table_stack_inside_the_same_on_clause()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted).Add<Ticket>(t => t.TenantId == 3);

        var sql = target.Select<Order>()
            .LeftJoin<Ticket>((o, t) => o.Id == t.TenantId)
            .Select((o, t) => new { o.Id, t.Title })
            .ToSql();

        // 后一条要叠在<em>已经贴过前一条</em>的那份 ON 上。注入那一步读的是改写后的数组而不是原模型，
        // 读原文的话第一条会被丢掉 —— 那是一条能过"只注册一个过滤器"的全部用例的漏法。
        Checks.Has("ON [t0].[Id] = [t1].[TenantId] AND NOT [t1].[Deleted] = 1 AND [t1].[TenantId] = @p0", sql);
        Assert.Equal(1, Checks.Occurrences(sql, "LEFT JOIN"));
    }

    [Fact]
    public void A_filter_on_the_root_of_a_left_join_still_goes_into_the_where()
    {
        var target = orm();

        target.Filters.Add<Order>(o => o.Amount > 1m);

        var sql = target.Select<Order>()
            .LeftJoin<Ticket>((o, t) => o.Id == t.TenantId)
            .Select((o, t) => new { o.Id, t.Title })
            .ToSql();

        // 根表没有"补 NULL 行"这一说（它的每一行都在），所以它的过滤器进 WHERE 与用户的条件同一个形状。
        Checks.Has("ON [t0].[Id] = [t1].[TenantId] WHERE [t0].[Amount] > @p0", sql);
        Checks.Absent("ON [t0].[Id] = [t1].[TenantId] AND", sql);
    }

    [Fact]
    public void A_filter_on_an_inner_joined_table_goes_into_the_where_after_the_callers_condition()
    {
        var sql = softDeleted().Select<Order>()
            .InnerJoin<Ticket>((o, t) => o.Id == t.TenantId)
            .Where((o, t) => t.Title != null)
            .Select((o, t) => new { o.Id, t.Title })
            .ToSql();

        // INNER 两侧等价（不存在补 NULL 行），所以与根表同一条规则；写在明处是因为"只有 LEFT 特殊"
        // 这件事得有两个见证，否则改天有人把分岔写成"右表一律进 ON"也测不出来。
        Checks.Has("WHERE [t1].[Title] IS NOT NULL AND NOT [t1].[Deleted] = 1", sql);
    }

    // ── 每次执行重新求值（规格示例那句「闭包捕获 AsyncLocal」） ────────

    [Fact]
    public void The_filter_rereads_its_closure_at_every_exit()
    {
        var tenant = 7;

        var target = orm();

        target.Filters.Add<Ticket>(t => t.TenantId == tenant);

        var query = target.Select<Ticket>();

        var firstSql = query.ToSql(out var first);

        tenant = 8;

        var secondSql = query.ToSql(out var second);

        // 同一条链、两次出口，值跟着变。缓存翻译结果的话这里会是两颗 7 —— 而那次换租户的读不会报错，
        // 只会读到上一个租户的行：比根本没装过滤器更糟，因为它让人以为隔离生效了。
        Assert.Equal(7, first[0].Value);
        Assert.Equal(8, second[0].Value);

        // 除了那颗值，两条语句其余部分一字不差（否则"重新求值"就顺带改了版式，那是另一件事）。
        Assert.Equal(firstSql, secondSql);
    }

    [Fact]
    public void The_filter_values_come_after_the_conditions_the_caller_wrote()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => t.TenantId == 3);

        var sql = target.Select<Ticket>().Where(t => t.Title == "标题").ToSql(out var parameters);

        // 参数按谓词在 AST 里的出现顺序编号，而 AST 又是「用户的先、过滤器的后」，所以调用方数得清
        // 哪一颗是自己那一句的（ADO 侧只有名字，没有"这颗是库加的"那种标注）。
        Assert.Equal(["@p0", "@p1"], parameters.Select(static p => p.Name).ToArray());
        Assert.Equal("标题", parameters[0].Value);
        Assert.Equal(3, parameters[1].Value);
        Checks.Has("WHERE [t0].[Title] = @p0 AND [t0].[TenantId] = @p1", sql);
    }

    // ── IgnoreQueryFilters 与 mandatory（约束 2） ─────────────────────

    [Fact]
    public void Ignoring_the_filters_brings_the_text_back_to_the_never_registered_one()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted).Add<Ticket>(t => t.TenantId == 3);

        var ignored = target.Select<Ticket>().Where(t => t.Title == "标题").IgnoreQueryFilters();

        var never = orm().Select<Ticket>().Where(t => t.Title == "标题");

        // 判据是逐字节相等，不是"少了一格"：关掉过滤器之后这条链必须与一个从没注册过的 orm 产出同一句话
        // （§13.2 那个「剔掉标记位之后逐字节相等」的口径在这里同样成立）。
        Assert.Equal(never.ToSql(), ignored.ToSql());
        Assert.Equal(never.Explain().ModelText, ignored.Explain().ModelText);
    }

    [Fact]
    public void A_mandatory_filter_refuses_IgnoreQueryFilters_at_the_call()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted, mandatory: true);

        // 抛点在 Ignore 那一句本身，不在执行：报错要落在写这句话的人手里，而不是落在一次 ToList 里
        // （链式复用时会让人找不到出处）。
        var failure = Checks.Raised<FastSqlException>(() => target.Select<Ticket>().IgnoreQueryFilters());

        Checks.Has("FastSqlTicket（表位 t0）", failure.Message);
        Checks.Has("注册自 Ticket", failure.Message);
        Checks.Has("IgnoreQueryFilters() 关掉", failure.Message);
    }

    [Fact]
    public void Ignoring_is_allowed_when_no_mandatory_filter_covers_this_chain()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted, mandatory: true);

        // 同一只 orm 上另一张表照常忽略：mandatory 判的是"这条链的表位上有没有"，不是"册子上有没有"。
        // 少了这一条，下一个用例就无法区分"按表判定"与"注册过 mandatory 就一律禁止忽略"。
        var sql = target.Select<Order>().IgnoreQueryFilters().Where(o => o.Id == 7).ToSql();

        Checks.Has("WHERE [t0].[Id] = @p0", sql);
        Checks.Absent("Deleted", sql);
    }

    [Fact]
    public void Joining_a_mandatory_table_after_the_ignore_still_raises_at_the_exit()
    {
        var target = orm();

        target.Filters.Add<Ticket>(t => !t.Deleted, mandatory: true);

        var chain = target.Select<Order>().IgnoreQueryFilters().LeftJoin<Ticket>((o, t) => o.Id == t.TenantId);

        // 先忽略、后把带 mandatory 过滤器的表联进来 —— 标记位此时已经置好了，出口不重判就是那条约束上的洞。
        // 这正是 FilterRegistry.AssertIgnorable 在出口再问一次的理由，而那一次问只在这条链上付。
        var failure = Checks.Raised<FastSqlException>(() => chain.ToSql());

        Checks.Has("FastSqlTicket（表位 t1）", failure.Message);
        Checks.Has("mandatory", failure.Message);
    }

    // ── 按接口注册（规格示例第一句）与它查不出来的那一半 ───────────────

    [Fact]
    public void An_interface_key_reaches_every_implementing_entity()
    {
        var target = orm();

        target.Filters.Add<ISoftDelete>(e => !e.Deleted);

        // 形参声明在接口上、表位是实现类，按相等判就贴不上 —— 放开的是「同一继承链」，
        // 不是「任意类型」（<c>Scope.ForFilter</c> 那一格，放开的原因写在那里）。
        Checks.Has("WHERE NOT [t0].[Deleted] = 1", target.Select<Ticket>().ToSql());

        // 写侧同一条规则：接口键在链式 UPDATE/DELETE 上一样叠得进去（CountFor 也按实现类数）。
        var reason = target.Delete<Ticket>().Where(t => t.Id == 7).Explain().Reason;

        Checks.Has("叠着 1 条全局过滤器", reason);
    }

    [Fact]
    public void An_interface_key_that_the_table_cannot_satisfy_names_both_sides()
    {
        var target = orm();

        target.Filters.Add<ISoftDelete>(e => !e.Deleted);

        // LooseNote 在 CLR 侧真的实现了接口（编译期看不出），而契约里没有那一列。
        // 这一条是约束 4 那一半的现场：接口键在注册期查不出来（没有契约可查），所以<em>第一次贴上表</em>时
        // 必须把「谁贴不进谁」说清 —— 只剩翻译器原话的话，读到的是一句关于 e.Deleted 的话，
        // 而错在一个看不见名字的注册上。
        var failure = Checks.Raised<FastSqlException>(() => target.Select<LooseNote>().ToSql());

        Checks.Has("ISoftDelete 上注册的全局过滤器贴不进 FastSqlLooseNote", failure.Message);
        Checks.Has("键是接口/基类", failure.Message);
        Checks.Has("翻译器原话：", failure.Message);
    }

    [Fact]
    public void An_untranslatable_predicate_fails_at_Add_and_leaves_the_registry_clean()
    {
        var target = orm();

        // 约束 4：过滤器 lambda 与用户 lambda 走同一个翻译器，所以"翻不动"在注册这一趟就要响，
        // 而不是留到第一次查询。这条链的翻译失败形态与 GuardrailTests 里那条同名的读法一致。
        var failure = Checks.Raised<FastSqlException>(() => target.Filters.Add<Ticket>(t => t.Title!.PadRight(3) == "x"));

        Checks.Has("Ticket 上注册的全局过滤器贴不进 FastSqlTicket", failure.Message);
        Checks.Has("System.String.PadRight", failure.Message);

        // 「先试翻译、后登记」的另一半：抛掉的那一条不能留在册子上，否则后面每一次查询都要再撞一遍
        // 同一个错，而调用方会以为注册成功了（Add 已经返回了 —— 返回值在这里没有意义，注册才是）。
        Assert.Equal(orm().Select<Ticket>().ToSql(), target.Select<Ticket>().ToSql());
    }

    // ── 两条写链（约束 1 的写侧那一半） ──────────────────────────────

    [Fact]
    public void The_update_chain_carries_the_filter_and_says_so_in_its_reason()
    {
        var plan = softDeleted().Update<Ticket>().Set(t => t.Title, "新标题").Where(t => t.Id == 7).Explain();

        Golden.Verify("filter-update", plan.Sql.Text);

        Checks.Has("UPDATE [t0] SET [Title] = @p0 FROM [FastSqlTicket] AS [t0] WHERE [t0].[Id] = @p1 AND NOT [t0].[Deleted] = 1", plan.Sql.Text);

        // 自述要<em>主动</em>报这件事：一个人照着 .Where(...) 那一句理解这条语句的命中范围，
        // 就会去改错的那些行 —— 而 §10.7 约束 1 存在的理由恰恰是"这两件事不一样"。
        Checks.Has("叠着 1 条全局过滤器", plan.Reason);
    }

    [Fact]
    public void The_delete_chain_carries_the_filter_without_losing_its_physical_delete_wording()
    {
        var plan = softDeleted().Delete<Ticket>().Where(t => t.Id == 7).Explain();

        Golden.Verify("filter-delete", plan.Sql.Text);

        Checks.Has("DELETE FROM [t0] FROM [FastSqlTicket] AS [t0] WHERE [t0].[Id] = @p0 AND NOT [t0].[Deleted] = 1", plan.Sql.Text);

        // 「物理删除」那三个字不能因为这里加了过滤器就改掉：那一句讲的是"行不在了、软删改写还没落地"
        // （约束 3 的欠账），与 WHERE 里叠了什么无关。它同时是那两条既有断言的前提
        // （SqlServerUpdateTests / SqlServerCudBuildingTests 都在数这个子串）。
        Checks.Has("物理删除，行不在了", plan.Reason);
        Checks.Has("叠着 1 条全局过滤器", plan.Reason);
    }

    [Fact]
    public void A_filter_is_not_a_condition_for_the_whereless_guard()
    {
        var target = softDeleted();

        // 全局过滤器不是「你说的条件」。那道闸门判的是调用方有没有说过要对哪些行做这件事，
        // 册子上有东西不能替他说过 —— 否则一条忘了写 Where 的 UPDATE 会因为项目注册过软删而获得免死。
        var update = Checks.Raised<FastSqlException>(() => target.Update<Ticket>().Set(t => t.Title, "x").ToSql());
        var delete = Checks.Raised<FastSqlException>(() => target.Delete<Ticket>().ToSql());

        Checks.Has("没有 WHERE", update.Message);
        Checks.Has("没有 WHERE", delete.Message);
    }

    [Fact]
    public void The_insert_chain_is_left_alone()
    {
        var target = softDeleted();

        var sql = target.Insert(new Ticket { Title = "x", TenantId = 3 }).ToSql();

        // 约束 1 列的是 SELECT / UPDATE / DELETE 三种语句，插入不在其中：<c>WHERE</c> 这个位置在
        // INSERT 上根本不存在，"别让这一行插进来越权"要的是值校验（§10.6）而不是谓词叠加。
        // 这一条钉的是"今天没做"，不是"不该做"：真要补那一档，得先给过滤器一个能拒绝值的形状，
        // 那句话是公开语义，归 §14 的 P1 余项。
        Assert.Equal(orm().Insert(new Ticket { Title = "x", TenantId = 3 }).ToSql(), sql);
        Checks.Absent("WHERE", sql);
    }
}
