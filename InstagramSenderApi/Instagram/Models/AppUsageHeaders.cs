using System.Text.Json.Serialization;

namespace InstagramSenderApi.Instagram.Models;

/// <summary>Maps the X-App-Usage response header from Instagram Graph API.</summary>
public sealed record AppUsageHeaders(
    [property: JsonPropertyName("call_count")]    int CallCount,
    [property: JsonPropertyName("total_time")]    int TotalTime,
    [property: JsonPropertyName("total_cputime")] int TotalCpuTime);
