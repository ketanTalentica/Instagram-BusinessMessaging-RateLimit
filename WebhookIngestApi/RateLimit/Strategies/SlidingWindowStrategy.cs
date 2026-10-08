namespace WebhookIngestApi.RateLimit.Strategies;

/// <summary>
/// Exact count over the trailing window, kept as a queue of request timestamps: expired entries
/// are dequeued on every call, so the window really slides rather than resetting on a boundary.
/// This is the original behaviour of the store and remains the default for every scope — the
/// figures in docs/DEMO.md were measured against it.
///
/// It is the log form, not the weighted two-counter approximation that is the usual production
/// default. Deliberate: in-memory, single-instance, limits in the hundreds per key, and the store
/// prunes — so the memory that normally rules a log out is not in play, and an exact answer needs
/// no caveat when an operator asks why a request was denied. Revisit per scope on a shared store.
/// </summary>
internal sealed class SlidingWindowStrategy : ICountingStrategy
{
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.SlidingWindow;

    public object CreateState(RateLimitPolicy policy, DateTimeOffset now) => new Queue<DateTimeOffset>();

    public CounterResult Acquire(object state, RateLimitPolicy policy, DateTimeOffset now)
    {
        var timestamps  = (Queue<DateTimeOffset>)state;
        var windowStart = now.AddSeconds(-policy.WindowSeconds);

        while (timestamps.Count > 0 && timestamps.Peek() < windowStart)
            timestamps.Dequeue();

        var effectiveLimit = policy.EffectiveLimit;

        // The window frees up when the oldest surviving request ages out, not a fixed period later.
        var resetAt = timestamps.Count > 0
            ? timestamps.Peek().AddSeconds(policy.WindowSeconds)
            : now.AddSeconds(policy.WindowSeconds);

        if (timestamps.Count >= effectiveLimit)
            return new CounterResult(false, 0, resetAt);

        timestamps.Enqueue(now);
        return new CounterResult(true, effectiveLimit - timestamps.Count, resetAt);
    }
}
