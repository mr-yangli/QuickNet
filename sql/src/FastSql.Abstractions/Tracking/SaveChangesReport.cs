using FastSql.Aop;

namespace FastSql.Tracking;

/// <summary>一条 <c>SaveChanges</c> 里做的那件事（§8.2 的「按 FK 依赖拓扑排序 → 先 Insert、再 Update、最后 Delete」）。
/// 只有这三档：<c>Unmodified</c>/<c>Detached</c> 的实例不进报告，所以报告里没有第四种操作可表达。</summary>
public enum WriteOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>一次写操作的记账（§8.2 要的「每实体每操作的受影响行数与生成的 SQL」）。
/// <see cref="SaveChangesOp.StatementCount"/> 与 <see cref="SaveChangesOp.Rows"/> 分开是因为这两件事
/// 在批量合并之前<em>必然不相等</em>：一百条同结构实体的 <c>Update</c> 今天是一百条语句
/// （<b>v0.16 接的是<em>报告侧</em>的按列掩码分桶</b>——同一批里脏列集合相同的那些合进一条记录，
/// 而<em>语句一条都没合</em>，所以一个桶的 <c>StatementCount</c> 就等于它装了几条；
/// 那一句「100 个同结构实体 SaveChanges 只发 1 条 UPDATE」是 §12.5 的验收项，今天没过，
/// 归属记在 §14），只有一个数的报告会让人以为已经合过了。</summary>
/// <param name="Rows">这一档覆盖的行数。<b>三档的来源各不相同，所以别把它们相加以后当"库动了多少行"</b>：
/// <see cref="WriteOperation.Insert"/> 上是<em>进语句的行数</em>（带 <c>OUTPUT</c> 的那一档不回 <c>@@ROWCOUNT</c>，
/// 拿不到"库报回几行"，而回填了键就等于证明了行存在），<see cref="WriteOperation.Update"/> 上是
/// <c>@@ROWCOUNT</c>（谓词命中数，不是值真的变了几行），<see cref="WriteOperation.Delete"/> 同样是命中数。</param>
/// <param name="Columns">这一行涉及的列，给的是 <c>ColumnName</c>（SQL 侧的名字，与 <see cref="Sql"/> 同一口径）。
/// <see cref="WriteOperation.Insert"/> 是列清单（自增列不在里面），
/// <see cref="WriteOperation.Update"/> 是<em>脏列</em> —— 这一格就是「脏列最小 UPDATE」的可读见证：
/// 只改备注的那一次，这里只应该有 <c>Note</c> 一个名字。<see cref="WriteOperation.Delete"/> 一律为空，
/// 因为那条语句里没有 <c>SET</c>，条件只有主键。</param>
/// <param name="Sql">语句文本。<b>多块时是第一块的文本而不是全部</b>：一条多值 <c>INSERT</c> 分块之后，
/// 块与块只差行的切片，而报告要能贴进日志被人读掉。逐条的出处是 <c>Aop.Executed</c>（§10.1），
/// 那里才有每一条。</param>
public sealed record SaveChangesOp(
    WriteOperation Operation,
    string Table,
    int Rows,
    int StatementCount,
    IReadOnlyList<string> Columns,
    string Sql);

/// <summary>一次 <c>SaveChanges</c> 的完整决策记录（§8.2）。与写侧三条链的 <c>Explain()</c> 同一取向：
/// <b>先给出"发了什么"，再让人自己去对账</b>，所以这里没有"回滚了没有"这种只有执行层知道的事。
/// 失败也不在这里表达：校验或库错的形态是抛（<see cref="ValidationFailedException"/> 带每行的定位），
/// 一份"哪半没成功"要靠自己捕获异常之后再看这次没有交回来的报告 —— 那笔账 §6 形态 (2) 的
/// <c>TransactionResult</c> 已经在事务那一层记了，不在这里重复一个更弱的版本。</summary>
/// <param name="HookReplay">这一批里<em>改写类</em>钩子实际改过的每一步（§10.1 硬规则 1 的写侧落点）。
/// 与写侧三条链 <c>Explain()</c> 上那一格同一口径：只记真改了的，没订阅、或"看过但没改"都是空。
/// <b>为什么这一格必须在这里，而不是只在线上的 <c>Explain()</c> 上</b>：<c>SaveChanges</c> 发的那些语句是
/// 跟踪器自己拼的（并发列的 <c>WHERE</c> 与 <c>OUTPUT</c> 只存在于这一条路上），三条链的 <c>Explain()</c> 看不见它们 ——
/// 而跟踪器既然有发射点，那些语句就有被改写的<em>事实</em>。一份说不出"最后执行的那一条被改过几步"的报告，
/// 现场排障只能靠猜；<see cref="SaveChangesOp.Sql"/> 是每一步改完之后的最终文本，两者一对上就是回放。</param>
public sealed record SaveChangesReport(IReadOnlyList<SaveChangesOp> Operations, IReadOnlyList<FastSql.Aop.HookTrace> HookReplay)
{
    /// <summary>各档 <see cref="SaveChangesOp.Rows"/> 之和，口径见那里：<b>UPDATE/DELETE 那一档上是"谓词命中数"
    /// 而不是"值真的变了几行"</b>（T-SQL 的 <c>@@ROWCOUNT</c> 语义，与 <c>IUpdate&lt;T&gt;.ExecuteAffrowsAsync</c>
    /// 上那句提醒同一件事）。报告非空而这里是 0 只证明没有行匹配上主键，不证明语句没发过。</summary>
    public int AffectedRows
    {
        get
        {
            var rows = 0;

            foreach (var operation in Operations)
                rows += operation.Rows;

            return rows;
        }
    }

    /// <summary>发出的语句条数（分块按块计）。§12.5 的合并那一格能不能验收，看的就是这个数。</summary>
    public int StatementCount
    {
        get
        {
            var statements = 0;

            foreach (var operation in Operations)
                statements += operation.StatementCount;

            return statements;
        }
    }

    /// <summary>逐行摘要，给人贴进日志。<b>它不是断言</b>：数字与 <see cref="Operations"/> 同源，
    /// 只是排好了版式。</summary>
    public override string ToString()
    {
        var text = $"SaveChanges：{Operations.Count} 个操作、{StatementCount} 条语句、受影响 {AffectedRows} 行。";

        foreach (var operation in Operations)
            text += $"\n  {operation.Operation} {operation.Table}：{operation.Rows} 行 / {operation.StatementCount} 条语句"
                  + (operation.Columns.Count > 0 ? $"（列：{string.Join("、", operation.Columns)}）" : "")
                  + $" {operation.Sql}";

        return text;
    }
}
