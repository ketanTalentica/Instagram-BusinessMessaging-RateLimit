using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// 50 virtual IPs each firing 25 requests/min — pushes the global ceiling.
/// Expected result: individual IP limits are not hit, but global limit triggers 429s.
/// Validates: global sliding window.
/// </summary>
public sealed class DDoSMultiIpScenario : IScenario
{
    public string Name => "DDoSMultiIp";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);
        var rotator   = new VirtualIpRotator(options.VirtualIpCount);

        var tasks = Enumerable.Range(0, options.VirtualIpCount).Select(i => Task.Run(async () =>
        {
            var ip       = rotator.Get(i);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(req.DurationSeconds);

            while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                var (status, headers, latency) = await sender.SendAsync(
                    clientId: $"ddos-client-{i}", virtualIp: ip, ct: ct);
                collector.Record(status, headers, latency);
                await Task.Delay(2_400, ct); // 25 req/min per IP
            }
        }, ct));

        await Task.WhenAll(tasks);
        return collector.Build();
    }
}
