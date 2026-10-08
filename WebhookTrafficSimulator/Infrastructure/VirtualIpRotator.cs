namespace WebhookTrafficSimulator.Infrastructure;

/// <summary>Cycles through a pool of fake IP addresses for multi-IP scenario testing.</summary>
public sealed class VirtualIpRotator
{
    private readonly string[] _ips;
    private          int      _index = -1;

    public VirtualIpRotator(int count)
    {
        _ips = Enumerable.Range(1, Math.Max(1, count))
            .Select(i => $"10.{i / 256 % 256}.{i % 256}.1")
            .ToArray();
    }

    /// <summary>Returns the next IP in round-robin order.</summary>
    public string Next() => _ips[Interlocked.Increment(ref _index) % _ips.Length];

    /// <summary>Returns a deterministic IP for a given index (useful for per-IP tests).</summary>
    public string Get(int index) => _ips[Math.Abs(index) % _ips.Length];

    public int Count => _ips.Length;
}
