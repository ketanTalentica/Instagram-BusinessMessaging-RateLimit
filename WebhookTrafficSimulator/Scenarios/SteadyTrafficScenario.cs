using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// Baseline: 5 concurrent senders, each sending 1 request every 2 seconds with valid HMAC.
/// Expected result: all 200 OK — proves normal traffic is never rate-limited.
/// </summary>
public sealed class SteadyTrafficScenario : IScenario
{
    public string Name => "SteadyTraffic";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);
        var deadline  = DateTimeOffset.UtcNow.AddSeconds(req.DurationSeconds);

        var tasks = Enumerable.Range(1, 5).Select(i => Task.Run(async () =>
        {
            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var (status, headers, latency) = await sender.SendAsync(
                    clientId: $"steady-client-{i}", ct: ct);
                collector.Record(status, headers, latency);
                await Task.Delay(2_000, ct);
            }
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
