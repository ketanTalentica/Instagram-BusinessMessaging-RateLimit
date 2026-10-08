using InstagramGraphMock.State;

namespace InstagramGraphMock.Endpoints;

/// <summary>
/// Control surface for the mock server. Lets developers configure per-tenant state and
/// trigger named scenarios without restarting — decoupled from the Graph API surface.
/// </summary>
public static class SimulatorControlEndpoints
{
    public static void MapSimulatorControlEndpoints(this WebApplication app)
    {
        // ── State inspection ─────────────────────────────────────────────────────
        app.MapGet("/simulator/state/{tenantId}", (string tenantId, TenantStateStore store) =>
            store.Get(tenantId) is { } s ? Results.Ok(s) : Results.NotFound());

        app.MapGet("/simulator/state", (TenantStateStore store) =>
            Results.Ok(store.GetAll()));

        // ── Fine-grained configuration ────────────────────────────────────────────
        app.MapPost("/simulator/configure/{tenantId}", (string tenantId, TenantSimState cfg, TenantStateStore store) =>
        {
            var s = store.GetOrCreate(tenantId);
            s.CallCountPct                    = cfg.CallCountPct;
            s.TotalTimePct                    = cfg.TotalTimePct;
            s.TotalCpuTimePct                 = cfg.TotalCpuTimePct;
            s.AutoIncrementPerCallPct         = cfg.AutoIncrementPerCallPct;
            s.IsBlocked                       = cfg.IsBlocked;
            s.EstimatedTimeToRegainAccessMinutes = cfg.EstimatedTimeToRegainAccessMinutes;
            s.HardBlockAtCallCountPct         = cfg.HardBlockAtCallCountPct;
            s.ReturnErrorCode                 = cfg.ReturnErrorCode;
            s.ReturnErrorSubcode              = cfg.ReturnErrorSubcode;
            s.BlockedUntilUtc                 = cfg.BlockedUntilUtc;
            s.RespondWith429                  = cfg.RespondWith429;
            s.RetryAfterSeconds               = cfg.RetryAfterSeconds;
            return Results.Ok(s);
        });

        // Shared app-level X-App-Usage percentage (null = per-tenant values, as before)
        app.MapPost("/simulator/app-usage", (AppUsageRequest req, TenantStateStore store) =>
        {
            store.GlobalAppUsagePct = req.Pct;
            return Results.Ok(new { globalAppUsagePct = store.GlobalAppUsagePct });
        });

        app.MapPost("/simulator/block/{tenantId}", (string tenantId, BlockRequest req, TenantStateStore store) =>
        {
            var s = store.GetOrCreate(tenantId);
            s.IsBlocked = true;
            s.EstimatedTimeToRegainAccessMinutes = req.Minutes;
            s.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(req.Minutes);
            s.CallCountPct = 100;
            return Results.Ok(s);
        });

        app.MapPost("/simulator/reset/{tenantId}", (string tenantId, TenantStateStore store) =>
        { store.Reset(tenantId); return Results.Ok(); });

        app.MapPost("/simulator/reset", (TenantStateStore store) =>
        { store.ResetAll(); return Results.Ok(); });

        // ── Named scenarios ───────────────────────────────────────────────────────
        app.MapPost("/simulator/scenario", (ScenarioRequest req, TenantStateStore store) =>
        {
            ApplyScenario(req, store);
            return Results.Ok(store.Get(req.TenantId ?? "tenant-1"));
        });
    }

