namespace WebhookIngestApi.RateLimit.Strategies;

/// <summary>
/// One counter per window, reset when the window rolls over. Constant memory per key instead of
/// one timestamp per request, which is why it is worth having for very high-cardinality scopes.
///
/// Known trade-off, and the reason it is not the default: the boundary burst. A caller can spend
/// the whole allowance in the last instant of one window and the whole allowance again in the
/// first instant of the next — up to 2x the nominal rate across that seam.
/// </summary>
internal sealed class FixedWindowStrategy : ICountingStrategy
{
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.FixedWindow;

    public object CreateState(RateLimitPolicy policy, DateTimeOffset now) => new State { WindowStart = now };

    public CounterResult Acquire(object state, RateLimitPolicy policy, DateTimeOffset now)
    {
        var s      = (State)state;
        var window = TimeSpan.FromSeconds(policy.WindowSeconds);

        if (now - s.WindowStart >= window)
        {
            s.WindowStart = now;
            s.Count       = 0;
        }

        var resetAt        = s.WindowStart + window;
        var effectiveLimit = policy.EffectiveLimit;

        if (s.Count >= effectiveLimit)
            return new CounterResult(false, 0, resetAt);

        s.Count++;
        return new CounterResult(true, effectiveLimit - s.Count, resetAt);
    }

    private sealed class State
    {
        public DateTimeOffset WindowStart;
        public int            Count;
    }
}
