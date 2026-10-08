using WebhookTrafficSimulator;
using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;
using WebhookTrafficSimulator.Scenarios;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SimulatorOptions>(
    builder.Configuration.GetSection(SimulatorOptions.SectionName));

// Typed HttpClient for hitting WebhookIngestApi � no retries; we WANT to see 429s
builder.Services.AddHttpClient("webhook", (sp, client) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulatorOptions>>().Value;
    client.BaseAddress = new Uri(opts.TargetBaseUrl);
    client.Timeout     = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// -- Registry of all available scenarios --------------------------------------
var scenarios = new Dictionary<string, IScenario>(StringComparer.OrdinalIgnoreCase)
{
    ["SteadyTraffic"]     = new SteadyTrafficScenario(),
    ["BurstSingleIp"]     = new BurstSingleIpScenario(),
    ["DDoSMultiIp"]       = new DDoSMultiIpScenario(),
    ["OversizedPayload"]  = new OversizedPayloadScenario(),
    ["InvalidSignature"]  = new InvalidSignatureScenario(),
    ["SlowLoris"]         = new SlowLorisScenario(),
    ["GlobalFlood"]       = new GlobalFloodScenario(),
    ["MixedAttack"]       = new MixedAttackScenario(),
};

// -- Endpoints ------------------------------------------------------------------
app.MapGet("/simulator/scenarios", () =>
    Results.Ok(scenarios.Keys.OrderBy(k => k)));

app.MapGet("/simulator/health", async (IHttpClientFactory factory, ILogger<Program> logger) =>
{
    try
    {
        using var client = factory.CreateClient("webhook");
        using var resp   = await client.GetAsync("/");   // any 4xx means it's up
        return Results.Ok(new { status = "reachable", targetStatusCode = (int)resp.StatusCode });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Cannot reach target: {ex.Message}");
    }
});

app.MapPost("/simulator/run", async (
    ScenarioRequest req,
    IHttpClientFactory factory,
    Microsoft.Extensions.Options.IOptions<SimulatorOptions> optionsWrapper,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!scenarios.TryGetValue(req.ScenarioName, out var scenario))
        return Results.BadRequest(new { error = $"Unknown scenario '{req.ScenarioName}'." });

    var opts   = optionsWrapper.Value;
    var client = factory.CreateClient("webhook");
    var sender = new WebhookHttpSender(client, opts);

    logger.LogInformation("Starting scenario {Name}", scenario.Name);
    var result = await scenario.RunAsync(sender, opts, req, ct);
    logger.LogInformation("Scenario {Name} complete: {Sent} sent", scenario.Name, result.TotalSent);

    return Results.Ok(result);
});

app.MapPost("/simulator/run-all", async (
    IHttpClientFactory factory,
    Microsoft.Extensions.Options.IOptions<SimulatorOptions> optionsWrapper,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var opts    = optionsWrapper.Value;
    var client  = factory.CreateClient("webhook");
    var sender  = new WebhookHttpSender(client, opts);
    var results = new List<ScenarioResult>();
    var defaultReq = new ScenarioRequest("", 20, 15, 200);

    foreach (var (name, scenario) in scenarios)
    {
        logger.LogInformation("Running scenario {Name}", name);
        var req    = defaultReq with { ScenarioName = name };
        var result = await scenario.RunAsync(sender, opts, req, ct);
        results.Add(result);
    }

    return Results.Ok(results);
});

app.Run();
