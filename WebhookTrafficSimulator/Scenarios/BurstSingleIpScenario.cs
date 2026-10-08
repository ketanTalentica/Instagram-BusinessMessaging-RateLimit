using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// Fires req.RequestCount requests from a single IP as fast as possible.
/// Expected result: first ~60 return 200; remainder return 429 with Retry-After.
/// Validates: per-IP sliding window.
/// </summary>
public sealed class BurstSingleIpScenario : IScenario
{
    public string Name => "BurstSingleIp";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);
        const string singleIp = "192.168.1.100";

        var tasks = Enumerable.Range(0, req.RequestCount).Select(_ => Task.Run(async () =>
        {
            var (status, headers, latency) = await sender.SendAsync(
                clientId: "burst-client", virtualIp: singleIp, ct: ct);
            collector.Record(status, headers, latency);
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
