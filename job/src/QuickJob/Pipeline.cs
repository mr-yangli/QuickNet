namespace QuickJob;

using System.Collections.Concurrent;
using System.Text;

/// <summary>
/// The run pipeline: slot machine (cron/interval), store claim loop, gate-protected dispatch, and
/// the worker pool. One instance per scheduler; everything the dashboard sees (pending/running
/// lists, cancellations, stats) lives here.
/// </summary>
internal sealed class Pipeline
{
    private readonly QuickJobScheduler _owner;
    private readonly IJobStore _store;
    private readonly TimeProvider _tp;
    private readonly StatsBucket _stats;
    private readonly int _claimBatch;
    private readonly int _pendingCapacity;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _wake = new(0, 1_000_000);            // claim loop
    private readonly SemaphoreSlim _roomForWork = new(0, 1_000_000);     // workers
    private readonly object _pendingGate = new();
    private readonly List<PendingItem> _pending = [];                    // claimed-but-not-started
    private readonly object _runningGate = new();
    private readonly Dictionary<Guid, RunBadge> _running = [];
    private readonly ConcurrentDictionary<Guid, byte> _cancelled = [];
    private ITimer? _slotPump;

    private volatile bool _stopping;
    private Task[] _workers = [];
    private Task _claimLoop = Task.CompletedTask;

    internal Pipeline(QuickJobScheduler owner)
    {
        _owner = owner;
        _store = owner.Options.Store;
        _tp = owner.Options.Time;
        _stats = owner.StatsOf();
        _claimBatch = Math.Clamp(owner.Options.ClaimBatch, 1, 256);
        _pendingCapacity = Math.Max(64, owner.Options.PendingCapacity);
    }

    internal sealed record PendingItem(StoredDueRun Run, LeasedDueRun Lease);

    /// <summary>Per-attempt live badge: the dashboard-facing <see cref="RunState"/> instance is shared
    /// with the handler's context, so <c>ctx.Report(...)</c> lands where snapshots read from.</summary>
    internal sealed class RunBadge(
        Guid id,
        string jobName,
        int attempt,
        JobTrigger trigger,
        DateTimeOffset scheduledAt)
    {
        internal readonly RunState State = new()
        {
            ExecutionId = id,
            JobName = jobName,
            Attempt = attempt,
            Trigger = trigger,
            ScheduledAt = scheduledAt,
        };
        internal CancellationTokenSource? RunCts;
        internal int GateClaimed;

        internal RunState Frozen() => State with { };
    }

    // ---------------------------------------------------------------- lifecycle

