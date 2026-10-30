namespace QuickSql;

/// <summary>一只会话同时只许一个操作在飞的那颗闸门。EF Core 的 <c>ConcurrencyDetector</c> 是同一条语义
/// （2026-10-30 落地，§14 P1 那颗"要不要守卫"就此裁决：参考 EF Core）：<see cref="Enter"/> 用一颗
/// <see cref="Interlocked"/> 整数抓闸，抓不到（上一个操作还在飞）就抛 —— <b>拒绝必须是一句听得懂的话</b>，
/// 而不是跟踪器深处一颗随机 NRE。落地前的本机实测（SQL Server LocalDB，各循环十遍）就是它要换掉的那两格：
/// 同一会话并发读 6/10 在 <c>ChangeTracker.DetectChanges</c> 炸 NRE、4/10 静默；同一会话并发写 10/10
/// 把 2 行双倍落成 4 行，全程无一异常 —— 最后那格比崩溃危险一个量级，因为只有对账才看得见。
///
/// <para><b>EF Core 同款的两句实话要带在身上</b>：① <b>尽力检测</b> —— 上一个操作收尾<em>之后</em>才进场
/// 的第二个操作抓不到（那是顺序使用，本来就该放行）；② <b>抓到即拒</b> —— 抓到的那一次两边语义都确定，
/// 谁也不会悄悄改坏谁的数据。所以这句结论不因闸门存在而松动：<b>不要共用会话，而不是指望闸门兜底</b>。</para>
///
/// <para><b>闸口收在七个读出口与两个写口</b>：读侧是 <c>QueryEngine</c> 的七个公开执行口
/// （List/Array/Capped/Aggregate/Any/Paged/Stream —— <c>FirstAsync</c> 走 <c>CappedAsync</c>，
/// 不另设闸），写侧是 <c>DbContext.SaveChangesAsync</c> 与 <c>DbSet.LoadAsync</c> 的补载。
/// 根链（<c>orm.Select&lt;T&gt;()</c> 与 <c>Orm.Insert</c> 那一族）没有会话，天然不进闸 ——
/// 并发的根链各是各的操作，这正是 <c>ThreadConcurrencyPathTests</c> 第一颗钉住的那条正道。
/// 闸口嵌套不设重入：七个读口彼此不调用（<c>PagedAsync</c> 走私有 <c>Rows</c> 与私有 <c>TotalCount</c>，
/// 拆分 include 的子读在私有路径里过 <c>Ado</c> 直连，都不再进闸）。</para></summary>
sealed class SessionGate
{
    int _busy;

    /// <summary>进场。已有操作在飞就抛：同一只会话不接受第二个并发操作。</summary>
    public void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new QuickSqlException(
                "同一只 DbContext 上开出了第二个操作，而上一个还没有完：DbContext 不是线程安全的，"
                + "并发请各用各的会话（各 new 各的，或各开各的流）。这颗闸门是尽力检测：抓到即拒，"
                + "但不保证抓到所有重叠 —— 所以不要共用，而不是指望它兜底。");
    }

    /// <summary>出场。与 <see cref="Enter"/> 一一配对（try/finally 那一对）。</summary>
    public void Exit() => Interlocked.Exchange(ref _busy, 0);
}
