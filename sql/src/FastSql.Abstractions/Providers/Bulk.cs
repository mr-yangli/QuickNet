using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using FastSql.Metadata;

namespace FastSql.Providers;

/// <summary>批量写入走哪一层（§12.2 那张阶梯）。<b>只有今天真有实现的层才进这颗枚举</b>：
/// §12.2 的 L2 数组绑定、L4 临时表、MERGE 那一整档在这里都没有成员，因为加一颗点不到货的名号
/// 只会让 <c>BulkReport.Method</c> 多一个永不为真的取值，而那条阶梯的全部用处就是"报得出今天走到第几档"。
/// <see cref="Auto"/> 不是"另一层"，是"由这一条出口自己从上往下取第一个满足条件的层"（判据见 <c>InsertEngine.Decide</c>）。</summary>
public enum BulkMethod
{
    /// <summary>从上往下取第一个满足条件的层：装载通道 → 多值语句。</summary>
    Auto,

    /// <summary>§12.1 的 L1「批量装载通道」：不走 SQL 文本、不走参数（SQL Server 的 <c>SqlBulkCopy</c>）。</summary>
    CopyChannel,

    /// <summary>§12.1 的 L3 多值语句：一条 <c>INSERT … VALUES (…),(…)</c>，按参数预算分块（§12.3）。</summary>
    MultiValue,

    /// <summary>§12.1 的 L5 逐条：同一份文本反复执行，一次往返一行。阶梯的兜底档，永远可达。</summary>
    SingleRow,
}

/// <summary>请求的层拿不到时怎么办（§12.2）。默认 <see cref="DegradeAndLog"/>：批量在绝大多数现场是纯优化路径，
/// 挡在门外比放进来更糟；但"静默逐条"必须是能被订阅出来的，否则一次 600 行的保存悄悄变成 600 次往返，
/// 而现场只看得到"它跑通了"。<b>这一格里那句 <c>LogLevel.Warning</c> + OTel 标签今天没有对象</b>
/// （库里没有日志器也没有 OTel），留痕的落地形态是 <see cref="Aop.FastSqlAop.BulkDegraded"/> 那一个订阅位。</summary>
public enum FallbackPolicy
{
    /// <summary>降级，并在 <see cref="Aop.FastSqlAop.BulkDegraded"/> 上留一次痕（未订阅时零成本，§10.1 硬规则 2）。</summary>
    DegradeAndLog,

    /// <summary>降级且不声张：只在批量本身是缓存回填那一类纯优化路径时用。</summary>
    DegradeSilently,

    /// <summary>拿不到请求的层就抛。给 CI 与上线前压测用 —— §12.2 那句"唯一能防住静默逐条的手段"。</summary>
    Never,
}

/// <summary>一次批量写到底走了哪一层、付了几次往返、为什么（§12.1 的 <c>BulkPlan</c> 那一格：纯函数、可单测、可打印）。
/// <see cref="Reason"/> 必须是给人看的一句话，形状与 <c>InsertPlan.Reason</c> 同一条纪律（现场先看到"为什么是 5 次往返"，
/// 而不是去读源码）。<see cref="Requested"/> 与 <see cref="Method"/> 分开记，因为 <c>Requested != Method</c>
/// 正是 §12.2 要三处留痕的那一件事。</summary>
public sealed record BulkReport(
    BulkMethod Requested,
    BulkMethod Method,
    int RowCount,
    int RoundTrips,
    int ChunkSize,
    bool Degraded,
    string Reason);

/// <summary>递给装载通道的一批数据。<b>值已经是驱动侧的形态</b>（<c>null</c> 是 <see cref="DBNull"/>、枚举已经折成底层整数），
/// 由内核过 <see cref="Values.IValueBinder"/> 那一扇门 —— 通道不许自己再猜一遍类型，否则"同一条插入在几家参数化形状不同"
/// 那句话就有第二个出处。代价是这一趟每格多一次 <see cref="Sql.AdoParameter"/> 装箱，记在 <c>SqlServerBulkCopy</c> 的抬头。</summary>
/// <param name="DestinationTable">已经按方言取号限定过的目标表名（由 <see cref="ISqlDialect.EmitTableName"/> 产出，
/// 所以 COPY 落的那一张与 <c>INSERT</c> 写的必然是同一张，§9.2 那句"建出来的表与查的表是同一张"在这里有了第三个读者）。</param>
/// <param name="Rows">一行一个数组，数组下标与 <paramref name="Columns"/> 下标对齐。<b>行序就是入参顺序</b>，
/// 与 §12.4 第 1 项对文本层提的同一要求：装载通道没有回包可对，于是"进去的第 n 行是哪一行"只有这一处真相。</param>
public sealed record BulkCopyRequest(
    string DestinationTable,
    IReadOnlyList<ColumnContract> Columns,
    IReadOnlyList<object?[]> Rows);

/// <summary>装载通道借用执行层的那三格：一条连接、它上面可能挂着的事务、超时。<b>与
/// <see cref="BulkCopyRequest"/> 分成两颗，是因为来路不同</b>：数据那一半归链式对象（取值段算一次），
/// 连接那一半只在执行的一刻才知道（环境事务 / 会话连接 / 工厂 / 新建四支，见 <c>AdoExecutor</c>）。
/// 分错了就是一句"批量插入对象自己持有连接"，而 §3 的第 5 层不许被上面几层看见。</summary>
public sealed record BulkConnection(DbConnection Connection, DbTransaction? Transaction, int TimeoutSeconds);

/// <summary>§12.1 的 L1「批量装载通道」在 SPI 上的形状。<b>签名里没有驱动类型</b>：给的是一条已经开好的
/// <see cref="DbConnection"/> 与它上面可能挂着的事务，所以 provider 内部怎么找到自家的装载类（反射、缓存委托、
/// 或直接强类型）都是它自己的事，共用层与其余六家一个字都不必知道。
/// <see cref="WriteAsync"/> 交回的是"这一批真进去了多少行"，通道给不出这个数时按 §12.1 那句"失败不能定位到行"
/// 处理：宁可抛，也不要报一个看着对的数。</summary>
public interface IBulkCopyChannel
{
    /// <summary>这一列能不能进装载通道。<b>假是一等公民</b>：一列不合格时整批退回上一层（L3），
    /// 而不是把那一列从通道里省掉 —— 省一列等于那一列的值不进库，那是丢写，与
    /// <see cref="IChangeGuard.Compares"/> 那句"返回 false 时调用方必须整批退回"同一个道理。</summary>
    bool SupportsColumn(ColumnContract column);

    [RequiresUnreferencedCode("装载类不在 DbProviderFactory 的抽象面上，只能按类型名在驱动程序集里找；被裁剪的驱动会在这里报通道不存在。")]
    [RequiresDynamicCode("与 RequiresUnreferencedCode 同一条：反射解析驱动类型需要运行时生成代码。")]
    Task<int> WriteAsync(BulkConnection target, BulkCopyRequest request, CancellationToken ct);
}
