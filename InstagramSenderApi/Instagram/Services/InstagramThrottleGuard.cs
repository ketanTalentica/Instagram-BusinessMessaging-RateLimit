using Microsoft.Extensions.Options;
using InstagramSenderApi.Instagram.Infrastructure;

namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// Pre-flight check called before every outbound Instagram API call. Runs each
/// <see cref="IOutboundGate"/> in ascending Order and waits out whatever each one asks for, so we
/// never voluntarily exceed a rate limit. A gate throwing TenantBlockedException ends the attempt
/// immediately — no HTTP call is made and the queue re-enqueues the job.
///
/// The guard owns the waiting and nothing else: gates compute delays, this decides they are worth
/// sleeping through. All enforcement is gated by RateLimiting:Outbound:Enabled — when off, calls
/// pass straight through (the header-parsing handler keeps observing regardless).
/// </summary>
public sealed class InstagramThrottleGuard
{
    private readonly IOutboundGate[] _gates;
    private readonly IOptions<OutboundRateLimitOptions> _options;

    public InstagramThrottleGuard(
        IEnumerable<IOutboundGate> gates,
        IOptions<OutboundRateLimitOptions> options)
    {
        // Order is a property, not registration order: the per-second gate must run last or its
        // slot is reserved before an earlier gate's sleep, and that is not a wiring detail.
        _gates   = gates.OrderBy(g => g.Order).ToArray();
        _options = options;
    }

    /// <summary>The resolved pre-flight sequence, in the order it runs. Exposed for tests.</summary>
    public IReadOnlyList<IOutboundGate> Gates => _gates;

    /// <summary>
    /// Blocks the caller if the tenant needs throttling or is hard-blocked.
    /// <paramref name="dispatchClass"/> selects which of Meta's per-second caps applies —
    /// they differ by an order of magnitude between call classes.
    /// Respects the passed CancellationToken for graceful shutdown.
    /// </summary>
    public async Task EnforceAsync(
        string tenantId,
        DispatchClass dispatchClass = DispatchClass.Unclassified,
        CancellationToken ct = default)
    {
        if (!_options.Value.Enabled)
            return;

        var dispatch = new OutboundDispatch(tenantId, dispatchClass);

        foreach (var gate in _gates)
        {
            var delay = await gate.GetDelayAsync(dispatch, ct);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
        }
    }
}
