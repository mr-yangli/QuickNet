using QuickSql.Metadata;

namespace QuickSql.Aop;

/// <summary>钩子分类学（§10.1）：类别决定能不能改 SQL、异常后果、以及能否在 <c>Explain()</c> 里回放。
/// P0 只有观察类全量 + 改写类的 <see cref="AuditValue"/> 与 <see cref="ParameterBinding"/>。</summary>
public enum HookCategory { Observe, Rewrite, Decide }

/// <summary>硬规则 2「未订阅即零成本」由这些 bool 兑现：先判订阅，再决定要不要构造事件对象。</summary>
public sealed class QuickSqlAop
{
    // ── 观察类：异常只进 Error，不中断执行（硬规则 3）
    /// <summary>执行前的播报位。<b>发射点只有一处</b>：<c>AdoExecutor.BeforeExecute</c>，位置在
    /// <see cref="CommandTerminating"/> 之后、命令交给驱动之前 —— 顺序是有裁决的：被闸门取消的句子
    /// 不该留下一行"正在执行"，所以先判可否、再播报。
    /// <para><b>它为什么不是 <see cref="CommandLogEvent"/></b>（v0.38 那一版把它记成"声明在场而不响"，
    /// 开这一格的同时要一并改的那个类型）：那一颗记录的三格读数是 <see cref="CommandLogEvent.ElapsedMilliseconds"/>、
    /// <see cref="CommandLogEvent.AffectedRows"/>、<see cref="CommandLogEvent.Route"/>，而播报这一刻
    /// 一个都还不存在 —— 句子还没跑，没有耗时也没有行数，<c>Route</c> 更是要等取连接那一步由
    /// <c>DataSourceRouting.Resolve</c> 才判得出（那颗记录自陈"它报的是结论不是意图"）。填 0 与假路由
    /// 等于让订阅方每次都猜"这一份是读数还是占位"。要耗时、行数与实际走了哪条连接，订
    /// <see cref="Executed"/>；要"这一句即将发出去"（挂住、死锁时唯一还留得下现场的那一位），订这一位。
    /// 零成本规则照旧：未订阅时不构造事件对象。</para></summary>
    public Action<CommandExecutingEvent>? Executing;
    public Action<CommandLogEvent>? Executed;
    public Action<CommandErrorEvent>? Error;
    public Action<SlowCommandEvent>? SlowQuery;
    public Action<MaterializedEvent>? Materialized;
    /// <summary>观察类：<b>这一轮有哪些实体被写出去</b>（§10.1 观察类那一族里最新的一格）。
    ///
    /// <para><b>发射点是会话的写阶段</b>（<c>EntityTracker.WritePhaseAsync</c>）：三档（增/改/删）各一次、
    /// <b>逐只实体</b>派发，<b>且在语句建出来之前</b>。于是订阅者有两件事可做：
    /// ① 观察（发领域事件、发实体变更事件、记审计流水）；② <b>改实体</b>——
    /// 那一格改出来的值会被紧接着建出来的语句带出去（取值段在建语句之前，而取值段读的是实体）。
    /// 这条「改完就写出去」是它与 <see cref="AuditValue"/> 的分工：<c>AuditValue</c> 逐格改值，
    /// 这一位逐只改实体。</para>
    ///
    /// <para><b>零成本规则照旧</b>：未订阅时不构造事件对象。</para>
    /// <para><b>只覆盖会话那一条路</b>：链式的 <c>orm.Insert</c> / <c>orm.UpdateRange</c> / <c>orm.DeleteRange</c>
    /// 不走跟踪器，那三条路上没有这一格（它们各有自己的值钩子 <see cref="AuditValue"/> 与整句钩子
    /// <see cref="CudBuilding"/>）。</para></summary>
    public Action<EntityWrittenEvent>? EntityWritten;
    /// <summary>改写类：<b>软删那一档往上加的 SET 项</b>（列 + 值）。
    ///
    /// <para><b>为什么要有它</b>：软删（<c>orm.Behaviors.SoftDelete</c>）自己只写两格 —— 置位那一格与时间那一格。
    /// 而「谁删的」那一格（操作人、删除原因）是<b>调用方的领域事实</b>，库里替不了：
    /// 库不知道当前用户是谁、也不该知道。这一颗让调用方把那一格往上加，库负责把它参数化、写出去、
    /// 并按与那两格同一把尺子验（自增列与并发列不收）。</para>
    ///
    /// <para><b>逐行触发</b>：有行才触发（<see cref="EntityWrittenEvent.Entity"/> 那一格就是这一行）。
    /// 链式 <c>orm.Delete&lt;T&gt;().Where(...)</c> 那一档没有行可给（它删的是一批行，不是一只），
    /// 那一档不触发这一颗 —— 那一档要自己写那一格。</para>
    ///
    /// <para><b>零成本规则照旧</b>：未订阅时不构造事件对象，SET 清单与原来逐字节同形。</para></summary>
    public Action<SoftDeleteValuesEvent>? SoftDeleteValues;

