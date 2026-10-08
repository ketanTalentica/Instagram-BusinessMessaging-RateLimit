using System.Text.Json;
using InstagramGraphMock.State;

namespace InstagramGraphMock.HeaderBuilders;

/// <summary>Builds the X-App-Usage response header JSON string from current tenant state.</summary>
public static class AppUsageHeaderBuilder
{
    /// <param name="globalAppUsagePct">
    /// Store-level shared app usage; when set it overrides the per-tenant value because the
    /// real X-App-Usage budget is shared by every account on the FB app.
    /// </param>
    public static string Build(TenantSimState state, int? globalAppUsagePct = null) =>
        JsonSerializer.Serialize(new
        {
            call_count   = globalAppUsagePct ?? state.CallCountPct,
            total_time   = state.TotalTimePct,
            total_cputime = state.TotalCpuTimePct
        });
}
