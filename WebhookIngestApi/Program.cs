using WebhookIngestApi.RateLimit;
using WebhookIngestApi.RateLimit.Rules;
using WebhookIngestApi.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<InboundRateLimitOptions>(
    builder.Configuration.GetSection(InboundRateLimitOptions.SectionName));

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRateLimitStore,          InMemoryRateLimitStore>();
builder.Services.AddSingleton<ClientIdentityResolver>();
builder.Services.AddSingleton<InboundRateLimitPipeline>();

// The inbound pipeline. Execution order comes from each rule's Order property, not from these
// lines — they are listed in pipeline order for readability only. A new limit is one more line.
builder.Services.AddSingleton<IInboundRule, BlockedIpRule>();
builder.Services.AddSingleton<IInboundRule, AllowedIpRule>();
builder.Services.AddSingleton<IInboundRule, PayloadSizeRule>();
builder.Services.AddSingleton<IInboundRule, HmacSignatureRule>();
builder.Services.AddSingleton<IInboundRule, GlobalLimitRule>();
builder.Services.AddSingleton<IInboundRule, PerIpLimitRule>();
builder.Services.AddSingleton<IInboundRule, PerClientLimitRule>();
builder.Services.AddSingleton<IInboundRule, ConcurrencyRule>();

WebhookResiliencePipeline.Register(builder.Services);

var app = builder.Build();

// EnableBuffering must run before InboundRateLimitMiddleware reads the body for HMAC
app.Use(async (ctx, next) => { ctx.Request.EnableBuffering(); await next(ctx); });
app.UseMiddleware<InboundRateLimitMiddleware>();

// Health probe — GET bypasses rate limiting by method; POST bypasses via ExcludedPaths.
// The POST variant exists to make the ExcludedPaths bypass demonstrable under load
// (POSTs are otherwise subject to every rate-limit check).
app.MapMethods("/health", ["GET", "POST"], () => Results.Ok(new { status = "ok" }));

// Main webhook endpoint � rate limiting is already enforced by the middleware above
app.MapPost("/webhook", async (HttpContext ctx, ILogger<Program> logger, CancellationToken ct) =>
{
    ctx.Request.Body.Position = 0;
    var body = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ct);
    logger.LogInformation("Webhook accepted: {Length} bytes from {Ip}",
        body.Length, ctx.Connection.RemoteIpAddress);
    // TODO: forward to downstream processor / queue via the WebhookResiliencePipeline
    return Results.Accepted();
});

// Dev-only: slow endpoint used by the SlowLoris simulator scenario to test concurrency limiter
if (app.Environment.IsDevelopment())
{
    app.MapPost("/webhook/slow", async (
        HttpContext ctx,
        ILogger<Program> logger,
        CancellationToken ct) =>
    {
        ctx.Request.Body.Position = 0;
        await Task.Delay(8_000, ct); // hold the request open � exercises SemaphoreSlim
        return Results.Accepted();
    });
}

app.Run();
