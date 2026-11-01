namespace QuickJob.Tests;

using System.Threading;

/// <summary>
/// 确定性时钟：TimeProvider 家族的自养档。本 SDK 的抽象形状是
/// <c>ITimer CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan)</c>（virtual concrete），所以重写这一颗。
/// 典型用法是 create(∞,∞) 后按需 <c>Change(due, ∞)</c> —— infinite due = 不武装、infinite period =
/// 单次触发（触后停摆），且 <c>Change</c> 必须能把停摆的条目复活。<see cref="Advance"/> 把虚拟时间
/// 一路推过每个挂起的到期点（GetUtcNow 直读）；测试里所有调度时序都靠它钉死。
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private long _nowTicks = 638_000_000_000_000_000; // 固定原点，免得时钟漂测试
    private readonly List<TimerEntry> _timers = [];

    private sealed class TimerEntry(TimerCallback callback, object? state)
    {
        public long NextTick;
        public readonly TimerCallback Callback = callback;
        public readonly object? State = state;
        public TimeSpan Period;
        public bool Stopped;
    }

    public override DateTimeOffset GetUtcNow() => new(new DateTime(Interlocked.Read(ref _nowTicks), DateTimeKind.Utc));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var entry = new TimerEntry(callback, state);
        entry.Period = period;
        lock (_gate)
        {
            ArmTick(entry, dueTime);
            _timers.Add(entry);
        }
        return new FakeTimer(this, entry);
    }

    /// <summary>武装备到 <paramref name="dueTime"/>；infinite due 视作不武装（Stopped），负/零档按下一 tick 立即。</summary>
    private void ArmTick(TimerEntry entry, TimeSpan dueTime)
    {
        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            entry.Stopped = true;
            return;
        }
        entry.Stopped = false;
        entry.NextTick = Interlocked.Read(ref _nowTicks) + Math.Max(1, dueTime.Ticks);
    }

    /// <summary>当前虚拟时间上挂起的（未停摆）定时器数量（断言用）。</summary>
    public int PendingTimers
    {
        get { lock (_gate) return _timers.Count(t => !t.Stopped); }
    }

    /// <summary>推出 <paramref name="span"/> 的虚拟时长：逐个火点拨表开火（回调看到的 GetUtcNow
    /// 恰是它自己的预定时刻 —— 真实 OS 定时器语义），全部过站后才把余量拨完。</summary>
    public void Advance(TimeSpan span)
    {
        var limit = Interlocked.Read(ref _nowTicks) + Math.Max(0, span.Ticks);
        while (true)
        {
            TimerCallback? fire;
            object? state;
            lock (_gate)
            {
                var entry = _timers.Where(t => !t.Stopped && t.NextTick <= limit).OrderBy(t => t.NextTick).FirstOrDefault();
                if (entry is null)
                {
                    if (Interlocked.Read(ref _nowTicks) < limit) Interlocked.Exchange(ref _nowTicks, limit);
                    return;
                }
                var preset = entry.NextTick;
                if (preset > Interlocked.Read(ref _nowTicks)) Interlocked.Exchange(ref _nowTicks, preset);
                if (entry.Period > TimeSpan.Zero)
                {
                    // 重排按预定时刻起算（.NET 真钟口径），非实际开火时刻
                    entry.NextTick = preset + entry.Period.Ticks;
                    if (entry.NextTick <= preset) entry.NextTick = preset + 1;
                }
                else
                {
                    entry.Stopped = true; // infinite/zero period = 单次触发，触后停摆（Change 可复活）
                }
                fire = entry.Callback;
                state = entry.State;
            }
            fire?.Invoke(state);
        }
    }

    /// <summary>直接拨表到某时刻（过站触发，火序与 <see cref="Advance"/> 相同）。</summary>
    public void SetTime(DateTimeOffset utc)
    {
        var limit = utc.Ticks;
        var from = Interlocked.Read(ref _nowTicks);
        if (limit <= from) { Interlocked.Exchange(ref _nowTicks, limit); return; }
        Advance(TimeSpan.FromTicks(limit - from));
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerEntry entry) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                entry.Period = period;
                owner.ArmTick(entry, dueTime);
            }
            return true;
        }

        public void Dispose()
        {
            lock (owner._gate) entry.Stopped = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>压测/泄漏档共通的小工具：GC 推三轮只触发终结器镰刀。</summary>
public static class GcClean
{
    public static void Collect()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    /// <summary>全程累计分配量（吞吐口径）—— 注意它不随回收下降，不能当驻留用。</summary>
    public static long Heap() => GC.GetTotalAllocatedBytes(precise: true);

    /// <summary>强收后的驻留量（live 字节数）—— 泄漏断言用这个。</summary>
    public static long LiveHeap()
    {
        Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}

/// <summary>并发测试计次匣：handler 里塞进去的内容序号。</summary>
public sealed class Collector
{
    private long _seen;
    public long Increment() => Interlocked.Increment(ref _seen);
    public long Value => Interlocked.Read(ref _seen);
}
