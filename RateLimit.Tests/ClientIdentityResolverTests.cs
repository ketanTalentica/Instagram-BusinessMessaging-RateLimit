using System.Net;
using Microsoft.AspNetCore.Http;
using WebhookIngestApi.RateLimit;
using Xunit;

namespace RateLimit.Tests;

/// <summary>
/// Identity decides which bucket a request is counted against, so getting it wrong is not a
/// cosmetic bug: trusting X-Forwarded-For on a directly-exposed endpoint lets one host rotate
/// fake IPs and make the per-IP limit unenforceable.
/// </summary>
public class ClientIdentityResolverTests
{
    private static ClientIdentityResolver Resolver(bool trustForwardedFor) =>
        new(TestOptions.Of(new InboundRateLimitOptions { TrustForwardedFor = trustForwardedFor }));

    private static DefaultHttpContext Context(string? xff = null, string? source = null, string? apiKey = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.0.9");
        if (xff    is not null) ctx.Request.Headers["X-Forwarded-For"]  = xff;
        if (source is not null) ctx.Request.Headers["X-Webhook-Source"] = source;
        if (apiKey is not null) ctx.Request.Headers["X-Api-Key"]        = apiKey;
        return ctx;
    }

    [Fact]
    public void Forwarded_for_is_ignored_when_it_is_not_trusted()
    {
        Assert.Equal("192.168.0.9", Resolver(false).ResolveIp(Context(xff: "1.2.3.4")));
    }

    [Fact]
    public void Forwarded_for_wins_when_a_trusted_proxy_sets_it()
    {
        Assert.Equal("1.2.3.4", Resolver(true).ResolveIp(Context(xff: "1.2.3.4")));
    }

    [Fact]
    public void Only_the_first_hop_of_a_forwarded_chain_is_used()
    {
        Assert.Equal("1.2.3.4", Resolver(true).ResolveIp(Context(xff: "1.2.3.4, 5.6.7.8, 9.9.9.9")));
    }

    [Fact]
    public void An_empty_forwarded_for_falls_back_to_the_socket_address()
    {
        Assert.Equal("192.168.0.9", Resolver(true).ResolveIp(Context(xff: "   ")));
    }

    [Fact]
    public void Client_id_prefers_the_webhook_source_header()
    {
        Assert.Equal("meta", Resolver(true).ResolveClientId(Context(source: "meta", apiKey: "key")));
    }

    [Fact]
    public void Client_id_falls_back_to_the_api_key_then_to_the_ip()
    {
        Assert.Equal("key", Resolver(true).ResolveClientId(Context(apiKey: "key")));
        Assert.Equal("192.168.0.9", Resolver(true).ResolveClientId(Context()));
    }

    [Fact]
    public void Attacker_supplied_keys_are_truncated_so_they_cannot_bloat_the_bucket_dictionary()
    {
        var id = Resolver(true).ResolveClientId(Context(source: new string('x', 5_000)));
        Assert.Equal(64, id.Length);
    }
}
