using InstagramSenderApi.Instagram.Models;

namespace InstagramSenderApi.Instagram.Infrastructure;

public interface ITenantRateLimitRepository
{
    Task<TenantRateLimitState?> GetAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(TenantRateLimitState state, CancellationToken ct = default);

    /// <summary>Creates the TenantRateLimitState table if it does not already exist.</summary>
    Task EnsureTableExistsAsync(CancellationToken ct = default);
}
