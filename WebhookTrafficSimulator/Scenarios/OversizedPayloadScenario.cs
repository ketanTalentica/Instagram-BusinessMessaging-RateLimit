using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// Sends 10 requests with a payload that exceeds MaxPayloadBytes (default 1 MB).
/// Expected result: all 413 Payload Too Large — rate counters are NOT incremented.
/// Validates: payload size guard runs before rate checking.
/// </summary>
public sealed class OversizedPayloadScenario : IScenario
{
    public string Name => "OversizedPayload";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);
        var bigBody   = PayloadFactory.CreateOversized(options.OversizedPayloadMb);

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            var (status, headers, latency) = await sender.SendAsync(
                clientId: "oversize-client", payload: bigBody, ct: ct);
            collector.Record(status, headers, latency);
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
