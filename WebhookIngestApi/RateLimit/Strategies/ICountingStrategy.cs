namespace WebhookIngestApi.RateLimit.Strategies;

/// <summary>
/// The counting mechanism behind one policy. Implementations hold no keys and no dictionary — the
/// store owns per-key state, its lifetime and its locking, and hands the state back in on every
/// call. That split is what keeps a new mechanism to one small file: <see cref="Acquire"/> is
/// always called under the key's lock, so an implementation never needs to be thread-safe itself.
/// </summary>
internal interface ICountingStrategy
{
    RateLimitAlgorithm Kind { get; }

    /// <summary>Fresh per-key state for a first-time key (or one whose policy changed).</summary>
    object CreateState(RateLimitPolicy policy, DateTimeOffset now);

    /// <summary>
    /// Counts one request. Called under the key's lock with the state this algorithm created.
    /// Must not consume allowance when it denies.
    /// </summary>
    CounterResult Acquire(object state, RateLimitPolicy policy, DateTimeOffset now);
}
