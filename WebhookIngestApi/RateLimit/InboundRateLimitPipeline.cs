using Microsoft.Extensions.Options;
using WebhookIngestApi.RateLimit.Rules;

namespace WebhookIngestApi.RateLimit;

/// <summary>
/// Runs the inbound rules in ascending Order, fail-fast, and returns the first terminal decision:
/// block list → allow list → payload size → HMAC → global → per-IP → per-client → concurrency.
/// The order is a security property (cheap checks before I/O, HMAC before any counter moves), so
/// it comes from each rule's Order rather than from DI registration, and is sorted once here.
///
/// Adding a limit is a new IInboundRule plus one DI line; nothing in this class changes.
/// </summary>
public sealed class InboundRateLimitPipeline
{
    private readonly IInboundRule[] _rules;
    private readonly ClientIdentityResolver _identity;
    private readonly InboundRateLimitOptions _options;

    public InboundRateLimitPipeline(
        IEnumerable<IInboundRule> rules,
        ClientIdentityResolver identity,
        IOptions<InboundRateLimitOptions> options)
    {
        _rules    = rules.OrderBy(r => r.Order).ToArray();
        _identity = identity;
        _options  = options.Value;
    }

    public InboundRateLimitOptions Options => _options;

    /// <summary>The resolved pipeline, in the order it runs. Exposed for diagnostics and tests.</summary>
    public IReadOnlyList<IInboundRule> Rules => _rules;

    public async Task<LimitDecision> EvaluateAsync(HttpContext http, CancellationToken ct = default)
    {
        var ctx = new InboundRequest(http, _identity.ResolveIp(http), _identity.ResolveClientId(http));

        foreach (var rule in _rules)
        {
            var decision = await rule.EvaluateAsync(ctx, ct);
            if (decision.HasValue) return decision.Value;
        }

        return new LimitDecision(true, DenialReason.None, 0, ctx.Remaining);
    }
}
