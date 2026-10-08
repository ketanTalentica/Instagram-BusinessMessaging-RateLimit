using WebhookTrafficSimulator.Infrastructure;
using WebhookTrafficSimulator.Models;

namespace WebhookTrafficSimulator.Scenarios;

public interface IScenario
{
    string Name { get; }

    Task<ScenarioResult> RunAsync(
        WebhookHttpSender sender,
        SimulatorOptions  options,
        ScenarioRequest   request,
        CancellationToken ct);
}
