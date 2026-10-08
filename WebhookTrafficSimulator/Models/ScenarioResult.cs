namespace WebhookTrafficSimulator.Models;

/// <summary>Aggregated result returned by every scenario run.</summary>
public sealed record ScenarioResult(
    string                      Scenario,
    int                         DurationSeconds,
    int                         TotalSent,
    Dictionary<int, int>        StatusCounts,
    double                      AverageLatencyMs,
    List<int>                   RetryAfterValuesSeen,
    Dictionary<string, string>  RateLimitHeadersObserved,
    List<string>                Errors);
