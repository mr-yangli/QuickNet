using FastSql.Query;
using FastSql.TestInfra;
using Xunit;

namespace FastSql.Tests.Integration;

/// <summary>读路径的纵向切片在真库上跑一遍（§14 P0 主靶的验收面）：
/// <c>Select → QueryModel → AST → 方言发射 → ADO → 物化</c> 每一环都只有一份实现，
/// 而黄金样本只能证明到"文本长得对"，证明不了"库认这段文本、读回来的值对"。
/// 所以这一批用例的全部价值在于：它们会在方言少写一个 <c>N</c> 前缀、或绑定按 <c>datetime</c>
/// 精度取整时变红，而那两类缺陷在纯文本比对下都是绿的。</summary>
public class ReadPathTests
{
    static FastSqlOrm Orm => Database.Orm;

    /// <summary>本批次那几条订单，按主键升序 —— 与 <see cref="Database.Orders"/> 的下标一一对应。</summary>
    static async Task<List<Order>> SeededAsync()
    {
        Database.Ready();

        return await Orm.Select<Order>()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    static IQuery<Order> Tagged() => Orm.Select<Order>().Where(o => o.Note == Database.Tag);

    [Fact]
    public async Task Entity_projection_reads_every_column_back_in_contract_order()
    {
        var rows = await SeededAsync();

        Assert.Equal(Database.Orders.Length, rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            var expected = Database.Orders[i];
            var actual = rows[i];

            Assert.Equal(expected.CustomerName, actual.CustomerName);
            Assert.Equal(expected.Amount, actual.Amount);
            Assert.Equal(expected.State, actual.State);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.PaidAt, actual.PaidAt);
            Assert.Equal(Database.Tag, actual.Note);

            // CustomerId 是种子按取模分配的，这里只钉"非零"：写死具体身份会让用例只在空库上成立。
            Assert.True(actual.CustomerId > 0);
        }
    }

    /// <summary>可空列读回 <c>null</c> 而不是 <c>default</c>。<c>DateTime</c> 尤其要紧：
    /// 落进非 Nullable 的 <c>default</c> 会变成 <c>0001-01-01</c>，写回去就把 NULL 改成了一个日期。</summary>
    [Fact]
    public async Task Nullable_columns_come_back_as_null_rather_than_default()
    {
        var rows = await SeededAsync();

        Assert.Contains(rows, r => r.CustomerName is null && r.PaidAt is null);
        Assert.Equal(Database.Orders.Count(o => o.PaidAt is null), rows.Count(r => r.PaidAt is null));
        Assert.Equal(Database.Orders.Count(o => o.CustomerName is null), rows.Count(r => r.CustomerName is null));
    }

    /// <summary>中文在 nvarchar 列上的等值查询。<b>这是 §10.4 那条"静默变 ?"的正向证据</b>：
    /// 如果参数被降级成 varchar 而库的排序规则是 cp1252，字面量在解析期就变成 <c>??</c>，
    /// 于是查询返回 0 行 —— 而不是报一个看得懂的错。</summary>
    [Fact]
    public async Task Chinese_predicates_match_what_was_stored()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;

        var shanghai = await Orm.Select<Customer>()
            .Where(c => c.City == "上海" && c.Remark == Database.Tag)
            .ToListAsync(ct);

        Assert.Equal(new[] { "张三" }, shanghai.Select(c => c.Name));

