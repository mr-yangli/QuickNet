using FastSql.Metadata;

namespace FastSql.Aop;

/// <summary>钩子分类学（§10.1）：类别决定能不能改 SQL、异常后果、以及能否在 <c>Explain()</c> 里回放。
/// P0 只有观察类全量 + 改写类的 <see cref="AuditValue"/> 与 <see cref="ParameterBinding"/>。</summary>
public enum HookCategory { Observe, Rewrite, Decide }

/// <summary>硬规则 2「未订阅即零成本」由这些 bool 兑现：先判订阅，再决定要不要构造事件对象。</summary>
public sealed class FastSqlAop
{
    // ── 观察类：异常只进 Error，不中断执行（硬规则 3）
    public Action<CommandLogEvent>? Executing;
    public Action<CommandLogEvent>? Executed;
    public Action<CommandErrorEvent>? Error;
    public Action<SlowCommandEvent>? SlowQuery;
    public Action<MaterializedEvent>? Materialized;

    // ── 改写类：值级改写，按注册顺序串联
    public Action<AuditValueEvent>? AuditValue;
    public Action<ParameterBindingEvent>? ParameterBinding;

    /// <summary>整句文本级的改写槽位（软删、影子表、全局注释）。<b>v0.18 起它有发射点了</b>：
    /// <c>StatementEmitter.ApplyCud</c>，写侧三条链与跟踪器的两个发射口都走那里，位置在 AST 已成文之后、
    /// <see cref="ParameterBinding"/> 之前。读侧的 <c>QueryBuilding</c> 与 <c>CommandCreating</c> 仍是桩
    /// （§14 的 P1 那一格），黄金样本里断言"钩子表为空时产出不变量"的那 3 条继续有效。</summary>
    public Action<CudBuildingEvent>? CudBuilding;

    // ── 决策类：可取消 / 可接管（§10.1）
    /// <summary>执行前最后一道闸门：返回 <c>true</c> = 取消执行，抛 <see cref="FastSqlCommandCancelledException"/>。
    /// 典型用法是拦没有 WHERE 的 UPDATE/DELETE（生产禁整表写硬闸），也可以做租户级禁写、审计拦截等。
    /// <b>零成本规则</b>：未订阅时 <see cref="HasDeciders"/> 为 false，执行路径上只走一次分支预测。</summary>
    public Func<CommandTerminatingEvent, bool>? CommandTerminating;

    public ICacheAdapter? Cache { get; set; }

    public long SlowQueryThresholdMilliseconds { get; set; } = 500;

    public bool HasObservers => Executed is not null || Error is not null || SlowQuery is not null || Materialized is not null;

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

public sealed record CommandLogEvent(
    Sql.DialectSql Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames,
    long ElapsedMilliseconds,
    int AffectedRows,
    int? FirstRowSummary)
{
    public Guid CommandId { get; init; } = Guid.NewGuid();
}

public sealed record CommandErrorEvent(Exception Exception, string Sql, long ElapsedMilliseconds);

public sealed record SlowCommandEvent(Sql.DialectSql Sql, long ElapsedMilliseconds, long ThresholdMilliseconds);

public sealed record MaterializedEvent(object Entity, string EntityTypeName, int OrdinalInBatch);

/// <summary>列级自动填充。零装箱的做法是回调只在「该列需要填充」时触发，且 <see cref="SetValue"/> 直接写回实体字段。</summary>
public sealed class AuditValueEvent : IEquatable<AuditValueEvent>
{
    readonly Action<object?> _setter;

    internal AuditValueEvent(object entity, ColumnContract column, object? currentValue, Action<object?> setter)
    {
        Entity = entity;
        Column = column;
        CurrentValue = currentValue;
        _setter = setter;
    }

    public object Entity { get; }

    public ColumnContract Column { get; }

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
/// 订阅者拿到这只对象后返回 <c>true</c> = 取消执行（抛 <see cref="FastSqlCommandCancelledException"/>），
/// 返回 <c>false</c> 或 <c>null</c> 订阅 = 放行。典型用法：拦没有 WHERE 的 UPDATE/DELETE、租户级禁写。</summary>
public sealed record CommandTerminatingEvent(
    string Sql,
    string OperationKind,
    IReadOnlyList<string> TableNames,
    IReadOnlyList<Sql.AdoParameter> Parameters);

/// <summary><c>CommandTerminating</c> 钩子返回 <c>true</c> 时抛出的异常。
/// 继承 <see cref="FastSqlException"/>，调用方可以按这一档单独 catch。</summary>
public sealed class FastSqlCommandCancelledException : FastSqlException
{
    public FastSqlCommandCancelledException(string message)
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
