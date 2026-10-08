using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using WebhookIngestApi.RateLimit;
using WebhookIngestApi.RateLimit.Rules;
using Xunit;

namespace RateLimit.Tests;

/// <summary>
/// The pipeline's value is its order: cheap checks before I/O, and signature validation before any
/// counter moves so forged traffic cannot spend a real client's quota. These tests assert the
/// order itself and then prove each short-circuit by watching whether the store was touched.
/// </summary>
public class InboundPipelineTests
{
    private const string Secret = "test-secret";

    private static (InboundRateLimitPipeline Service, RecordingRateLimitStore Store) Build(
        Action<InboundRateLimitOptions>? configure = null)
    {
        var options = new InboundRateLimitOptions
        {
            HmacSecretKey           = Secret,
            TrustForwardedFor       = true,
            GlobalLimitPerMinute    = 1000,
            PerIpLimitPerMinute     = 60,
            PerClientLimitPerMinute = 100,
            BurstSize               = 20,
            WindowSeconds           = 60,
            MaxPayloadBytes         = 1024,
            MaxConcurrencyPerClient = 10
        };
        configure?.Invoke(options);

        var opts  = TestOptions.Of(options);
        var store = new RecordingRateLimitStore();

        IInboundRule[] rules =
        [
            new BlockedIpRule(opts, NullLogger<BlockedIpRule>.Instance),
            new AllowedIpRule(opts),
            new PayloadSizeRule(opts, NullLogger<PayloadSizeRule>.Instance),
            new HmacSignatureRule(opts, NullLogger<HmacSignatureRule>.Instance),
            new GlobalLimitRule(store, opts, NullLogger<GlobalLimitRule>.Instance),
            new PerIpLimitRule(store, opts, NullLogger<PerIpLimitRule>.Instance),
            new PerClientLimitRule(store, opts, NullLogger<PerClientLimitRule>.Instance),
            new ConcurrencyRule(opts, NullLogger<ConcurrencyRule>.Instance)
        ];

        // Shuffled on the way in: the service must sort by Order, not trust its input.
        var service = new InboundRateLimitPipeline(rules.Reverse(), new ClientIdentityResolver(opts), opts);
        return (service, store);
    }

    private static DefaultHttpContext Request(
        string method   = "POST",
        string body     = "{}",
        string ip       = "10.0.0.1",
        string? clientId = "sim",
        bool sign       = true,
        long? contentLength = null)
    {
        var ctx   = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);

        ctx.Request.Method        = method;
        ctx.Request.Path          = "/webhook";
        ctx.Request.Body          = new MemoryStream(bytes);
        ctx.Request.ContentLength = contentLength ?? bytes.Length;
        ctx.Request.Headers["X-Forwarded-For"] = ip;

        if (clientId is not null) ctx.Request.Headers["X-Webhook-Source"] = clientId;
        if (sign) ctx.Request.Headers["X-Hub-Signature-256"] = Sign(body);

