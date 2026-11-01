namespace QuickJob;

/// <summary>Point-in-time counters, plus live queue gauges (pending / running) for the dashboard.</summary>
public sealed record QuickJobStats(
    long RunsStarted,
    long RunsCompleted,
    long RunsFailed,
    long RunsRetried,
    long RunsSkipped,
    long RunsCancelled,
    int PendingRuns,
    int RunningRuns);

/// <summary>The active book behind <see cref="QuickJobStats"/> — counters update with Interlocked, snapshots forge the record.</summary>
internal sealed class StatsBucket
{
    internal long Started;
    internal long Completed;
    internal long Failed;
    internal long Retried;
    internal long Skipped;
    internal long Cancelled;

    internal QuickJobStats Snapshot(int pending, int running) => new(
        Interlocked.Read(ref Started),
        Interlocked.Read(ref Completed),
        Interlocked.Read(ref Failed),
        Interlocked.Read(ref Retried),
        Interlocked.Read(ref Skipped),
        Interlocked.Read(ref Cancelled),
        pending,
        running);
}

/// <summary>One attempt's live/read-only view — shared by the dashboard ("running" and "pending" lists).</summary>
public sealed record RunState
{
    /// <summary>Run correlation id (retries keep it).</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Job name.</summary>
    public required string JobName { get; init; }

    /// <summary>1-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>What produced this run.</summary>
    public required JobTrigger Trigger { get; init; }

    /// <summary>The claimed slot.</summary>
    public required DateTimeOffset ScheduledAt { get; init; }

    /// <summary>When the attempt began (null while still queued).</summary>
    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>The note the handler wrote via <see cref="JobRunContext.Report"/> (dashboard reading).</summary>
    public string? Progress { get; internal set; }
}
