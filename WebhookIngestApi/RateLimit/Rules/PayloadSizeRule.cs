using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// Runs before HMAC because the signature check has to buffer the body: an unbounded stream must
/// be refused on the Content-Length header alone, before a single byte is read.
/// </summary>
public sealed class PayloadSizeRule : IInboundRule
{
    private readonly InboundRateLimitOptions _options;
    private readonly ILogger<PayloadSizeRule> _logger;

    public PayloadSizeRule(IOptions<InboundRateLimitOptions> options, ILogger<PayloadSizeRule> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public int Order => RuleOrder.PayloadSize;

    public string Name => "payload-size";

    public ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default)
    {
        // A body-carrying request without Content-Length (chunked encoding) would bypass this
        // guard and let the HMAC step buffer an unbounded stream — reject it outright.
        if (ctx.HasBody && ctx.Http.Request.ContentLength is null)
        {
            _logger.LogWarning("Missing Content-Length from {ClientId} at {Ip}", ctx.ClientId, ctx.Ip);
            return ValueTask.FromResult<LimitDecision?>(
                new LimitDecision(false, DenialReason.LengthRequired, 0, 0));
        }

        // Deliberately not limited to body methods: any request that declares an oversized body
        // is refused, whatever its verb.
        if (ctx.Http.Request.ContentLength > _options.MaxPayloadBytes)
        {
            _logger.LogWarning("Payload too large from {ClientId}: {Size} bytes",
                ctx.ClientId, ctx.Http.Request.ContentLength);
            return ValueTask.FromResult<LimitDecision?>(
                new LimitDecision(false, DenialReason.PayloadSize, 0, 0));
        }

        return ValueTask.FromResult<LimitDecision?>(null);
    }
}
