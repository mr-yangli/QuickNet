using FastSql.Metadata;
using FastSql.Metadata.Fluent;
using FastSql.Providers;
using FastSql.Schema;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>按<em>成员名</em>排序（v0.38 第二十笔）在<b>离线</b>能判的那一半。
///
/// <para>这一颗口的全部价值在一个不对称上：传进来的字符串<em>不参与</em>写 SQL，它只是拿去契约上认列的钥匙，
/// 认出来发的是那一颗列加方言引号，认不出当场抛。所以"注入面"在这里不是一个要过滤的风险，而是没有对应
/// 代码路径的一形 —— 这一点离线就判得动（判的正是文本），不必等活库。</para>
///
/// <para>判不到的那一半归集成层（<c>OrderingPathTests</c>）：那一头量的是<em>行真的按这一列排了序</em>，
/// 而文本相等不等于排序正确 —— 这里断言的是"发出去的是哪一颗列"，序对不对只有库能答。</para></summary>
public sealed class MemberOrderingTests
{
    static readonly IDbProvider Provider = SqlServer.Provider(SqlServer.NoDriverFactory.Instance);

    static FastSqlOrm orm() => SqlServer.Orm();

    /// <summary>带 Fluent 配置的那一台：列名那一档与"撞名"那一档都要靠改列名才造得出来，
    /// 而改的是<em>这台 orm 的解析输入</em>，不是往共享实体上加注解（那份实体是全仓库共用的列序基准）。</summary>
    static FastSqlOrm ormWith(ModelConfiguration model)
        => new(new FastSqlOrmOptions { Provider = Provider, ConnectionString = SqlServer.OfflineConnectionString },
            new EntitySchemaRegistry(new ContractResolver(Provider, model)));

    // ── 文本：认出来的那一颗列，以及"与 lambda 形态逐字相等" ──────────

    /// <summary>本笔的主判据：<b>成员名那条口发出来的 SQL 与 lambda 那条逐字节相等</b>。
    /// 这一句比"断言里写着列名"有力 —— 它钉的是"两条口共用同一份发射"，而"共用"正是
    /// §5.5 那句"算子只实现一次"在排序这一格的全部内容；谁另写一份字符串拼接，这一句当场红。</summary>
    [Fact]
    public void A_member_name_emits_the_same_text_the_lambda_emits()
    {
        var byName = orm().Select<Order>().OrderBy("CreatedAt", descending: true).ThenBy("Id");
        var byLambda = orm().Select<Order>().OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id);

