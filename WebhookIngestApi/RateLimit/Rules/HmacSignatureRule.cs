using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// Last of the cheap checks and the last one before any counter moves: forged traffic must not be
/// able to consume the global or per-IP window on its way to being rejected. Body methods only —
/// a GET carries no signed payload (and never reaches here anyway, the middleware bypasses GETs).
/// </summary>
public sealed class HmacSignatureRule : IInboundRule
{
    private readonly InboundRateLimitOptions _options;
    private readonly ILogger<HmacSignatureRule> _logger;

    public HmacSignatureRule(IOptions<InboundRateLimitOptions> options, ILogger<HmacSignatureRule> logger)
    {
        _options = options.Value;
        _logger  = logger;

        if (string.IsNullOrEmpty(_options.HmacSecretKey))
            _logger.LogWarning(
                "HmacSecretKey is not configured — HMAC signature validation is DISABLED. " +
                "Never run this way in production.");
    }

    public int Order => RuleOrder.Hmac;

    public string Name => "hmac-signature";

    public async ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default)
    {
        if (!ctx.HasBody || await IsSignatureValidAsync(ctx.Http, ct))
            return null;

        _logger.LogWarning("Invalid HMAC from {ClientId} at {Ip}", ctx.ClientId, ctx.Ip);
        return new LimitDecision(false, DenialReason.Signature, 0, 0);
    }

    private async Task<bool> IsSignatureValidAsync(HttpContext ctx, CancellationToken ct)
    {
        // HMAC disabled when no secret is configured (dev/test)
        if (string.IsNullOrEmpty(_options.HmacSecretKey)) return true;

        if (!ctx.Request.Headers.TryGetValue("X-Hub-Signature-256", out var sigHeader))
            return false;

        var sig = sigHeader.ToString();
        if (!sig.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;

        var expectedHex = sig[7..].ToLowerInvariant();

        // Body was buffered by the EnableBuffering() call in Program.cs
        ctx.Request.Body.Position = 0;
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        ctx.Request.Body.Position = 0;

        var keyBytes     = Encoding.UTF8.GetBytes(_options.HmacSecretKey);
        var bodyBytes    = Encoding.UTF8.GetBytes(body);
        var computedHash = HMACSHA256.HashData(keyBytes, bodyBytes);
        var computedHex  = Convert.ToHexString(computedHash).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computedHex),
            Encoding.ASCII.GetBytes(expectedHex));
    }
}
