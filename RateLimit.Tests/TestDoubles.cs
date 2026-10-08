using InstagramSenderApi.Instagram.Infrastructure;
using InstagramSenderApi.Instagram.Models;
using Microsoft.Extensions.Options;
using WebhookIngestApi.RateLimit;

namespace RateLimit.Tests;

/// <summary>
/// Hand-written fakes rather than a mocking library: the solution has no test dependencies to
/// speak of, and each of these is small enough that a reader can see exactly what is being
/// simulated without learning a matcher DSL.
/// </summary>
internal static class TestOptions
{
    public static IOptions<T> Of<T>(T value) where T : class => Options.Create(value);
}

/// <summary>Clock the tests drive by hand, so window expiry needs no real waiting.</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}

/// <summary>
/// Records every call so a test can prove a rule short-circuited *before* the store was touched —
/// the ordering guarantees are the point of the pipeline, and only a counting fake can show them.
/// </summary>
internal sealed class RecordingRateLimitStore : IRateLimitStore
{
    private readonly Queue<CounterResult> _scripted = new();

    public List<(string Key, RateLimitPolicy Policy)> Calls { get; } = [];

    public int CallCount => Calls.Count;

    /// <summary>Queue a specific answer; anything unscripted is allowed.</summary>
    public RecordingRateLimitStore Returns(CounterResult result)
    {
        _scripted.Enqueue(result);
        return this;
    }

    public Task<CounterResult> TryAcquireAsync(string key, RateLimitPolicy policy, CancellationToken ct = default)
    {
        Calls.Add((key, policy));

        var result = _scripted.Count > 0
            ? _scripted.Dequeue()
            : new CounterResult(true, 99, DateTimeOffset.UtcNow.AddSeconds(60));

        return Task.FromResult(result);
    }
}

/// <summary>In-memory stand-in for the SQL repository.</summary>
internal sealed class FakeTenantRateLimitRepository : ITenantRateLimitRepository
{
    private readonly Dictionary<string, TenantRateLimitState> _rows = new(StringComparer.OrdinalIgnoreCase);

    public List<TenantRateLimitState> Upserts { get; } = [];

    public void Seed(TenantRateLimitState state) => _rows[state.TenantId] = state;

    public Task<TenantRateLimitState?> GetAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(tenantId, out var state) ? state : null);

    public Task UpsertAsync(TenantRateLimitState state, CancellationToken ct = default)
    {
        _rows[state.TenantId] = state;
        Upserts.Add(state);
        return Task.CompletedTask;
    }

    public Task EnsureTableExistsAsync(CancellationToken ct = default) => Task.CompletedTask;
}
