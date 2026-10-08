using System.Collections.Concurrent;
using WebhookIngestApi.RateLimit.Strategies;

namespace WebhookIngestApi.RateLimit;

/// <summary>
/// Owns per-key state, its locking and its eviction; the counting itself belongs to the
/// <see cref="ICountingStrategy"/> named by each call's policy, so two scopes can be counted
/// two different ways through one store. Idle buckets are pruned once the store grows past a
/// threshold, so an attacker rotating IPs / client ids cannot grow memory without bound.
///
/// Redis upgrade path: implement IRateLimitStore (ZADD + ZREMRANGEBYSCORE for the sliding window,
/// INCR + EXPIRE for the fixed window). Change ONE line in Program.cs — no other code changes.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    // Prune pass triggers above this many tracked keys (well above legitimate cardinality)
    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly Dictionary<RateLimitAlgorithm, ICountingStrategy> _strategies;
    private readonly TimeProvider _time;

    /// <param name="timeProvider">
    /// Injected so window expiry is testable without sleeping. Defaults to the system clock.
    /// </param>
    public InMemoryRateLimitStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _strategies = new ICountingStrategy[]
        {
            new SlidingWindowStrategy(),
            new FixedWindowStrategy(),
            new TokenBucketStrategy()
        }.ToDictionary(a => a.Kind);
    }

    public Task<CounterResult> TryAcquireAsync(
        string key, RateLimitPolicy policy, CancellationToken ct = default)
    {
        if (!_strategies.TryGetValue(policy.Algorithm, out var strategy))
            throw new ArgumentOutOfRangeException(
                nameof(policy), policy.Algorithm, "No counting strategy registered for this algorithm.");

        var now = _time.GetUtcNow();

        CounterResult result;
        while (true)
        {
            var bucket = _buckets.GetOrAdd(key, _ => new Bucket());

            lock (bucket.Lock)
            {
                // A prune pass removed this bucket between GetOrAdd and lock — get a fresh one
                if (bucket.Removed) continue;

                bucket.LastSeenUtc = now;

                // First use of this key, or the scope's algorithm was reconfigured: the old
                // state belongs to a different mechanism and cannot be reinterpreted.
                if (bucket.State is null || bucket.StateKind != policy.Algorithm)
                {
                    bucket.State     = strategy.CreateState(policy, now);
                    bucket.StateKind = policy.Algorithm;
                }

                result = strategy.Acquire(bucket.State, policy, now);
            }
            break;
        }

        // Prune outside any bucket lock — taking other buckets' locks while holding one
        // would create a lock-ordering deadlock between concurrent pruners.
        PruneIfNeeded(now, policy.WindowSeconds);
        return Task.FromResult(result);
    }

    private void PruneIfNeeded(DateTimeOffset now, int windowSeconds)
    {
        if (_buckets.Count <= PruneThreshold) return;

        var idleCutoff = now.AddSeconds(-windowSeconds * 2);
        foreach (var (key, bucket) in _buckets)
        {
            if (bucket.LastSeenUtc >= idleCutoff) continue;
            lock (bucket.Lock)
            {
                if (bucket.LastSeenUtc >= idleCutoff) continue; // re-check under lock
                bucket.Removed = true;
                _buckets.TryRemove(key, out _);
            }
        }
    }

    private sealed class Bucket
    {
        public readonly object Lock = new();
        public DateTimeOffset  LastSeenUtc;
        public bool            Removed;

        /// <summary>Opaque to the store — only the strategy that created it may read it.</summary>
        public object?            State;
        public RateLimitAlgorithm StateKind;
    }
}
