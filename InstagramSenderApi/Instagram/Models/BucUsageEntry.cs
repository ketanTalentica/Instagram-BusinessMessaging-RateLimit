using System.Text.Json.Serialization;

namespace InstagramSenderApi.Instagram.Models;

/// <summary>Maps one entry inside the X-Business-Use-Case-Usage response header.</summary>
public sealed record BucUsageEntry(
    [property: JsonPropertyName("type")]          string Type,
    [property: JsonPropertyName("call_count")]    int CallCount,
    [property: JsonPropertyName("total_cputime")] int TotalCpuTime,
    [property: JsonPropertyName("total_time")]    int TotalTime,
    [property: JsonPropertyName("estimated_time_to_regain_access")] int EstimatedTimeToRegainAccessMinutes);
