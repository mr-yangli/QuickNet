namespace QuickSql.Transactions;

/// <summary>§6 形态 (2) 的返回值：不抛异常、返回码（对齐 SqlSugar 的 <c>UseTran</c>）。
/// <b>没有 <c>ErrorCode</c></b>：规格原稿的那行注释里写了 <c>r.ErrorCode</c>，而 <see cref="System.Data.Common.DbException"/>
/// 这一层没有与库无关的错误码可读（SQL Server 的 <c>SqlException.Number</c>、金仓的 <c>PostgresError.Code</c> 都在派生类型上），
/// 要做就得先给驱动 SPI 加一个 <c>IDbProvider.DescribeError(DbException) → string?</c>。
/// 与其塞一个恒为 null 的槽位，不如先把它记成待决项：真出现"业务要按错误码分支"的需求时连同 SPI 一起做（§14 的 P1 行）。
/// 错误原文已经在 <see cref="Exception"/> 里，分支可以先按 <c>Exception is SqlException</c> 之类由调用方自己做。</summary>
public record TransactionResult
{
    public required bool Success { get; init; }

    /// <summary>失败时是异常原文（含内层消息），成功时为 null。给日志用，不参与分支。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>失败时的事务内首个异常。<b>已经吞掉了，不再向上抛</b>——形态 (2) 的承诺就是"不抛异常"。</summary>
    public Exception? Exception { get; init; }

    internal static TransactionResult Ok() => new() { Success = true };

    internal static TransactionResult Failed(Exception exception)
        => new() { Success = false, ErrorMessage = Text(exception), Exception = exception };

    internal static TransactionResult<T> Ok<T>(T value)
        => new() { Success = true, Value = value };

    internal static TransactionResult<T> Failed<T>(Exception exception)
        => new() { Success = false, Value = default, ErrorMessage = Text(exception), Exception = exception };

    /// <summary>取最内层消息：<c>DbException</c> 常被 <c>TargetInvocationException</c> 一类壳包着，
    /// 只报外层就等于没报。</summary>
    static string Text(Exception exception)
    {
        var inner = exception;

        while (inner.InnerException is { } next)
            inner = next;

        return inner.Message;
    }
}

/// <summary>带业务返回值的形态 (2)。<see cref="Value"/> 只在 <see cref="TransactionResult.Success"/> 为真时有意义：
/// 回滚之后的 <c>Value</c> 是 <c>default</c>，不是"业务算出来的那个数"。</summary>
public sealed record TransactionResult<T> : TransactionResult
{
    public T? Value { get; init; }
}
