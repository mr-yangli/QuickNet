namespace QuickJob;

/// <summary>
/// Fluent registration surface handed to <c>QuickJobScheduler.Create(...)</c> — the DSL for code
/// that prefers explicit wiring over the [QuickJob] attribute path. Both routes produce the same
/// internal <see cref="JobDefinition"/>s, so dashboards and the store seam treat them identically.
/// </summary>
public sealed class QuickJobBuilder
{
    private readonly QuickJobScheduler _scheduler;

    internal QuickJobBuilder(QuickJobScheduler scheduler) => _scheduler = scheduler;

    /// <summary>Recurring cron job.</summary>
    public void Cron(string name, string cron, JobHandler handler, Action<JobOptions>? options = null)
    {
        var def = new JobDefinition
        {
            Name = name,
            Kind = JobKind.Cron,
            Cron = cron,
            Options = Materialise(options),
        };
        _scheduler.Register(def, handler);
    }

    /// <summary>Recurring interval job — fixed wall-clock cadence (gates protect from pile-ups).</summary>
    public void Interval(string name, TimeSpan every, JobHandler handler, Action<JobOptions>? options = null)
    {
        var def = new JobDefinition
        {
            Name = name,
            Kind = JobKind.Interval,
            Every = every,
            Options = Materialise(options),
        };
        _scheduler.Register(def, handler);
    }

    /// <summary>One-shot job at an instant (payload optional, JSON-serialised).</summary>
    public void Once(string name, DateTimeOffset at, JobHandler handler, Action<JobOptions>? options = null, object? payload = null)
    {
        var def = new JobDefinition
        {
            Name = name,
            Kind = JobKind.Once,
            RunAt = at,
            Options = Materialise(options),
        };
        _scheduler.Register(def, handler);
        _scheduler.SetOncePayload(def.Name, payload);
    }

    /// <summary>Handler-only registration for <c>Enqueue</c>/<c>TriggerNow</c> traffic.</summary>
    public void OnDemand(string name, JobHandler handler, Action<JobOptions>? options = null)
    {
        var def = new JobDefinition
        {
            Name = name,
            Kind = JobKind.OnDemand,
            Options = Materialise(options),
        };
        _scheduler.Register(def, handler);
    }

    private static JobOptions Materialise(Action<JobOptions>? configure)
    {
        var options = new JobOptions();
        configure?.Invoke(options);
        return options;
    }
}
