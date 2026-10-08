using System.Text.Json;

namespace InstagramSenderApi.Instagram.Workers;

/// <summary>A single outbound send job queued per tenant.</summary>
public sealed record SendJob(
    string TenantId,
    string TargetEndpoint,
    JsonElement Payload,
    int AttemptCount = 0)
{
    public SendJob WithNextAttempt() => this with { AttemptCount = AttemptCount + 1 };
}
