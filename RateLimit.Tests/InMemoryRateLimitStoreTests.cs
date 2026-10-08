using WebhookIngestApi.RateLimit;
using Xunit;

namespace RateLimit.Tests;

/// <summary>
/// The store is the one component every inbound limit runs through, and the only one whose
/// behaviour is a formula rather than a branch. Each algorithm is tested against the property
/// that distinguishes it from the other two, not just "denies past the limit".
/// </summary>
public class InMemoryRateLimitStoreTests
{
    private static async Task<int> AllowedOutOf(IRateLimitStore store, string key, RateLimitPolicy policy, int attempts)
    {
        var allowed = 0;
        for (var i = 0; i < attempts; i++)
        {
            var result = await store.TryAcquireAsync(key, policy);
            if (result.IsAllowed) allowed++;
        }
        return allowed;
    }

    // ---------- sliding window (the default for every scope) ----------

    [Fact]
    public async Task SlidingWindow_allows_limit_plus_burst_then_denies()
    {
        var store  = new InMemoryRateLimitStore(new TestTimeProvider());
        var policy = RateLimitPolicy.Sliding(windowSeconds: 60, limit: 5, burstSize: 2);

        Assert.Equal(7, await AllowedOutOf(store, "k", policy, 10));
    }

    [Fact]
    public async Task SlidingWindow_reports_remaining_down_to_zero()
    {
        var store  = new InMemoryRateLimitStore(new TestTimeProvider());
        var policy = RateLimitPolicy.Sliding(60, limit: 3);

        Assert.Equal(2, (await store.TryAcquireAsync("k", policy)).Remaining);
        Assert.Equal(1, (await store.TryAcquireAsync("k", policy)).Remaining);
        Assert.Equal(0, (await store.TryAcquireAsync("k", policy)).Remaining);

        var denied = await store.TryAcquireAsync("k", policy);
        Assert.False(denied.IsAllowed);
        Assert.Equal(0, denied.Remaining);
    }

    [Fact]
    public async Task SlidingWindow_frees_allowance_gradually_as_individual_requests_age_out()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        var policy = RateLimitPolicy.Sliding(60, limit: 2);

        await store.TryAcquireAsync("k", policy);       // t = 0
        time.Advance(30);
        await store.TryAcquireAsync("k", policy);       // t = 30
        Assert.False((await store.TryAcquireAsync("k", policy)).IsAllowed);

