using System.Diagnostics;

namespace Glossator.AI.Diagnostics;

/// <summary>
/// Tek bir StreamingChatHelper çağrısı içinde ResilienceChatClient tarafından yapılan
/// yeniden denemelerin başlangıç zamanını ve toplam backoff bekleme süresini tutar.
/// </summary>
internal sealed class ResilienceAttemptTracker(long initialStartTimestamp)
{
    private long _activeAttemptStartTimestamp = initialStartTimestamp;
    private int _attemptCount;
    private long _totalRetryWaitTicks;

    public int AttemptCount => Volatile.Read(ref _attemptCount);
    public int RetryCount => Math.Max(0, AttemptCount - 1);
    public long ActiveAttemptStartTimestamp => Interlocked.Read(ref _activeAttemptStartTimestamp);
    public TimeSpan TotalRetryWait => TimeSpan.FromTicks(Interlocked.Read(ref _totalRetryWaitTicks));

    public void RecordAttemptStart(int attemptNumber)
    {
        Volatile.Write(ref _attemptCount, attemptNumber);
        Interlocked.Exchange(ref _activeAttemptStartTimestamp, Stopwatch.GetTimestamp());
    }

    public void RecordRetryWait(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Interlocked.Add(ref _totalRetryWaitTicks, delay.Ticks);
        }
    }
}

/// <summary>
/// Active execution flow boyunca geçerli ResilienceAttemptTracker kapsamını yönetir.
/// </summary>
internal static class ResilienceAttemptContext
{
    private static readonly AsyncLocal<ResilienceAttemptTracker?> CurrentTracker = new();

    public static ResilienceAttemptTracker? Current => CurrentTracker.Value;

    public static Scope BeginScope()
    {
        var previous = CurrentTracker.Value;
        var tracker = new ResilienceAttemptTracker(Stopwatch.GetTimestamp());
        CurrentTracker.Value = tracker;
        return new Scope(tracker, previous);
    }

    internal readonly struct Scope(ResilienceAttemptTracker tracker, ResilienceAttemptTracker? previous) : IDisposable
    {
        public ResilienceAttemptTracker Tracker => tracker;
        public void Dispose() => CurrentTracker.Value = previous;
    }
}
