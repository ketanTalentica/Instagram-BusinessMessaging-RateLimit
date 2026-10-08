using Microsoft.Extensions.Caching.Memory;
using InstagramSenderApi.Instagram.Client;
using InstagramSenderApi.Instagram.Infrastructure;
using InstagramSenderApi.Instagram.Services;
using InstagramSenderApi.Instagram.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMemoryCache();

builder.Services.Configure<OutboundRateLimitOptions>(
    builder.Configuration.GetSection(OutboundRateLimitOptions.SectionName));

// Infrastructure
builder.Services.AddSingleton<ITenantRateLimitRepository, SqlTenantRateLimitRepository>();
builder.Services.AddSingleton<ITenantRateLimitService,    TenantRateLimitService>();
builder.Services.AddSingleton<InstagramThrottleGuard>();

// Outbound pre-flight gates. Execution order comes from each gate's Order property, not from
// these lines. PerSecondDispatchGate is resolved through its concrete registration so the guard
// and anything else share one instance — the token buckets are the state.
builder.Services.AddSingleton<PerSecondDispatchGate>();
builder.Services.AddSingleton<IOutboundGate, HeaderUsageThrottleGate>();
builder.Services.AddSingleton<IOutboundGate>(sp => sp.GetRequiredService<PerSecondDispatchGate>());
builder.Services.AddTransient<InstagramRateLimitHandler>();

// SendQueueWorker registered as both singleton (for injection into controller) and hosted service
builder.Services.AddSingleton<SendQueueWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SendQueueWorker>());

// Per-tenant Polly pipelines are created lazily by InstagramClient from this registry —
// a shared AddResilienceHandler pipeline would make all tenants share one circuit breaker.
builder.Services.AddSingleton<Polly.Registry.ResiliencePipelineRegistry<string>>();

// Typed HttpClient with DelegatingHandler (header parsing). Resilience is applied
// inside InstagramClient per tenant, wrapping the whole handler chain.
builder.Services
    .AddHttpClient<IInstagramClient, InstagramClient>(client =>
        client.BaseAddress = new Uri(
            builder.Configuration["Instagram:GraphApiBaseUrl"]
            ?? "https://graph.facebook.com/v25.0/"))
    .AddHttpMessageHandler<InstagramRateLimitHandler>();

var app = builder.Build();

// Ensure TenantRateLimitState table exists on startup
var repo = app.Services.GetRequiredService<ITenantRateLimitRepository>();
await repo.EnsureTableExistsAsync();

app.MapControllers();
app.Run();
