namespace InstagramSenderApi.Instagram.Infrastructure;

/// <summary>
/// Configuration for the outbound rate-limiting layer, bound from "RateLimiting:Outbound".
/// Section shape matches the IGAutopilot integration plan so the components transfer
/// without config renames.
/// </summary>
public sealed class OutboundRateLimitOptions
{
    public const string SectionName = "RateLimiting:Outbound";

    /// <summary>
    /// Master switch for enforcement (proactive throttle delays, block enforcement,
    /// per-second dispatch gate). The header-parsing handler is NOT gated by this flag —
    /// it only observes and records state, which is safe and useful even before
    /// enforcement is switched on (observe-first rollout).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Client-side per-second dispatch cap per tenant/account for **text-class** sends
    /// (text, links, reactions, stickers) — Meta's documented 100 calls/s per account.
    /// Usage headers reflect hourly budget windows and give no warning before a per-second
    /// violation, so this gate is the only proactive defence for that limit class.
    /// </summary>
    public int PerSecondDispatchLimit { get; set; } = 100;

    /// <summary>
    /// Per-second cap for audio/video sends — Meta documents **10 calls/s per account**,
    /// a tenth of the text-class cap. Applying the text cap here overruns Meta by 10×.
    /// </summary>
    public int PerSecondMediaDispatchLimit { get; set; } = 10;

    /// <summary>
    /// Per-second cap for the Conversations API — Meta documents **2 calls/s per account**,
    /// the tightest of the messaging caps.
    /// </summary>
    public int PerSecondConversationsDispatchLimit { get; set; } = 2;

    /// <summary>
    /// Per-second cap applied when the call cannot be classified. Deliberately the lowest
    /// documented value: an unrecognised endpoint might be Conversations-class, and
    /// guessing high is the failure mode that gets an account blocked.
    /// </summary>
    public int PerSecondUnclassifiedDispatchLimit { get; set; } = 2;

    /// <summary>
    /// Identifier of the FB app whose X-App-Usage budget is shared by every tenant/account.
    /// Usage observed in X-App-Usage is recorded under the global state row app:{AppId}
    /// so one tenant's observation throttles all tenants (the budget is genuinely shared).
    /// </summary>
    public string AppId { get; set; } = "default";

    /// <summary>Key of the global state row holding shared X-App-Usage state.</summary>
    public string AppStateKey => $"app:{AppId}";
}
