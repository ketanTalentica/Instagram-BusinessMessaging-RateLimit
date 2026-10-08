namespace WebhookIngestApi.RateLimit.Rules;

/// <summary>
/// One check in the inbound pipeline. Rules run in ascending <see cref="Order"/>, fail-fast: the
/// first rule to return a non-null evaluation ends the pipeline — whether that evaluation denies
/// (block list, bad signature, a full window) or allows outright (the allow-list bypass).
/// Returning null means "no opinion, carry on".
///
/// Order is a property rather than DI registration order because the sequence is a security
/// property, not a wiring detail: the cheap no-I/O checks must run before the store is touched,
/// and HMAC must run before any counter is incremented so forged traffic cannot consume quota.
/// A property can be asserted in a test; registration order cannot.
/// </summary>
public interface IInboundRule
{
    /// <summary>Position in the pipeline — see <see cref="RuleOrder"/>.</summary>
    int Order { get; }

    /// <summary>Short name used in diagnostics and in the pipeline-order test.</summary>
    string Name { get; }

    /// <summary>
    /// Returns null to pass the request to the next rule, or a terminal evaluation to end the
    /// pipeline. Must not block: the whole pipeline runs inside the request path.
    /// </summary>
    ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default);
}

/// <summary>
/// The documented pipeline order, spaced so a rule can be inserted without renumbering:
/// block list → allow list → payload size → HMAC → global → per-IP → per-client → concurrency.
/// </summary>
public static class RuleOrder
{
    public const int BlockedIp       = 100;
    public const int AllowedIp       = 200;
    public const int PayloadSize     = 300;
    public const int Hmac            = 400;
    public const int GlobalLimit    = 500;
    public const int PerIpLimit     = 600;
    public const int PerClientLimit = 700;
    public const int Concurrency     = 800;
}
