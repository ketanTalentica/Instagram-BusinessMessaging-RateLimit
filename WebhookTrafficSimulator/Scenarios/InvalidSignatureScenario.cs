using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// Sends 30 requests at a valid rate but with a deliberately wrong HMAC signature.
/// Expected result: all 401 Unauthorized — rate counters are NOT consumed.
/// Validates: HMAC check runs before rate counting.
/// </summary>
public sealed class InvalidSignatureScenario : IScenario
{
    public string Name => "InvalidSignature";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);

        var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            var (status, headers, latency) = await sender.SendAsync(
                clientId: $"bad-sig-client-{i % 5}",
                validHmac: false,
                ct: ct);
            collector.Record(status, headers, latency);
            await Task.Delay(100, ct);
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