    internal async Task StartAsync(CancellationToken bootCt)
    {
        await _store.InitializeAsync(bootCt);
        _slotPump = _tp.CreateTimer(OnSlotTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ArmAllSlots();
        RescheduleSlotPump();

        var workers = new List<Task>(_maxParallelismSafe());
        for (var i = 0; i < _maxParallelismSafe(); i++)
        {
            var captured = i;
            workers.Add(Task.Run(() => WorkerLoopAsync(captured), CancellationToken.None));
        }
        _workers = [.. workers];
        _claimLoop = Task.Run(ClaimLoopAsync, CancellationToken.None);
    }

    private int _maxParallelismSafe()
    {
        var capped = Math.Clamp(Math.Max(1, _owner.Options.ResolveMaxParallelism()), 1, 512);
        return capped;
    }

    /// <summary>Stops accepting, then waits for queued+running work to finish inside StopGrace.</summary>
    internal async Task<bool> DrainAsync(CancellationToken ct)
    {
        _stopping = true;
        _slotPump?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _wake.Release();
        _roomForWork.Release(_maxParallelismSafe());

        var all = new Task[_workers.Length + 1];
        Array.Copy(_workers, all, _workers.Length);
        all[^1] = _claimLoop;
        var wait = Task.WhenAll(all);
        var floor = TimeSpan.FromMilliseconds(100);
        var grace = _owner.Options.StopGrace < floor ? floor : _owner.Options.StopGrace;
        var done = await Task.WhenAny(wait, Task.Delay(grace, _tp, ct)).ConfigureAwait(false);
        var drained = done == wait;
        if (drained) SweepOverflowLeases();
        _shutdownCts.Cancel();
        return drained;
    }

    /// <summary>Raised by the scheduler stop entry: no more pending acceptance from the store either.</summary>
    internal void StopFromScheduler()
    {
        _stopping = true;
        _slotPump?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _wake.Release();
    }

    /// <summary>Store event: something new is claimable — wake the claim loop.</summary>
    internal void OnStorePump() => _wake.Release();

    internal void Dispose()
    {
        _slotPump?.Dispose();
        _shutdownCts.Cancel();
        _wake.Dispose();
        _roomForWork.Dispose();
        _shutdownCts.Dispose();
    }

    // ------------------------------------------------------------ slot machine

    private void OnSlotTick(object? state)
    {
        if (_stopping || _slotPump is null) return;
        var now = _tp.GetUtcNow();
        var minNext = DateTimeOffset.MaxValue;
        var anyArmed = false;
        var work = new List<Task>();

        foreach (var runtime in _owner.JobsSnapshot())
        {
            var slotNow = false;
            lock (runtime.ArmLock)
            {
                if (!runtime.SlotArmed) continue;
                if (runtime.NextSlot <= now) slotNow = true;
                else if (runtime.NextSlot < minNext) { minNext = runtime.NextSlot; anyArmed = true; }
            }

            if (slotNow)
            {
                lock (runtime.ArmLock)
                {
                    var slot = runtime.NextSlot;
                    if (runtime.Def.Options.Enabled && !runtime.Paused && runtime.Resolved)
                    {
                        work.Add(PutAsync(new StoredDueRun
                        {
                            RunId = Guid.NewGuid(),
                            JobName = runtime.Def.Name,
                            DueAt = slot,
                            Attempt = 1,
                            Origin = runtime.Def.Kind == JobKind.Cron ? JobTrigger.CronTick : JobTrigger.IntervalTick,
                            Payload = runtime.Def.Kind == JobKind.Once ? runtime.OncePayload : null,
                        }));
                    }
                    RearmAfterSlot(runtime, slot);
                    if (runtime.SlotArmed && runtime.NextSlot < minNext) { minNext = runtime.NextSlot; anyArmed = true; }
                }
            }
        }

        _ = Task.WhenAll(work);
        if (!anyArmed) { _slotPump.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        var delay = minNext - now;
        _slotPump.Change(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Arms the next slot strictly after the slot we just consumed (cron) or plus (interval).</summary>
    private void RearmAfterSlot(QuickJobScheduler.JobRuntime runtime, DateTimeOffset consumed)
    {
        switch (runtime.Def.Kind)
        {
            case JobKind.Cron:
                var tz = runtime.ResolveTimeZone(_owner.Options.ResolveDefaultTimeZone());
                var next = runtime.Cron?.Next(consumed, tz);
                runtime.NextSlot = next ?? DateTimeOffset.MaxValue;
                runtime.SlotArmed = next is not null;
                break;
            case JobKind.Interval:
                // 墙钟节奏锚在槽上；执行迟到不持久漂移。
                runtime.NextSlot = consumed + runtime.Def.Every!.Value;
                runtime.SlotArmed = true;
                break;
            default:
                runtime.SlotArmed = false;
                break;
        }
    }

    /// <summary>Initial arm for a definition (boot and dynamic register alike).</summary>
    internal void ArmRuntime(QuickJobScheduler.JobRuntime runtime)
    {
        var now = _tp.GetUtcNow();
        lock (runtime.ArmLock)
        {
            switch (runtime.Def.Kind)
            {
                case JobKind.Cron:
                {
                    var tz = runtime.ResolveTimeZone(_owner.Options.ResolveDefaultTimeZone());
                    var cron = runtime.Cron;
                    DateTimeOffset? catchup = null;
                    if (runtime.Def.Options.OnMissedSlot == MissedSlotPolicy.CatchUpLatest && cron is not null)
                    {
                        var latest = cron.Previous(now, tz);
                        if (latest is { } late && now - late > runtime.Def.Options.Grace) catchup = late;
                    }
                    var next = cron?.Next(now, tz) ?? DateTimeOffset.MaxValue;
                    runtime.NextSlot = next;
                    runtime.SlotArmed = cron is not null;
                    if (catchup is { } cu)
                    {
                        _ = PutAsync(new StoredDueRun
                        {
                            RunId = Guid.NewGuid(),
                            JobName = runtime.Def.Name,
                            DueAt = cu,
                            Attempt = 1,
                            Origin = JobTrigger.CronTick,
                            Payload = null,
                        });
                    }
                    break;
                }
                case JobKind.Interval:
                    runtime.NextSlot = now + runtime.Def.Every!.Value;
                    runtime.SlotArmed = true;
                    break;
                case JobKind.Once:
                    // 立刻把 RunAt 排进 store（含过去的时间：立刻 eligible，靠 dispatch 端 grace 决定走或留）
                    if (runtime.Def.RunAt is { } at)
                    {
                        _ = PutAsync(new StoredDueRun
                        {
                            RunId = Guid.NewGuid(),
                            JobName = runtime.Def.Name,
                            DueAt = at,
                            Attempt = 1,
                            Origin = JobTrigger.Scheduled,
                            Payload = runtime.OncePayload,
                        });
                    }
                    runtime.SlotArmed = false;
                    break;
                default:
                    runtime.SlotArmed = false;
                    break;
            }
        }
        RescheduleSlotPump();
    }

    internal void ArmAllSlots()
    {
        foreach (var runtime in _owner.JobsSnapshot())
            ArmRuntime(runtime);
    }

    private void RescheduleSlotPump()
    {
        if (_slotPump is null) return;
        var minNext = DateTimeOffset.MaxValue;
        var any = false;
        foreach (var runtime in _owner.JobsSnapshot())
        {
            lock (runtime.ArmLock)
            {
                if (runtime.SlotArmed && runtime.NextSlot < minNext) { minNext = runtime.NextSlot; any = true; }
            }
        }
        if (!any) { _slotPump.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); return; }
        var d = minNext - _tp.GetUtcNow();
        _slotPump.Change(d > TimeSpan.Zero ? d : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private async Task PutAsync(StoredDueRun run)
    {
        try
        {
            await _store.PutDueRunAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _owner.Anomaly($"PutDueRun({run.JobName}) failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------- claim loop

    private async Task ClaimLoopAsync()
    {
        while (true)
        {
            try
            {
                await _wake.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
                if (_stopping) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var moved = await TryClaimBatchAsync().ConfigureAwait(false);
            if (moved > 0)
            {
                _roomForWork.Release(Math.Min(moved, _maxParallelismSafe()));
            }
        }
    }

    private async Task<int> TryClaimBatchAsync()
    {
        if (_stopping) return 0;
        int free;
        lock (_pendingGate) free = _pendingCapacity - _pending.Count;
        if (free <= 0) return 0;

        try
        {
            var batch = Math.Min(_claimBatch, free);
            var leases = await _store.ClaimDueRunsAsync(_owner.Options.ResolveInstanceId(), batch, CancellationToken.None).ConfigureAwait(false);
            var moved = 0;
            foreach (var lease in leases)
            {
                if (_cancelled.TryRemove(lease.Run.RunId, out _))
                {
                    await _store.FinishLeaseAsync(lease, DueRunOutcome.Skipped).ConfigureAwait(false);
                    await RecordDropAsync(lease.Run, "Cancelled-before-dispatch");
                    Interlocked.Increment(ref _stats.Cancelled);
                    continue;
                }
                lock (_pendingGate)
                {
                    if (_pending.Count >= _pendingCapacity) break;
                    _pending.Add(new PendingItem(lease.Run, lease));
                    moved++;
                }
            }
            return moved;
        }
        catch (Exception ex)
        {
            _owner.Anomaly($"claim failed: {ex.Message}");
            return 0;
        }
    }

    // ----------------------------------------------------------- worker loops

    private async Task WorkerLoopAsync(int workerId)
    {
        while (true)
        {
            PendingItem? item;
            lock (_pendingGate)
            {
                if (_pending.Count == 0) item = null;
                else
                {
                    var index = IndexByPriority(_pending);
                    item = _pending[index];
                    _pending.RemoveAt(index);
                }
            }

            if (item is null)
            {
                if (_stopping)
                {
                    lock (_runningGate)
                    {
                        if (_running.Count == 0) break;
                    }
                    continue;
                }
                try
                {
                    await _roomForWork.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    // stoprip：一口气把栅栏拆完——pending 空、running 空才算下班
                    lock (_pendingGate)
                    {
                        if (_pending.Count == 0)
                        {
                            lock (_runningGate)
                            {
                                if (_running.Count == 0) break;
                            }
                        }
                    }
                    continue;
                }
            }

            try
            {
                await ExecuteAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _owner.Anomaly($"execute({item.Run.JobName}) burst: {ex.Message}");
            }

            // 完成/扔掉一次 run 都要叫醒看门人：pending 有新的了 / stopping 需要重新盘点
            _roomForWork.Release();
        }
        _ = workerId;
    }

    /// <summary>High priority first, then FIFO order (Low shares Normal's queue tail).</summary>
    private int IndexByPriority(List<PendingItem> list)
    {
        for (var i = 0; i < list.Count; i++)
        {
            // 所属 job 的当前优先级（不搞缓存：登记表就是真相）
            if (PriorityOf(list[i]) == JobPriority.High) return i;
        }
        return 0;
    }

    private JobPriority PriorityOf(PendingItem item)
        => _owner.FindJob(item.Run.JobName) is { } runtime ? runtime.Def.Options.Priority : JobPriority.Normal;

    // ---------------------------------------------------------------- execute

    private async Task ExecuteAsync(PendingItem item)
    {
        var run = item.Run;
        var runtime = _owner.FindJob(run.JobName);

        if (runtime is null || !runtime.Resolved)
        {
            Interlocked.Increment(ref _stats.Skipped);
            await FinishDropAsync(item, "NoHandler").ConfigureAwait(false);
            return;
        }
        if (!runtime.Def.Options.Enabled)
        {
            Interlocked.Increment(ref _stats.Skipped);
            await FinishDropAsync(item, "Disabled").ConfigureAwait(false);
            return;
        }
        if (runtime.Paused)
        {
            Interlocked.Increment(ref _stats.Skipped);
            await FinishDropAsync(item, "Paused").ConfigureAwait(false);
            return;
        }

        var now = _tp.GetUtcNow();
        var late = now - run.DueAt;
        if (run.Origin is JobTrigger.CronTick or JobTrigger.Scheduled
            && runtime.Def.Options.OnMissedSlot != MissedSlotPolicy.CatchUpLatest
            && runtime.Def.Options.Grace < late)
        {
            Interlocked.Increment(ref _stats.Skipped);
            await FinishDropAsync(item, $"Late{FormatLateness(late)}").ConfigureAwait(false);
            return;
        }

        var badge = new RunBadge(run.RunId, run.JobName, run.Attempt, run.Origin, run.DueAt);
        lock (_runningGate) _running[run.RunId] = badge;

        var gateClaimed = false;
        if (runtime.Def.Options.OnOverlap != OverlapPolicy.RunAnyway)
        {
            lock (runtime.Gate)
            {
                if (runtime.Gate.InFlight >= runtime.Gate.Limit)
                {
                    switch (runtime.Def.Options.OnOverlap)
                    {
                        case OverlapPolicy.Skip:
                            lock (_runningGate) _running.Remove(run.RunId);
                            Interlocked.Increment(ref _stats.Skipped);
                            Publish(new RunSkippedByGate(run.JobName, run.RunId, run.Attempt, "overlap Skip") { At = now });
                            _ = FinishDropAsync(item, "OverlapSkip");
                            return;

                        case OverlapPolicy.Queue:
                            if (runtime.Gate.Overflow.Count >= runtime.Def.Options.OverlapQueueLimit)
                            {
                                lock (_runningGate) _running.Remove(run.RunId);
                                Interlocked.Increment(ref _stats.Skipped);
                                Publish(new RunSkippedByGate(run.JobName, run.RunId, run.Attempt, "overlap Queue full") { At = now });
                                _ = FinishDropAsync(item, "OverlapQueueFull");
                                return;
                            }
                            runtime.Gate.Overflow.Enqueue(item);
                            return; // 判决延到前一拍放锁时
                    }
                }
                runtime.Gate.InFlight++;
                gateClaimed = true;
            }
        }
        badge.GateClaimed = gateClaimed ? 1 : 0;

        var startedAt = _tp.GetUtcNow();
        badge.State.StartedAt = startedAt;
        Interlocked.Increment(ref _stats.Started);
        Publish(new RunStarted(run.JobName, run.RunId, run.Attempt, run.Origin) { At = startedAt });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        if (runtime.Def.Options.Timeout is { } t) cts.CancelAfter(t);
        badge.RunCts = cts;

        var wasCancelled = false;
        string? error = null;
        var outcome = DueRunOutcome.Completed;
        try
        {
            var ctx = new JobRunContext
            {
                JobName = run.JobName,
                ExecutionId = run.RunId,
                Attempt = run.Attempt,
                ScheduledAt = run.DueAt,
                StartedAt = startedAt,
                Trigger = run.Origin,
                Payload = PayloadJson.FromStoreText(run.Payload),
                Token = cts.Token,
                RunState = badge.State,
            };
            await runtime.Handler(ctx).ConfigureAwait(false);
            outcome = DueRunOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            if (_cancelled.TryRemove(run.RunId, out _) || _shutdownCts.IsCancellationRequested)
            {
                outcome = DueRunOutcome.Skipped;
            }
            else
            {
                // 本机 timeout：按失败处理（走重试轮），错误记成超时
                outcome = DueRunOutcome.FailedFinal;
                error = $"timeout after {runtime.Def.Options.Timeout}";
            }
        }
        catch (Exception ex)
        {
            outcome = DueRunOutcome.FailedFinal;
            error = ex.ToString();
        }
        finally
        {
            badge.RunCts = null;
            cts.Dispose();
        }
        wasCancelled = outcome == DueRunOutcome.Skipped;
        if (outcome == DueRunOutcome.FailedFinal)
        {
            var done = run.Attempt;
            var moreLeft = runtime.Def.Options.MaxRetries >= 1
                && done <= runtime.Def.Options.MaxRetries
                && !_stopping
                && !_shutdownCts.IsCancellationRequested;
            if (moreLeft)
            {
                var delay = runtime.Def.Options.RetryDelay(done, Random.Shared.NextDouble());
                var rerun = run with { Attempt = done + 1, DueAt = _tp.GetUtcNow() + delay, Origin = JobTrigger.Retry };
                await _store.PutDueRunAsync(rerun, CancellationToken.None).ConfigureAwait(false);
                await _store.FinishLeaseAsync(item.Lease, DueRunOutcome.FailedRetryPlanned).ConfigureAwait(false);
                Interlocked.Increment(ref _stats.Retried);
                Publish(new RunRetrying(run.JobName, run.RunId, done + 1, delay, error ?? "unknown failure") { At = _tp.GetUtcNow() });
                await AppendHistoryAsync(badge, run, startedAt, "Retrying", error).ConfigureAwait(false);
            }
            else
            {
                await _store.FinishLeaseAsync(item.Lease, DueRunOutcome.FailedFinal).ConfigureAwait(false);
                Interlocked.Increment(ref _stats.Failed);
                Publish(new RunFailed(run.JobName, run.RunId, run.Attempt, error ?? "unknown failure") { At = _tp.GetUtcNow() });
                await AppendHistoryAsync(badge, run, startedAt, "Failed", error).ConfigureAwait(false);
            }
        }
        else
        {
            await _store.FinishLeaseAsync(item.Lease, outcome).ConfigureAwait(false);
            if (!wasCancelled)
            {
                Interlocked.Increment(ref _stats.Completed);
                Publish(new RunCompleted(run.JobName, run.RunId, run.Attempt, startedAt, _tp.GetUtcNow()));
                await AppendHistoryAsync(badge, run, startedAt, "Completed", null).ConfigureAwait(false);
            }
            else
            {
                Interlocked.Increment(ref _stats.Cancelled);
                Publish(new RunFailed(run.JobName, run.RunId, run.Attempt, "cancelled") { At = _tp.GetUtcNow() });
                await AppendHistoryAsync(badge, run, startedAt, "Cancelled", "cancelled").ConfigureAwait(false);
            }
        }

        lock (_runningGate) _running.Remove(run.RunId);
        if (gateClaimed) ReleaseGate(runtime);
        _ = wasCancelled;

        // 一次性 job 走完（跑成了 / 终败）就离场：定义从注册表和 store 里一起退。
        if (runtime.Def.Kind == JobKind.Once && outcome is DueRunOutcome.Completed or DueRunOutcome.FailedFinal)
        {
            _owner.ForgetJob(run.JobName);
            _ = DeleteDefinitionAsync(run.JobName);
        }
    }

    private void ReleaseGate(QuickJobScheduler.JobRuntime runtime)
    {
        PendingItem? repost = null;
        lock (runtime.Gate)
        {
            runtime.Gate.InFlight--;
            if (runtime.Gate.InFlight < 0) runtime.Gate.InFlight = 0;
            if (runtime.Gate.Overflow.Count > 0) repost = runtime.Gate.Overflow.Dequeue();
        }
        if (repost is null) return;
        lock (_pendingGate)
        {
            _pending.Insert(0, repost); // 立即出列的头席
        }
        _roomForWork.Release();
    }

    private async Task FinishDropAsync(PendingItem item, string tag)
    {
        await _store.FinishLeaseAsync(item.Lease, DueRunOutcome.Skipped).ConfigureAwait(false);
        await RecordDropAsync(item.Run, tag).ConfigureAwait(false);
    }

    private async Task RecordDropAsync(StoredDueRun run, string tag)
    {
        try
        {
            await _store.AppendExecutionAsync(new ExecutionRecord
            {
                ExecutionId = run.RunId,
                JobName = run.JobName,
                ScheduledAt = run.DueAt,
                StartedAt = null,
                FinishedAt = _tp.GetUtcNow(),
                Attempt = run.Attempt,
                Origin = run.Origin,
                Outcome = $"Skipped:{tag}",
                Error = null,
                Progress = null,
            }).ConfigureAwait(false);
        }
        catch
        {
            // 历史只影响观察面
        }

        // 一次性任务被 drop 也直接离场（用户已经选择了）
        if (_owner.FindJob(run.JobName) is { } def && def.Def.Kind == JobKind.Once && run.Origin == JobTrigger.Scheduled)
        {
            _owner.ForgetJob(run.JobName);
            _ = DeleteDefinitionAsync(run.JobName);
        }
    }

    private async Task AppendHistoryAsync(RunBadge badge, StoredDueRun run, DateTimeOffset startedAt, string outcome, string? error)
    {
        try
        {
            await _store.AppendExecutionAsync(new ExecutionRecord
            {
                ExecutionId = run.RunId,
                JobName = run.JobName,
                ScheduledAt = run.DueAt,
                StartedAt = startedAt,
                FinishedAt = _tp.GetUtcNow(),
                Attempt = run.Attempt,
                Origin = run.Origin,
                Outcome = outcome,
                Error = error,
                Progress = badge.State.Progress,
            }).ConfigureAwait(false);
        }
        catch
        {
            // Same deal — dashboard read-only
        }
    }

    private static string FormatLateness(TimeSpan late)
        => late.TotalHours >= 1 ? $"{late.TotalHours:F1}h" : late.TotalMinutes >= 1 ? $"{late.TotalMinutes:F1}m" : $"{late.TotalMilliseconds:F0}ms";

    private void Publish(QuickJobEvent e) => _owner.PublishEvent(e);

    // ---------------------------------------------------------- store hygiene

    internal async Task SaveBootDefinitionAsync(QuickJobScheduler.JobRuntime runtime)
    {
        try
        {
            await _store.SaveDefinitionAsync(new StoredDefinition
            {
                Name = runtime.Def.Name,
                Contract = DefinitionSerialization.Write(runtime.Def),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _owner.Anomaly($"definition save '{runtime.Def.Name}' failed: {ex.Message}");
        }
    }

    internal async Task DeleteDefinitionAsync(string name)
    {
        try { await _store.DeleteDefinitionAsync(name).ConfigureAwait(false); }
        catch (Exception ex) { _owner.Anomaly($"definition delete '{name}' failed: {ex.Message}"); }
    }

    /// <summary>Sweeps the leftover leases stuck in gate overflow queues at stop time.</summary>
    internal void SweepOverflowLeases()
    {
        foreach (var runtime in _owner.JobsSnapshot())
        {
            PendingItem[] leftovers;
            lock (runtime.Gate)
            {
                if (runtime.Gate.Overflow.Count == 0) continue;
                leftovers = [.. runtime.Gate.Overflow];
                runtime.Gate.Overflow.Clear();
            }
            foreach (var item in leftovers)
            {
                _store.FinishLeaseAsync(item.Lease, DueRunOutcome.Skipped).AsTask().GetAwaiter().GetResult();
            }
        }
    }

    // -------------------------------------------------------- dashboard reads

    internal QuickJobStats StatsSnapshot()
    {
        var pending = 0;
        lock (_pendingGate) pending += _pending.Count;
        foreach (var runtime in _owner.JobsSnapshot())
        {
            lock (runtime.Gate) pending += runtime.Gate.Overflow.Count;
        }
        int running;
        lock (_runningGate) running = _running.Count;
        return _stats.Snapshot(pending, running);
    }

    internal IReadOnlyList<RunState> PendingSnapshot()
    {
        var list = new List<RunState>();
        lock (_pendingGate)
        {
            foreach (var item in _pending)
            {
                list.Add(new RunState
                {
                    ExecutionId = item.Run.RunId,
                    JobName = item.Run.JobName,
                    Attempt = item.Run.Attempt,
                    Trigger = item.Run.Origin,
                    ScheduledAt = item.Run.DueAt,
                });
            }
        }
        foreach (var runtime in _owner.JobsSnapshot())
        {
            lock (runtime.Gate)
            {
                foreach (var item in runtime.Gate.Overflow)
                {
                    list.Add(new RunState
                    {
                        ExecutionId = item.Run.RunId,
                        JobName = item.Run.JobName,
                        Attempt = item.Run.Attempt,
                        Trigger = item.Run.Origin,
                        ScheduledAt = item.Run.DueAt,
                    });
                }
            }
        }
        return list;
    }

    internal IReadOnlyList<RunState> RunningSnapshot()
    {
        lock (_runningGate)
        {
            var badges = _running.Values.ToArray();
            var list = new List<RunState>(badges.Length);
            foreach (var badge in badges) list.Add(badge.Frozen());
            return list;
        }
    }

    internal async Task<bool> CancelAsync(Guid runId, CancellationToken ct)
    {
        var hit = false;
        PendingItem? dequeued = null;
        lock (_pendingGate)
        {
            var index = _pending.FindIndex(p => p.Run.RunId == runId);
            if (index >= 0)
            {
                dequeued = _pending[index];
                _pending.RemoveAt(index);
                hit = true;
            }
        }
        if (dequeued is not null)
        {
            Interlocked.Increment(ref _stats.Cancelled);
            await _store.FinishLeaseAsync(dequeued.Lease, DueRunOutcome.Skipped).ConfigureAwait(false);
            await RecordDropAsync(dequeued.Run, "Cancelled-by-user").ConfigureAwait(false);
            return true;
        }

        lock (_runningGate)
        {
            if (_running.TryGetValue(runId, out var badge))
            {
                badge.RunCts?.Cancel();
                hit = true;
            }
        }
        if (!hit) _cancelled[runId] = 0; // 已 claim 未在岗：claim loop 处理
        _ = ct;
        return hit;
    }

    // 诊断视图
    public override string ToString()
    {
        var sb = new StringBuilder("[QuickJob pipeline] pending=");
        lock (_pendingGate) sb.Append(_pending.Count);
        lock (_runningGate) sb.Append(" running=").Append(_running.Count);
        return sb.ToString();
    }
}
