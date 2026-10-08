namespace InstagramGraphMock.State;

public sealed class TenantSimState
{
    public int CallCountPct              { get; set; }
    public int TotalTimePct              { get; set; }
    public int TotalCpuTimePct           { get; set; }
    public int AutoIncrementPerCallPct   { get; set; } = 2;
    public bool IsBlocked                { get; set; }
    public int EstimatedTimeToRegainAccessMinutes { get; set; }
    public int? HardBlockAtCallCountPct  { get; set; }
    public int ReturnErrorCode           { get; set; } = 80001;
    public int ReturnErrorSubcode        { get; set; }
    public DateTime? BlockedUntilUtc     { get; set; }

    // Flapping scenario
    public string? ActiveScenario        { get; set; }
    public DateTime? ScenarioStartedAtUtc { get; set; }
    public int FlappingIntervalSeconds   { get; set; } = 30;

    // Per-second rate limit scenario
    public int PerSecondCallCount        { get; set; }
    public DateTime PerSecondWindowStart { get; set; } = DateTime.UtcNow;
    public int PerSecondLimit            { get; set; } = 100;

    // RetryAfter429 scenario: when blocked, answer HTTP 429 + Retry-After header
    // instead of the usual 400 + OAuthException error body
    public bool RespondWith429           { get; set; }
    public int RetryAfterSeconds         { get; set; } = 90;
}
