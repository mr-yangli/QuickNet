namespace QuickJob;

/// <summary>
/// Per-job knobs — what retried runs look like, what concurrency means for this function, what a
/// timezone-shifted cron should fire on. One instance riders along with every job definition;
/// the source-generator and the fluent DSL both funnel into this shape.
/// </summary>
public sealed class JobOptions
{
    /// <summary>IANA or Windows timezone id the cron expression is evaluated in (BCL resolves both
    /// on .NET 6+ via <c>TimeZoneInfo.FindSystemTimeZoneById</c>). <c>null</c> = system default.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Extra attempts after the first failure. 0 (the default) means fail-and-forget.</summary>
    public int MaxRetries { get; set; }

    /// <summary>How the next attempt's delay is computed. Default <see cref="RetryBackoff.Exponential"/>.</summary>
    public RetryBackoff RetryBackoff { get; set; } = RetryBackoff.Exponential;

    /// <summary>Base delay in the backoff formulas. Default 10 seconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper clamp for exponential/linear growth. Default 10 minutes.</summary>
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Relative jitter (0–1) subtracted or added on top of the computed delay. Default 0.25.</summary>
    public double RetryJitter { get; set; } = 0.25;

    /// <summary>How many runs of this one job may execute simultaneously. Default 1 (serialized).</summary>
    public int MaxConcurrentPerJob { get; set; } = 1;

    /// <summary>What to do with a slot that lands while the gate (above) is full. Default <see cref="OverlapPolicy.Skip"/>.</summary>
    public OverlapPolicy OnOverlap { get; set; } = OverlapPolicy.Skip;

    /// <summary>Queue length kept per job when <see cref="OnOverlap"/> is <see cref="OverlapPolicy.Queue"/>. Default 128.</summary>
    public int OverlapQueueLimit { get; set; } = 128;

    /// <summary>How late a discovered cron slot may be and still run. Default 5 minutes.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Late-slot behaviour. Default <see cref="MissedSlotPolicy.RunWithinGrace"/>.</summary>
    public MissedSlotPolicy OnMissedSlot { get; set; } = MissedSlotPolicy.RunWithinGrace;

    /// <summary>Queue pick order. High runs are drained before Normal; Low shares Normal's queue.</summary>
    public JobPriority Priority { get; set; } = JobPriority.Normal;

    /// <summary>Per-run cooperative wall-clock budget: a cancellation token fires when elapsed exceeds it.
    /// The scheduler does not abort threads — handlers must honour the token. Default null = unbounded.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Human-readable note that shows up on the dashboard. Has no runtime effect.</summary>
    public string? Description { get; set; }

    /// <summary>A disabled job stays registered (and visible) but never fires. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Resolves this instance's timezone, falling back to the scheduler default when unset.</summary>
    internal TimeZoneInfo ResolveTimeZone(TimeZoneInfo schedulerDefault)
    {
        if (TimeZoneId is null) return schedulerDefault;
        return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
    }

    /// <summary>Field-for-field copy with nested-collection-free members only (all of them are).</summary>
    internal JobOptions CloneShallow() => new()
    {
        TimeZoneId = TimeZoneId,
        MaxRetries = MaxRetries,
        RetryBackoff = RetryBackoff,
        RetryBaseDelay = RetryBaseDelay,
        RetryMaxDelay = RetryMaxDelay,
        RetryJitter = RetryJitter,
        MaxConcurrentPerJob = MaxConcurrentPerJob,
        OnOverlap = OnOverlap,
        OverlapQueueLimit = OverlapQueueLimit,
        Grace = Grace,
        OnMissedSlot = OnMissedSlot,
        Priority = Priority,
        Timeout = Timeout,
        Description = Description,
        Enabled = Enabled,
    };

    /// <summary>Computes the delay before attempt <paramref name="nextAttempt"/> (1-based after the first failure).</summary>
    internal TimeSpan RetryDelay(int nextAttempt, double jitterSample)
        => RetryPolicyMath.Delay(RetryBackoff, RetryBaseDelay, RetryMaxDelay, RetryJitter, nextAttempt, jitterSample);
}

/// <summary>Backoff arithmetic shared by registration-time validation and the runtime retry path.</summary>
public static class RetryPolicyMath
{
    /// <summary>Computes one backoff sample. Exposed because the source generator validates with it too.</summary>
    public static TimeSpan Delay(RetryBackoff backoff, TimeSpan baseDelay, TimeSpan maxDelay, double jitter, int attempt, double jitterSample)
    {
        if (attempt < 1) attempt = 1;
        var d = backoff switch
        {
            RetryBackoff.Fixed => baseDelay,
            RetryBackoff.Linear => TimeSpan.FromTicks(baseDelay.Ticks * attempt),
            RetryBackoff.Exponential => TimeSpan.FromTicks(ClampTicks(baseDelay.Ticks * Pow2(attempt - 1))),
            _ => baseDelay,
        };
        if (d > maxDelay) d = maxDelay;
        if (jitter > 0)
        {
            // [1-j, 1+j] 区间的随机因子：jitterSample ∈ [0,1) 居中映射到 [1-j, 1+j]
            var j = Math.Clamp(jitter, 0, 1);
            var factor = 1.0 + (2.0 * j * jitterSample) - j;
            d = TimeSpan.FromTicks((long)(d.Ticks * factor));
        }
        return d < TimeSpan.Zero ? TimeSpan.Zero : d;
    }

    private static long Pow2(int exponent)
    {
        const int capBits = 40; // 2^40 ticks ≈ 550 年，永远先被 maxDelay 截下来
        if (exponent >= capBits) return 1L << capBits;
        return 1L << exponent;
    }

    private static long ClampTicks(long ticks) => Math.Min(ticks, long.MaxValue / 2);
}
