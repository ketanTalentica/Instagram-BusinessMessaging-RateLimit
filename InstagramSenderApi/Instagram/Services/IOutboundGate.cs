namespace InstagramSenderApi.Instagram.Services;

/// <summary>One outbound call, described in the terms every gate needs.</summary>
/// <param name="TenantId">The account the call belongs to — every limit here is per-tenant.</param>
/// <param name="Class">
/// Which of Meta's per-second caps applies. Carried on the dispatch rather than derived per gate,
/// so classification happens once, at the queue, and every gate agrees on it.
/// </param>
public readonly record struct OutboundDispatch(string TenantId, DispatchClass Class);

/// <summary>
/// A pre-flight gate: one reason a call might have to wait before it is allowed onto the network.
/// Gates run in ascending <see cref="Order"/> and each returns how long the caller must wait
/// (<see cref="TimeSpan.Zero"/> = go now); the composer does the waiting.
///
/// Returning the delay instead of sleeping is deliberate. It keeps every gate unit-testable
/// without a clock or a real minute of wall time, and it keeps the decision to wait in one place
/// (<see cref="InstagramThrottleGuard"/>), which is the only code that knows whether waiting is
/// even the right answer.
///
/// A gate throws <see cref="TenantBlockedException"/> when no amount of waiting inside this call
/// can help — a hard block measured in minutes. That is not a delay to sleep through on a worker
/// thread: SendQueueWorker catches it and re-queues the job.
/// </summary>
public interface IOutboundGate
{
    /// <summary>Position in the pre-flight sequence — see <see cref="GateOrder"/>.</summary>
    int Order { get; }

    /// <summary>Short name used in diagnostics and in the gate-order test.</summary>
    string Name { get; }

    /// <summary>
    /// Returns the wait this gate requires before the call may proceed, or TimeSpan.Zero.
    /// Throws <see cref="TenantBlockedException"/> if the tenant must not call at all.
    /// </summary>
    ValueTask<TimeSpan> GetDelayAsync(OutboundDispatch dispatch, CancellationToken ct = default);
}

/// <summary>
/// Pre-flight order, spaced so a gate can be inserted without renumbering. The per-second gate is
/// deliberately last: it hands out a token for *now*, so anything that sleeps must already have
/// slept, or the token is spent on a call that has not happened yet.
/// </summary>
public static class GateOrder
{
    public const int HeaderUsageThrottle = 100;
    public const int PerSecondDispatch   = 200;
}
