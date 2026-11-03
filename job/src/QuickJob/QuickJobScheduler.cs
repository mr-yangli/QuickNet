namespace QuickJob;

using System.Collections.Concurrent;

/// <summary>
/// The scheduler itself: definitions in, handlers out, the run pipeline in between. Create via
/// <see cref="Create"/> (fluent) — [QuickJob] methods the generator discovered land in
/// <see cref="PendingRegistrations"/> and are consumed at <see cref="StartAsync"/>.
/// </summary>
public sealed class QuickJobScheduler : IAsyncDisposable
{
    private QuickJobScheduler(QuickJobOptions options)
    {
        Options = options;
        _stats = new StatsBucket();
    }

    /// <summary>Creates a scheduler; <paramref name="configure"/> registers jobs via the fluent DSL
    /// (on top of whatever [QuickJob] methods the generator already picked up).</summary>
    public static QuickJobScheduler Create(Action<QuickJobBuilder>? configure = null, QuickJobOptions? options = null)
    {
        var scheduler = new QuickJobScheduler(options ?? new QuickJobOptions());
        if (configure is not null)
        {
            var builder = new QuickJobBuilder(scheduler);
            configure(builder);
        }
        return scheduler;
    }

    /// <summary>Global knobs this instance runs with.</summary>
    public QuickJobOptions Options { get; }

    /// <summary>The event hub (run lifecycle / anomalies).</summary>
    public QuickJobEvents Events { get; } = new();

    // ------------------------------------------------------------- lifecycle

    private enum State { New, Starting, Running, Stopping, Stopped }

    private State _state = State.New;
    private Pipeline? _pipeline;
    private readonly StatsBucket _stats;
    private readonly ConcurrentDictionary<string, JobRuntime> _jobs = new();
    private volatile Action? _storePumpHandler;

    /// <summary>Brings the scheduler up: consumes the generator's registrations, validates everything,
    /// syncs definitions with the store, arms the slot machine and opens the doors.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_state is State.Running) return;
        if (_state is State.Stopping or State.Stopped)
            throw new QuickJobException("a stopped scheduler cannot restart — create a new one");
        _state = State.Starting;

        // 生成器送来的登记到表：坏数据此刻才炸（编译期诊断已经报了大头）。
        foreach (var p in PendingRegistrations.Drain())
        {
            try
            {
                RegisterFromAttribute(p);
            }
            catch (Exception ex) when (ex is not QuickJobException)
            {
                throw new QuickJobException($"[QuickJob] '{p.Name}' registration failed: {ex.Message}", ex);
            }
        }

        var store = Options.Store;
        _storePumpHandler = () => _pipeline?.OnStorePump();
        store.RunsPending += _storePumpHandler;

        _pipeline = new Pipeline(this);
        foreach (var runtime in _jobs.Values)
        {
            await _pipeline.SaveBootDefinitionAsync(runtime).ConfigureAwait(false);
        }

        // 存档里"只有定义"的鬼影（Dashboard 建的 / 前一版代码的）：挂上标记，让 dashboard 可见，不投递执行。
        foreach (var sd in await store.LoadDefinitionsAsync(ct).ConfigureAwait(false))
        {
            if (_jobs.ContainsKey(sd.Name)) continue;
            var revived = DefinitionSerialization.TryLoad(sd.Contract);
            if (revived is null)
            {
                Anomaly($"stored definition '{sd.Name}' has an unreadable contract — leaving it visible but unresolved");
                continue;
            }
            _jobs[sd.Name] = new JobRuntime(revived, MissingHandler);
            Anomaly($"stored definition '{sd.Name}' has no registered handler — re-declare the [QuickJob] method to resolve it");
        }

