using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// Shared shape of the three counted limits: build a key, ask the store whether one more request
/// fits the scope's policy, deny with the scope's <see cref="DenialReason"/> if not. The scopes differ
/// only in key, limit, burst and algorithm — which is exactly what makes the mechanism swappable
/// per use case: each scope names its own <see cref="RateLimitAlgorithm"/> in config, and the store
/// applies it. Nothing here knows how a window is counted.
/// </summary>
public abstract class CountedLimitRule : IInboundRule
{
    private readonly IRateLimitStore _store;
    private readonly ILogger _logger;

    protected CountedLimitRule(IRateLimitStore store, IOptions<InboundRateLimitOptions> options, ILogger logger)
    {
        _store  = store;
        Options = options.Value;
        _logger = logger;
    }

    protected InboundRateLimitOptions Options { get; }

    public abstract int Order { get; }

    public abstract string Name { get; }

    protected abstract DenialReason DeniedWith { get; }

    protected abstract string BuildKey(InboundRequest ctx);

    protected abstract RateLimitPolicy BuildPolicy();

    /// <summary>Each scope keeps its own denial message — they are what an operator greps for.</summary>
    protected abstract void LogDenied(ILogger logger, InboundRequest ctx);

    public async ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default)
    {
        var result = await _store.TryAcquireAsync(BuildKey(ctx), BuildPolicy(), ct);

        if (result.IsAllowed)
        {
            // Narrowest scope wins: the rules run widest-first, so the last write is per-client.
            ctx.Remaining = result.Remaining;
            return null;
        }

        LogDenied(_logger, ctx);
        return new LimitDecision(false, DeniedWith, RetryAfter(result.ResetAt), 0);
    }

    private static int RetryAfter(DateTimeOffset resetAt) =>
        Math.Max(1, (int)(resetAt - DateTimeOffset.UtcNow).TotalSeconds);
}

/// <summary>
/// Whole-service ceiling — one key for all callers, so it also caps distributed floods.
///
/// Sliding window, and the usual objection to it does not apply: key cardinality is 1, so the
/// whole scope costs at most EffectiveLimit timestamps. Exactness is free here, and a fixed
/// window's boundary burst would mean twice the whole-service ceiling across the seam.
/// </summary>
public sealed class GlobalLimitRule : CountedLimitRule
{
    public GlobalLimitRule(IRateLimitStore store, IOptions<InboundRateLimitOptions> options,
                            ILogger<GlobalLimitRule> logger)
        : base(store, options, logger) { }

    public override int Order => RuleOrder.GlobalLimit;

    public override string Name => "global-limit";

    protected override DenialReason DeniedWith => DenialReason.Global;

    protected override string BuildKey(InboundRequest ctx) => "global";

    protected override RateLimitPolicy BuildPolicy() => new(
        Options.GlobalAlgorithm, Options.WindowSeconds, Options.GlobalLimitPerMinute, Options.BurstSize);

    protected override void LogDenied(ILogger logger, InboundRequest ctx) =>
        logger.LogWarning("Global rate limit hit from {Ip}/{ClientId}", ctx.Ip, ctx.ClientId);
}

/// <summary>
/// Per source address. No burst allowance: a single IP gets the flat documented rate.
///
/// This is an abuse control, not a fairness quota, which is why the exact window earns its memory:
/// a fixed window's boundary burst is tunable by the attacker — cluster either side of the seam
/// for 2x the rate — and that is the traffic this layer exists to stop. It is also the scope most
/// likely to want FixedWindow once keys are internet-cardinality on a shared store.
/// </summary>
public sealed class PerIpLimitRule : CountedLimitRule
{
    public PerIpLimitRule(IRateLimitStore store, IOptions<InboundRateLimitOptions> options,
                           ILogger<PerIpLimitRule> logger)
        : base(store, options, logger) { }

    public override int Order => RuleOrder.PerIpLimit;

    public override string Name => "per-ip-limit";

    protected override DenialReason DeniedWith => DenialReason.Ip;

    protected override string BuildKey(InboundRequest ctx) => $"ip:{ctx.Ip}";

    protected override RateLimitPolicy BuildPolicy() => new(
        Options.PerIpAlgorithm, Options.WindowSeconds, Options.PerIpLimitPerMinute, 0);

    protected override void LogDenied(ILogger logger, InboundRequest ctx) =>
        logger.LogWarning("Per-IP rate limit hit: {Ip}", ctx.Ip);
}

/// <summary>
/// Per identified sender (X-Webhook-Source / X-Api-Key, falling back to IP). Runs last of the
/// three, so its Remaining is the figure reported back to the caller.
///
/// The sender is authenticated by the time this runs, so the job is fairness between honest
/// callers rather than abuse defence. BurstSize gives a legitimately bursty sender headroom
/// without switching mechanism — token-bucket depth with window exactness.
/// </summary>
public sealed class PerClientLimitRule : CountedLimitRule
{
    public PerClientLimitRule(IRateLimitStore store, IOptions<InboundRateLimitOptions> options,
                               ILogger<PerClientLimitRule> logger)
        : base(store, options, logger) { }

    public override int Order => RuleOrder.PerClientLimit;

    public override string Name => "per-client-limit";

    protected override DenialReason DeniedWith => DenialReason.Client;

    protected override string BuildKey(InboundRequest ctx) => $"client:{ctx.ClientId}";

    protected override RateLimitPolicy BuildPolicy() => new(
        Options.PerClientAlgorithm, Options.WindowSeconds, Options.PerClientLimitPerMinute, Options.BurstSize);

    protected override void LogDenied(ILogger logger, InboundRequest ctx) =>
        logger.LogWarning("Per-client rate limit hit: {ClientId}", ctx.ClientId);
}