        return ctx;
    }

    private static string Sign(string body) =>
        "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

    [Fact]
    public void Rules_run_in_the_documented_order()
    {
        var (service, _) = Build();

        Assert.Equal(
            ["blocked-ip", "allowed-ip", "payload-size", "hmac-signature",
             "global-limit", "per-ip-limit", "per-client-limit", "concurrency"],
            service.Rules.Select(r => r.Name));
    }

    [Fact]
    public async Task Allowed_request_passes_every_rule_and_counts_all_three_windows()
    {
        var (service, store) = Build();

        var result = await service.EvaluateAsync(Request());

        Assert.True(result.IsAllowed);
        Assert.Equal(DenialReason.None, result.Reason);
        Assert.Equal(["global", "ip:10.0.0.1", "client:sim"], store.Calls.Select(c => c.Key));
    }

    [Fact]
    public async Task Blocked_ip_is_denied_before_the_store_is_touched()
    {
        var (service, store) = Build(o => o.BlockedIps.Add("10.0.0.1"));

        var result = await service.EvaluateAsync(Request());

        Assert.False(result.IsAllowed);
        Assert.Equal(DenialReason.Blocked, result.Reason);
        Assert.Equal(0, store.CallCount);   // a banned host cannot spend the global window
    }

    [Fact]
    public async Task Allow_listed_ip_bypasses_signature_validation_and_every_counter()
    {
        var (service, store) = Build(o => o.AllowedIps.Add("10.0.0.1"));

        var result = await service.EvaluateAsync(Request(sign: false));

        Assert.True(result.IsAllowed);
        Assert.Equal(int.MaxValue, result.Remaining);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task Body_without_content_length_is_411_before_the_signature_is_read()
    {
        var (service, store) = Build();

        var ctx = Request(sign: false);
        ctx.Request.ContentLength = null;

        var result = await service.EvaluateAsync(ctx);

        Assert.Equal(DenialReason.LengthRequired, result.Reason);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_on_the_header_alone()
    {
        var (service, store) = Build(o => o.MaxPayloadBytes = 10);

        var result = await service.EvaluateAsync(Request(body: new string('x', 50)));

        Assert.Equal(DenialReason.PayloadSize, result.Reason);
        Assert.Equal(0, store.CallCount);
    }

    [Fact]
    public async Task Forged_signature_is_denied_before_any_counter_moves()
    {
        var (service, store) = Build();

        var ctx = Request(sign: false);
        ctx.Request.Headers["X-Hub-Signature-256"] = "sha256=" + new string('0', 64);

        var result = await service.EvaluateAsync(ctx);

        Assert.Equal(DenialReason.Signature, result.Reason);
        Assert.Equal(0, store.CallCount);   // the whole point of HMAC running before the windows
    }

    [Fact]
    public async Task Missing_signature_header_is_denied()
    {
        var (service, _) = Build();

        Assert.Equal(DenialReason.Signature,
            (await service.EvaluateAsync(Request(sign: false))).Reason);
    }

    [Fact]
    public async Task Empty_secret_disables_signature_validation_for_local_runs()
    {
        var (service, _) = Build(o => o.HmacSecretKey = string.Empty);

        Assert.True((await service.EvaluateAsync(Request(sign: false))).IsAllowed);
    }

    [Fact]
    public async Task Methods_without_a_body_skip_the_signature_check()
    {
        var (service, _) = Build();

        var ctx = Request(method: "DELETE", sign: false);
        ctx.Request.ContentLength = null;

        Assert.True((await service.EvaluateAsync(ctx)).IsAllowed);
    }

    [Fact]
    public async Task A_full_window_denies_with_that_scopes_limit_type_and_a_retry_after()
    {
        var (service, store) = Build();

        store.Returns(new CounterResult(true, 5, DateTimeOffset.UtcNow.AddSeconds(60)))    // global
             .Returns(new CounterResult(false, 0, DateTimeOffset.UtcNow.AddSeconds(42))); // per-IP

        var result = await service.EvaluateAsync(Request());

        Assert.Equal(DenialReason.Ip, result.Reason);
        Assert.InRange(result.RetryAfterSeconds, 40, 42);
        Assert.Equal(2, store.CallCount);   // stopped at per-IP; per-client never ran
    }

    [Fact]
    public async Task Remaining_reported_to_the_caller_comes_from_the_narrowest_window()
    {
        var (service, store) = Build();

        store.Returns(new CounterResult(true, 900, DateTimeOffset.UtcNow.AddSeconds(60)))  // global
             .Returns(new CounterResult(true, 50,  DateTimeOffset.UtcNow.AddSeconds(60)))  // per-IP
             .Returns(new CounterResult(true, 7,   DateTimeOffset.UtcNow.AddSeconds(60))); // per-client

        Assert.Equal(7, (await service.EvaluateAsync(Request())).Remaining);
    }

    [Fact]
    public async Task Each_scope_is_counted_with_its_own_configured_algorithm()
    {
        var (service, store) = Build(o =>
        {
            o.GlobalAlgorithm    = RateLimitAlgorithm.FixedWindow;
            o.PerIpAlgorithm     = RateLimitAlgorithm.SlidingWindow;
            o.PerClientAlgorithm = RateLimitAlgorithm.TokenBucket;
        });

        await service.EvaluateAsync(Request());

        Assert.Equal(
            [RateLimitAlgorithm.FixedWindow, RateLimitAlgorithm.SlidingWindow, RateLimitAlgorithm.TokenBucket],
            store.Calls.Select(c => c.Policy.Algorithm));
    }

    [Fact]
    public async Task Per_ip_window_gets_no_burst_allowance()
    {
        var (service, store) = Build();

        await service.EvaluateAsync(Request());

        var perIp = store.Calls.Single(c => c.Key.StartsWith("ip:"));
        Assert.Equal(0, perIp.Policy.BurstSize);
        Assert.Equal(60, perIp.Policy.Limit);
    }

    [Fact]
    public async Task Concurrency_cap_denies_a_second_in_flight_request_from_the_same_client()
    {
        var (service, _) = Build(o => o.MaxConcurrencyPerClient = 1);

        var first = await service.EvaluateAsync(Request());   // holds its slot: no response completes
        Assert.True(first.IsAllowed);

        var second = await service.EvaluateAsync(Request());
        Assert.False(second.IsAllowed);
        Assert.Equal(DenialReason.Concurrency, second.Reason);
    }

    [Fact]
    public async Task Concurrency_slots_are_per_client_not_global()
    {
        var (service, _) = Build(o => o.MaxConcurrencyPerClient = 1);

        Assert.True((await service.EvaluateAsync(Request(clientId: "a"))).IsAllowed);
        Assert.True((await service.EvaluateAsync(Request(clientId: "b"))).IsAllowed);
    }
}
