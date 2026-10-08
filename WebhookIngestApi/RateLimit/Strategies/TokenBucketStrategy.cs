namespace WebhookIngestApi.RateLimit.Strategies;

/// <summary>
/// Steady refill at Limit/WindowSeconds tokens per second, capacity Limit + BurstSize. Unlike the
/// two window algorithms it paces rather than counts: an idle caller banks up to a full bucket and
/// may spend it at once, then proceeds at the refill rate. That is the right shape for a scope
/// where a legitimate sender is naturally bursty and should not be punished for it.
///
/// The balance never goes negative here — a denial consumes nothing, so a rejected caller does not
/// push its own recovery further away. (The outbound PerSecondDispatchGate deliberately does the
/// opposite: it reserves, because there the caller is us and waiting is an option.)
/// </summary>
internal sealed class TokenBucketStrategy : ICountingStrategy
{
    public RateLimitAlgorithm Kind => RateLimitAlgorithm.TokenBucket;

    public object CreateState(RateLimitPolicy policy, DateTimeOffset now) =>
        new State { Tokens = policy.EffectiveLimit, LastRefill = now };

    public CounterResult Acquire(object state, RateLimitPolicy policy, DateTimeOffset now)
    {
        var s        = (State)state;
        var capacity = policy.EffectiveLimit;

        // Refill rate is the nominal limit, not the burst-inflated capacity: burst is depth, not speed.
        var perSecond = Math.Max(1, policy.Limit) / (double)Math.Max(1, policy.WindowSeconds);

        var elapsed = (now - s.LastRefill).TotalSeconds;
        if (elapsed > 0)
        {
            s.Tokens     = Math.Min(capacity, s.Tokens + elapsed * perSecond);
            s.LastRefill = now;
        }

        if (s.Tokens < 1)
        {
            var secondsToNextToken = (1 - s.Tokens) / perSecond;
            return new CounterResult(false, 0, now.AddSeconds(secondsToNextToken));
        }

        s.Tokens -= 1;

        // A full bucket is "reset" now; otherwise report when it would be full again.
        var secondsToFull = (capacity - s.Tokens) / perSecond;
        return new CounterResult(true, (int)Math.Floor(s.Tokens), now.AddSeconds(secondsToFull));
    }

    private sealed class State
    {
        public double         Tokens;
        public DateTimeOffset LastRefill;
    }
}
