using System.Collections.Concurrent;

namespace WebhookTrafficSimulator.Models;

/// <summary>Thread-safe collector used by all scenarios to aggregate results.</summary>
public sealed class ResultCollector
{
    private readonly string _scenario;
    private readonly int    _duration;
    private          int    _totalSent;
    private          long   _totalLatencyMs;

    private readonly ConcurrentDictionary<int, int>    _statusCounts = new();
    private readonly ConcurrentBag<int>                _retryAfters  = [];
    private readonly ConcurrentDictionary<string, string> _headers   = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<string>             _errors       = [];

    public ResultCollector(string scenario, int duration)
    {
        _scenario = scenario;
        _duration = duration;
    }

    public void Record(int status, Dictionary<string, string> headers, long latencyMs, string? error = null)
    {
        Interlocked.Increment(ref _totalSent);
        _statusCounts.AddOrUpdate(status, 1, (_, c) => c + 1);
        Interlocked.Add(ref _totalLatencyMs, latencyMs);

        if (headers.TryGetValue("Retry-After", out var ra) && int.TryParse(ra, out var raVal))
            _retryAfters.Add(raVal);

        foreach (var (k, v) in headers)
            _headers.TryAdd(k, v);

        if (error is not null)
            _errors.Add(error);
    }

    public ScenarioResult Build() => new(
        Scenario:                  _scenario,
        DurationSeconds:           _duration,
        TotalSent:                 _totalSent,
        StatusCounts:              new Dictionary<int, int>(_statusCounts),
        AverageLatencyMs:          _totalSent > 0 ? (double)_totalLatencyMs / _totalSent : 0,
        RetryAfterValuesSeen:      [.. _retryAfters.Distinct().Order()],
        RateLimitHeadersObserved:  new Dictionary<string, string>(_headers),
        Errors:                    [.. _errors.Take(20)]);
}
