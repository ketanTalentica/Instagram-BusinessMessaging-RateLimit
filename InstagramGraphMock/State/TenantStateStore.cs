using System.Collections.Concurrent;

namespace InstagramGraphMock.State;

/// <summary>Thread-safe in-memory store for per-tenant simulator state.</summary>
public sealed class TenantStateStore
{
    private readonly ConcurrentDictionary<string, TenantSimState> _states =
        new(StringComparer.OrdinalIgnoreCase);

    public TenantSimState GetOrCreate(string tenantId) =>
        _states.GetOrAdd(tenantId, _ => new TenantSimState());

    public TenantSimState? Get(string tenantId) =>
        _states.TryGetValue(tenantId, out var s) ? s : null;

    public IReadOnlyDictionary<string, TenantSimState> GetAll() => _states;

    /// <summary>
    /// When set, every tenant's X-App-Usage header reports this call_count percentage —
    /// mirrors the real Graph API, where app usage is one shared budget across all
    /// accounts on the FB app rather than a per-account value.
    /// </summary>
    public int? GlobalAppUsagePct { get; set; }

    public void Reset(string tenantId)   => _states[tenantId] = new TenantSimState();

    public void ResetAll()
    {
        _states.Clear();
        GlobalAppUsagePct = null;
    }
}
