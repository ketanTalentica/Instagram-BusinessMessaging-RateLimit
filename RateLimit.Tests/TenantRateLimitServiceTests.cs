using InstagramSenderApi.Instagram.Infrastructure;
using InstagramSenderApi.Instagram.Models;
using InstagramSenderApi.Instagram.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RateLimit.Tests;

/// <summary>
/// The state layer decides both "how long should this tenant wait" and "may it call at all", and
/// it is where the two levels (this account vs the shared app budget) are kept apart. The
/// level-separation cases here are the ones that regressed in production before (finding A12.8).
/// </summary>
public class TenantRateLimitServiceTests
{
    private const string AppId    = "test-app";
    private const string AppRowId = "app:test-app";

    private static (TenantRateLimitService Service, FakeTenantRateLimitRepository Repo) Build()
    {
        var repo    = new FakeTenantRateLimitRepository();
        var options = TestOptions.Of(new OutboundRateLimitOptions { AppId = AppId });

        var service = new TenantRateLimitService(
            repo, new MemoryCache(new MemoryCacheOptions()), options,
            NullLogger<TenantRateLimitService>.Instance);

        return (service, repo);
    }

    private static TenantRateLimitState Row(string id, int pct, DateTime? blockedUntil = null) => new()
    {
        TenantId        = id,
        MaxCallCountPct = pct,
        BlockedUntilUtc = blockedUntil
    };

    // ---------- proactive delay ----------

    [Fact]
    public async Task No_delay_below_the_eighty_percent_threshold()
    {
        var (service, repo) = Build();
        repo.Seed(Row("t", 79));

        Assert.Null(await service.GetThrottleDelayAsync("t"));
    }

    [Fact]
    public async Task A_tenant_with_no_history_is_not_delayed()
    {
        var (service, _) = Build();

        Assert.Null(await service.GetThrottleDelayAsync("brand-new"));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(92)]
    [InlineData(99)]
    public async Task Delay_starts_at_the_threshold_and_grows_as_the_budget_runs_out(int pct)
    {
        var (service, repo) = Build();
        repo.Seed(Row("t", pct));

        var delay = await service.GetThrottleDelayAsync("t");

        Assert.NotNull(delay);
        Assert.InRange(delay!.Value, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(60_000));
    }

    [Fact]
    public async Task The_delay_grows_monotonically_with_usage()
    {
        var (service, repo) = Build();
        repo.Seed(Row("low", 85));
        repo.Seed(Row("high", 99));

        var low  = await service.GetThrottleDelayAsync("low");
        var high = await service.GetThrottleDelayAsync("high");

        Assert.True(high > low);
    }

    // ---------- level separation: the shared app budget ----------

    [Fact]
    public async Task A_hot_shared_app_budget_delays_a_tenant_whose_own_usage_is_tiny()
    {
        var (service, repo) = Build();
        repo.Seed(Row(AppRowId, 92));
        repo.Seed(Row("tenant-light", 5));

        // Demo 2.3: throttling is decided on max(tenantPct, appPct).
        Assert.NotNull(await service.GetThrottleDelayAsync("tenant-light"));
    }

    [Fact]
    public async Task A_hot_tenant_is_delayed_even_when_the_shared_budget_is_cold()
    {
        var (service, repo) = Build();
        repo.Seed(Row(AppRowId, 5));
        repo.Seed(Row("tenant-heavy", 95));

        Assert.NotNull(await service.GetThrottleDelayAsync("tenant-heavy"));
    }

    [Fact]
    public async Task An_app_level_block_holds_every_tenant()
    {
        var (service, repo) = Build();
        repo.Seed(Row(AppRowId, 100, DateTime.UtcNow.AddMinutes(6)));

        await Assert.ThrowsAsync<TenantBlockedException>(() => service.GetThrottleDelayAsync("innocent"));
    }

    [Fact]
    public async Task An_account_block_does_not_leak_onto_other_tenants()
    {
        var (service, repo) = Build();
        repo.Seed(Row("guilty", 100, DateTime.UtcNow.AddMinutes(9)));

        await Assert.ThrowsAsync<TenantBlockedException>(() => service.GetThrottleDelayAsync("guilty"));
        Assert.Null(await service.GetThrottleDelayAsync("bystander"));
    }

    [Fact]
    public async Task An_expired_block_stops_holding_the_tenant()
    {
        var (service, repo) = Build();
        repo.Seed(Row("t", 10, DateTime.UtcNow.AddMinutes(-1)));

        Assert.Null(await service.GetThrottleDelayAsync("t"));
    }

    // ---------- block lifecycle on record ----------

    [Fact]
    public async Task A_positive_eta_sets_the_block_with_a_one_minute_safety_buffer()
    {
        var (service, repo) = Build();

        await service.RecordUsageAsync("t", 100, 0, 0, estimatedBlockMinutes: 8);

        var blockedUntil = repo.Upserts.Single().BlockedUntilUtc;
        Assert.NotNull(blockedUntil);

        var minutes = (blockedUntil!.Value - DateTime.UtcNow).TotalMinutes;
        Assert.InRange(minutes, 8.5, 9.1);   // 8 + 1 buffer
    }

    [Fact]
    public async Task Zero_clears_the_block_because_a_success_proves_recovery()
    {
        var (service, repo) = Build();
        repo.Seed(Row("t", 100, DateTime.UtcNow.AddMinutes(5)));

        await service.RecordUsageAsync("t", 20, 0, 0, estimatedBlockMinutes: 0);

        Assert.Null(repo.Upserts.Single().BlockedUntilUtc);
    }

    [Fact]
    public async Task Null_preserves_an_existing_block_so_an_unrelated_failure_cannot_unblock_a_tenant()
    {
        var (service, repo) = Build();
        var until = DateTime.UtcNow.AddMinutes(5);
        repo.Seed(Row("t", 100, until));

        await service.RecordUsageAsync("t", 50, 0, 0, estimatedBlockMinutes: null);

        Assert.Equal(until, repo.Upserts.Single().BlockedUntilUtc);
    }

    [Fact]
    public async Task Recorded_usage_is_visible_to_the_next_throttle_decision()
    {
        var (service, _) = Build();

        await service.RecordUsageAsync("t", 95, 0, 0, estimatedBlockMinutes: null);

        Assert.NotNull(await service.GetThrottleDelayAsync("t"));
    }

    [Fact]
    public async Task The_app_row_and_the_tenant_row_keep_their_own_percentages()
    {
        var (service, repo) = Build();

        await service.RecordUsageAsync(AppRowId, 92, 0, 0, null);
        await service.RecordUsageAsync("tenant-light", 5, 0, 0, null);

        // A12.8: before the fix both rows read 92 and every account looked as hot as the app.
        Assert.Equal(92, (await service.GetStateAsync(AppRowId))!.MaxCallCountPct);
        Assert.Equal(5,  (await service.GetStateAsync("tenant-light"))!.MaxCallCountPct);
    }
}