        Assert.Equal(byLambda.ToSql(), byName.ToSql());
        Checks.Has("ORDER BY [t0].[CreatedAt] DESC, [t0].[Id] ASC", byName.ToSql());
    }

    /// <summary>认列的三档之二、之三：<b>列名</b>（Fluent 改过名的那一颗）与<b>大小写不敏感的唯一命中</b>
    /// （HTTP 查询串上过来的是 <c>createdAt</c> 那一形）。这两档分开看才有意义：前者说明"认的是契约里的列名，
    /// 不是 CLR 属性名"，后者说明"给 camelCase 留了门，但只留一条缝"。</summary>
    [Fact]
    public void The_column_name_tier_and_the_case_insensitive_tier_each_answer()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.Amount).HasColumnName("amount_due");

        var renamed = ormWith(model).Select<Order>().OrderBy("amount_due");

        Checks.Has("ORDER BY [t0].[amount_due] ASC", renamed.ToSql());

        // 逐字节的两次命中都失败（属性名是 Amount、列名是 amount_due），第三档才接住它。
        Checks.Has("ORDER BY [t0].[Amount] ASC", orm().Select<Order>().OrderBy("amount").ToSql());
        Checks.Has("ORDER BY [t0].[CreatedAt] ASC", orm().Select<Order>().OrderBy("createdAt").ToSql());
    }

    // ── 拒绝：认不出的一律当场响，且消息点名那颗字符串 ────────────────

    /// <summary>五颗都归"不是列"：拼错的名字、导航属性、带点的成员路径、SQL 片段、空串。
    /// <b>逐颗断言消息</b>是这一族的交付物本身 —— 一条 <c>"invalid order key"</c> 半年后什么都说不出（§5.8）。
    /// 那颗注入用的字符串要的读数不是"被转义了"，而是<em>它根本没进句子</em>。</summary>
    [Fact]
    public void Nothing_that_is_not_a_column_of_this_table_can_be_an_order_key()
    {
        var query = orm().Select<Order>();

        Checks.Has("FastSqlOrder 没有名为 CreatedA 的列",
            Checks.Raised<FastSqlException>(() => query.OrderBy("CreatedA")).Message);

        Checks.Has("导航属性、投影列与 SQL 片段都不认",
            Checks.Raised<FastSqlException>(() => query.OrderBy("Customer")).Message);

        Checks.Has("要排别的表那一侧的列请先联表",
            Checks.Raised<FastSqlException>(() => query.OrderBy("Customer.Name")).Message);

        var injected = Checks.Raised<FastSqlException>(
            () => query.OrderBy("CreatedAt; DROP TABLE FastSqlOrder--").ToSql());

        // 消息把那颗字符串原样念出来 —— 这是"它被当成一个名字去认，而不是被当成一段句子"的读数。
        Checks.Has("DROP TABLE FastSqlOrder", injected.Message);

        Checks.Has("排序成员名是空的",
            Checks.Raised<FastSqlException>(() => query.OrderBy("   ")).Message);
    }

    /// <summary>撞名那一档：大小写不敏感命中两颗列时<em>不替调用方挑一颗</em>，消息把候选列点名列出。
    /// 造形用的是同一个改名列的两种写法（<c>amount</c> 与 <c>Amount</c>）—— 真实项目里"蛇形与驼峰混着改列名"
    /// 就会撞出这一格，而它必须响。
    ///
    /// <para><b>同一份夹具还钉住逐字节的那两档各自的位置</b>：<c>"amount"</c> 只有列名那一档认（属性名是 Amount），
    /// <c>"Amount"</c> 只有属性名那一档认——而两份在第三档都是<em>两颗命中</em>。所以前两档不是第三档的加速器，
    /// 是<em>消歧的优先级</em>：掐掉任何一档，对应那一句会从"认出列"变成"撞名而响"，本用例因此是有牙的。</para></summary>
    [Fact]
    public void An_ambiguous_name_refuses_instead_of_choosing()
    {
        var model = new ModelConfiguration();

        model.Entity<Order>().Property(o => o.Amount).HasColumnName("amount");
        model.Entity<Order>().Property(o => o.PaidAt).HasColumnName("Amount");

        var query = ormWith(model).Select<Order>();

        // 逐字节的两档各判一次，而且判的是同一颗列：<c>"amount"</c> 走列名那一档，<c>"Amount"</c> 走属性名那一档
        // （属性名 Amount 的列已经被改成 amount）。两档都不是第三档的加速器 —— 第三档在这一份夹具上是两颗命中，
        // 掐掉任何一档，对应那一句都会从"认出列"变成"撞名而响"。
        Checks.Has("ORDER BY [t0].[amount] ASC", query.OrderBy("amount").ToSql());
        Checks.Has("ORDER BY [t0].[amount] ASC", query.OrderBy("Amount").ToSql());

        var failure = Checks.Raised<FastSqlException>(() => query.OrderBy("AMOUNT").ToSql());

        Checks.Has("能对上 AMOUNT 的列不止一颗", failure.Message);
        Checks.Has("Amount / PaidAt", failure.Message);
    }

    // ── 与"投影之后不能再排序"那道闸门的关系 ─────────────────────────

    /// <summary>投影之后列契约就没了，两条口要给出同一句话。这一颗落在 <see cref="GuardrailTests"/>
    /// 那一份的隔壁：那里钉的是 lambda 那一条，这里钉的是"字符串这一条没有绕过同一道闸门"。</summary>
    [Fact]
    public void A_projected_query_refuses_a_member_name_the_same_way_it_refuses_a_lambda()
    {
        var projected = orm().Select<Order>().Select(o => new { o.Id, Money = o.Amount });

        var failure = Checks.Raised<FastSqlException>(() => projected.OrderBy("Id").ToSql());

        Checks.Has("投影之后不能再排序", failure.Message);
    }
}
