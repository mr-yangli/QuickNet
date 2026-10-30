namespace QuickSql.Sql;

/// <summary>查询侧的方便口（SqlSugar 的 <c>SqlFunc</c> 同族，日期三颗签名按旧封装一比一移植，行号四颗按本库口径新立）。
///
/// <para><b>一颗都没有 C# 实现</b>：它们只在查询表达式树里有意义，由内核逐颗翻译成 SQL
/// ——日期三口译文只含比较运算与 <c>AND</c>（口径钉在文档站 06 篇 §2），不落任何方言日期函数，
/// 所以十八家 provider <b>一张模板都不用登记</b>（对照：凡要落 <see cref="FunctionExpression"/>
/// 的形状都得逐家给模板）。那颗 <see cref="FunctionExpression"/> 在同一命名空间，
/// <c>see cref</c> 引得到；翻译器在 <c>QuickSql.Core</c>，从本程序集引不到，文档站里讲。</para>
///
/// <para>两族形状分工不同：日期三口是<b>谓词形状</b>（返回 <see cref="bool"/>，接 <c>Where</c>）；
/// 行号四颗是<b>投影形状</b>（返回 <see cref="int"/>，只接 <c>Select</c>），发的是
/// <c>ROW_NUMBER() OVER (…)</c>——它与日期三口同一档「共用层出文本、方言只出自己的引号」，
/// 十八家同样一张模板都不登记。窗口函数落在 <c>WHERE</c>/<c>GROUP BY</c> 上在任何一家都是不认的句子，
/// 所以那一格由内核在<b>组合时当场拒</b>，不等库报错；要按行号筛就把带行号的内层句交进
/// <c>WithSql</c> 的 FROM 段，或整句走原生 SQL（文档站 14 篇）。</para>
///
/// <para>直接在代码里调用得到 <see cref="NotSupportedException"/>：每一颗方法体的原话都是指路的，
/// 要在内存里过滤，先 <c>ToList()/ToListAsync()</c> 拿回行，再用 <see cref="DateOnly"/> 的普通算术比。</para></summary>
public static class QuickFx
{
    /// <summary>日期范围：把一列时间（或一列日期）按<b>天</b>圈进 [beginDate, endDate] —— <b>两端都含</b>。
    ///
    /// <para>发出的谓词是半开式 <c>date &gt;= beginDate AND date &lt; endDate+1天</c>：
    /// 上界的"+1 天"用不调用方记口径 —— 内核在<b>翻译时</b>把两个边界求值成 <see cref="DateOnly"/>
    /// 并在<b>参数侧</b>算出下一天，列上不挂任何表达式，索引照走。两个后果各有名字：
    /// ① 两颗边界必须<b>能当场读出值</b>（局部变量、闭包、内联常量都算数；拿查询行上的成员当边界
    /// 就得落方言日期算术，当场拒并指路——那档请用 <see cref="DateIntersection"/> 或手写比较）；
    /// ② <paramref name="endDate"/> 传<b>当天</b>，不再像旧 SqlSugar 封装那样由调用方 +1
    /// （端到端等价：求值后的参数与手写过 AddDays(1) 逐字节相同，<c>QuickFxTests</c> 的口径等价钉）。
    /// <paramref name="beginDate"/> &gt; <paramref name="endDate"/> 是合法的空段（区间的空集），照发不拦；
    /// <paramref name="endDate"/> 传 <see cref="DateOnly.MaxValue"/> 时上界那半边不发（它没有下一天）。</para>
    ///
    /// <para><paramref name="date"/> 实参的列可空时 NULL 行不中 —— 与手写 <c>&gt;=</c> 是同一句 SQL 比较语义。</para></summary>
    public static bool DateRange(object date, DateOnly beginDate, DateOnly endDate)
        => throw new NotSupportedException(
            "QuickFx.DateRange 只活在查询表达式树里（会被翻译成 SQL 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里过滤，先 ToList()/ToListAsync() 落到内存，再用日期算术比较。");

    /// <summary>日期交集：实体存的一段 [beginDate, endDate]（<b>闭端</b>）与用户选的一段
    /// [beginDate2, endDate2]（<b>闭端</b>）有没有同一天。发出的谓词是
    /// <c>beginDate &lt;= endDate2 AND endDate &gt;= beginDate2</c> —— 两段相触的那一天算相交
    /// （闭端区间的数学）。上界与下界都是表达树里的自由实参（列、闭包、行成员都行），不计边界算术。</summary>
    public static bool DateIntersection(DateOnly beginDate, DateOnly endDate, DateOnly beginDate2, DateOnly endDate2)
        => throw new NotSupportedException(
            "QuickFx.DateIntersection 只活在查询表达式树里（会被翻译成 SQL 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里过滤，先 ToList()/ToListAsync() 落到内存，再按闭端区间算。");

    /// <summary>时间段 × 日期段的交集：实体两端是 <see cref="DateTime"/>（<b>闭端</b>），
    /// 用户给的是 <see cref="DateOnly"/> 日起止（<b>闭端</b>）。发出的谓词与 <see cref="DateIntersection"/>
    /// 同一形状：<c>beginDate &lt;= endDate2 AND endDate &gt;= beginDate2</c>。
    /// 两颗 <see cref="DateOnly"/> 实参按本家的 date 绑参（每家 Values 的 DateOnly 臂），不经列的存储型转换。</summary>
    public static bool DateTimeIntersection(DateTime beginDate, DateTime endDate, DateOnly beginDate2, DateOnly endDate2)
        => throw new NotSupportedException(
            "QuickFx.DateTimeIntersection 只活在查询表达式树里（会被翻译成 SQL 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里过滤，先 ToList()/ToListAsync() 落到内存，再按闭端区间算。");

    // ── 行号四颗（投影形状） ──────────────────────────────────────────

    /// <summary>行号：整集按 <paramref name="orderKey"/> 排，第一行发 1、往下连续 ——
    /// <c>ROW_NUMBER() OVER (ORDER BY &lt;orderKey&gt; ASC)</c>。</summary>
    public static int RowNumber(object orderKey)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");

    /// <summary>同上，<paramref name="descending"/> 决定那一列的序（<see langword="true"/> 落 <c>DESC</c>）。
    /// 这颗必须<b>当场读得出值</b>：序是句子的形状，不是每行各问一次的东西，拿行上的成员当方向当场拒。</summary>
    public static int RowNumber(object orderKey, bool descending)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");

    /// <summary>分行号：按 <paramref name="partitionKey"/> 分堆，每堆各自从 1 数起 ——
    /// <c>ROW_NUMBER() OVER (PARTITION BY &lt;partitionKey&gt; ORDER BY &lt;orderKey&gt; ASC)</c>。
    /// 分堆与排序的判据都是<b>句子里的列</b>，所以两侧都不许挂方言函数之外的形状。</summary>
    public static int RowNumber(object partitionKey, object orderKey)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");

    /// <summary>分行号带方向，与前两颗同一档分工：<paramref name="descending"/> 读的是 <paramref name="orderKey"/>
    /// 那一列的方向，<paramref name="partitionKey"/> 不参与排序。</summary>
    public static int RowNumber(object partitionKey, object orderKey, bool descending)
        => throw new NotSupportedException(
            "QuickFx.RowNumber 只活在查询表达式树里（会被翻译成 ROW_NUMBER() OVER (…) 交给库执行），方法体永远不该被真的调到。" +
            "要在内存里编号，先 ToList()/ToListAsync() 落到内存，再按下标自己数。");
}
