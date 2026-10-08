using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

/// <summary>
/// Runs BurstSingleIp + DDoSMultiIp + InvalidSignature in parallel.
/// Expected result: each attack type receives its correct response code simultaneously,
/// proving that layers do not interfere with each other.
/// </summary>
public sealed class MixedAttackScenario : IScenario
{
    public string Name => "MixedAttack";

    public async Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender, SimulatorOptions options, ScenarioRequest req, CancellationToken ct)
    {
        var collector = new ResultCollector(Name, req.DurationSeconds);

        // Arm three concurrent attack streams
        var burst = Task.WhenAll(Enumerable.Range(0, 80).Select(_ => Task.Run(async () =>
        {
            var (s, h, l) = await sender.SendAsync(clientId: "mix-burst", virtualIp: "192.168.99.1", ct: ct);
            collector.Record(s, h, l);
        }, ct)));

        var ddos = Task.WhenAll(Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            var (s, h, l) = await sender.SendAsync(
                clientId: $"mix-ddos-{i}", virtualIp: $"10.50.{i}.1", ct: ct);
            collector.Record(s, h, l);
        }, ct)));

        var badSig = Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            var (s, h, l) = await sender.SendAsync(clientId: "mix-badsig", validHmac: false, ct: ct);
            collector.Record(s, h, l);
        }, ct)));

        await Task.WhenAll(burst, ddos, badSig);
        return collector.Build();
    }
}
