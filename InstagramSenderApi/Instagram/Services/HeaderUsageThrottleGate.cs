namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// The proactive half of the outbound strategy: slow down before Meta has to say no. The usage
/// percentages come from the headers on previous responses (per-account BUC on the tenant row,
/// the shared budget on the app row), so this gate is only ever as current as the last call —
/// which is why it is paired with a reactive layer rather than trusted alone.
///
/// Nothing here counts our own calls, and that is the point: the usage percentage is the
/// authority's own figure. A local counter would be a model of someone else's budget, blind to the
/// other instances of this service, to anything else on the same FB app, and to the 24 h BUC
/// window — three ways to be confidently wrong that reading the header cannot be.
///
/// It owns no state of its own; <see cref="ITenantRateLimitService"/> decides both the delay and
/// whether the tenant is hard-blocked. The exception it lets through is the app-level or
/// account-level block, and it must propagate: the queue, not this gate, owns that retry.
/// </summary>
public sealed class HeaderUsageThrottleGate : IOutboundGate
{
    private readonly ITenantRateLimitService _rateLimitService;
    private readonly ILogger<HeaderUsageThrottleGate> _logger;

    public HeaderUsageThrottleGate(
        ITenantRateLimitService rateLimitService,
        ILogger<HeaderUsageThrottleGate> logger)
    {
        _rateLimitService = rateLimitService;
        _logger           = logger;
    }

    public int Order => GateOrder.HeaderUsageThrottle;

    public string Name => "header-usage throttle";

    public async ValueTask<TimeSpan> GetDelayAsync(OutboundDispatch dispatch, CancellationToken ct = default)
    {
        // May throw TenantBlockedException (account-level OR app-level block) — let it
        // propagate to SendQueueWorker, which re-enqueues the job.
        var delay = await _rateLimitService.GetThrottleDelayAsync(dispatch.TenantId, ct);

        if (!delay.HasValue) return TimeSpan.Zero;

        _logger.LogInformation(
            "Proactive throttle for tenant {TenantId}: delaying {DelayMs} ms",
            dispatch.TenantId, delay.Value.TotalMilliseconds);

        return delay.Value;
    }
}
