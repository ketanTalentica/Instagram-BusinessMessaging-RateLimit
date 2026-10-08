namespace WebhookIngestApi.RateLimit;

/// <summary>
/// Counting mechanisms a limit can be enforced with. Each answers "does one more request fit?"
/// differently, and the difference is operational, not cosmetic:
/// </summary>
public enum RateLimitAlgorithm
{
    /// <summary>
    /// Exact count over the trailing window. No edge burst, but it keeps one timestamp per
    /// request — the most memory of the three. The default, and what every documented figure
    /// in this solution was measured against.
    /// </summary>
    SlidingWindow,

    /// <summary>
    /// One counter per fixed window. Cheapest by far, at the cost of the classic boundary burst:
    /// a caller can spend a full window at the end of one and again at the start of the next.
    /// </summary>
    FixedWindow,

    /// <summary>
    /// Steady refill at Limit/WindowSeconds per second, capacity Limit + BurstSize. Smooths
    /// traffic instead of counting it, and is the right shape for a caller that should be allowed
    /// to bank a short burst and then be paced.
    /// </summary>
    TokenBucket
}

/// <summary>
/// What one limit is, independent of how it is counted. Passing the whole policy (rather than the
/// four loose numbers the sliding window happens to need) is what lets a scope pick its mechanism:
/// <c>BurstSize</c> means nothing to a fixed window and is capacity to a token bucket, and neither
/// the caller nor the store has to care.
/// </summary>
/// <param name="Algorithm">How the count is kept.</param>
/// <param name="WindowSeconds">Length of the window, or the refill period for a token bucket.</param>
/// <param name="Limit">Requests allowed per window.</param>
/// <param name="BurstSize">Extra allowance on top of <paramref name="Limit"/>; 0 for a flat rate.</param>
public readonly record struct RateLimitPolicy(
    RateLimitAlgorithm Algorithm,
    int                WindowSeconds,
    int                Limit,
    int                BurstSize)
{
    /// <summary>Total allowance including burst — never below 1, or a limit could deny everything.</summary>
    public int EffectiveLimit => Math.Max(1, Limit + BurstSize);

    public static RateLimitPolicy Sliding(int windowSeconds, int limit, int burstSize = 0) =>
        new(RateLimitAlgorithm.SlidingWindow, windowSeconds, limit, burstSize);
}

public interface IRateLimitStore
{
    /// <summary>
    /// Counts one request against <paramref name="key"/> under <paramref name="policy"/>.
    /// Returns IsAllowed = false when the policy has no room; nothing is consumed on a denial.
    /// </summary>
    Task<CounterResult> TryAcquireAsync(
        string key,
        RateLimitPolicy policy,
        CancellationToken ct = default);
}

/// <param name="IsAllowed">True if the request is within the allowed count.</param>
/// <param name="Remaining">Requests remaining in the current window.</param>
/// <param name="ResetAt">When the oldest request in the window falls out.</param>
public readonly record struct CounterResult(bool IsAllowed, int Remaining, DateTimeOffset ResetAt);
