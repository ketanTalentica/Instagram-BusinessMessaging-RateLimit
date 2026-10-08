using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using InstagramSenderApi.Instagram.Infrastructure;

namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// Client-side token bucket per tenant enforcing Meta's per-second messaging cap.
/// Reservation-style (token balance may go negative): a caller that finds the bucket
/// empty still takes a token and is told how long to wait, so concurrent callers are
/// serialised fairly by increasing delays instead of spinning against the bucket.
///
/// It shapes rather than rejects because here we are the client: a send we refuse is work lost,
/// while a send we delay still happens. Returning a wait gives the behaviour of a leaky bucket
/// with the state of a token bucket — two values per key, no queue.
///
/// Known trade-off, the classic one for this algorithm: a fresh bucket starts full, so the first
/// second after a restart can emit up to 2x the cap. It is what makes demo 2.1 flaky (docs/DEMO.md
/// section 4) and it is an open decision, not an oversight.
///
/// Runs last in the pre-flight sequence: the token it hands out is for dispatching *now*,
/// so any earlier gate's wait must already have elapsed.
/// </summary>
public sealed class PerSecondDispatchGate : IOutboundGate
{
    private sealed class Bucket
    {
        public double Tokens;
        public long   LastRefillTicks;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<OutboundRateLimitOptions> _options;
    private readonly ILogger<PerSecondDispatchGate> _logger;

    public PerSecondDispatchGate(
        IOptions<OutboundRateLimitOptions> options,
        ILogger<PerSecondDispatchGate> logger)
    {
        _options = options;
        _logger  = logger;
    }

    public int Order => GateOrder.PerSecondDispatch;

    public string Name => "per-second dispatch";

    /// <summary>
    /// Reserves this tenant's next slot for the call's class and returns how long the caller must
    /// wait to stay inside the cap. Each class has its own bucket because Meta enforces them as
    /// separate limits — 100 text sends/s and 2 Conversations calls/s can run concurrently.
    /// </summary>
    public ValueTask<TimeSpan> GetDelayAsync(OutboundDispatch dispatch, CancellationToken ct = default)
    {
        var rate  = Math.Max(1, RateFor(dispatch.Class));
        var delay = Reserve($"{dispatch.TenantId}|{dispatch.Class}", rate);

        if (delay > TimeSpan.Zero)
            _logger.LogInformation(
                "Per-second gate for tenant {TenantId}: class={Class} cap={Rate}/s → waiting {DelayMs} ms",
                dispatch.TenantId, dispatch.Class, rate, delay.TotalMilliseconds);

        return ValueTask.FromResult(delay);
    }

    /// <summary>Returns the per-second cap for a class; see OutboundRateLimitOptions for the Meta figures.</summary>
    public int RateFor(DispatchClass dispatchClass)
    {
        var o = _options.Value;
        return dispatchClass switch
        {
            DispatchClass.TextSend       => o.PerSecondDispatchLimit,
            DispatchClass.MediaSend      => o.PerSecondMediaDispatchLimit,
            DispatchClass.Conversations  => o.PerSecondConversationsDispatchLimit,
            // Publishing has no documented per-second cap — its ceiling is the 24 h BUC
            // budget, already covered by the header-driven throttle. Use the text rate so
            // the gate never becomes the bottleneck for a legitimate publish burst.
            DispatchClass.ContentPublish => o.PerSecondDispatchLimit,
            _                            => o.PerSecondUnclassifiedDispatchLimit
        };
    }

    private TimeSpan Reserve(string bucketKey, int ratePerSecond)
    {
        var bucket = _buckets.GetOrAdd(bucketKey, _ => new Bucket
        {
            Tokens          = ratePerSecond,
            LastRefillTicks = Environment.TickCount64
        });

        lock (bucket)
        {
            var now            = Environment.TickCount64;
            var elapsedSeconds = (now - bucket.LastRefillTicks) / 1000.0;
            bucket.LastRefillTicks = now;
            bucket.Tokens = Math.Min(ratePerSecond, bucket.Tokens + elapsedSeconds * ratePerSecond);

            bucket.Tokens -= 1;
            if (bucket.Tokens >= 0)
                return TimeSpan.Zero;

            // Negative balance = reservation: wait until the deficit has refilled
            return TimeSpan.FromSeconds(-bucket.Tokens / ratePerSecond);
        }
    }
}
