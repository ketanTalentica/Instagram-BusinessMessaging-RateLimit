namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// Everything the rules share about one request. Identity is resolved once, up front, by
/// <see cref="ClientIdentityResolver"/> — every rule keys off the same IP and client id, and no
/// rule can accidentally re-derive them from different headers.
/// </summary>
public sealed class InboundRequest
{
    public InboundRequest(HttpContext http, string ip, string clientId)
    {
        Http     = http;
        Ip       = ip;
        ClientId = clientId;
        HasBody  = HttpMethods.IsPost(http.Request.Method)
                || HttpMethods.IsPut(http.Request.Method)
                || HttpMethods.IsPatch(http.Request.Method);
    }

    public HttpContext Http { get; }

    public string Ip { get; }

    public string ClientId { get; }

    /// <summary>True for POST/PUT/PATCH — the methods that carry a signed payload.</summary>
    public bool HasBody { get; }

    /// <summary>
    /// Allowance left in the narrowest window that has run so far; surfaced as
    /// <c>X-RateLimit-Remaining</c> on the allow path. Each window rule overwrites it, so the
    /// value that reaches the caller is the per-client one — the tightest of the three.
    /// </summary>
    public int Remaining { get; set; } = int.MaxValue;
}
