namespace QuickJob;

/// <summary>Kind of event published on <see cref="QuickJobEvents"/>.</summary>
public enum QuickJobEventKind
{
    /// <summary>An attempt began.</summary>
    RunStarted = 1,

    /// <summary>An attempt finished successfully.</summary>
    RunCompleted = 2,

    /// <summary>An attempt finished in failure and no retry follows (or retries are exhausted).</summary>
    RunFailed = 3,

    /// <summary>An attempt finished in failure and the next attempt was scheduled.</summary>
    RunRetrying = 4,

    /// <summary>The scheduler noticed an inconsistency it could only work around (unresolved definition, gate overflow…).</summary>
    Anomaly = 5,

    /// <summary>A due run was dropped by policy before it could execute (overlap gate, pause, grace…).</summary>
    RunSkippedByGate = 6,
}

/// <summary>Base of all events — every event published on <see cref="QuickJobEvents"/> derives from this.</summary>
public abstract record QuickJobEvent
{
    /// <summary>Categorical kind, handy for coarse-grained filtering.</summary>
    public abstract QuickJobEventKind Kind { get; }

    /// <summary>Wall-clock moment the event was raised.</summary>
    public DateTimeOffset At { get; init; } = TimeProvider.System.GetUtcNow();
}

/// <summary>An attempt began.</summary>
public sealed record RunStarted(
    string JobName,
    Guid ExecutionId,
    int Attempt,
    JobTrigger Trigger) : QuickJobEvent
{
    /// <inheritdoc/>
    public override QuickJobEventKind Kind => QuickJobEventKind.RunStarted;
}

/// <summary>An attempt finished successfully.</summary>
public sealed record RunCompleted(
    string JobName,
    Guid ExecutionId,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt) : QuickJobEvent
{
    /// <inheritdoc/>
    public override QuickJobEventKind Kind => QuickJobEventKind.RunCompleted;
}

/// <summary>An attempt failed and nothing follows (retries disabled or exhausted).</summary>
public sealed record RunFailed(
    string JobName,
    Guid ExecutionId,
    int Attempt,
    string Error) : QuickJobEvent
{
    /// <inheritdoc/>
    public override QuickJobEventKind Kind => QuickJobEventKind.RunFailed;
}

/// <summary>An attempt failed; the next attempt is queued with the given delay.</summary>
public sealed record RunRetrying(
    string JobName,
    Guid ExecutionId,
    int NextAttempt,
    TimeSpan Delay,
    string Error) : QuickJobEvent
{
    /// <inheritdoc/>
    public override QuickJobEventKind Kind => QuickJobEventKind.RunRetrying;
}

/// <summary>The scheduler worked around an inconsistency; worth a look.</summary>
public sealed record SchedulerAnomaly(
    string Message) : QuickJobEvent
{
    /// <inheritdoc/>
    public override QuickJobEventKind Kind => QuickJobEventKind.Anomaly;
}

/// <summary>A due run was dropped before it could execute (overlap-skip, queue full, pause…).</summary>
public sealed record RunSkippedByGate(
    string JobName,
    Guid ExecutionId,
    int Attempt,
    string Reason) : QuickJobEvent
{
    /// <inheritdoc/>
    public override QuickJobEventKind Kind => QuickJobEventKind.RunSkippedByGate;
}

/// <summary>
/// The event hub. Sinks are called synchronously and exceptions from sinks are swallowed —
/// an observer must never break a run. Registration order decides which order each individual
/// fan-out visits its sinks; whole publishes are serialised under one gate, so every sink
/// observes the <b>same total order</b> of publishes even from concurrent worker threads
/// (a dashboard ring and a user tap never disagree on adjacent events). Never synchronously
/// re-enter the scheduler from a sink — the fan-out gate is held across callbacks.
/// </summary>
public sealed class QuickJobEvents
{
    private Action<QuickJobEvent>[] _sinks = [];
    private readonly object _fanOutGate = new();

    /// <summary>Registers a sink; the disposable removes it (not thread-safe with concurrent Observe storms, fine in practice).</summary>
    public IDisposable Observe(Action<QuickJobEvent> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (this)
        {
            var next = new Action<QuickJobEvent>[_sinks.Length + 1];
            _sinks.CopyTo(next, 0);
            next[^1] = sink;
            _sinks = next;
        }
        return new Unsubscriber(this, sink);
    }

    internal void Publish(QuickJobEvent e)
    {
        if (e is null) return;
        // 扇出整段由单门串行：并发 worker 同刻各发一条（如两个 run 的 Start/Complete）时，
        // 所有 sink 都按同一先后落地 —— 事件流的全局总序由引擎负责，而不是消费方自己的锁。
        lock (_fanOutGate)
        {
            Action<QuickJobEvent>[] copy = _sinks;
            foreach (var sink in copy)
            {
                try { sink(e); }
                catch { /* belly flop, never a run breaker */ }
            }
        }
    }

    private sealed class Unsubscriber(QuickJobEvents owner, Action<QuickJobEvent> sink) : IDisposable
    {
        public void Dispose()
        {
            lock (owner)
            {
                var old = owner._sinks;
                var i = Array.IndexOf(old, sink);
                if (i < 0) return;
                var next = new Action<QuickJobEvent>[old.Length - 1];
                Array.Copy(old, 0, next, 0, i);
                Array.Copy(old, i + 1, next, i, old.Length - i - 1);
                owner._sinks = next;
            }
        }
    }
}
