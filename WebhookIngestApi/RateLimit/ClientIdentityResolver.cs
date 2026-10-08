using Microsoft.Extensions.Options;

namespace WebhookIngestApi.RateLimit;

/// <summary>
/// Derives the two keys every limit is counted against. Extracted from the evaluation pipeline so
/// the spoofing rules (which header is trusted, and when) live in exactly one place and can be
/// tested without an HTTP stack.
/// </summary>
public sealed class ClientIdentityResolver
{
    // Header values are attacker-controlled — cap length so they cannot be used to bloat
    // rate-limit keys and tracking dictionaries.
    private const int MaxKeyLength = 64;

    private readonly InboundRateLimitOptions _options;

    public ClientIdentityResolver(IOptions<InboundRateLimitOptions> options) => _options = options.Value;

    /// <summary>
    /// X-Forwarded-For is attacker-controlled unless a trusted reverse proxy overwrites it.
    /// Trusting it on a directly-exposed endpoint lets one host rotate fake IPs and sidestep the
    /// per-IP limit entirely.
    /// </summary>
    public string ResolveIp(HttpContext ctx)
    {
        if (_options.TrustForwardedFor
            && ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xff))
        {
            var first = xff.ToString().Split(',')[0].Trim();
            if (first.Length > 0) return Truncate(first);
        }
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    public string ResolveClientId(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("X-Webhook-Source", out var src)) return Truncate(src.ToString());
        if (ctx.Request.Headers.TryGetValue("X-Api-Key",        out var key)) return Truncate(key.ToString());
        return ResolveIp(ctx);
    }

    private static string Truncate(string value) =>
        value.Length <= MaxKeyLength ? value : value[..MaxKeyLength];
}
