namespace InstagramSenderApi.Instagram.Models;

/// <summary>
/// Per-tenant rate-limit state persisted in SQL Server.
/// Loaded on first request; refreshed after every Instagram API response.
/// </summary>
public sealed class TenantRateLimitState
{
    public string    TenantId           { get; set; } = string.Empty;
    public int       MaxCallCountPct    { get; set; }
    public int       MaxTotalTimePct    { get; set; }
    public int       MaxTotalCpuTimePct { get; set; }
    public DateTime? BlockedUntilUtc    { get; set; }
    public DateTime  LastUpdatedUtc     { get; set; } = DateTime.UtcNow;

    public bool    IsCurrentlyBlocked => BlockedUntilUtc.HasValue && BlockedUntilUtc.Value > DateTime.UtcNow;
    public TimeSpan? BlockedFor       => IsCurrentlyBlocked ? BlockedUntilUtc!.Value - DateTime.UtcNow : null;
}
