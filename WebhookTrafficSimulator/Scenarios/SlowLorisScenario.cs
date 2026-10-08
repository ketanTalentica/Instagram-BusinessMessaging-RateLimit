using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// Fires req.Concurrency requests simultaneously to the /webhook/slow dev endpoint,
/// which holds the connection open for 8 seconds. The concurrency limiter allows
/// MaxConcurrencyPerClient (default 10) simultaneous requests per client — requests
/// beyond that threshold receive 429 immediately.
/// Expected result: first 10 → 202 Accepted; requests 11+ → 429.
/// Validates: SemaphoreSlim concurrency limiter in InboundRateLimitPipeline.
/// </summary>
public sealed class SlowLorisScenario : IScenario
{
    public string Name => "SlowLoris";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector   = new ResultCollector(Name, req.DurationSeconds);
        var concurrency = req.Concurrency > 0 ? req.Concurrency : 15;

        // All requests fire at the same moment so the semaphore fills quickly
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            var (status, headers, latency) = await sender.SendAsync(
                path:     options.SlowWebhookPath,
                clientId: "slow-loris-client",
                ct:       ct);
            collector.Record(status, headers, latency);
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