        await _pipeline.StartAsync(ct).ConfigureAwait(false);
        _state = State.Running;
    }

    /// <summary>Stops accepting, drains in-flight work within <see cref="QuickJobOptions.StopGrace"/>, cancels what's left.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_state is not State.Running) { _state = State.Stopped; return; }
        _state = State.Stopping;
        if (_pipeline is { } pipeline)
        {
            pipeline.StopFromScheduler();
            await pipeline.DrainAsync(ct).ConfigureAwait(false);
        }
        _state = State.Stopped;
    }

    /// <summary>Graceful stop then unhook — <see cref="IAsyncDisposable"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (QuickJobException)
        {
            // 停不动的残局照样释放资源；错误向上抛会挡住 using 语义。
        }
        finally
        {
            if (_pipeline is { } pipeline)
            {
                pipeline.Dispose();
                _pipeline = null;
            }
            if (_storePumpHandler is { } handler)
            {
                Options.Store.RunsPending -= handler;
                _storePumpHandler = null;
            }
            _state = State.Stopped;
        }
    }

    /// <summary>Synchronous best-effort stop.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // ------------------------------------------------------------ registration

    /// <summary>Registers one job (source generator, DSL and dashboard route here alike). Throws on
    /// schedule-shape violations and name problems; duplicate names replace the previous definition.</summary>
    public void Register(JobDefinition definition, JobHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new QuickJobException("job name must not be empty");
        if (!ScheduleShapeValid(definition, out var shapeError))
            throw new QuickJobException(shapeError ?? "invalid schedule shape");

        var runtime = new JobRuntime(definition, handler);
        if (definition.Kind == JobKind.Cron)
        {
            // 坏表达式现在就炸，而不是点亮引擎的第一拍。
            runtime.Cron = CronSchedule.Parse(definition.Cron!);
        }
        _jobs[definition.Name] = runtime;

        if (_state is State.Running or State.Starting && _pipeline is { } pipeline)
        {
            var boot = pipeline.SaveBootDefinitionAsync(runtime);
            _ = boot; // 热路径注册走同步等待，不给乱序留缝
            boot.GetAwaiter().GetResult();
            pipeline.ArmRuntime(runtime);
        }
    }

    /// <summary>Entirely removes one job (schedule + store contract go; already-dispatched runs finish or drop).</summary>
    public void Remove(string name)
    {
        if (_jobs.TryRemove(name, out _))
        {
            _ = _pipeline?.DeleteDefinitionAsync(name);
        }
    }

    /// <summary>Pauses: visible on the dashboard, its slots don't run (interval keeps ticking its slot machine).</summary>
    public void Pause(string name) => SetPaused(name, true);

    /// <summary>Resumes; cron continues from the next future slot (nothing missed mid-pause is replayed).</summary>
    public void Resume(string name) => SetPaused(name, false);

    private void SetPaused(string name, bool paused)
    {
        if (!_jobs.TryGetValue(name, out var runtime))
            throw new QuickJobException($"no job named '{name}'");
        runtime.Def.Paused = paused;
    }

    /// <summary>Dashboard-facing snapshot of all definitions (live ones and unresolved ghosts).</summary>
    public IReadOnlyList<JobDefinition> ListJobs()
    {
        var list = new List<JobDefinition>(_jobs.Count);
        foreach (var kv in _jobs) list.Add(kv.Value.Def.Snapshot());
        return list;
    }

    /// <summary>One definition snapshot, null when absent.</summary>
    public JobDefinition? GetJob(string name)
        => _jobs.TryGetValue(name, out var runtime) ? runtime.Def.Snapshot() : null;

    internal IReadOnlyList<JobRuntime> JobsSnapshot() => [.. _jobs.Values];
    internal JobRuntime? FindJob(string name) => _jobs.TryGetValue(name, out var runtime) ? runtime : null;
    internal void ForgetJob(string name) => _jobs.TryRemove(name, out _);

    internal void SetOncePayload(string name, object? payload)
    {
        if (_jobs.TryGetValue(name, out var runtime))
            runtime.OncePayload = PayloadJson.ToStoreText(PayloadJson.Serialize(payload));
    }

    // ----------------------------------------------------------------- runs

    /// <summary>Runs a job right now (payload = a JSON-serialisable object). Returns the run id.</summary>
    public async Task<(bool Accepted, Guid? RunId)> TriggerNowAsync(string name, object? payload = null, CancellationToken ct = default)
        => await EnqueueCoreAsync(name, PayloadJson.Serialize(payload), JobTrigger.Manual, at: null, ct).ConfigureAwait(false);

    /// <summary>Enqueues a background run (optionally at a specific instant) — same handler path.</summary>
    public async Task<(bool Accepted, Guid? RunId)> EnqueueAsync(string name, object? payload = null, DateTimeOffset? at = null, CancellationToken ct = default)
        => await EnqueueCoreAsync(name, PayloadJson.Serialize(payload), JobTrigger.Enqueued, at, ct).ConfigureAwait(false);

    private async Task<(bool, Guid?)> EnqueueCoreAsync(string name, byte[] payload, JobTrigger origin, DateTimeOffset? at, CancellationToken ct)
    {
        if (_state != State.Running || _pipeline is null) return (false, null);
        // 未登记的名字不收：dashboard 的手动触发面同规，ghost 档的恢复处理走 StartAsync 那条线
        if (!_jobs.ContainsKey(name)) return (false, null);
        var run = new StoredDueRun
        {
            RunId = Guid.NewGuid(),
            JobName = name,
            DueAt = at ?? Options.Time.GetUtcNow(),
            Attempt = 1,
            Origin = origin,
            Payload = PayloadJson.ToStoreText(payload),
        };
        await Options.Store.PutDueRunAsync(run, ct).ConfigureAwait(false);
        return (true, run.RunId);
    }

    /// <summary>Drops one pending run, or cooperatively cancels a running attempt (removal from the pipeline's queues).</summary>
    public Task<bool> CancelAsync(Guid runId, CancellationToken ct = default)
        => _pipeline is { } pipeline ? pipeline.CancelAsync(runId, ct) : Task.FromResult(false);

    /// <summary>Runs waiting in the queue/overflow, newest last.</summary>
    public IReadOnlyList<RunState> PendingRuns() => _pipeline?.PendingSnapshot() ?? [];

    /// <summary>In-flight attempts (incl. handler-written progress notes).</summary>
    public IReadOnlyList<RunState> RunningRuns() => _pipeline?.RunningSnapshot() ?? [];

    /// <summary>Counters since process start.</summary>
    public QuickJobStats Stats => _pipeline?.StatsSnapshot() ?? _stats.Snapshot(0, 0);

    // --------------------------------------------------------------- internals

    private void RegisterFromAttribute(PendingRegistration p)
    {
        var every = p.Every is null ? default(TimeSpan?) : DurationText.Parse(p.Every, nameof(p.Every));
        var def = new JobDefinition
        {
            Name = p.Name,
            Kind = p.Cron is not null ? JobKind.Cron : p.Every is not null ? JobKind.Interval : JobKind.OnDemand,
            Cron = p.Cron,
            Every = every,
            RunAt = null,
            Source = p.DeclaringType,
            Options = new JobOptions
            {
                TimeZoneId = p.TimeZone,
                MaxRetries = p.MaxRetries,
                RetryBackoff = p.RetryBackoff,
                RetryJitter = p.RetryJitter,
                MaxConcurrentPerJob = p.MaxConcurrentPerJob,
                OnOverlap = p.OnOverlap,
                OverlapQueueLimit = p.OverlapQueueLimit,
                Priority = p.Priority,
                OnMissedSlot = p.OnMissedSlot,
                Description = p.Description,
                Enabled = p.Enabled,
                RetryBaseDelay = p.RetryBaseDelay is null ? TimeSpan.FromSeconds(10) : DurationText.Parse(p.RetryBaseDelay, nameof(p.RetryBaseDelay)),
                RetryMaxDelay = p.RetryMaxDelay is null ? TimeSpan.FromMinutes(10) : DurationText.Parse(p.RetryMaxDelay, nameof(p.RetryMaxDelay)),
                Timeout = p.Timeout is null ? null : DurationText.Parse(p.Timeout, nameof(p.Timeout)),
                Grace = p.Grace is null ? TimeSpan.FromMinutes(5) : DurationText.Parse(p.Grace, nameof(p.Grace)),
            },
        };
        Register(def, p.HandlerFactory());
    }

    private static bool ScheduleShapeValid(JobDefinition def, out string? error)
    {
        error = null;
        switch (def.Kind)
        {
            case JobKind.Cron when string.IsNullOrWhiteSpace(def.Cron):
                error = "cron definition needs a Cron expression"; break;
            case JobKind.Interval when def.Every is not { } every || every <= TimeSpan.Zero:
                error = "interval definition needs a positive Every"; break;
            case JobKind.Once when def.RunAt is null:
                error = "one-shot definition needs a RunAt"; break;
        }
        return error is null;
    }

    internal void Anomaly(string message)
    {
        Options.Diagnostics?.Invoke($"[QuickJob anomaly] {message}");
        Events.Publish(new SchedulerAnomaly(message) { At = Options.Time.GetUtcNow() });
    }

    internal void PublishEvent(QuickJobEvent e) => Events.Publish(e);

    internal StatsBucket StatsOf() => _stats;

    internal static readonly JobHandler MissingHandler = static _ => ValueTask.CompletedTask;

    internal sealed class JobRuntime
    {
        internal JobRuntime(JobDefinition def, JobHandler handler)
        {
            Def = def;
            Handler = handler;
            Gate = new RunGate(def.Options.MaxConcurrentPerJob);
        }

        internal JobDefinition Def { get; }
        internal JobHandler Handler { get; }
        internal CronSchedule? Cron { get; set; }
        internal RunGate Gate { get; }
        internal bool Paused => Def.Paused;
        internal bool Resolved => !ReferenceEquals(Handler, MissingHandler);
        internal string? OncePayload { get; set; }
        internal readonly object ArmLock = new();
        internal DateTimeOffset NextSlot;
        internal bool SlotArmed;

        internal TimeZoneInfo ResolveTimeZone(TimeZoneInfo schedulerDefault)
        {
            if (Def.Options.TimeZoneId is not { } id) return schedulerDefault;
            return TimeZoneInfo.FindSystemTimeZoneById(id); // 坏 id 在 Register 阶段就该炸（延迟一步）
        }
    }

    /// <summary>The per-job (per-name) gate lives here so dashboard and pipeline agree on one truth.</summary>
    internal sealed class RunGate
    {
        internal readonly int Limit;
        internal readonly Queue<Pipeline.PendingItem> Overflow = [];
        internal int InFlight;

        internal RunGate(int limit) => Limit = Math.Max(1, limit);
    }
}
