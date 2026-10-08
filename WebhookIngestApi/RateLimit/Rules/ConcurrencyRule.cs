using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// Last rule, and the only one that holds a resource past its own return: a slot is taken here and
/// released when the response completes, which is what bounds slow-drip (slow-loris) traffic that
/// every rate window happily allows. Must be a singleton — the slots are the state.
/// </summary>
public sealed class ConcurrencyRule : IInboundRule
{
    // Past this many tracked clients, drop the fully-idle slots on the way through.
    private const int PruneThreshold = 10_000;

    // Seconds a rejected caller is told to wait; a slot frees on response completion, not a clock.
    private const int RetryAfterSeconds = 5;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _slots = new();
    private readonly InboundRateLimitOptions _options;
    private readonly ILogger<ConcurrencyRule> _logger;

    public ConcurrencyRule(IOptions<InboundRateLimitOptions> options, ILogger<ConcurrencyRule> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public int Order => RuleOrder.Concurrency;

    public string Name => "concurrency";

    public async ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default)
    {
        // clientId is attacker-controlled, so bound the dictionary: past the threshold, drop
        // slots that are fully idle. (A dropped-then-recreated slot briefly doubles one
        // client's allowance — acceptable versus unbounded memory growth.)
        if (_slots.Count > PruneThreshold)
        {
            foreach (var (key, slot) in _slots)
                if (slot.CurrentCount == _options.MaxConcurrencyPerClient)
                    _slots.TryRemove(key, out _);
        }

        var semaphore = _slots.GetOrAdd(
            ctx.ClientId,
            _ => new SemaphoreSlim(_options.MaxConcurrencyPerClient, _options.MaxConcurrencyPerClient));

        // Non-blocking: a queued request would defeat the point of a concurrency cap.
        if (!await semaphore.WaitAsync(0, ct))
        {
            _logger.LogWarning("Concurrency limit hit for client {ClientId}", ctx.ClientId);
            return new LimitDecision(false, DenialReason.Concurrency, RetryAfterSeconds, 0);
        }

        // Release when the response finishes, not when this rule returns.
        ctx.Http.Response.OnCompleted(() => { semaphore.Release(); return Task.CompletedTask; });
        return null;
    }
}
