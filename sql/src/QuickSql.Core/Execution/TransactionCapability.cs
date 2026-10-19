namespace QuickSql.Execution;

using QuickSql.Providers;

/// <summary>「事务可选」那颗位的唯一取法（【本笔（ClickHouse 重开·笔一）新增】）。
/// <see cref="DbCapabilities.Transactions"/> 从今天起是真分派位，读它的格子有四处，判词必须只有一种写法：
/// <see cref="Declared"/>。四处各管一半：<b>公开的 <c>AdoExecutor.BeginTransaction</c> 管拒绝的那一半</b>——没有位就抛（照 <see cref="IDbProvider.CanCreateDatabase"/>=false 先例的
/// <see cref="NotSupportedException"/> 带明话，严禁静默假事务）；<b>内核三条写路径（SaveChanges ∶
/// InsertOrUpdate ∶ 三条批量链的执行段）管放行的那一半</b>——没有位就不包事务，逐句按库的 autocommit 直发。
///
/// <para>分岔依据照 §4 那条"分派点必须落在被读的那一格，不是被声明的那一格"补齐：此前全 <c>src/</c>
/// 对这份掩码的真实读者只有 <c>SchemaSyncer</c> 那颗 <see cref="DbCapabilities.Alter_Column_Precision"/>，
/// "能力位零消费者"那句旧账在位表上没有兑现——它是靠把读者立起来结掉的，不是靠把位表改掉。</para>
///
/// <para>行为诚实条款（没有这一位时）：一次 SaveChanges / InsertOrUpdate / 批量里多条语句<b>不原子</b>——
/// 中途失败已发出去的语句不回滚；单条语句自身的事务性按各家库的文档记账。全文同一段落也住在位注释与
/// 用法册 §2.4，三处同源，改判必须三处一起动。</para></summary>
internal static class TransactionCapability
{
    /// <summary>声明了 <see cref="DbCapabilities.Transactions"/> 才允许内核开一条真的
    /// <see cref="System.Data.Common.DbTransaction"/>；mask 位上的请求总是最便宜的——一次按位与，没有 IO、
    /// <b>不碰驱动</b>（TryEnsureLoaded 那一道在它之后才轮得到跑）。</summary>
    internal static bool Declared(IDbProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return (provider.Capabilities & DbCapabilities.Transactions) != 0;
    }
}
