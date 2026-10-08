namespace WebhookTrafficSimulator.Models;

public sealed record ScenarioRequest(
    string ScenarioName,
    int    DurationSeconds = 30,
    int    Concurrency     = 10,
    int    RequestCount    = 200);
