using QuickSql.TestInfra;
using Xunit;

namespace QuickSql.Tests.Dialects;

/// <summary><c>Where(bool, predicate)</c> 那一颗条件入口（§5.4 读侧算子）。
///
/// <para>要它的人是一个检索表单的常态：十栏里用户填了两栏。<b>没有这一颗时调用方只有两种写法</b>——
/// 把可选条件折成 <c>|| !cond ||</c> 塞进同一句 lambda（读起来不像业务原话，而且翻译器要为那句合成形状
/// 多做一遍折叠），或者在链外摊一堆 <c>if (cond) q = q.Where(...)</c>（每多一栏多两行）。这一颗给的是
/// 第三种：<em>条件假就等于这一句没写过</em>。</para>
///
/// <para>"没写过"是可判的事实而不是修辞：<b>假档直接返回 <c>this</c></b>，模型、文本、参数表与参数编号
/// 逐字节不变；真档走的就是那颗一参的 <c>Where</c>，同一个咽喉、同一个翻译器，所以失败口径也只有一份
/// （§10.7 约束 4 那条"过滤器与用户 lambda 同一个翻译器"在这里同样成立）。
/// 三档 arity（1∶2∶3）都有这一颗，联表链上 <c>Where</c> 本来就有，条件档没有理由只长一半。</para></summary>
public sealed class ConditionalWhereTests
{
    static QuickSqlOrm orm() => SqlServer.Orm();

    [Fact]
    public void A_false_condition_costs_not_one_byte_while_a_true_one_is_the_plain_where()
    {
        var target = orm();
        var state = OrderState.Paid;
        var city = "上海";

        var bare = target.Select<Order>().Where(o => o.Amount > 100m);
        var withOff = bare.Where(false, o => o.State == state).Where(false, o => o.CustomerName == city);

        // 假档 ⇒ 同一个实例（返回 this），所以这里连文本都比不出差别，参数表也不许多一颗。
        Assert.Equal(bare.ToSql(), withOff.ToSql());
        Assert.Equal(bare.Explain().ModelText, withOff.Explain().ModelText);
        Assert.Equal(bare.ToSql(out var bareArgs).Length, withOff.ToSql(out var offArgs).Length);
        Assert.Equal(bareArgs.Count, offArgs.Count);

        // 真档与一参那颗逐字同形：这一颗不是第二条路，是同一颗出口的开关。
        Assert.Equal(
            bare.Where(o => o.State == state).ToSql(),
            bare.Where(true, o => o.State == state).ToSql());
    }

    [Fact]
    public void Stacked_conditions_fold_in_written_order_after_the_ones_already_on_the_chain()
    {
        var target = orm();
        var state = OrderState.Paid;
        var city = "上海";

        var sql = target.Select<Order>()
            .Where(o => o.Amount > 100m)
            .Where(true, o => o.State == state)
            .Where(false, o => o.Note == city)
            .Where(true, o => o.CustomerName == city)
            .ToSql();

        var amount = sql.IndexOf("[t0].[Amount] > ", StringComparison.Ordinal);
        var paid = sql.IndexOf("[t0].[State] = ", StringComparison.Ordinal);
        var name = sql.IndexOf("[t0].[CustomerName] = ", StringComparison.Ordinal);

        Assert.True(amount >= 0 && paid > amount && name > paid, $"AND 链的次序不对：{sql}");

        // 被关掉的中间那一条不留痕迹：连参数编号都不占（Note 那一颗不在产出里）。
        Assert.DoesNotContain("[t0].[Note] = ", sql);

        var counted = target.Select<Order>().Where(o => o.Amount > 100m)
            .Where(true, o => o.State == state).Where(true, o => o.CustomerName == city);

        counted.ToSql(out var args);

        Assert.Equal(3, args.Count);
    }

    [Fact]
    public void The_two_and_three_table_chains_take_the_same_switch()
    {
        var target = orm();
        var city = "上海";
        var sku = "A-1";

        var two = target.Select<Order>().InnerJoin<Customer>((o, c) => o.CustomerId == c.Id);
        var three = two.InnerJoin<OrderItem>((o, c, i) => i.OrderId == o.Id);

        Assert.Equal(two.ToSql(), two.Where(false, (o, c) => c.City == city).ToSql());
        Assert.Equal(
            two.Where((o, c) => c.City == city).ToSql(),
            two.Where(true, (o, c) => c.City == city).ToSql());

        Assert.Equal(three.ToSql(), three.Where(false, (o, c, i) => i.Sku == sku).ToSql());

        var joined = three.Where(true, (o, c, i) => i.Sku == sku).ToSql();

        Assert.Contains("[t2].[Sku] = ", joined);
        Assert.Contains("[t1].[Name] = ", three.Where(true, (o, c, i) => c.Name == city).ToSql());
    }
}
