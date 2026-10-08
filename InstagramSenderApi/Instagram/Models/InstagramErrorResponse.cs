using System.Text.Json.Serialization;

namespace InstagramSenderApi.Instagram.Models;

/// <summary>Deserialises the error body returned by Instagram Graph API on rate-limit hits.</summary>
public sealed class InstagramErrorResponse
{
    [JsonPropertyName("error")]
    public InstagramError? Error { get; set; }
}

public sealed class InstagramError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// <c>error_subcode</c>. Discriminates variants that share a parent code — notably
    /// 613/1996 ("inconsistent behavior in the API request volume of your app"), which is
    /// Meta flagging our traffic shape rather than a plain quota reset.
    /// </summary>
    [JsonPropertyName("error_subcode")]
    public int Subcode { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("estimated_time_to_regain_access")]
    public int EstimatedTimeToRegainAccessMinutes { get; set; }
}
