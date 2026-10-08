using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using InstagramSenderApi.Instagram.Infrastructure;
using InstagramSenderApi.Instagram.Models;

namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// Singleton facade over the SQL repository with a 10-second in-memory cache layer.
/// Coordinates proactive throttle decisions and persists state so limits survive restarts.
/// </summary>
public sealed class TenantRateLimitService : ITenantRateLimitService
{
    private const int ThrottleThresholdPct = 80;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly ITenantRateLimitRepository _repo;
    private readonly IMemoryCache _cache;
    private readonly IOptions<OutboundRateLimitOptions> _options;
    private readonly ILogger<TenantRateLimitService> _logger;

    public TenantRateLimitService(
        ITenantRateLimitRepository repo,
        IMemoryCache cache,
        IOptions<OutboundRateLimitOptions> options,
        ILogger<TenantRateLimitService> logger)
    {
        _repo    = repo;
        _cache   = cache;
        _options = options;
        _logger  = logger;
    }

    public async Task RecordUsageAsync(
        string tenantId,
        int maxCallCountPct,
        int maxTotalTimePct,
        int maxTotalCpuTimePct,
        int? estimatedBlockMinutes,
        CancellationToken ct = default)
    {
        var state = new TenantRateLimitState
        {
            TenantId           = tenantId,
            MaxCallCountPct    = maxCallCountPct,
            MaxTotalTimePct    = maxTotalTimePct,
            MaxTotalCpuTimePct = maxTotalCpuTimePct,
            LastUpdatedUtc     = DateTime.UtcNow
        };

        if (estimatedBlockMinutes is > 0)
        {
            // Add 1-minute buffer to avoid immediately re-hitting the wall
            state.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(estimatedBlockMinutes.Value + 1);
            _logger.LogWarning(
                "Tenant {TenantId} is blocked for {Minutes} min (+ 1 min buffer)",
                tenantId, estimatedBlockMinutes.Value);
        }
        else if (estimatedBlockMinutes is null)
        {
            // Caller saw no explicit unblock signal — keep any active block instead of wiping it
            var existing = await GetStateAsync(tenantId, ct);
            if (existing?.IsCurrentlyBlocked == true)
                state.BlockedUntilUtc = existing.BlockedUntilUtc;
        }
        // estimatedBlockMinutes == 0 → successful call proved recovery; leave BlockedUntilUtc null

        _cache.Set(CacheKey(tenantId), state, CacheTtl);
        await _repo.UpsertAsync(state, ct);
    }

    public async Task<TenantRateLimitState?> GetStateAsync(string tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey(tenantId), out TenantRateLimitState? cached))
            return cached;

        var state = await _repo.GetAsync(tenantId, ct);
        if (state is not null)
            _cache.Set(CacheKey(tenantId), state, CacheTtl);

        return state;
    }

    public async Task<TimeSpan?> GetThrottleDelayAsync(string tenantId, CancellationToken ct = default)
    {
        var state = await GetStateAsync(tenantId, ct);

        // Hard block on this account — caller must not make any HTTP call
        if (state?.IsCurrentlyBlocked == true)
            throw new TenantBlockedException(state.BlockedFor!.Value);

        // X-App-Usage is one budget shared by every account on the FB app, recorded under
        // the global app:{AppId} row. Throttle on whichever is worse: this account's own
        // usage or the shared app usage another account may have driven up.
        var appState = await GetStateAsync(_options.Value.AppStateKey, ct);

        // App-level block (Meta error 4 / 613): the limit applies to every account on the
        // app, so a tenant with a clean row must be held back too. Without this check the
        // other tenants keep calling straight into an app-wide limit.
        if (appState?.IsCurrentlyBlocked == true)
        {
            _logger.LogWarning(
                "Tenant {TenantId} held by an APP-level block for {Remaining}",
                tenantId, appState.BlockedFor!.Value);
            throw new TenantBlockedException(appState.BlockedFor!.Value);
        }

        var effectivePct = Math.Max(state?.MaxCallCountPct ?? 0, appState?.MaxCallCountPct ?? 0);

        // Below threshold — no delay needed
        if (effectivePct < ThrottleThresholdPct)
            return null;

        // Proactive throttle: spread remaining quota (~20%) evenly over the rest of the hour
        // Formula: 3600s / (remainingPct * calls_per_pct_unit) — keeps us just under the ceiling
        var remainingPct = Math.Max(1, 100 - effectivePct);
        var delayMs      = (int)(3_600_000.0 / (remainingPct * 48)); // ~48 calls per pct unit assumed
        return TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 200, 60_000));
    }

    private static string CacheKey(string tenantId) => $"rls:{tenantId}";
}
