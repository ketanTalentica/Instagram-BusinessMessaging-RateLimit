using InstagramSenderApi.Instagram.Models;

namespace InstagramSenderApi.Instagram.Services;

public interface ITenantRateLimitService
{
    /// <summary>
    /// Persists the worst-case usage percentages for a tenant.
    /// <paramref name="estimatedBlockMinutes"/> semantics:
    /// &gt; 0 → set BlockedUntilUtc to now + minutes (+1 min buffer);
    /// 0 → clear any existing block (a successful call proves access is regained);
    /// null → preserve the existing block (an unrelated failure must not unblock a tenant).
    /// </summary>
    Task RecordUsageAsync(
        string tenantId,
        int maxCallCountPct,
        int maxTotalTimePct,
        int maxTotalCpuTimePct,
        int? estimatedBlockMinutes,
        CancellationToken ct = default);

    Task<TenantRateLimitState?> GetStateAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Returns a delay to apply before the next call for this tenant.
    /// Returns null if no throttling is needed.
    /// Throws TenantBlockedException if the tenant is hard-blocked.
    /// </summary>
    Task<TimeSpan?> GetThrottleDelayAsync(string tenantId, CancellationToken ct = default);
}
