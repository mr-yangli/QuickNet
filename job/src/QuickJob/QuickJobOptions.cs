namespace QuickJob;

/// <summary>
/// Global scheduler knobs. Everything is overridable per job via <see cref="JobOptions"/> where it
/// exists there too (retries / concurrency / timezone). The seam points — clock, store, container —
/// are what make the whole engine testable and clusterable without touching a handler.
/// </summary>
public sealed class QuickJobOptions
{
    /// <summary>
    /// The clock. Default <see cref="TimeProvider.System"/>; tests inject a fake provider and every
    /// timer, cron walk and retry delay follows it.
    /// </summary>
    public TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>The store seam. Default: everything lives in process memory.</summary>
    public IJobStore Store { get; set; } = new InMemoryJobStore();

    /// <summary>Default timezone for jobs that don't name one (IANA or Windows id; BCL resolves both). Null = system local.</summary>
    public string? DefaultTimeZoneId { get; set; }

    /// <summary>How many attempts a single claim drain pulls from the store at once. Default 64.</summary>
    public int ClaimBatch { get; set; } = 64;

    /// <summary>Identity of this scheduler instance in the store's claim log. Default: machine-ephemeral GUID.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Order-of-magnitude concurrency cap for the whole scheduler. Default: min(32, CPU count), floor 4.</summary>
    public int MaxParallelism { get; set; } = 0; // resolved in QuickJobScheduler when 0

    /// <summary>Pending-run queue per scheduler (not the store's). Default 10 000.</summary>
    public int PendingCapacity { get; set; } = 10_000;

    /// <summary>Grace applied to a stopped-and-restarted host when resuming definitions from the store. Default 30 seconds.</summary>
    public TimeSpan RestartGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long StopAsync waits for in-flight runs before the master token cancels them. Default 10 seconds.</summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional diagnostics tap (e.g. Console.WriteLine). Anomalies and lossy-drop reports flow through here.</summary>
    public Action<string>? Diagnostics { get; set; }

    /// <summary>Upper bound on finished attempts the in-memory store keeps (database stores may cap differently). Default 2000.</summary>
    public int HistoryCapacity { get; set; } = 2000;

    /// <summary>Resolves the effective default timezone (null id → system local).</summary>
    internal TimeZoneInfo ResolveDefaultTimeZone()
    {
        if (DefaultTimeZoneId is null) return TimeZoneInfo.Local;
        return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
    }

    internal int ResolveMaxParallelism()
    {
        if (MaxParallelism > 0) return Math.Clamp(MaxParallelism, 1, 512);
        var cpus = Environment.ProcessorCount;
        return Math.Clamp(cpus is < 4 ? 4 : cpus, 4, 32);
    }

    internal string ResolveInstanceId() => InstanceId ??$"qj:{Environment.MachineName}:{Guid.NewGuid():N}";
}
