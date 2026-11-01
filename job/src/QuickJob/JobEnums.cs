namespace QuickJob;

/// <summary>The schedule shape a job was declared with.</summary>
public enum JobKind
{
    /// <summary>Recurring job fired by a cron expression (wall-clock aware).</summary>
    Cron = 1,

    /// <summary>Recurring job fired every N seconds (fixed-delay: the timer is re-armed after a run completes).</summary>
    Interval = 2,

    /// <summary>One-shot job fired exactly once at a given instant.</summary>
    Once = 3,

    /// <summary>Handler registered only for <c>Enqueue</c>/<c>TriggerNow</c> — no automatic schedule.</summary>
    OnDemand = 4,
}

/// <summary>What produced a particular run.</summary>
public enum JobTrigger
{
    /// <summary>Slotted by a cron expression.</summary>
    CronTick = 1,

    /// <summary>Slotted by an interval re-arm.</summary>
    IntervalTick = 2,

    /// <summary>Slotted by a one-shot schedule.</summary>
    Scheduled = 3,

    /// <summary>Someone asked the scheduler to run it right now.</summary>
    Manual = 4,

    /// <summary>Someone enqueued it as a background job with an optional payload.</summary>
    Enqueued = 5,

    /// <summary>A retry of an earlier failed attempt.</summary>
    Retry = 6,
}

/// <summary>What happens when a job is due while a previous run of the same job is still in flight.</summary>
public enum OverlapPolicy
{
    /// <summary>Drop the new slot (the most common choice for cron jobs that must not pile up).</summary>
    Skip = 1,

    /// <summary>Keep it queued and execute it once the previous run finishes.</summary>
    Queue = 2,

    /// <summary>Ignore the gate entirely and run it straight away (advance at your own risk).</summary>
    RunAnyway = 3,
}

/// <summary>How the delay before the next retry attempt is computed.</summary>
public enum RetryBackoff
{
    /// <summary>A constant <see cref="JobOptions.RetryBaseDelay"/> between attempts.</summary>
    Fixed = 1,

    /// <summary><see cref="JobOptions.RetryBaseDelay"/> × attemptNumber.</summary>
    Linear = 2,

    /// <summary><see cref="JobOptions.RetryBaseDelay"/> × 2^(n-1), clamped to <see cref="JobOptions.RetryMaxDelay"/>.</summary>
    Exponential = 3,
}

/// <summary>Queue pick order when several runs are waiting at once.</summary>
public enum JobPriority
{
    /// <summary>Drained together with Normal.</summary>
    Low = 0,

    /// <summary>Standard order.</summary>
    Normal = 1,

    /// <summary>Drained before every Normal/Low entry.</summary>
    High = 2,
}

/// <summary>What happens when a cron slot is discovered late (paused host, suspended process, store backlog).</summary>
public enum MissedSlotPolicy
{
    /// <summary>Run it if it is at most <see cref="JobOptions.Grace"/> late; otherwise drop it and continue from the next future slot.</summary>
    RunWithinGrace = 1,

    /// <summary>Never run late slots; always continue from the next future slot.</summary>
    SkipLate = 2,

    /// <summary>Run it late regardless of how much has passed, but only once — never sweep through every missed slot.</summary>
    CatchUpLatest = 3,
}