    private static void ApplyScenario(ScenarioRequest req, TenantStateStore store)
    {
        var tenantId = req.TenantId ?? "tenant-1";

        switch (req.Name)
        {
            case "GradualApproach":
            {
                var s = store.GetOrCreate(tenantId);
                s.CallCountPct = 0; s.TotalTimePct = 0; s.TotalCpuTimePct = 0;
                s.AutoIncrementPerCallPct = 5;      // reaches 80% after 16 calls → proactive throttle
                s.HardBlockAtCallCountPct = 100;    // blocks at 100% after 20 calls
                s.IsBlocked = false; s.BlockedUntilUtc = null;
                s.ActiveScenario = "GradualApproach";
                break;
            }
            case "SuddenBlock":
            {
                var s = store.GetOrCreate(tenantId);
                s.CallCountPct = 0; s.TotalTimePct = 0; s.TotalCpuTimePct = 0;
                s.AutoIncrementPerCallPct = 5;
                s.HardBlockAtCallCountPct = 100;    // triggers after ~20 calls
                s.ReturnErrorCode = 80001;
                s.EstimatedTimeToRegainAccessMinutes = 5;
                s.IsBlocked = false; s.BlockedUntilUtc = null;
                s.ActiveScenario = "SuddenBlock";
                break;
            }
            case "FlappingBlock":
            {
                var s = store.GetOrCreate(tenantId);
                s.CallCountPct = 5; s.AutoIncrementPerCallPct = 0;
                s.IsBlocked = false;
                s.ActiveScenario = "FlappingBlock";
                s.ScenarioStartedAtUtc = DateTime.UtcNow;
                s.FlappingIntervalSeconds = req.FlappingIntervalSeconds > 0 ? req.FlappingIntervalSeconds : 30;
                break;
            }
            case "MultiTenantMix":
            {
                // Tenant A — healthy (10%)
                var a = store.GetOrCreate("tenant-a");
                a.CallCountPct = 10; a.AutoIncrementPerCallPct = 1; a.IsBlocked = false;

                // Tenant B — approaching limit (75%)
                var b = store.GetOrCreate("tenant-b");
                b.CallCountPct = 75; b.AutoIncrementPerCallPct = 3;
                b.HardBlockAtCallCountPct = 100; b.IsBlocked = false;

                // Tenant C — hard blocked
                var c = store.GetOrCreate("tenant-c");
                c.CallCountPct = 100; c.IsBlocked = true;
                c.EstimatedTimeToRegainAccessMinutes = 10;
                c.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(10);
                break;
            }
            case "RecoveryTest":
            {
                var minutes = req.BlockForMinutes > 0 ? req.BlockForMinutes : 2;
                var s = store.GetOrCreate(tenantId);
                s.IsBlocked = true; s.CallCountPct = 100;
                s.EstimatedTimeToRegainAccessMinutes = minutes;
                s.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
                s.ActiveScenario = "RecoveryTest";
                break;
            }
            case "PerSecondRateLimit":
            {
                var s = store.GetOrCreate(tenantId);
                s.ActiveScenario = "PerSecondRateLimit";
                s.ReturnErrorCode = 17; // "User request limit reached" — per-second cap error code
                s.PerSecondLimit = req.PerSecondLimit > 0 ? req.PerSecondLimit : 100;
                s.PerSecondCallCount = 0;
                s.PerSecondWindowStart = DateTime.UtcNow;
                s.IsBlocked = false;
                break;
            }
            case "InstagramBucBlock":
            {
                // Error 80002 — the Business-Use-Case code Meta documents for Instagram
                // (80001 is Page calls with a Page/System-User token). HTTP 400 + error body,
                // no Retry-After header, so only the code list can recognise it.
                var s = store.GetOrCreate(tenantId);
                s.CallCountPct = 100; s.TotalTimePct = 90; s.TotalCpuTimePct = 90;
                s.AutoIncrementPerCallPct = 0;
                s.ReturnErrorCode = 80002; s.ReturnErrorSubcode = 0;
                s.RespondWith429 = false;
                s.EstimatedTimeToRegainAccessMinutes =
                    req.BlockForMinutes > 0 ? req.BlockForMinutes : 8;
                s.IsBlocked = true;
                s.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(
                    s.EstimatedTimeToRegainAccessMinutes);
                s.ActiveScenario = "InstagramBucBlock";
                break;
            }
            case "AppLevelBlock":
            {
                // Error 4 — app-level limit. Only the tenant that receives it sees the
                // error, but the limit applies to EVERY account on the FB app, so the
                // sender must block the shared app row, not this one account.
                var s = store.GetOrCreate(tenantId);
                s.CallCountPct = 20; s.AutoIncrementPerCallPct = 0;   // account itself is healthy
                s.ReturnErrorCode = 4; s.ReturnErrorSubcode = 0;
                s.RespondWith429 = false;
                s.EstimatedTimeToRegainAccessMinutes =
                    req.BlockForMinutes > 0 ? req.BlockForMinutes : 6;
                s.IsBlocked = true;
                s.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(
                    s.EstimatedTimeToRegainAccessMinutes);
                s.ActiveScenario = "AppLevelBlock";
                break;
            }
            case "CustomRateLimit613":
            {
                // Error 613 / subcode 1996 — "inconsistent behavior in the API request volume
                // of your app". Arrives on HTTP 400 with NO estimated_time_to_regain_access,
                // so the sender must apply its own long back-off from the subcode alone.
                var s = store.GetOrCreate(tenantId);
                s.CallCountPct = 60; s.AutoIncrementPerCallPct = 0;
                s.ReturnErrorCode = 613; s.ReturnErrorSubcode = 1996;
                s.RespondWith429 = false;
                s.EstimatedTimeToRegainAccessMinutes = 0;   // Meta sends no ETA for this one
                s.IsBlocked = true;
                s.BlockedUntilUtc = DateTime.UtcNow.AddMinutes(15);
                s.ActiveScenario = "CustomRateLimit613";
                break;
            }
            case "RetryAfter429":
            {
                // Standard 429 + Retry-After header (no recognised Graph error code) —
                // exercises the sender's Retry-After header parsing path in isolation.
                var seconds = req.RetryAfterSeconds > 0 ? req.RetryAfterSeconds : 90;
                var s = store.GetOrCreate(tenantId);
                s.IsBlocked = true;
                s.RespondWith429 = true;
                s.RetryAfterSeconds = seconds;
                s.EstimatedTimeToRegainAccessMinutes = 0;
                s.BlockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
                s.ActiveScenario = "RetryAfter429";
                break;
            }
            case "SharedAppBudget":
            {
                // App-level X-App-Usage is one budget shared across accounts: every tenant's
                // X-App-Usage reports the same high percentage while their own per-account
                // (BUC) usage stays low — a fresh tenant must still get throttled.
                store.GlobalAppUsagePct = req.AppUsagePct > 0 ? req.AppUsagePct : 92;

                var heavy = store.GetOrCreate("tenant-heavy");
                heavy.CallCountPct = 90; heavy.AutoIncrementPerCallPct = 1; heavy.IsBlocked = false;

                var light = store.GetOrCreate("tenant-light");
                light.CallCountPct = 5; light.AutoIncrementPerCallPct = 1; light.IsBlocked = false;
                break;
            }
        }
    }

    private sealed record BlockRequest(int Minutes);

    private sealed record AppUsageRequest(int? Pct);

    private sealed record ScenarioRequest(
        string Name,
        string? TenantId            = "tenant-1",
        int FlappingIntervalSeconds = 30,
        int BlockForMinutes         = 2,
        int PerSecondLimit          = 100,
        int RetryAfterSeconds       = 90,
        int AppUsagePct             = 92);
}
