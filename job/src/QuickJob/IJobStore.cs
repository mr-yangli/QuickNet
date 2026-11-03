namespace QuickJob;

/// <summary>
/// A job definition as the store sees it: everything except the code-registered handler,
/// serialised as a JSON contract. Restoring a definition at boot means mapping the name back
/// onto a handler that exists in the live registry — the payload/schedule survive, code does not.
/// </summary>
public sealed record StoredDefinition
{
    /// <summary>Job name (store key).</summary>
    public required string Name { get; init; }

    /// <summary>JSON of <see cref="JobDefinition"/> (schedule + options, no handler).</summary>
    public required string Contract { get; init; }
}

/// <summary>A due run waiting to be picked up. Payload rides along as JSON text (or null).</summary>
public sealed record StoredDueRun
{
    /// <summary>Stable run id — retries keep it, so log correlation survives attempts.</summary>
    public Guid RunId { get; init; }

    /// <summary>Job name.</summary>
    public required string JobName { get; init; }

    /// <summary>The slot this run claims.</summary>
    public required DateTimeOffset DueAt { get; init; }

    /// <summary>1-based attempt counter (1 = first run).</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The origin driver (CronTick / IntervalTick / Scheduled / Manual / Enqueued / Retry).</summary>
    public required JobTrigger Origin { get; init; }

    /// <summary>Raw JSON payload text, null when the run carries none.</summary>
    public string? Payload { get; init; }
}

/// <summary>The outcome of <see cref="IJobStore.FinishLeaseAsync"/>.</summary>
public enum DueRunOutcome
{
    /// <summary>Ran to completion (the store may forget the lease).</summary>
    Completed = 1,

    /// <summary>Ran in failure without a retry planned afterwards.</summary>
    FailedFinal = 2,

    /// <summary>Ran in failure and the scheduler has queued the next attempt (lease can be dropped).</summary>
    FailedRetryPlanned = 3,

    /// <summary>Dropped before running (gate policy, cancellation, pause).</summary>
    Skipped = 4,
}

/// <summary>A run claimed by a scheduler instance for the duration of one attempt.</summary>
public sealed record LeasedDueRun
{
    /// <summary>The run payload.</summary>
    public required StoredDueRun Run { get; init; }

    /// <summary>Opaque lease token handed back to <see cref="IJobStore.FinishLeaseAsync"/>.</summary>
    public required Guid LeaseId { get; init; }
}

/// <summary>One finished attempt, as recorded in history.</summary>
public sealed record ExecutionRecord
{
    /// <summary>Run correlation id (shared across attempt retries — <see cref="Attempt"/> disambiguates).</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Job name.</summary>
    public required string JobName { get; init; }

    /// <summary>The claimed slot.</summary>
    public required DateTimeOffset ScheduledAt { get; init; }

    /// <summary>When the attempt actually began (null: never started — dropped before dispatch).</summary>
    public required DateTimeOffset? StartedAt { get; init; }

    /// <summary>When the attempt ended (same null rule).</summary>
    public required DateTimeOffset? FinishedAt { get; init; }

    /// <summary>1-based attempt number.</summary>
    public required int Attempt { get; init; }

    /// <summary>Origin driver name.</summary>
    public required JobTrigger Origin { get; init; }

    /// <summary>Outcome tag (Completed / Failed / Retrying / Cancelled / Skipped:…).</summary>
    public required string? Outcome { get; init; }

    /// <summary>First exception line, when failure or retry.</summary>
    public string? Error { get; init; }

    /// <summary>Handler-written progress note, surfaced on the dashboard.</summary>
    public string? Progress { get; init; }
}

/// <summary>Read filter for history.</summary>
public sealed record ExecutionQuery
{
    /// <summary>Rows to return, newest first. Default 200.</summary>
    public int Take { get; init; } = 200;

    /// <summary>Restrict to one job.</summary>
    public string? JobName { get; init; }
}

/// <summary>
/// The scheduler-facing seam for persistence and cross-instance coordination. The in-memory
/// implementation ships in this package; a node-coordinated implementation (SQLite / SQL Server) is
/// a matter of implementing the same five verbs — plus the two optional read/remove faces below,
/// which the dashboard uses for runs that have not come due yet. Preserve these shapes when
/// implementing it: the claim (PutDueRun → ClaimDueRuns → FinishLease) is where cross-node CAS lives.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// Raises when the store wants the scheduler to attempt a claim: new due runs arrived, or
    /// previously-future runs crossed their due time. An in-memory store raises it instantly;
    /// a database-backed store raises it on its own poll cadence — both are correct.
    /// Calling <see cref="QuickJobScheduler.StartAsync"/>/<see cref="QuickJobScheduler.StopAsync"/>
    /// wires the attached scheduler to it.
    /// </summary>
    event Action? RunsPending;

    /// <summary>Creates backing structure if any. Called once before the first read/write.</summary>
    ValueTask InitializeAsync(CancellationToken ct = default);

    /// <summary>Upserts a definition contract (code-registered and dashboard-created definitions both flow here).</summary>
    ValueTask SaveDefinitionAsync(StoredDefinition definition, CancellationToken ct = default);

    /// <summary>All stored definitions, name-keyed (newest write wins per name).</summary>
    ValueTask<IReadOnlyList<StoredDefinition>> LoadDefinitionsAsync(CancellationToken ct = default);

    /// <summary>Removes one definition by name (no-op when absent).</summary>
    ValueTask DeleteDefinitionAsync(string name, CancellationToken ct = default);

    /// <summary>Enqueues a due run. <see cref="RunsPending"/> follows.</summary>
    ValueTask PutDueRunAsync(StoredDueRun run, CancellationToken ct = default);

    /// <summary>Atomically claims up to <paramref name="max"/> runs whose <see cref="StoredDueRun.DueAt"/> has arrived.
    /// A claimed run must be invisible to other claimers until lease finishes (or expires).</summary>
    ValueTask<IReadOnlyList<LeasedDueRun>> ClaimDueRunsAsync(string? instanceId, int max, CancellationToken ct = default);

    /// <summary>Optional read face: runs still unclaimed, soonest due first — the dashboard's 未到站 column.
    /// Default is an empty list, so a store that cannot answer simply shows nothing.</summary>
    ValueTask<IReadOnlyList<StoredDueRun>> PeekDueRunsAsync(int take, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<StoredDueRun>>([]);

    /// <summary>Optional removal face: forgets an unclaimed run and hands it back so the caller can record
    /// the drop; null when the run is absent (already claimed or gone). Default declines, which leaves
    /// cancelling a not-yet-due run to the claim-time cancellation marker.</summary>
    ValueTask<StoredDueRun?> DropDueRunAsync(Guid runId, CancellationToken ct = default)
        => ValueTask.FromResult<StoredDueRun?>(null);

    /// <summary>Releases a lease with its outcome — the run is now history's business.</summary>
    ValueTask FinishLeaseAsync(LeasedDueRun lease, DueRunOutcome outcome, CancellationToken ct = default);

    /// <summary>Appends one finished attempt to history.</summary>
    ValueTask AppendExecutionAsync(ExecutionRecord record, CancellationToken ct = default);

    /// <summary>Reads history newest-first.</summary>
    ValueTask<IReadOnlyList<ExecutionRecord>> ReadExecutionsAsync(ExecutionQuery? query = null, CancellationToken ct = default);

    /// <summary>Trims history older than the instant.</summary>
    ValueTask TrimExecutionsAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
