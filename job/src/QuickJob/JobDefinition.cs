using System.Text.Json;

namespace QuickJob;

/// <summary>
/// A job registration: name, schedule shape, body options. The runtime registry, the store seam
/// and the dashboard snapshot all speak this one shape.
/// </summary>
public sealed class JobDefinition
{
    /// <summary>Unique job name — the handler key for <c>Enqueue</c>/<c>TriggerNow</c> and the store key.</summary>
    public required string Name { get; init; }

    /// <summary>Schedule shape.</summary>
    public required JobKind Kind { get; set; }

    /// <summary>Cron expression (5/6/7 fields) when <see cref="Kind"/> is <see cref="JobKind.Cron"/>.</summary>
    public string? Cron { get; set; }

    /// <summary>Fixed interval when <see cref="Kind"/> is <see cref="JobKind.Interval"/>.</summary>
    public TimeSpan? Every { get; set; }

    /// <summary>Target instant when <see cref="Kind"/> is <see cref="JobKind.Once"/>.</summary>
    public DateTimeOffset? RunAt { get; set; }

    /// <summary>Body knobs (retries, gates, timezone…).</summary>
    public required JobOptions Options { get; init; }

    /// <summary>Currently paused? Controls live on the scheduler, mirrored here for snapshots.</summary>
    public bool Paused { get; set; }

    /// <summary>Returns a copy with the same schedule/options — the store-serialisable projection.</summary>
    internal JobDefinition Snapshot() => new()
    {
        Name = Name,
        Kind = Kind,
        Cron = Cron,
        Every = Every,
        RunAt = RunAt,
        Options = Options.CloneShallow(),
        Paused = Paused,
    };
}

/// <summary>The payload+context handed to every attempt.</summary>
public sealed class JobRunContext
{
    /// <summary>Job name as registered.</summary>
    public required string JobName { get; init; }

    /// <summary>Run correlation id — retries keep it, so log lines correlate across attempts.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>1 on first attempt, incremented on every retry.</summary>
    public required int Attempt { get; init; }

    /// <summary>The slot this attempt claims (its due time; enqueue moment for queued work).</summary>
    public required DateTimeOffset ScheduledAt { get; init; }

    /// <summary>The wall-clock moment the attempt began (or was enqueued, while still queuing).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>What produced this run.</summary>
    public required JobTrigger Trigger { get; init; }

    /// <summary>Raw payload as UTF-8 JSON bytes (generator-declared request types deserialise on top). Null when none.</summary>
    public byte[]? Payload { get; init; }

    /// <summary>Cooperative cancellation: scheduler stop, per-run timeout, explicit cancel, host shutdown.</summary>
    public required CancellationToken Token { get; init; }

    /// <summary>Deserialises <see cref="Payload"/> as <typeparamref name="TPayload"/> (System.Text.Json). Default when none.</summary>
    public TPayload? PayloadAs<TPayload>() => PayloadJson.Deserialize<TPayload>(Payload);

    /// <summary>Writes a progress note the dashboard shows on the live run view.</summary>
    public void Report(string note)
    {
        if (RunState is not null) RunState.Progress = note;
    }

    // 引擎把这条 run 的实时状态挂在这里，dashboard 轮询用它。
    internal RunState? RunState { get; set; }
}
