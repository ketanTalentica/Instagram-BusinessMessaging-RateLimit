using InstagramSenderApi.Instagram.Infrastructure;
using InstagramSenderApi.Instagram.Models;
using InstagramSenderApi.Instagram.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RateLimit.Tests;

/// <summary>
/// Gates return the wait they require instead of sleeping, which is what makes these assertions
/// possible at all: the per-second cap can be proven in milliseconds rather than by burning a
/// real second per case.
/// </summary>
public class OutboundGateTests
{
    private static PerSecondDispatchGate Gate(OutboundRateLimitOptions? options = null) =>
        new(TestOptions.Of(options ?? new OutboundRateLimitOptions()),
            NullLogger<PerSecondDispatchGate>.Instance);

    private static async Task<int> WaitsIn(PerSecondDispatchGate gate, string tenant, DispatchClass cls, int calls)
    {
        var waits = 0;
        for (var i = 0; i < calls; i++)
            if (await gate.GetDelayAsync(new OutboundDispatch(tenant, cls)) > TimeSpan.Zero)
                waits++;
        return waits;
    }

    // ---------- per-second gate ----------

    [Fact]
    public async Task Media_sends_are_capped_ten_times_tighter_than_text_on_the_same_endpoint()
    {
        // Demo 2.8 in code form: 20 calls of each class, defaults 100/s text and 10/s media.
        Assert.Equal(10, await WaitsIn(Gate(), "t-media", DispatchClass.MediaSend, 20));
        Assert.Equal(0,  await WaitsIn(Gate(), "t-text",  DispatchClass.TextSend,  20));
    }

    [Fact]
    public async Task Conversations_is_the_tightest_class()
    {
        Assert.Equal(4, await WaitsIn(Gate(), "t-conv", DispatchClass.Conversations, 6));
    }

    [Fact]
    public async Task An_unclassified_call_falls_to_the_tightest_cap_rather_than_guessing_high()
    {
        var options = new OutboundRateLimitOptions();
        Assert.Equal(options.PerSecondConversationsDispatchLimit,
                     Gate(options).RateFor(DispatchClass.Unclassified));
    }

    [Fact]
    public async Task Classes_hold_separate_buckets_so_one_does_not_starve_another()
    {
        var gate = Gate();

        await WaitsIn(gate, "t", DispatchClass.Conversations, 2);   // drains the 2/s bucket

        // Text has its own 100/s allowance and must be unaffected.
        Assert.Equal(TimeSpan.Zero, await gate.GetDelayAsync(new OutboundDispatch("t", DispatchClass.TextSend)));
    }

    [Fact]
    public async Task Tenants_hold_separate_buckets()
    {
        var gate = Gate();

        await WaitsIn(gate, "noisy", DispatchClass.Conversations, 2);

        Assert.Equal(TimeSpan.Zero, await gate.GetDelayAsync(new OutboundDispatch("quiet", DispatchClass.Conversations)));
    }

    [Fact]
    public async Task Reservations_stack_so_concurrent_callers_are_paced_not_spun()
    {
        var gate = Gate();

        await gate.GetDelayAsync(new OutboundDispatch("t", DispatchClass.Conversations));
        await gate.GetDelayAsync(new OutboundDispatch("t", DispatchClass.Conversations));

        var third  = await gate.GetDelayAsync(new OutboundDispatch("t", DispatchClass.Conversations));
        var fourth = await gate.GetDelayAsync(new OutboundDispatch("t", DispatchClass.Conversations));

        Assert.True(third > TimeSpan.Zero);
        Assert.True(fourth > third);   // each further caller waits longer, rather than racing
    }

    // ---------- throttle gate + guard composition ----------

    private static (InstagramThrottleGuard Guard, FakeTenantRateLimitRepository Repo) BuildGuard(
        bool enabled = true)
    {
        var options = TestOptions.Of(new OutboundRateLimitOptions { Enabled = enabled, AppId = "test-app" });
        var repo    = new FakeTenantRateLimitRepository();

        var service = new TenantRateLimitService(
            repo, new MemoryCache(new MemoryCacheOptions()), options,
            NullLogger<TenantRateLimitService>.Instance);

        IOutboundGate[] gates =
        [
            Gate(options.Value),
            new HeaderUsageThrottleGate(service, NullLogger<HeaderUsageThrottleGate>.Instance)
        ];

        return (new InstagramThrottleGuard(gates, options), repo);
    }

    [Fact]
    public void Gates_run_in_the_documented_order_whatever_order_they_were_registered_in()
    {
        var (guard, _) = BuildGuard();

        // Deliberately constructed per-second-first above; the guard must reorder it last,
        // or its token is spent before the proactive delay has been slept through.
        Assert.Equal(["header-usage throttle", "per-second dispatch"], guard.Gates.Select(g => g.Name));
    }

    [Fact]
    public async Task An_account_level_block_stops_the_call_before_any_gate_lets_it_out()
    {
        var (guard, repo) = BuildGuard();
        repo.Seed(new TenantRateLimitState
        {
            TenantId        = "blocked-tenant",
            MaxCallCountPct = 100,
            BlockedUntilUtc = DateTime.UtcNow.AddMinutes(9)
        });

        await Assert.ThrowsAsync<TenantBlockedException>(() => guard.EnforceAsync("blocked-tenant"));
    }

    [Fact]
    public async Task An_app_level_block_holds_a_tenant_that_has_no_row_of_its_own()
    {
        var (guard, repo) = BuildGuard();
        repo.Seed(new TenantRateLimitState
        {
            TenantId        = "app:test-app",
            MaxCallCountPct = 100,
            BlockedUntilUtc = DateTime.UtcNow.AddMinutes(6)
        });

        // Demo 2.7: the innocent tenant never reaches the network and never gets a row.
        await Assert.ThrowsAsync<TenantBlockedException>(() => guard.EnforceAsync("tenant-innocent"));
    }

    [Fact]
    public async Task Disabling_enforcement_bypasses_every_gate_including_a_live_block()
    {
        var (guard, repo) = BuildGuard(enabled: false);
        repo.Seed(new TenantRateLimitState
        {
            TenantId        = "blocked-tenant",
            BlockedUntilUtc = DateTime.UtcNow.AddMinutes(9)
        });

        await guard.EnforceAsync("blocked-tenant");   // must not throw: observe-only rollout
    }

    [Fact]
    public async Task A_healthy_tenant_passes_straight_through()
    {
        var (guard, repo) = BuildGuard();
        repo.Seed(new TenantRateLimitState { TenantId = "healthy", MaxCallCountPct = 10 });

        var started = DateTime.UtcNow;
        await guard.EnforceAsync("healthy", DispatchClass.TextSend);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }
}