    /// <summary>批量降级的那一位（§12.2 的 <c>DegradeAndLog</c>）。<b>它存在的理由是一格缺口</b>：
    /// 那一节原稿要求"每一次降级在三处留痕（<c>LogLevel.Warning</c> + OTel 标签 + <c>BulkReport</c>）"，
    /// 而本仓库既没有日志器也没有 OTel，三处里今天只有 <c>BulkReport</c> 是自带的产物 —— 那一句"务必用
    /// <c>FallbackPolicy.Never</c> 在 CI 跑一遍"因此一直没有对象。这一位就是它的落地形态：
    /// 降级这件事从此<em>可订阅</em>，"批量悄悄变成逐条"不再只能靠人读 <c>Reason</c>。
    /// 零成本规则照旧：未订阅时不构造事件对象。</summary>
    public Action<BulkDegradedEvent>? BulkDegraded;

    // ── 改写类：值级改写，按注册顺序串联
    /// <summary>逐格取值时问一次，两个发射点：<b>插入链的取值段</b>（<c>InsertEngine.Prepare</c>）与
    /// <b>整行更新链的取值段</b>（<c>UpdateBulkEngine.Prepare</c>，即 <c>orm.UpdateRange(实体集)</c>）。
    /// 两者靠 <see cref="AuditValueEvent.OperationKind"/> 分派，一条订阅可以同时管创建列与修改列。
    /// <b>点名式的那条更新链（<c>orm.Update&lt;T&gt;().Set(...).Where(...)</c>）与删除链都不问它</b>：
    /// 那两条链上没有"整行的值"这个东西可读，在这里跑一次等于凭空写调用方没说的列 ——
    /// 那一条的改写槽位是 <see cref="ParameterBinding"/>。零成本规则照旧：未订阅就不构造事件对象。</summary>
    public Action<AuditValueEvent>? AuditValue;
    public Action<ParameterBindingEvent>? ParameterBinding;

    /// <summary>整句文本级的改写槽位（软删、影子表、全局注释）。<b>v0.18 起它有发射点了</b>：
    /// <c>StatementEmitter.ApplyCud</c>，写侧三条链与跟踪器的两个发射口都走那里，位置在 AST 已成文之后、
    /// <see cref="ParameterBinding"/> 之前。读侧的 <c>QueryBuilding</c> 与 <c>CommandCreating</c> 仍是桩
    /// （§14 的 P1 那一格），黄金样本里断言"钩子表为空时产出不变量"的那 3 条继续有效。</summary>
    public Action<CudBuildingEvent>? CudBuilding;

    // ── 决策类：可取消 / 可接管（§10.1）
    /// <summary>执行前最后一道闸门：返回 <c>true</c> = 取消执行，抛 <see cref="QuickSqlCommandCancelledException"/>。
    /// 典型用法是拦没有 WHERE 的 UPDATE/DELETE（生产禁整表写硬闸），也可以做租户级禁写、审计拦截等。
    /// <b>零成本规则</b>：未订阅时 <see cref="HasDeciders"/> 为 false，执行路径上只走一次分支预测。</summary>
    public Func<CommandTerminatingEvent, bool>? CommandTerminating;

    public ICacheAdapter? Cache { get; set; }

    public long SlowQueryThresholdMilliseconds { get; set; } = 500;

    public bool HasObservers => Executing is not null || Executed is not null || Error is not null || SlowQuery is not null || Materialized is not null || EntityWritten is not null;

    public bool HasValueRewriters => AuditValue is not null || ParameterBinding is not null;

    public bool HasCudRewriters => CudBuilding is not null;

    public bool HasDeciders => CommandTerminating is not null;

