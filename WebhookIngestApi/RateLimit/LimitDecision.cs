namespace WebhookIngestApi.RateLimit;

/// <summary>
/// Why a request was refused — not all of these are rate limits, which is the point of the name:
/// the pipeline also carries the signature and size checks.
/// </summary>
public enum DenialReason
{
    None,           // Allowed
    Blocked,        // IP is in the block list             → 403
    PayloadSize,    // Body exceeds MaxPayloadBytes        → 413
    LengthRequired, // Body method without Content-Length  → 411
    Signature,      // HMAC-SHA256 mismatch                → 401
    Global,         // Global limit reached                → 429
    Ip,             // Per-IP limit reached                → 429
    Client,         // Per-client limit reached            → 429
    Concurrency     // MaxConcurrencyPerClient hit         → 429
}

/// <param name="IsAllowed">Whether the request should proceed.</param>
/// <param name="Reason">Which layer rejected the request (None = allowed).</param>
/// <param name="RetryAfterSeconds">Seconds until the window resets (0 for non-rate-limit denials).</param>
/// <param name="Remaining">Requests remaining for this client in the current window.</param>
public readonly record struct LimitDecision(
    bool         IsAllowed,
    DenialReason Reason,
    int          RetryAfterSeconds,
    int          Remaining);
