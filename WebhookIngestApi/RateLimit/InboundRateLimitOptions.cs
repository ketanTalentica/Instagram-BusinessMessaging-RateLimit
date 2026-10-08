namespace WebhookIngestApi.RateLimit;

public sealed class InboundRateLimitOptions
{
    // Section shape matches the IGAutopilot integration plan so the components
    // transfer without config renames.
    public const string SectionName = "RateLimiting:Inbound";

    /// <summary>Master switch — when false the middleware passes every request straight through.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Observe-first rollout mode: all counters run and violations are logged with the
    /// status code that WOULD have been returned, but no request is ever denied.
    /// Used to measure real traffic for ~a week before choosing enforcement thresholds.
    /// </summary>
    public bool ObserveOnly { get; set; }

    public int    GlobalLimitPerMinute      { get; set; } = 1000;
    public int    PerIpLimitPerMinute       { get; set; } = 60;
    public int    PerClientLimitPerMinute   { get; set; } = 100;
    public int    BurstSize                 { get; set; } = 20;
    public int    WindowSeconds             { get; set; } = 60;
    public long   MaxPayloadBytes           { get; set; } = 1_048_576; // 1 MB
    public int    MaxConcurrencyPerClient   { get; set; } = 10;

    /// <summary>
    /// Counting mechanism per scope — the three limits are independent, so a high-cardinality
    /// scope can trade exactness for memory (FixedWindow) or pace a bursty sender (TokenBucket)
    /// without touching the other two. All three default to SlidingWindow: it is what every
    /// figure in docs/DEMO.md was measured against, so changing one changes observed behaviour.
    /// </summary>
    public RateLimitAlgorithm GlobalAlgorithm    { get; set; } = RateLimitAlgorithm.SlidingWindow;
    public RateLimitAlgorithm PerIpAlgorithm     { get; set; } = RateLimitAlgorithm.SlidingWindow;
    public RateLimitAlgorithm PerClientAlgorithm { get; set; } = RateLimitAlgorithm.SlidingWindow;

    /// <summary>
    /// HMAC-SHA256 secret shared with webhook senders.
    /// Load from environment variable or Key Vault — never hardcode.
    /// Leave empty to disable HMAC validation (dev/test only).
    /// </summary>
    public string HmacSecretKey { get; set; } = string.Empty;

    public HashSet<string> BlockedIps  { get; set; } = [];
    public HashSet<string> AllowedIps  { get; set; } = [];

    /// <summary>
    /// Path prefixes that bypass rate limiting entirely (health probes, swagger, etc.).
    /// GET requests always bypass regardless of this list — Meta's hub.challenge webhook
    /// verification handshake is a GET and must never be throttled, or re-verification
    /// breaks and the subscription can be disabled.
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = [];

    /// <summary>
    /// Only enable when this service sits behind a trusted reverse proxy that overwrites
    /// X-Forwarded-For. When false (default), the socket RemoteIpAddress is used — otherwise
    /// any direct caller can rotate fake X-Forwarded-For values to bypass the per-IP limit.
    /// The WebhookTrafficSimulator relies on this being true for local multi-IP scenarios.
    /// </summary>
    public bool TrustForwardedFor { get; set; }
}
