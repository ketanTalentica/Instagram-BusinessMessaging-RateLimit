using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// First in the pipeline: the fastest check there is, and the one whose traffic deserves the
/// least work. A blocked IP never reaches the store, so a banned host cannot consume the shared
/// global window just by being denied.
/// </summary>
public sealed class BlockedIpRule : IInboundRule
{
    private readonly InboundRateLimitOptions _options;
    private readonly ILogger<BlockedIpRule> _logger;

    public BlockedIpRule(IOptions<InboundRateLimitOptions> options, ILogger<BlockedIpRule> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public int Order => RuleOrder.BlockedIp;

    public string Name => "blocked-ip";

    public ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default)
    {
        if (!_options.BlockedIps.Contains(ctx.Ip))
            return ValueTask.FromResult<LimitDecision?>(null);

        _logger.LogWarning("Blocked IP {Ip} attempted access to {Path}", ctx.Ip, ctx.Http.Request.Path);
        return ValueTask.FromResult<LimitDecision?>(
            new LimitDecision(false, DenialReason.Blocked, 0, 0));
    }
}
