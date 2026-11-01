namespace QuickJob;

using System.Collections.Concurrent;
using System.Globalization;

/// <summary>
/// The inbox the source generator writes into. Every emitted assembly carries a module initializer
/// that calls <see cref="Add"/> exactly once per [QuickJob] method — no reflection, no assembly
/// scanning, and everything validated at <see cref="QuickJobScheduler.StartAsync"/> time.
/// </summary>
public static class PendingRegistrations
{
    private static readonly ConcurrentBag<PendingRegistration> Inbox = [];
    private static readonly ConcurrentDictionary<string, byte> Known = new();
    private static int _taken;

    /// <summary>Registers one compile-time entry. Called from generated code only; duplicate job
    /// names across all input assemblies throw at start (they'd be ambiguous by race order otherwise).</summary>
    public static void Add(PendingRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (!Known.TryAdd(registration.Name, 0))
            throw new QuickJobException($"[QuickJob] duplicate job name '{registration.Name}' between compile-time registrations");
        Inbox.Add(registration);
    }

    /// <summary>Handed away at start; a scheduler that starts twice must not re-consume the same entries.</summary>
    internal static IReadOnlyList<PendingRegistration> Drain()
    {
        if (Interlocked.Exchange(ref _taken, 1) == 1) return [];
        var snapshot = Inbox.ToArray();
        return snapshot;
    }
}

/// <summary>One [QuickJob] method, as the generator saw it (attribute values + a bound handler factory).</summary>
public sealed record PendingRegistration
{
    /// <summary>Job name (must be unique).</summary>
    public required string Name { get; init; }

    /// <summary>Cron expression when the attribute declared one.</summary>
    public string? Cron { get; init; }

    /// <summary>Interval text when the attribute declared one ("00:05:00", "5m"…).</summary>
    public string? Every { get; init; }

    /// <summary>Timezone id (null = scheduler default).</summary>
    public string? TimeZone { get; init; }

    /// <summary>Builds the handler (typed-request shapes resolve their generic binder here).</summary>
    public required Func<JobHandler> HandlerFactory { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public int MaxRetries { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public RetryBackoff RetryBackoff { get; init; } = RetryBackoff.Exponential;

    /// <summary>Attribute mirrored job knobs.</summary>
    public string? RetryBaseDelay { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public string? RetryMaxDelay { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public double RetryJitter { get; init; } = 0.25;

    /// <summary>Attribute mirrored job knobs.</summary>
    public int MaxConcurrentPerJob { get; init; } = 1;

    /// <summary>Attribute mirrored job knobs.</summary>
    public OverlapPolicy OnOverlap { get; init; } = OverlapPolicy.Skip;

    /// <summary>Attribute mirrored job knobs.</summary>
    public int OverlapQueueLimit { get; init; } = 128;

    /// <summary>Attribute mirrored job knobs.</summary>
    public JobPriority Priority { get; init; } = JobPriority.Normal;

    /// <summary>Attribute mirrored job knobs.</summary>
    public string? Grace { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public MissedSlotPolicy OnMissedSlot { get; init; } = MissedSlotPolicy.RunWithinGrace;

    /// <summary>Attribute mirrored job knobs.</summary>
    public string? Timeout { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public string? Description { get; init; }

    /// <summary>Attribute mirrored job knobs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Serialising helper used by the emitted registration call for the typed-request binder.</summary>
    internal string Describe() => string.Create(CultureInfo.InvariantCulture, $"PendingRegistration({Name}, cron={Cron}, every={Every})");
}
