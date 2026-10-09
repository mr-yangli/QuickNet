using FastSql.Query;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Dialects;

/// <summary>链式集合加载（<c>IncludeMany(…).ThenIncludeMany(…)</c>，§5.7）在<b>离线</b>能判的那一半。
///
/// <para>这一家与 <c>Include</c>/<c>ThenInclude</c> 最大的形状差别是：<b>层数不进主查询</b>。
/// <c>Include</c> 每一层多一张 LEFT JOIN 与一列扩展列，而集合这一档每层是执行期另发的一条
/// <c>SELECT … WHERE 外键 IN (父端主键集合)</c> —— 模型里只多一颗挂在父节点 <c>Children</c> 上的条目。
/// 所以离线这一半判的就是这两件事：<em>主查询文本一个字都没动</em>（往下那一层与旁边那一根各钉一颗 ——
/// 引用那一档从 v0.39 第四笔起也挂得上这条链），以及挂错形状（那一层不是集合导航、那一根不是引用导航）
/// 在<em>接上链的那一刻</em>就响，而不是等一句看起来合理的句子。</para>
///
/// <para>真库那一半（三张表各一句、第二层的 IN 里躺的是刚缝出来的那批子行的键、钩挂对了父；
/// 以及明细→物资那一句的去重与"配不到行的那一只留空"）在
/// <c>CascadePathTests</c> 的 <c>A_chained_include_many_loads_two_collection_levels</c> 与
/// <c>A_reference_leg_on_a_collection_level_loads_the_target</c>：
/// 子查询的文本由执行层现造，离线这一步压根不发它，而外键回填要库给值。</para></summary>
public sealed class IncludeManyChainTests
{
    static FastSqlOrm orm() => SqlServer.Orm();

    /// <summary>靶子是货架那三层（架 → 位 → 钩）：两层都是 <c>[ForeignKey]</c> 点名的边，
    /// 而这一格判的只有一句话 —— 往下多挂一层，主查询那条文本必须与单层逐字符相同。</summary>
    [Fact]
    public void A_second_level_adds_nothing_to_the_main_select()
    {
        var flat = orm().Select<Rack>().IncludeMany(r => r.Slots!).ToSql();
        var chained = orm().Select<Rack>().IncludeMany(r => r.Slots!).ThenIncludeMany(s => s.Hooks!).ToSql();

        Assert.Equal(flat, chained);
    }

    /// <summary>下一层挂在谁的契约上是<em>编译期</em>就定死的：<see cref="ShelfSlot"/> 那一侧只有
    /// <c>Hooks</c> 这一根集合边，把<em>根上</em>那根（<c>Slots</c>）再点一次根本接不上去 ——
    /// 这一格不需要一句断言，它需要的是那句 CS 报错，也正是这一族从前缺的东西
    /// （<c>Include(x =&gt; x.Groups).ThenInclude(g =&gt; g.Details)</c> 报的是
    /// <c>List&lt;…&gt; 上没有 Details</c>，读起来像链写错了，其实是那一家根本没有第二副形状）。</summary>
    [Fact]
    public void A_second_level_resolves_against_the_level_above_it()
    {
        // 点错了父（拿根的 Slots 当第二层）在编译期就接不上，所以这里只钉一句：往下走一层这件事
        // 本身不发任何主查询的东西 —— 与上面那颗同一把尺子，但靶子是"链的父端换了一层"。
        var chained = orm().Select<Rack>().IncludeMany(r => r.Slots!).ThenIncludeMany(s => s.Hooks!);

        Checks.Has("[FastSqlStorageRack]", chained.ToSql());
        Checks.Absent("[FastSqlShelfSlot]", chained.ToSql());
        Checks.Absent("[FastSqlSlotHook]", chained.ToSql());
    }

    /// <summary><c>IncludeMany</c> 的那道"投影之后不能再加载导航"闸门在本次拆口（节点构造与入清单分成两颗）
    /// 之后必须还在，而且响的还是原来那句话 —— 拆的是<em>怎么落模型</em>，不是<em>什么时候不许落</em>。
    /// 靶子投影刻意<em>投成一只实体</em>（<c>ShelfSlot</c>）：那道闸判的是计划形态，不是 CLR 类型，
    /// 而链上的形参只有长成 <c>IQuery&lt;ShelfSlot&gt;</c> 才递得进第二颗 lambda。</summary>
    [Fact]
    public void A_projection_still_refuses_the_collection_navigation()
    {
        var failure = Checks.Raised<FastSqlException>(
            () => orm().Select<Rack>()
                .Select(r => new ShelfSlot { Id = r.Id, RackKey = r.Id, Bin = r.Label })
                .IncludeMany(s => s.Hooks!));

        Checks.Has("IncludeMany 只能在实体查询上使用", failure.Message);
    }

    /// <summary>旁边那一根（<c>IncludeMany(…).ThenInclude(…)</c>，§5.7 引用那一档）与往下那一层同一条纪律：
    /// <b>引用边的目标表也不进主查询</b>。靶子是托盘 → 明细那一层集合、明细 → 物资那一根引用 ——
    /// 物资这一侧的 <c>LEFT JOIN</c> 今天只存在于执行期另发的那条 SELECT 里（离线这一步不发它，
    /// 所以这里量的是"主查询没被它碰过"，那条文本本身归 <c>CascadePathTests</c> 的真库那一半）。</summary>
    [Fact]
    public void A_reference_leg_adds_nothing_to_the_main_select()
    {
        var flat = orm().Select<Pallet>().IncludeMany(p => p.Lines!).ToSql();
        var chained = orm().Select<Pallet>().IncludeMany(p => p.Lines!).ThenInclude(l => l.Material).ToSql();

        Assert.Equal(flat, chained);
        Checks.Absent("[FastSqlPalletLine]", chained);
        Checks.Absent("[FastSqlMaterial]", chained);
    }

    /// <summary>引用那一档收在集合链上是<em>旁边多一根</em>，不是<em>下面多一层</em>：把集合导航递进这颗口
    /// 必须在接上链的那一刻就响，并指名换 <c>ThenIncludeMany</c>。
    ///
    /// <para><b>为什么这一格只能靠库自己判</b>：编译期拦不住 —— 形参是 <c>Expression&lt;Func&lt;ShelfSlot, TProp&gt;&gt;</c>，
    /// 而 <c>List&lt;SlotHook&gt;</c> 是一只完全合法的 <c>TProp</c>。放过去的话执行层按<em>主键</em>缝引用，
    /// 一根集合边只会剩下最后一只，而"少了几只"在返回的结果里看不出形状。</para></summary>
    [Fact]
    public void The_reference_mouth_refuses_a_collection_navigation()
    {
        var failure = Checks.Raised<FastSqlException>(
            () => orm().Select<Rack>().IncludeMany(r => r.Slots!).ThenInclude(s => s.Hooks!));

        Checks.Has("是集合导航", failure.Message);
        Checks.Has("ThenIncludeMany", failure.Message);
    }
}
