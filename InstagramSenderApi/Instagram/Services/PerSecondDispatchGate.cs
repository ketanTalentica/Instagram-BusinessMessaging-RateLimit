using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using InstagramSenderApi.Instagram.Infrastructure;

namespace InstagramSenderApi.Instagram.Services;

/// <summary>
/// Client-side gate per tenant enforcing Meta's per-second messaging cap: never more than the cap
/// in ANY one-second span, including the first second after a start.
///
/// Mechanism: a sliding-window log of reservations. Each key keeps the dispatch times of its last
/// N calls (N = the cap); a caller is given the later of "now" and "the oldest of those N plus one
/// second". Any N+1 consecutive dispatches therefore span at least one second, which is exactly the
/// guarantee — and bursts up to the cap still go out at once, so 20 text sends under a 100/s cap
/// never wait.
///
/// Why not the token bucket this replaced: a bucket that starts full and refills at the cap admits
/// up to (capacity + rate - 1) calls inside one second — 7 for a 4/s gate — which is how demo 2.1
/// hit the mock's 5/s cap. Shrinking the capacity closes that gap but smooths every burst, so text
/// bursts well inside the cap would start waiting. The log costs N timestamps per key instead of two
/// values; for the largest cap (100) that is 800 bytes per tenant and class.
///
/// It shapes rather than rejects because here we are the client: a send we refuse is work lost,
/// while a send we delay still happens. Reservations are taken under the key's lock, so concurrent
/// callers are serialised into increasing delays instead of spinning against the window.
///
/// What it cannot see: the guarantee holds at the moment we dispatch, not at Meta's door. Variable
/// network latency can bunch calls together on arrival, so a cap configured exactly at Meta's limit
/// has no margin for that — the demo runs 4/s against the mock's 5/s for this reason.
///
/// Runs last in the pre-flight sequence: the slot it hands out is for dispatching *now*,
/// so any earlier gate's wait must already have elapsed.
/// </summary>
public sealed class PerSecondDispatchGate : IOutboundGate
{
    private sealed class Window
    {
        // Dispatch times (TimeProvider timestamps) of the last Slots.Length reservations, as a
        // ring. Reservations are non-decreasing, so Slots[Next] is always the oldest.
        public long[] Slots = [];
        public int    Next;
        public bool   Full;

        public void Resize(int cap)
        {
            Slots = new long[cap];
            Next  = 0;
            Full  = false;
        }
    }

    private readonly ConcurrentDictionary<string, Window> _windows =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptions<OutboundRateLimitOptions> _options;
    private readonly ILogger<PerSecondDispatchGate> _logger;
    private readonly TimeProvider _time;

    public PerSecondDispatchGate(
        IOptions<OutboundRateLimitOptions> options,
        ILogger<PerSecondDispatchGate> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _logger  = logger;
        _time    = timeProvider ?? TimeProvider.System;
    }

    public int Order => GateOrder.PerSecondDispatch;

    public string Name => "per-second dispatch";

    /// <summary>
    /// Reserves this tenant's next slot for the call's class and returns how long the caller must
    /// wait to stay inside the cap. Each class has its own window because Meta enforces them as
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

    private TimeSpan Reserve(string key, int capPerSecond)
    {
        var window = _windows.GetOrAdd(key, _ => new Window());

        lock (window)
        {
            if (window.Slots.Length != capPerSecond)
                window.Resize(capPerSecond);

            var now = _time.GetTimestamp();
            var at  = now;

            // The window holds N reservations already: this call may not go out until the oldest
            // of them is a full second old.
            if (window.Full)
                at = Math.Max(now, window.Slots[window.Next] + _time.TimestampFrequency);

            window.Slots[window.Next] = at;
            window.Next = (window.Next + 1) % capPerSecond;
            if (window.Next == 0) window.Full = true;

            return at > now ? _time.GetElapsedTime(now, at) : TimeSpan.Zero;
        }
    }
}