    /// <summary>观察类钩子的异常边界：吞掉但上报，不让日志代码打挂业务写入。</summary>
    public void Observe(Action? hook, CommandLogEvent? context = null)
    {
        if (hook is null)
            return;

        try
        {
            hook();
        }
        catch (Exception ex)
        {
            if (Error is { } error)
                error(new CommandErrorEvent(ex, context?.Sql.Text ?? string.Empty, context?.ElapsedMilliseconds ?? 0));
        }
    }
}

/// <summary>执行前播报的事件（<see cref="QuickSqlAop.Executing"/>）。形状就是"这一刻已知的东西"：
/// 文本与参数（<paramref name="Sql"/> 自己带着）、操作名、涉及的表 —— 三格都是发射点手上确实有的，
/// 没有一格是填了好看的占位。参数值不进日志之外的事：这一位是观察类，改不了句子，
/// 整句文本级的改写槽位是 <see cref="CudBuilding"/>，取消这一句的闸门是 <see cref="CommandTerminating"/>。</summary>
public sealed record CommandExecutingEvent(
    Sql.DialectSql Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames);

/// <summary>一句已执行语句的观察事件。<paramref name="Route"/> 是<b>必填</b>的一格：发射点只有
/// <c>AdoExecutor.Succeeded</c> 一处，而它手上一定有那次借用的路由结论，所以"订阅方看不见这句走了哪条连接"
/// 在这一版是结构上不可能的事，不是还没做。</summary>
/// <param name="Route">这句落在哪座集群、哪个角色、为什么（§16.4）。读写分离与分库最难验收的部分正是
/// "这一句到底走了哪条连接、按哪一档走的"，而翻库比读这一格贵得多。注意它是<em>结论</em>不是<em>意图</em>：
/// <c>FromReplica()</c> 在没配从库的集群上是当场拒而不是静默落主库，所以订阅方看见的理由就是那句语句实际走的路。</param>
public sealed record CommandLogEvent(
    Sql.DialectSql Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames,
    long ElapsedMilliseconds,
    int AffectedRows,
    int? FirstRowSummary,
    DataSourceRoute Route)
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
}

public sealed record CommandErrorEvent(Exception Exception, string Sql, long ElapsedMilliseconds);

public sealed record SlowCommandEvent(Sql.DialectSql Sql, long ElapsedMilliseconds, long ThresholdMilliseconds);

public sealed record MaterializedEvent(object Entity, string EntityTypeName, int OrdinalInBatch);

/// <summary>会话那一轮写出去的实体（<see cref="QuickSqlAop.EntityWritten"/>）。
///
/// <para><b>逐只一次</b>：一只实体在这一轮里只被写一次，所以订阅者收到的事件与「这一轮改了几行」一一对应。
/// <see cref="OperationKind"/> 是 <c>insert</c> / <c>update</c> / <c>delete</c>，与 <c>ExecutionTag</c>
/// 与 <see cref="CudBuildingEvent.OperationKind"/> 同一个词。</para>
/// <para><b>它在语句建出来之前发</b>：订阅者改得动实体，而那一格改出来的值会被紧接着建出来的语句带出去。</para></summary>
public sealed record EntityWrittenEvent(object Entity, string OperationKind);

/// <summary>软删那一档往上加的 SET 项（<see cref="QuickSqlAop.SoftDeleteValues"/>）。
///
/// <para>订阅者往 <see cref="ExtraValues"/> 里加（列, 值）对，库把它们参数化并写进那句 UPDATE 的 SET。
/// 值的形状与 <see cref="AuditValueEvent.SetValue"/> 那一格同形（CLR 值，库负责绑定）。</para>
/// <para><b>列要过与那两格同一把尺子</b>：自增列与并发列不收（前者不接受写，后者只比较不书写）。</para></summary>
public sealed class SoftDeleteValuesEvent
{
    internal SoftDeleteValuesEvent(object entity, string tableName)
    {
        Entity = entity;
        TableName = tableName;
    }

    /// <summary>被删的那一只。</summary>
    public object Entity { get; }

    /// <summary>那张表的表名。</summary>
    public string TableName { get; }

    /// <summary>订阅者往上加的 SET 项（列 + 值）。</summary>
    public IList<KeyValuePair<ColumnContract, object?>> ExtraValues { get; } = [];
}

