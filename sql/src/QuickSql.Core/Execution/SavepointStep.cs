namespace QuickSql.Execution;

/// <summary>保存点三步：<b>创建</b> ∶ <b>回退</b> ∶ <b>释放</b>，与 ADO.NET 那三颗
/// <c>DbTransaction.Save</c> ∶ <c>Rollback(name)</c> ∶ <c>Release(name)</c> 一一对应。
/// 它存在只有一个理由：文本那一路要把"哪一句"传给方言的三个发射口，而驱动那一路已经有三个方法名了。
///
/// <para><b>为什么没有"提交"与"回滚"这两步</b>：那是根事务的事，走 <c>DbTransaction.Commit()</c> 与
/// <c>Rollback()</c>（无参那两颗），与保存点无关。<b>为什么没有"嵌套的嵌套"这一类</b>：三步对每一层都是
/// 同样三步，只是名字不同 —— 名字的计数器长在外层事务上，见 <c>QuickSqlTransaction._nextSavepointId</c>。</para></summary>
internal enum SavepointStep
{
    Create,
    Rollback,
    Release
}
