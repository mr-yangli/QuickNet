namespace QuickSql.Transactions;

/// <summary>事务句柄（§6 的形态 (1) 与 (3)）。
/// <b>同步 <see cref="IDisposable"/>，不是 <c>IAsyncDisposable</c>，也没有 <c>await orm.TransactionAsync(...)</c></b>
/// —— 两处都和规格原稿不同，理由都是硬事实而不是口味：
/// 一是 ADO.NET 侧 <c>DbConnection.BeginTransaction</c> 与 <c>DbTransaction.Commit/Rollback</c> 根本没有异步重载，
/// 一个 <c>TransactionAsync</c> 里能 await 的只有 <c>connection.Open()</c>；
/// 二是形态 (1) 的示例里后续语句不带句柄参数（<c>orm.Insert(order).ExecuteAffrowsAsync(ct)</c>），
/// 也就是依赖 AsyncLocal 传播，而实测在 <c>await</c> 之前<b>或之后</b>于被调方内写 <c>AsyncLocal</c>，
/// 调用方都看不见，只有<b>同步被调方</b>的写入会回到调用方的帧 —— 于是 <c>await orm.TransactionAsync()</c>
/// 会得到一个"对象拿到了、后面的写入却没进事务"的句柄，那正是本仓库拒绝的"看起来能用"。
/// begin 既然必须同步，恢复槽位的 <c>Dispose</c> 也必须同步（异步 Dispose 在另一个帧里执行，等于没恢复）。
/// 规格 §6 的示例已按这个形状改写（v0.12 回写）。</summary>
public interface IQuickSqlTransaction : IDisposable
{
    /// <summary>已提交或已回滚。结束之后 <see cref="Commit"/> 抛、<see cref="Rollback"/> 静默（幂等）。</summary>
    bool Completed { get; }

    /// <summary>SAVEPOINT 嵌套事务（<c>true</c>）还是根事务（<c>false</c>）。
    /// 嵌套事务的 <see cref="Commit"/> 释放保存点、<see cref="Rollback"/> 回滚到保存点，只有根事务真正提交或回滚底层连接。</summary>
    bool IsSavepoint { get; }

    void Commit();

    void Rollback();
}
