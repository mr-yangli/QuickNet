namespace QuickJob;

/// <summary>
/// Declares a static method as a job function. The companion <c>QuickJob.Generators</c> analyzer
/// picks these up at compile time and emits the registration — no runtime scan, no reflection,
/// AOT-clean. Handler shapes: no-arg / <c>CancellationToken</c> / <see cref="JobRunContext"/> (+
/// optional trailing <c>CancellationToken</c>); with <see cref="RequestType"/> set, a typed first
/// parameter deserialised by System.Text.Json.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class QuickJobAttribute : Attribute
{
    /// <summary>Job name — unique across the registry.</summary>
    public QuickJobAttribute(string name) => Name = name;

    /// <summary>Job name — unique across the registry.</summary>
    public string Name { get; }

    /// <summary>Cron expression (5/6/7 fields or an @ alias) for recurring jobs. Mutually exclusive with <see cref="Every"/>.</summary>
    public string? Cron { get; set; }

    /// <summary>Fixed interval for interval jobs ("00:05:00", "5m", "30s", "2d"…). Mutually exclusive with <see cref="Cron"/>.</summary>
    public string? Every { get; set; }

    /// <summary>IANA or Windows timezone id the cron is evaluated in (null = scheduler default).</summary>
    public string? TimeZone { get; set; }

    /// <summary>Typed-payload request. When set, the handler's first parameter must be this type
    /// (System.Text.Json deserialises the payload into it at every attempt).</summary>
    public Type? RequestType { get; set; }

    /// <summary>Extra attempts after the first failure. Default 0.</summary>
    public int MaxRetries { get; set; }

    /// <summary>How the next attempt's delay is computed. Default exponential.</summary>
    public RetryBackoff RetryBackoff { get; set; } = RetryBackoff.Exponential;

    /// <summary>Base delay for the backoff formulas ("00:00:10", "30s"…). Default 10 seconds.</summary>
    public string? RetryBaseDelay { get; set; }

    /// <summary>Upper clamp for backoff growth ("00:10:00", "10m"…). Default 10 minutes.</summary>
    public string? RetryMaxDelay { get; set; }

    /// <summary>Relative jitter (0–1) over the computed delay. Default 0.25.</summary>
    public double RetryJitter { get; set; } = 0.25;

    /// <summary>How many runs of this job may run simultaneously. Default 1.</summary>
    public int MaxConcurrentPerJob { get; set; } = 1;

    /// <summary>What happens to a slot that lands while the concurrency gate is full. Default Skip.</summary>
    public OverlapPolicy OnOverlap { get; set; } = OverlapPolicy.Skip;

    /// <summary>Queue depth when <see cref="OnOverlap"/> is <see cref="OverlapPolicy.Queue"/>. Default 128.</summary>
    public int OverlapQueueLimit { get; set; } = 128;

    /// <summary>Pick order across waiting runs. Default Normal.</summary>
    public JobPriority Priority { get; set; } = JobPriority.Normal;

    /// <summary>How late a discovered cron slot may be and still run. Default 5 minutes.</summary>
    public string? Grace { get; set; }

    /// <summary>Late-slot policy. Default RunWithinGrace.</summary>
    public MissedSlotPolicy OnMissedSlot { get; set; } = MissedSlotPolicy.RunWithinGrace;

    /// <summary>Per-run wall-clock budget for the cooperative token ("00:10:00"). Default unbounded.</summary>
    public string? Timeout { get; set; }

    /// <summary>Dashboard note; no runtime effect.</summary>
    public string? Description { get; set; }

    /// <summary>A disabled job stays registered but never fires. Default true.</summary>
    public bool Enabled { get; set; } = true;
}
