namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// Thrown by InstagramThrottleGuard when a tenant is hard-blocked (BlockedUntilUtc is in the future).
/// SendQueueWorker catches this and re-enqueues the job after the retry delay.
/// </summary>
public sealed class TenantBlockedException : Exception
{
    public TimeSpan RetryAfter { get; }

    public TenantBlockedException(TimeSpan retryAfter)
        : base($"Tenant is rate-limited. Retry after {retryAfter.TotalMinutes:F1} minutes.")
    {
        RetryAfter = retryAfter;
    }
}
