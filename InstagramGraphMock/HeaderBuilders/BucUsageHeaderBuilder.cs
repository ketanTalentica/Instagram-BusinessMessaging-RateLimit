using System.Text.Json;
using InstagramGraphMock.State;

namespace InstagramGraphMock.HeaderBuilders;

/// <summary>Builds the X-Business-Use-Case-Usage response header JSON string.</summary>
public static class BucUsageHeaderBuilder
{
    public static string Build(string tenantId, TenantSimState state)
    {
        var entry = new
        {
            type          = "instagram_platform",
            call_count    = state.CallCountPct,
            total_cputime = state.TotalCpuTimePct,
            total_time    = state.TotalTimePct,
            estimated_time_to_regain_access = state.EstimatedTimeToRegainAccessMinutes
        };

        return JsonSerializer.Serialize(
            new Dictionary<string, object[]> { [tenantId] = [entry] });
    }
}
