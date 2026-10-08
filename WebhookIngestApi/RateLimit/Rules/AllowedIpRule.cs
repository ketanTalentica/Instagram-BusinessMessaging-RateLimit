using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// The one rule that ends the pipeline with an *allow*: a configured allow-list entry skips every
/// remaining check, signature validation included. An empty list means "no bypass" rather than
/// "allow nobody", which is why the count is tested before the lookup.
/// </summary>
public sealed class AllowedIpRule : IInboundRule
{
    private readonly InboundRateLimitOptions _options;

    public AllowedIpRule(IOptions<InboundRateLimitOptions> options) => _options = options.Value;

    public int Order => RuleOrder.AllowedIp;

    public string Name => "allowed-ip";

    public ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default)
    {
        if (_options.AllowedIps.Count > 0 && _options.AllowedIps.Contains(ctx.Ip))
            return ValueTask.FromResult<LimitDecision?>(
                new LimitDecision(true, DenialReason.None, 0, int.MaxValue));

        return ValueTask.FromResult<LimitDecision?>(null);
    }
}