        // At t = 61 only the first request has aged out, so exactly one slot is free —
        // this is what separates a sliding window from a fixed one.
        time.Advance(31);
        Assert.True((await store.TryAcquireAsync("k", policy)).IsAllowed);
        Assert.False((await store.TryAcquireAsync("k", policy)).IsAllowed);
    }

    [Fact]
    public async Task SlidingWindow_ResetAt_tracks_the_oldest_surviving_request()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        var policy = RateLimitPolicy.Sliding(60, limit: 1);

        var first = await store.TryAcquireAsync("k", policy);
        Assert.Equal(time.GetUtcNow().AddSeconds(60), first.ResetAt);

        time.Advance(10);
        var denied = await store.TryAcquireAsync("k", policy);

        // 60 s after the *first* request, not 60 s from now.
        Assert.Equal(time.GetUtcNow().AddSeconds(50), denied.ResetAt);
    }

    [Fact]
    public async Task Keys_are_counted_independently()
    {
        var store  = new InMemoryRateLimitStore(new TestTimeProvider());
        var policy = RateLimitPolicy.Sliding(60, limit: 1);

        Assert.True((await store.TryAcquireAsync("ip:1.1.1.1", policy)).IsAllowed);
        Assert.True((await store.TryAcquireAsync("ip:2.2.2.2", policy)).IsAllowed);
        Assert.False((await store.TryAcquireAsync("ip:1.1.1.1", policy)).IsAllowed);
    }

    [Fact]
    public async Task Concurrent_callers_get_exactly_limit_plus_burst_allows()
    {
        var store  = new InMemoryRateLimitStore(new TestTimeProvider());
        var policy = RateLimitPolicy.Sliding(60, limit: 50, burstSize: 10);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 500).Select(_ => store.TryAcquireAsync("hammered", policy)));

        Assert.Equal(60, results.Count(r => r.IsAllowed));
    }

    // ---------- fixed window ----------

    [Fact]
    public async Task FixedWindow_resets_the_whole_allowance_on_the_boundary()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        var policy = new RateLimitPolicy(RateLimitAlgorithm.FixedWindow, 60, Limit: 2, BurstSize: 0);

        Assert.Equal(2, await AllowedOutOf(store, "k", policy, 5));

        time.Advance(60);

        // The boundary burst this algorithm is known for: a full allowance immediately again.
        Assert.Equal(2, await AllowedOutOf(store, "k", policy, 5));
    }

    [Fact]
    public async Task FixedWindow_does_not_free_allowance_mid_window()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        var policy = new RateLimitPolicy(RateLimitAlgorithm.FixedWindow, 60, Limit: 1, BurstSize: 0);

        Assert.True((await store.TryAcquireAsync("k", policy)).IsAllowed);
        time.Advance(59);
        Assert.False((await store.TryAcquireAsync("k", policy)).IsAllowed);
    }

    // ---------- token bucket ----------

    [Fact]
    public async Task TokenBucket_starts_full_and_refills_at_the_nominal_rate()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        // 60 per 60 s = one token per second.
        var policy = new RateLimitPolicy(RateLimitAlgorithm.TokenBucket, 60, Limit: 60, BurstSize: 0);

        Assert.Equal(60, await AllowedOutOf(store, "k", policy, 100));   // drains the full bucket

        time.Advance(3);
        Assert.Equal(3, await AllowedOutOf(store, "k", policy, 100));    // exactly three refilled
    }

    [Fact]
    public async Task TokenBucket_denial_consumes_nothing()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        var policy = new RateLimitPolicy(RateLimitAlgorithm.TokenBucket, 60, Limit: 60, BurstSize: 0);

        await AllowedOutOf(store, "k", policy, 60);
        await AllowedOutOf(store, "k", policy, 500);   // hammer while empty

        // A rejected caller must not push its own recovery further away.
        time.Advance(1);
        Assert.True((await store.TryAcquireAsync("k", policy)).IsAllowed);
    }

    [Fact]
    public async Task TokenBucket_burst_adds_depth_not_speed()
    {
        var time   = new TestTimeProvider();
        var store  = new InMemoryRateLimitStore(time);
        var policy = new RateLimitPolicy(RateLimitAlgorithm.TokenBucket, 60, Limit: 60, BurstSize: 20);

        Assert.Equal(80, await AllowedOutOf(store, "k", policy, 200));   // capacity = limit + burst

        time.Advance(5);
        Assert.Equal(5, await AllowedOutOf(store, "k", policy, 200));    // still one per second
    }

    // ---------- policy plumbing ----------

    [Fact]
    public async Task Changing_a_scopes_algorithm_restarts_its_state_rather_than_reinterpreting_it()
    {
        var time  = new TestTimeProvider();
        var store = new InMemoryRateLimitStore(time);

        var sliding = RateLimitPolicy.Sliding(60, limit: 1);
        var fixedW  = new RateLimitPolicy(RateLimitAlgorithm.FixedWindow, 60, Limit: 1, BurstSize: 0);

        Assert.True((await store.TryAcquireAsync("k", sliding)).IsAllowed);
        Assert.False((await store.TryAcquireAsync("k", sliding)).IsAllowed);

        // Same key, different mechanism: the queue of timestamps means nothing to a counter,
        // so the state is rebuilt instead of being read as the wrong type.
        Assert.True((await store.TryAcquireAsync("k", fixedW)).IsAllowed);
    }

    [Fact]
    public async Task EffectiveLimit_never_denies_everything_when_a_limit_is_misconfigured_to_zero()
    {
        var store  = new InMemoryRateLimitStore(new TestTimeProvider());
        var policy = RateLimitPolicy.Sliding(60, limit: 0);

        Assert.True((await store.TryAcquireAsync("k", policy)).IsAllowed);
        Assert.False((await store.TryAcquireAsync("k", policy)).IsAllowed);
    }

    [Fact]
    public async Task Unknown_algorithm_fails_loudly_rather_than_letting_traffic_through()
    {
        var store  = new InMemoryRateLimitStore(new TestTimeProvider());
        var policy = new RateLimitPolicy((RateLimitAlgorithm)99, 60, 10, 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryAcquireAsync("k", policy));
    }
}
