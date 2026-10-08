using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// 120 virtual IPs each sending 15 requests/min — total throughput exceeds
/// GlobalLimitPerMinute (default 1000) within the window.
/// Expected result: 429 on all new requests once global ceiling is hit.
/// Validates: global sliding window regardless of individual IP quotas.
/// </summary>
public sealed class GlobalFloodScenario : IScenario
{
    public string Name => "GlobalFlood";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);
        var rotator   = new VirtualIpRotator(120);

        var tasks = Enumerable.Range(0, 120).Select(i => Task.Run(async () =>
        {
            var ip       = rotator.Get(i);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(req.DurationSeconds);

            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var (status, headers, latency) = await sender.SendAsync(
                    clientId: $"flood-{i}", virtualIp: ip, ct: ct);
                collector.Record(status, headers, latency);
                await Task.Delay(4_000, ct); // 15 req/min per IP → 1800 total > 1000 global
            }
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