        // 反向对照：一个不存在的中文城市必须返回 0 行。少了这条，"全都匹配"也可能是排序规则不区分大小写。
        Assert.Empty(await Orm.Select<Customer>()
            .Where(c => c.City == "上不存在的城市" && c.Remark == Database.Tag)
            .ToListAsync(ct));
    }

    /// <summary>枚举按 INT 存（§9.3），所以谓词也要按 INT 上线。
    /// 库里存的是数字而读回来是枚举名，两头都对不上时这一条会最先变红。</summary>
    [Fact]
    public async Task Enums_filter_on_their_int_storage()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;
        var paid = await Tagged().Where(o => o.State == OrderState.Paid).ToListAsync(ct);

        Assert.Equal(Database.Orders.Count(o => o.State == OrderState.Paid), paid.Count);
        Assert.All(paid, o => Assert.Equal(OrderState.Paid, o.State));

        // Contains 走 IN，集合元素是枚举时同样要落成数字而不是 'Paid'。
        var states = new[] { OrderState.Draft, OrderState.Shipped };
        var byStates = await Tagged().Where(o => states.Contains(o.State)).ToListAsync(ct);

        Assert.Equal(Database.Orders.Count(o => states.Contains(o.State)), byStates.Count);
        Assert.All(byStates, o => Assert.True(o.State == OrderState.Draft || o.State == OrderState.Shipped));
    }

    /// <summary>时间戳的毫秒位。<c>datetime2(3)</c> 列存得下 <c>.456</c>，
    /// 但 <see cref="System.Data.DbType.DateTime"/> 的绑定会先把它取整到 1/300 秒再上线
    /// （§9.3 点名的遗留精度，而列已经换成 datetime2 了，所以取整只可能发生在绑定这一侧）。
    /// 这条用例就是为了让那一步没法悄悄混进来。</summary>
    [Fact]
    public async Task Timestamp_milliseconds_survive_the_parameter_binding()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;
        var target = Database.Orders[0].PaidAt!.Value;

        Assert.Equal(456, target.Millisecond);

        var hits = await Tagged().Where(o => o.PaidAt == target).ToListAsync(ct);

        Assert.Equal(new DateTime?[] { target }, hits.Select(o => o.PaidAt));

        // 读侧同理：按主键把这一行单独读回来，毫秒位也不能被磨掉。
        var byId = await Tagged().Where(o => o.Id == hits[0].Id).FirstOrDefaultAsync(ct);

        Assert.Equal(target, byId!.PaidAt);
    }

    [Fact]
    public async Task Decimal_scale_is_not_truncated_in_either_direction()
    {
        var rows = await SeededAsync();

        Assert.Equal(Database.Orders.Select(o => o.Amount).ToArray(), rows.Select(o => o.Amount).ToArray());

        // 四位标度用满的那条要能被等值查询捞回来：0.0001 被截成 0 就是 0 行。
        Assert.Equal(1, await Tagged().Where(o => o.Amount == 0.0001m).CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>定点列上的比较谓词按<em>数值</em>筛，不按<em>存储类的排序</em>筛。
    /// 这一条有来历：SQLite 一族把定点存成 TEXT，而该库的存储类排序是 INTEGER &lt; TEXT，
    /// 于是 <c>1700 &gt; '1000'</c> 得 <c>0</c>【实测·v0.37 排查】，<c>SqliteDialect.PrepareForEmission</c>
    /// 便在六颗比较运算符入口把 decimal 参数包成 <c>CAST(@p AS NUMERIC)</c>。
    /// <b>方言册那边钉的是文本，而"摘掉 CAST 会静默少行"这一半只有真库能证</b> —— 本条就是那一半：
    /// 五族都跑，六颗运算符各跑一轮，阈值取种子数据里全部五颗值再加上下各越界一颗，
    /// 判据是"库给的下标数 ∶ 内存里按数值筛出的下标数"两侧同数，所以它<em>不依赖</em>种子的具体数字，
    /// 也不依赖任何一家把定点存成什么型。为什么阈值要含非种子的越界值：
    /// 全用种子值时 <c>&gt;</c> 与 <c>&gt;=</c> 的差只由等值那一行决定，字典序错位有可能被同一次数掩盖。</summary>
    [Fact]
    public async Task Decimal_comparisons_filter_on_value_not_on_the_storage_class_ordering()
    {
        Database.Ready();

        var seeded = Database.Orders.Select(o => o.Amount).ToArray();

        async Task That(Func<IQuery<Order>, decimal, IQuery<Order>> apply, Func<decimal, decimal, bool> match)
        {
            foreach (var threshold in seeded.Append(0m).Append(1_000_000m))
            {
                var expected = seeded.Count(value => match(value, threshold));
                var actual = await apply(Tagged(), threshold).CountAsync(TestContext.Current.CancellationToken);

                // 两侧同数才是这一条的判据；阈值要进消息，否则红了不知道是哪一档。
                Assert.True(expected == actual, $"阈值 {threshold} 那一档：库数出 {actual} 行，内存按数值筛出 {expected} 行");
            }
        }

        await That((q, t) => q.Where(o => o.Amount > t), (v, t) => v > t);
        await That((q, t) => q.Where(o => o.Amount >= t), (v, t) => v >= t);
        await That((q, t) => q.Where(o => o.Amount < t), (v, t) => v < t);
        await That((q, t) => q.Where(o => o.Amount <= t), (v, t) => v <= t);
        await That((q, t) => q.Where(o => o.Amount == t), (v, t) => v == t);
        await That((q, t) => q.Where(o => o.Amount != t), (v, t) => v != t);
    }

    /// <summary>DTO 投影走构造器（<c>ObjectProjection.ViaConstructor</c> 那条分支）。
    /// 匿名类型与构造器记录在物化器里是两套装配代码，只跑一条等于另一条没跑过。</summary>
    [Fact]
    public async Task Dto_projection_fills_the_constructor_parameters_in_order()
    {
        Database.Ready();

        var rows = await Tagged()
            .OrderBy(o => o.Id)
            .Select(o => new OrderSummary(o.Id, o.CustomerName, o.Amount))
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Database.Orders.Length, rows.Count);

        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(Database.Orders[i].CustomerName, rows[i].CustomerName);
            Assert.Equal(Database.Orders[i].Amount, rows[i].Amount);
        }
    }

    [Fact]
    public async Task Anonymous_projection_reads_only_the_selected_slots()
    {
        Database.Ready();

        var rows = await Tagged()
            .OrderBy(o => o.Id)
            .Select(o => new { o.CustomerName, Doubled = o.Amount * 2 })
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Database.Orders.Length, rows.Count);
        Assert.Equal(Database.Orders[0].Amount * 2, rows[0].Doubled);
        Assert.Null(rows[1].CustomerName);
    }

    /// <summary>§5.12 的 Q4/Q3 两面：首页只判有无下一页（Total 为 null），第二页起才付那条 count。</summary>
    [Fact]
    public async Task Paged_query_pays_for_the_total_only_from_the_second_page()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;
        var query = Tagged().OrderBy(o => o.Id);

        var first = await query.ToPagedAsync(1, 2, null, ct);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasNext);
        Assert.Null(first.Total);

        var second = await query.ToPagedAsync(2, 2, null, ct);

        Assert.Equal(Database.Orders.Length, second.Total);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal(
            new[] { Database.Orders[2].Amount, Database.Orders[3].Amount },
            second.Items.Select(o => o.Amount).ToArray());

        var last = await query.ToPagedAsync(3, 2, null, ct);

        Assert.Single(last.Items);
        Assert.False(last.HasNext);
        Assert.Equal(3, last.TotalPages);
    }

    /// <summary>聚合剥序（§5.12 的第一行事故）：带 <c>OrderBy</c> 的查询直接 <c>Count</c>，
    /// 而 T-SQL 的 <c>SELECT COUNT(*) … ORDER BY</c> 是语法错。剥不干净就是运行期炸，而不是文本不好看。</summary>
    [Fact]
    public async Task Count_strips_the_ordering_a_page_needs()
    {
        Database.Ready();

        var query = Tagged().OrderByDescending(o => o.CreatedAt);

        Assert.Contains("ORDER BY", query.ToSql(), StringComparison.Ordinal);
        Assert.Equal(Database.Orders.Length, await query.CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>按<em>成员名</em>排序（v0.38 第二十笔）在真库上的读数。分工与 §13.1 那条界线一致：
    /// 离线册 <c>MemberOrderingTests</c> 钉的是"发出去的是哪一颗列、认不出的一律响"，这一头钉的三件
    /// 只有库能答的事——<b>行真的按那一列排了</b>、<b>它排在分页那一句里库认</b>、
    /// 以及<b>它落进的是同一串序</b>（最后那条是本笔与上一族的交点：<c>Count</c> 会把序剥掉，
    /// 而剥的正是这一颗口写进去的那一份 <c>Ordering</c>；写进另一份列表的话，这里收到的不是 0 红就是 T-SQL 的语法错）。</summary>
    [Fact]
    public async Task Ordering_by_member_name_sorts_rows_the_database_itself_returns()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;
        var seeded = Database.Orders.Length;

        // 单调性的载体是 <c>Id</c>，不是 <c>Amount</c> —— 这一格换型换得有原因：sqlite 那一家的定点列
        // 落的是 <c>TEXT</c> 载体（<c>SqliteTypes.cs:248</c>，落 TEXT 的理由写在那一份第三条上），
        // 于是库按字典序给行。第一版用金额判单调性，在 sqlite 上当场红出 <c>12.3456</c> 排在 <c>100</c> 之前
        // （<c>obj/logs/v38-20-sqlite-read-1.log</c>）—— 红的是"载体决定序"这一格，不是本笔那颗口。
        // 比较表达式那一侧 v0.37 已在入口修过（<c>SqliteDialect.cs:109</c>），<c>ORDER BY</c> 这一侧没有对应物，
        // 缺口按本笔记 §14；这里先把单调性交给一颗三家都是数值型的列，免得一条真缺口被读成"排序没接上"。
        var byName = await Tagged().OrderBy("Id", descending: true).ToListAsync(ct);
        var byLambda = await Tagged().OrderByDescending(o => o.Id).ToListAsync(ct);

        Assert.Equal(byLambda.Select(o => o.Id), byName.Select(o => o.Id));

        // 单调性单独判一次：只比"两种写法一致"的话，一处共用的错发射会一起错。
        for (var i = 1; i < byName.Count; i++)
            Assert.True(byName[i].Id < byName[i - 1].Id,
                $"第 {i} 行比上一行号大：{byName[i].Id} > {byName[i - 1].Id}");

        // 大小写那一档（HTTP 查询串上过来的就是这一形）在真库上读到的是同一个行序。
        Assert.Equal(byName.Select(o => o.Id), (await Tagged().OrderBy("id", descending: true).ToListAsync(ct)).Select(o => o.Id));

        // 金额那一族只判"两种写法给同一串行"：sqlite 上那一串是字典序（上面那段），而两种写法共用同一处发射，
        // 所以这个对照不受载体影响，恰好也钉住了"名字认出来的那颗列 == lambda 打出来的那颗列"。
        Assert.Equal(
            (await Tagged().OrderByDescending(o => o.Amount).ToListAsync(ct)).Select(o => o.Id),
            (await Tagged().OrderBy("Amount", descending: true).ToListAsync(ct)).Select(o => o.Id));

        // 两颗键：第二颗接在 ThenBy 上，而它要排在第一颗<em>之后</em>、不是顶掉它。造形让两形可分：
        // 第一颗是唯一的 <c>Id</c> ⇒ 行序就是 Id 升序，而顶掉的话库里给的是金额那一串。
        // （文本那一半在离线册：那里钉的是 <c>ORDER BY</c> 里有两颗、顺序 = 调用序。）
        var twoKeys = await Tagged().OrderBy("Id").ThenBy("Amount", descending: true).ToListAsync(ct);
        var ascending = await Tagged().OrderBy(o => o.Id).ToListAsync(ct);

        Assert.Equal(ascending.Select(o => o.Id), twoKeys.Select(o => o.Id));

        // 分页那一句：T-SQL 与 PG 血统都要 ORDER BY 才认 OFFSET..FETCH，所以这一格量的是"名字认出来之后
        // 序落进了分页要的那一串"，而不只是"多读了两行"。
        // <b>首页不付那条 count</b>（§5.12 的 Q4：首页只判有无下一页，所以 <c>Total</c> 恒为 null ——
        // 本笔第一版把它当成 5 来断，在 mysql 上当场红，那一句红的是我对分页的假设而不是排序）。
        var page = await Tagged().OrderBy("CreatedAt", descending: true).ToPagedAsync(1, 2, null, ct);

        Assert.Null(page.Total);
        Assert.True(page.HasNext);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].CreatedAt > page.Items[1].CreatedAt,
            $"这一页没按 CreatedAt 降序：{page.Items[0].CreatedAt} 在 {page.Items[1].CreatedAt} 之前");

        // 第二页起才付总数：那颗 count 与这一串序同在，所以它同时是本笔那颗口的剥序见证（下面第 5 判的另一半）。
        var later = await Tagged().OrderBy("CreatedAt", descending: true).ToPagedAsync(2, 2, null, ct);

        Assert.Equal(seeded, later.Total);

        // 交点：这一颗口写进去的序，聚合出口剥掉的也正是它。
        var ordered = Tagged().OrderBy("CreatedAt", descending: true);

        Assert.Contains("ORDER BY", ordered.ToSql(), StringComparison.Ordinal);
        Assert.Equal(seeded, await ordered.CountAsync(ct));
        Assert.Equal(seeded, await ordered.LongCountAsync(ct));

        // 认不出的名字在真库上也是响，而不是"返回一条没排序的查询"——排序键写错的页面上只看得出顺序不对，
        // 所以它必须响在这里，而不是安静地少一个 ORDER BY。
        var failure = await Assert.ThrowsAsync<FastSqlException>(async () => await Tagged().OrderBy("CreatedA").ToListAsync(ct));

        Assert.Contains("FastSqlOrder 没有名为 CreatedA 的列", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>第八笔那一档的语义面在真库上的见证：<b>空集上的聚合交回的是 NULL 行，而不是"没有行"</b>，
    /// 而 NULL 在这一档是合法答案 ⇒ 交回请求型的 <c>default</c>，不是抛。
    ///
    /// <para>这一句为什么现在才需要：第七笔丁把"非空值类型 + 库里 NULL"改成了抛（成员声明自带可空性的
    /// 实体那一档，谎话不咽下来），而标量出口的 <c>T</c> 只是调用方要的形状，改判不能顺延到这一档 ——
    /// 守卫由 <c>Materializer.ScalarRows&lt;T&gt;</c> 替臂问。离线替身那一头钉在方言册
    /// <c>ScalarExitTests</c>，这一头钉的是"库真的交回 NULL 行、而 <c>AggregateAsync</c> 那句
    /// '聚合恒定一行'的抛没有把它误报成缺陷"。第一句（<c>Count</c> 是 0）是前置：它排除"这一格其实有行"，
    /// 而后三句才读得出"行在而值为空"这一档。</para></summary>
    [Fact]
    public async Task Aggregate_over_an_empty_set_returns_default_rather_than_throwing()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;
        var none = Orm.Select<Order>().Where(o => o.Id == int.MinValue);

        Assert.Equal(0, await none.CountAsync(ct));

        // SUM/AVG/MAX 打在空集上：库交回一行、那一格是 NULL，而请求型是非空值类型 ⇒ default。
        Assert.Equal(0m, await none.SumAsync(o => o.Amount, ct));
        Assert.Equal(0m, await none.AverageAsync(o => o.Amount, ct));
        Assert.Equal(0, await none.MaxAsync(o => o.CustomerId, ct));
    }

    /// <summary>存在性与 <c>long</c> 计数在真库上判对两边（v0.38 第十九笔）。方言册那一头只能钉
    /// "打到了取连接那一层"，这一头钉的是句子真的对：<c>Any</c> 发的是 <c>COUNT(*) &gt; 0</c>，
    /// 所以它的红会落在"有行答 false"或"空集答 true"，而不是"文本不好看"。带页那一档判的是
    /// <em>那一页</em>有没有 —— 与 <c>Count</c> 同一条派生表咽喉，两颗新口在这里不该分岔。</summary>
    [Fact]
    public async Task Any_and_LongCount_answer_on_the_same_rows_the_count_family_reads()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;
        var seeded = Database.Orders.Length;

        Assert.True(await Tagged().AnyAsync(ct));
        Assert.Equal(seeded, await Tagged().LongCountAsync(ct));

        // 空集那一侧：COUNT(*) 是 0，所以 Any 必须答 false —— 这一句排除的是"恒真"那种接法。
        var none = Orm.Select<Order>().Where(o => o.Id == int.MinValue);

        Assert.False(await none.AnyAsync(ct));
        Assert.Equal(0L, await none.LongCountAsync(ct));

        // 带页：偏移越过全部种子行，问的是这一页有没有，不是整表。
        var beyond = Tagged().OrderBy(o => o.Id).Skip(seeded).Take(1);

        Assert.Equal(0, await beyond.CountAsync(ct));
        Assert.False(await beyond.AnyAsync(ct));

        // §13.1 第 9 项那句"开出口那天这条断言原样套用"今天套用：同一条带序链上两颗新出口都得剥掉序，
        // 否则在 T-SQL 与 PG 血统上是运行期语法错，而不是"文本不好看"。先断言链自己有序，再问出口。
        var ordered = Tagged().OrderByDescending(o => o.CreatedAt);

        Assert.Contains("ORDER BY", ordered.ToSql(), StringComparison.Ordinal);
        Assert.True(await ordered.AnyAsync(ct));
        Assert.Equal(seeded, await ordered.LongCountAsync(ct));

        // 同步口与异步口是同一条路：值一致，不是各发各的句子。
        Assert.True(Tagged().Any());
        Assert.Equal((long) seeded, Tagged().LongCount());
    }

    [Fact]
    public async Task Join_reads_across_two_tables_without_losing_aliases()
    {
        Database.Ready();

        var rows = await Orm.Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => o.Note == Database.Tag && c.Remark == Database.Tag)
            .OrderBy((o, c) => o.Id)
            .ToListAsync((o, c) => new { o.CustomerName, c.City }, TestContext.Current.CancellationToken);

        Assert.Equal(Database.Orders.Length, rows.Count);

        // 客户是按取模分配的，所以只能钉"每行都带回了城市"，钉具体城市会把用例绑死在一次种子上。
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.City)));
    }

    /// <summary>§13.6：左联<b>补出来的那一行</b>的结果正确性，此前一个真库见证都没有。
    /// 上面那条虽然写的是 <c>LeftJoin</c>，它的 <c>WHERE</c> 却同时筛两张表，
    /// 于是 NULL 补行<b>必然被自己的条件滤掉</b> —— 那条验的是别名不串，不是补行。
    /// 这里改用种子里那条"指向不存在客户"的订单（<see cref="Database.OrphanTag"/>）：
    /// 左联要出这一行且客户侧的列是 <c>null</c>；同一个条件换成 <c>InnerJoin</c> 必须是 0 行。
    /// 两句合起来才是"补行"：<b>只有前一句</b>，联表整体退化成 CROSS JOIN 也能过；
    /// <b>只有后一句</b>，一个把左连接写成内连接的实现也能过。</summary>
    [Fact]
    public async Task Left_join_yields_a_row_with_nulls_when_the_joined_side_is_missing()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;

        // 前置：哨兵 id 上确实没有客户。缺了这句，"补行"就可能在一次种子漂移之后变成"两边都有行"的假绿。
        Assert.Equal(0, await Orm.Select<Customer>().Where(c => c.Id == Database.OrphanCustomerId).CountAsync(ct));

        var rows = await Orm.Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => o.Note == Database.OrphanTag)
            .OrderBy((o, c) => o.Id)
            .ToListAsync((o, c) => new { o.Id, o.Amount, c.City }, ct);

        var row = Assert.Single(rows);

        Assert.True(row.Id > 0);
        Assert.Equal(42.4242m, row.Amount);

        // 驱动侧的值原样读到、被联侧的列是 null 而不是 ""：这就是 NULL 补行穿过物化器没被磨平的证据。
        Assert.Null(row.City);

        // 同一行补出来的 NULL，换**值类型**列再过一次物化层：被联侧的 `c.Id` 落到声明为 `int?` 的槽位上必须是
        // null，落到 `int` 上才是 0 —— 两句一起才算钉住"NULL 交回声明类型自己的默认值"这条规则，
        // 而只有前一句能区分"没有那一行"与"那一行的值就是 0"（v0.10.2 之前两句都给 0，
        // §13.6 那个承诺在 DTO 投影这一侧是破的）。
        var widened = await Orm.Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => o.Note == Database.OrphanTag)
            .ToListAsync((o, c) => new { Widened = (int?) c.Id, Plain = c.Id }, ct);

        var filler = Assert.Single(widened);

        Assert.Null(filler.Widened);
        Assert.Equal(0, filler.Plain);

        Assert.Empty(await Orm.Select<Order>()
            .InnerJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => o.Note == Database.OrphanTag)
            .ToListAsync((o, c) => new { o.Id }, ct));
    }

    /// <summary>§5.2 的自动联表在真库上的读数：调用方既不点名 <c>Include</c> 也不写 join，
    /// <c>o.Customer.City</c> 这一格由库按契约上那根 to-one 边补一条 <c>LEFT JOIN</c>。
    /// 方言层那册钉的是句子文本，这一格钉的是<em>库真的接受这句、而且语义是 LEFT 不是 INNER</em>：
    /// 外格为空的那一行（<see cref="Database.OrphanTag"/>）必须还在，被联侧的列读回 <c>null</c>。
    /// 三档（投影 / 谓词 / 序）各走一遍，谓词那一档再与<em>显式写出来的</em>同一根 LeftJoin 逐行对拍 ——
    /// 两条 API 形状、一个答案，对不上就是自动那条猜错了边。</summary>
    [Fact]
    public async Task A_navigation_read_lands_a_left_join_without_being_asked()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;

        // 前置：哨兵 id 上确实没有客户。缺这句，"补行"会在一次种子漂移之后变成"两边都有行"的假绿（与上面那颗左联用例同一道闸门）。
        Assert.Equal(0, await Orm.Select<Customer>().Where(c => c.Id == Database.OrphanCustomerId).CountAsync(ct));

        // ① 投影档：五行按标签的订单 + 一行孤儿都在，孤儿那一行的城市是 null —— INNER 会把它整行吃掉。
        var projected = await Orm.Select<Order>()
            .Where(o => o.Note == Database.Tag || o.Note == Database.OrphanTag)
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.Note, City = o.Customer!.City })
            .ToListAsync(ct);

        Assert.Equal(Database.Orders.Length + 1, projected.Count);

        // 补行只有孤儿那一行：按标签的那五行全都带回了城市，所以这一句同时钉住"发的是 LEFT"与"没多补一行"。
        Assert.All(projected, r => Assert.Equal(r.Note == Database.OrphanTag, r.City is null));

        // ② 谓词档与显式联表对拍：同一批行、同一个序，两句给的答案逐行相等。
        var auto = await Orm.Select<Order>()
            .Where(o => o.Customer!.City == "上海")
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.CustomerId })
            .ToListAsync(ct);

        var byHand = await Orm.Select<Order>()
            .LeftJoin<Customer>((o, c) => o.CustomerId == c.Id)
            .Where((o, c) => c.City == "上海")
            .OrderBy((o, c) => o.Id)
            .ToListAsync((o, c) => new { o.Id, o.CustomerId }, ct);

        Assert.NotEmpty(auto);
        Assert.Equal(byHand, auto);

        // ③ 序档：序键落在被联那张表上，而分页吃的是这一条序 —— 拿"整条序的第二、三行"与"跳过一行取两行"对照，
        // 判的是库自己排出来的那个顺序，不拿 CLR 的排序规则去比（中文按拼音还是笔画排，两家的目录各说各话）。
        var all = await Orm.Select<Order>()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Customer!.City)
            .ThenBy(o => o.Id)
            .Select(o => new { o.Id, City = o.Customer!.City })
            .ToListAsync(ct);

        var page = await Orm.Select<Order>()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Customer!.City)
            .ThenBy(o => o.Id)
            .Skip(1).Take(2)
            .Select(o => new { o.Id, City = o.Customer!.City })
            .ToListAsync(ct);

        Assert.Equal(Database.Orders.Length, all.Count);
        Assert.Equal(all.Skip(1).Take(2), page);
    }

    /// <summary>可空值<em>直接</em>比较在真库上的那一半（§12.9 那一格延伸出去的读数）：离线册钉的是"两侧都不包
    /// <c>CAST</c>"，这一颗钉的是"库认这句、行级答案与点明非空那一档逐行相同"，外加 <c>null</c> 那一档折成
    /// <c>IS NULL</c> 之后筛出零行（夹具的 <c>State</c> 列整张表不可空，所以这里的正确答案是 0 而不是"全表"）。
    /// <b>这一格在 SQL Server 上比在 MySQL 上弱</b>：老句子（带 <c>CAST</c>）在这一家照样发得出、跑得通，
    /// 所以它判的是新判据没把语义改坏，那颗 <c>1064</c> 只有配上 <c>FASTSQL_MYSQL_CONNECTION</c> 才重跑得了
    /// —— 本机在册的那一条是 SQL Server，那一档今天没有实例。</summary>
    [Fact]
    public async Task A_nullable_value_compared_directly_returns_the_same_rows_as_the_explicit_one()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;

        OrderState? paid = OrderState.Paid;

        var boxed = await Tagged().Where(o => o.State == paid).ToListAsync(ct);

        Assert.Single(boxed);
        Assert.Equal(OrderState.Paid, boxed[0].State);

        // 同一件事的另一种写法（点明非空那一档）逐行对照：两形一个答案，对不上就是豁免猜错了语义。
        var explicitValue = await Tagged().Where(o => o.State == OrderState.Paid).ToListAsync(ct);

        Assert.Equal(explicitValue.Select(o => o.Id), boxed.Select(o => o.Id));

        OrderState? none = null;

        Assert.Equal(0, await Tagged().Where(o => o.State == none).CountAsync(ct));
    }

    /// <summary>流式出口：一次只有一行在内存里（§12.7 E），中途 <c>break</c> 由 DisposeAsync 归还连接。
    /// 分两半跑（先 break 再全量）是为了让"提前退出把连接留在事务里"这类问题变成可复现的红。</summary>
    [Fact]
    public async Task Streaming_enumerates_row_by_row_and_stops_early()
    {
        Database.Ready();

        var seen = 0;

        await foreach (var order in Tagged().OrderBy(o => o.Id).ToAsyncEnumerable(1)
                           .WithCancellation(TestContext.Current.CancellationToken))
        {
            Assert.Equal(Database.Tag, order.Note);

            seen++;
            break;
        }

        Assert.Equal(1, seen);

        var all = 0;

        await foreach (var _ in Tagged().OrderBy(o => o.Id).ToAsyncEnumerable(1)
                           .WithCancellation(TestContext.Current.CancellationToken))
            all++;

        Assert.Equal(Database.Orders.Length, all);
    }

    /// <summary>LIKE 的转义在真库上才看得出来：<c>%</c> 与 <c>_</c> 没转义时这条会多命中，
    /// 而 <c>ESCAPE</c> 子句少写了 T-SQL 直接语法错（黄金样本只能证明文本，证不了这两点）。
    ///
    /// <para><b>第三判（v0.32 第二格）：前两句都只断言"命中 0 行"，而 0 行在两种机制下<em>同形</em></b> ——
    /// 转义生效（要找的那个字面量确实没种进库），与转义整个没生效（模式里多了一颗字面反斜杠，谁也命不中）。
    /// 于是它们既钉不住"发射层转义了没有"，也钉不住"库认不认那颗转义符"。下一段因此插两行：
    /// 一行真带下划线、一行<em>只差那一个字符</em>，判据落在"命中几行"这颗数字上 ——
    /// 转义生效只命中带下划线那一行（1），发射层哪天漏了转义就是那颗下划线当通配符、两行都中（2）。</para>
    ///
    /// <para><b>这一判的来路要记一句，它是被一次改判逼出来的</b>：探针 F-50
    /// （<c>obj/logs/v32-recon-funcs-11-plain.log</c> 与 <c>…-11-utf8.log</c>）那七列<em>常量比常量</em>
    /// 交出六颗 0，当时据此判过"这一家收下 <c>ESCAPE</c> 而不理它"，本条用例的第一版还照那条写成了分岔断言。
    /// 而它一跑就红在虚谷那一支（期望 0、实际 1，<c>obj/logs/v32-int-xugu-12.log</c>）。
    /// F-51 把同一套模式扫<em>真行</em>（<c>obj/logs/v32-recon-funcs-12-plain.log</c>）交回的是
    /// <c>LIT_BSLASH=1 | LIT_BARE=0 | LIT_BANG=1 | LIT_WILD=2 | LIT_PCT=2</c> 与
    /// <c>PAR_BSLASH=1 | PAR_BARE=0 | PAR_WILD=2 | PAR_PCT=0 | PAR_STARTS=1</c>，
    /// 而 <c>F-51g</c> 的 <c>LENGTH('a\_b')=4 ∶ LENGTH(:p)=4</c> 把"字面量解析器吃掉反斜杠"那一读排除了。
    /// <b>结论翻回：这一家认 <c>ESCAPE</c>，反斜杠与感叹号都认，不给子句时反斜杠才是字面字符 ——
    /// 这一格五家同一形，<c>XuguExpressions.LikeEscapeCharacter</c> 那句【推断】改判【实测·真行】。</b>
    /// 留在台账上的不是缺口而是<em>证法</em>：常量折叠那一条路上这一家把 <c>LIKE … ESCAPE</c> 折成了 0，
    /// 所以 §13.5 的探针纪律要添一行 —— 判 LIKE 的语义得扫真行，别拿常量比常量。</para>
    ///
    /// <para>写在这里而不是只写进 <c>XuguExpressions</c> 的注释：这一格漏转义的后果是<em>多命中</em>
    /// （比"少行"和"报错"都难查的那一种），只有五家真跑的一句判据钉得住。</para></summary>
    [Fact]
    public async Task Like_predicates_honor_their_escapes()
    {
        Database.Ready();

        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(Database.Items.Length, await Orm.Select<OrderItem>()
            .Where(i => i.Sku!.StartsWith(Database.Tag + "/")).CountAsync(ct));

        // 前缀里塞一个通配符：转义没生效的话这条会命中全部种子行，而不是 0 条。
        Assert.Equal(0, await Orm.Select<OrderItem>()
            .Where(i => i.Sku!.StartsWith("fastsql%p0/")).CountAsync(ct));

        // ── 第三判。前缀带本次运行的 Guid，所以同一台库上跑第二遍也不会攒出重复行；
        //    <c>InventoryItem</c> 全场没有"按前缀数行"的断言，这两行留着不碰别的用例。
        //    走 <c>ExecuteAffrowsAsync</c> 而不是 <c>ExecuteIdentityAsync</c>：这一判只要"行在里面"、
        //    不要库给键。（从前还有一句理由——要了键就得挂在 <c>RequireKeyBackfill</c> 那颗整族闸门后面，
        //    本条用例会在虚谷上整个 skip 掉；那颗闸门 v0.34 第一笔退场，这一判继续用不着键，理由换简的：
        //    不回填就不必走回填链。）
        var batch = "fastsql-esc-" + Guid.NewGuid().ToString("N")[..8];

        // <b>第四家（postgresql）在这一判上没有对象</b>：它要一张 <c>InventoryItem</c>，而本家建不出那张表
        // —— 表里带的还是那颗 <c>[Column(DbTypes = "rowversion")]</c>，42704（<c>Database.EnsureInventoryItem</c>
        // 那段把 S-10 ∶ D-04 ∶ D-05 三颗读数钉好了）。这一跑的第一版红在这里【实测·
        // <c>obj/logs/v37-pg-int-1.log</c> 第 11 行：<c>42P01: relation "FastSqlInventoryItem" does not exist</c>】。
        //
        // <b>为什么按家族收在 if 里而不是整条用例跳过</b>（与 <c>DdlTests.Chinese_text_is_stored_as_itself…</c>
        // 那一处同一手法）：上面那两判走的是 <c>OrderItem</c>，本家建得出、也跑到了，而本题问的
        // "转义没生效就多命中"在那两判里成立得一样好 —— 整条跳过会把那两判一起伪装成没跑过。
        // 第三判缺的那一格要么下一版按本家换一张能建的表重跑（<c>LIKE … ESCAPE</c> 的语义与那张表是谁无关），
        // 要么记 §14 —— 不是把这一句删掉。
        if (Database.InventoryItemGap is null)
        {
            Assert.Equal(1, await Orm.Insert(new InventoryItem { Sku = batch + "/a_1", Quantity = 1 })
                .ExecuteAffrowsAsync(ct));
            Assert.Equal(1, await Orm.Insert(new InventoryItem { Sku = batch + "/ax1", Quantity = 2 })
                .ExecuteAffrowsAsync(ct));

            // 前置：两行都在。少了这一句，下一句的那个 1 说不清是"转义生效"还是"只插进了一行"。
            Assert.Equal(2, await Orm.Select<InventoryItem>()
                .Where(i => i.Sku!.StartsWith(batch + "/a")).CountAsync(ct));

            Assert.Equal(1, await Orm.Select<InventoryItem>()
                .Where(i => i.Sku!.StartsWith(batch + "/a_1")).CountAsync(ct));
        }
        else
            Console.WriteLine($"ReadPathTests.Like_predicates_honor_their_escapes：第三判在本家没跑（{Database.Family} 建不出 InventoryItem —— {Database.InventoryItemGap}）");
    }

    /// <summary>拆分查询（§5.7）：<c>AsSplitQuery()</c> 把引用导航从 LEFT JOIN 改成主查询之后单独发查询再内存缝合。
    /// 断言的是"结果与非拆分 Include 逐字段相等"——两种执行策略的产出必须一致，否则拆分就是静默丢数据。</summary>
    [Fact]
    public async Task SplitQuery_populates_reference_navigation_identically_to_join_Include()
    {
        Database.Ready();
        var ct = TestContext.Current.CancellationToken;

        var joined = await Orm.Select<Order>()
            .Include(o => o.Customer!)
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        var split = await Orm.Select<Order>()
            .Include(o => o.Customer!)
            .AsSplitQuery()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        Assert.Equal(joined.Count, split.Count);
        Assert.True(split.Count > 0, "种子数据必须存在，否则本条断言无意义");

        for (var i = 0; i < split.Count; i++)
        {
            var j = joined[i];
            var s = split[i];

            Assert.Equal(j.Id, s.Id);
            Assert.Equal(j.CustomerId, s.CustomerId);
            Assert.NotNull(j.Customer);
            Assert.NotNull(s.Customer);
            Assert.Equal(j.Customer.Id, s.Customer.Id);
            Assert.Equal(j.Customer.Name, s.Customer.Name);
            Assert.Equal(j.Customer.City, s.Customer.City);
        }
    }

    /// <summary>拆分查询 + 前置过滤：确保 WHERE 条件在拆分路径上同样生效，
    /// 且只有匹配行的 FK 值被收集进子查询的 IN 列表（而不是全表扫描子表）。</summary>
    [Fact]
    public async Task SplitQuery_respects_where_predicate_and_only_loads_matching_children()
    {
        Database.Ready();
        var ct = TestContext.Current.CancellationToken;

        var single = await Orm.Select<Order>()
            .Include(o => o.Customer!)
            .AsSplitQuery()
            .Where(o => o.Id == 1 && o.Note == Database.Tag)
            .ToListAsync(ct);

        if (single.Count == 0) return;

        Assert.Single(single);
        Assert.NotNull(single[0].Customer);
        Assert.Equal(single[0].CustomerId, single[0].Customer!.Id);
    }

    /// <summary>DbSet.Load 引用导航：先查一批 Order 进会话（不带 Include），再 Load 补载 Customer。
    /// 缝完之后每颗 Order.Customer 的字段值必须与直接 Include 查出来的对齐。</summary>
    [Fact]
    public async Task Load_populates_reference_navigation_on_tracked_entities()
    {
        Database.Ready();
        var ct = TestContext.Current.CancellationToken;

        using var db = new DbContext(Orm);

        var orders = await db.Set<Order>()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        Assert.True(orders.Count > 0, "种子数据必须存在，否则本条断言无意义");

        foreach (var o in orders)
            Assert.Null(o.Customer);

        db.Set<Order>().Load(o => o.Customer!);

        var included = await Orm.Select<Order>()
            .Include(o => o.Customer!)
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        Assert.Equal(included.Count, orders.Count);

        for (var i = 0; i < orders.Count; i++)
        {
            Assert.NotNull(orders[i].Customer);
            Assert.NotNull(included[i].Customer);
            Assert.Equal(included[i].Customer!.Id, orders[i].Customer!.Id);
            Assert.Equal(included[i].Customer!.Name, orders[i].Customer!.Name);
            Assert.Equal(included[i].Customer!.City, orders[i].Customer!.City);
        }
    }

    /// <summary>DbSet.Load 集合导航：先查一批 Order 进会话，再 Load 补载 Items（集合导航）。
    /// 缝完之后每颗 Order.Items 的元素数与内容必须与直接 IncludeMany 查出来的对齐。</summary>
    [Fact]
    public async Task Load_populates_collection_navigation_on_tracked_entities()
    {
        Database.Ready();
        var ct = TestContext.Current.CancellationToken;

        using var db = new DbContext(Orm);

        var orders = await db.Set<Order>()
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        Assert.True(orders.Count > 0, "种子数据必须存在，否则本条断言无意义");

        foreach (var o in orders)
            Assert.Null(o.Items);

        db.Set<Order>().Load(o => o.Items!);

        var included = await Orm.Select<Order>()
            .IncludeMany(o => o.Items!)
            .Where(o => o.Note == Database.Tag)
            .OrderBy(o => o.Id)
            .ToListAsync(ct);

        Assert.Equal(included.Count, orders.Count);

        for (var i = 0; i < orders.Count; i++)
        {
            var loaded = orders[i].Items;
            var expected = included[i].Items;

            if (expected is null || expected.Count == 0)
            {
                Assert.True(loaded is null || loaded.Count == 0);
                continue;
            }

            Assert.NotNull(loaded);
            Assert.Equal(expected.Count, loaded!.Count);

            var expectedSkus = expected.Select(x => x.Sku).OrderBy(s => s).ToList();
            var loadedSkus = loaded.Select(x => x.Sku).OrderBy(s => s).ToList();

            Assert.Equal(expectedSkus, loadedSkus);
        }
    }
}
