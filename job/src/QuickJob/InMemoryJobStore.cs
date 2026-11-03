namespace QuickJob;

/// <summary>
/// The default store: everything in process memory, nothing survives a restart, and it is exactly
/// as correct as the store contract demands — claims are atomic under a lock, leases vanish with
/// process death, and <c>RunsPending</c> raises the moment a run enters (or its due time arrives,
/// via the injected <see cref="TimeProvider"/>, which is what makes fake-clock tests fully deterministic).
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _tp;
    private readonly Dictionary<string, StoredDefinition> _definitions = [];
    private readonly List<StoredDueRun> _due = [];
    private readonly Dictionary<Guid, StoredDueRun> _leases = [];
    private readonly List<ExecutionRecord> _history = [];
    private readonly int _historyCapacity;
    private readonly ITimer _pump;

    /// <summary>Creates the store. <paramref name="timeProvider"/> defaults to the system clock;
    /// pass a fake provider to make the claim pipeline dance to test time.
    /// <paramref name="historyCapacity"/> bounds how many finished attempts the ring keeps.</summary>
    public InMemoryJobStore(TimeProvider? timeProvider = null, int historyCapacity = 2000)
    {
        _tp = timeProvider ?? TimeProvider.System;
        _historyCapacity = Math.Max(64, historyCapacity);
        // 单只钟摆：Put 首先把摆推到最近的 due；Claim 把摆拨回"没有在未来 due"或下一个最近 due。
        _pump = _tp.CreateTimer(OnPumpTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised when the pipeline should be drained; store tests never need it — the engine listens.</summary>
    public event Action? RunsPending;

    private void OnPumpTick(object? state) => RunsPending?.Invoke();

    private void ReschedulePumpLocked()
    {
        // 也把"过去的 due"视作立即摆动；没有未来 due 时摆停在无穷远。
        var now = _tp.GetUtcNow();
        DateTimeOffset? next = _due.Count == 0 ? null : _due.Min(r => r.DueAt);
        if (next is not { } target) { _pump.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        var delay = target <= now ? TimeSpan.Zero : target - now;
        _pump.Change(delay, Timeout.InfiniteTimeSpan);
    }

    ValueTask IJobStore.InitializeAsync(CancellationToken ct) => ValueTask.CompletedTask;

    ValueTask IJobStore.SaveDefinitionAsync(StoredDefinition definition, CancellationToken ct)
    {
        lock (_gate) _definitions[definition.Name] = definition;
        return ValueTask.CompletedTask;
    }

    ValueTask<IReadOnlyList<StoredDefinition>> IJobStore.LoadDefinitionsAsync(CancellationToken ct)
    {
        StoredDefinition[] snapshot;
        lock (_gate) snapshot = [.. _definitions.Values];
        return ValueTask.FromResult<IReadOnlyList<StoredDefinition>>(snapshot);
    }

    ValueTask IJobStore.DeleteDefinitionAsync(string name, CancellationToken ct)
    {
        lock (_gate) _definitions.Remove(name);
        return ValueTask.CompletedTask;
    }

    ValueTask IJobStore.PutDueRunAsync(StoredDueRun run, CancellationToken ct)
    {
        bool immediate;
        lock (_gate)
        {
            _due.Add(run);
            immediate = run.DueAt <= _tp.GetUtcNow();
            ReschedulePumpLocked();
        }
        if (immediate) RunsPending?.Invoke();
        return ValueTask.CompletedTask;
    }

    ValueTask<IReadOnlyList<StoredDueRun>> IJobStore.PeekDueRunsAsync(int take, CancellationToken ct)
    {
        StoredDueRun[] snapshot;
        lock (_gate) snapshot = [.. _due.OrderBy(r => r.DueAt).Take(Math.Max(0, take))];
        return ValueTask.FromResult<IReadOnlyList<StoredDueRun>>(snapshot);
    }

    ValueTask<StoredDueRun?> IJobStore.DropDueRunAsync(Guid runId, CancellationToken ct)
    {
        StoredDueRun? dropped;
        lock (_gate)
        {
            var index = _due.FindIndex(r => r.RunId == runId);
            if (index < 0) return ValueTask.FromResult<StoredDueRun?>(null);
            dropped = _due[index];
            _due.RemoveAt(index);
            ReschedulePumpLocked(); // 摆可能正指着这一条，摘掉要重拨
        }
        return ValueTask.FromResult<StoredDueRun?>(dropped);
    }

    ValueTask<IReadOnlyList<LeasedDueRun>> IJobStore.ClaimDueRunsAsync(string? instanceId, int max, CancellationToken ct)
    {
        var leases = new List<LeasedDueRun>();
        var now = _tp.GetUtcNow();
        lock (_gate)
        {
            for (var i = 0; i < _due.Count && leases.Count < max;)
            {
                var run = _due[i];
                if (run.DueAt > now) { i++; continue; }
                _due.RemoveAt(i);
                var leaseId = Guid.NewGuid();
                _leases[leaseId] = run;
                leases.Add(new LeasedDueRun { Run = run, LeaseId = leaseId });
            }
        }
        return ValueTask.FromResult<IReadOnlyList<LeasedDueRun>>(leases);
    }

    ValueTask IJobStore.FinishLeaseAsync(LeasedDueRun lease, DueRunOutcome outcome, CancellationToken ct)
    {
        lock (_gate) _leases.Remove(lease.LeaseId);
        return ValueTask.CompletedTask;
    }

    ValueTask IJobStore.AppendExecutionAsync(ExecutionRecord record, CancellationToken ct)
    {
        lock (_gate)
        {
            _history.Add(record);
            if (_history.Count > _historyCapacity)
                _history.RemoveRange(0, _history.Count - _historyCapacity);
        }
        return ValueTask.CompletedTask;
    }

    ValueTask<IReadOnlyList<ExecutionRecord>> IJobStore.ReadExecutionsAsync(ExecutionQuery? query, CancellationToken ct)
    {
        var q = query ?? new ExecutionQuery();
        lock (_gate)
        {
            IEnumerable<ExecutionRecord> seq = _history;
            if (q.JobName is not null) seq = seq.Where(r => r.JobName == q.JobName);
            var page = seq.TakeLast(q.Take).Reverse().ToArray();
            return ValueTask.FromResult<IReadOnlyList<ExecutionRecord>>(page);
        }
    }

    ValueTask IJobStore.TrimExecutionsAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        lock (_gate) _history.RemoveAll(r => r.FinishedAt < olderThan);
        return ValueTask.CompletedTask;
    }
}