/// <summary>批量降级事件（<see cref="QuickSqlAop.BulkDegraded"/>）。<b>只在真的降级时派发</b>：
/// 请求 <c>Auto</c> 而拿到的就是阶梯上第一档不算降级，请求了装载通道而那一家根本没有通道才算。
/// <see cref="Reason"/> 与 <c>BulkReport.Reason</c> 是同一句话的两个读者（一个给人看、一个是订阅方要打的字段），
/// 所以两处由 <c>InsertEngine</c> 一次算出、不分头编。</summary>
public sealed record BulkDegradedEvent(
    Providers.BulkMethod Requested,
    Providers.BulkMethod Method,
    int RowCount,
    string Reason);

/// <summary>列级自动填充。零装箱的做法是回调只在「该列需要填充」时触发，且 <see cref="SetValue"/> 直接写回实体字段。</summary>
public sealed class AuditValueEvent : IEquatable<AuditValueEvent>
{
    readonly Action<object?> _setter;

    internal AuditValueEvent(object entity, ColumnContract column, object? currentValue, string operationKind, Action<object?> setter)
    {
        Entity = entity;
        Column = column;
        CurrentValue = currentValue;
        OperationKind = operationKind;
        _setter = setter;
    }

    public object Entity { get; }

    public ColumnContract Column { get; }

    /// <summary>这一格值是为哪一种写操作取的，<c>insert</c> / <c>update</c>，与 <c>ExecutionTag</c>、
    /// <see cref="CudBuildingEvent.OperationKind"/> 同一个词。<b>它是这一颗事件里唯一能把两条链分开的东西</b>：
    /// 一条链上是同一只实体、同一批列，而"创建时间只在新行补、修改时间每次改都盖"这类填充恰恰要分这两条链。
    /// 没有 <c>delete</c> 这一档：删除不取整行的列值，所以它没有可问的格（§10.1 的"没有实体可读的链不跑值钩子"）。</summary>
    public string OperationKind { get; }

    public object? CurrentValue { get; private set; }

    public bool WasSet { get; private set; }

    public void SetValue(object? value)
    {
        CurrentValue = value;
        WasSet = true;
        _setter(value);
    }

    public bool Equals(AuditValueEvent? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

public sealed class ParameterBindingEvent
{
    internal ParameterBindingEvent(Sql.AdoParameter parameter, ColumnContract? column)
    {
        Parameter = parameter;
        Column = column;
    }

    public Sql.AdoParameter Parameter { get; private set; }

    public ColumnContract? Column { get; }

    public void SetValue(object? value) => Parameter = Parameter with { Value = value };

    public void ChangeType(System.Data.DbType dbType, int size) => Parameter = Parameter with { DbType = dbType, Size = size };
}

/// <summary>整句文本级改写的事件。<paramref name="Sql"/> 是已经串到当前的那一份（前一个订阅者的产出），
/// 订阅者要改就整体换掉 <see cref="Rewritten"/>；不写这一格就是"看过但不改"，不产生回放记录。
/// <see cref="OperationKind"/> 是 <c>insert</c> / <c>update</c> / <c>delete</c>，与 <c>ExecutionTag</c> 同一个词。</summary>
public sealed class CudBuildingEvent
{
    public required string OperationKind { get; init; }

    public required Sql.DialectSql Sql { get; init; }

    public Sql.DialectSql? Rewritten { get; set; }
}

/// <summary>决策类钩子的事件载体（§10.1 <c>CommandTerminating</c>）。
/// 订阅者拿到这只对象后返回 <c>true</c> = 取消执行（抛 <see cref="QuickSqlCommandCancelledException"/>），
/// 返回 <c>false</c> 或 <c>null</c> 订阅 = 放行。典型用法：拦没有 WHERE 的 UPDATE/DELETE、租户级禁写。</summary>
public sealed record CommandTerminatingEvent(
    string Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames,
    IReadOnlyList<Sql.AdoParameter> Parameters);

/// <summary><c>CommandTerminating</c> 钩子返回 <c>true</c> 时抛出的异常。
/// 继承 <see cref="QuickSqlException"/>，调用方可以按这一档单独 catch。</summary>
public sealed class QuickSqlCommandCancelledException : QuickSqlException
{
    public QuickSqlCommandCancelledException(string message)
        : base(message)
    {
    }
}

public interface ICacheAdapter
{
    bool TryGet<T>(string key, out T? value);

    void Set<T>(string key, T value, TimeSpan ttl);
}

/// <summary>硬规则 1：已注册钩子在 Explain() 输出里逐个可回放、顺序可见。</summary>
public sealed record HookTrace(int Step, string HookName, HookCategory Category, string Before, string After);
