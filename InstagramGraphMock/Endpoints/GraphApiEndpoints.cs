using InstagramGraphMock.HeaderBuilders;
using InstagramGraphMock.State;

namespace InstagramGraphMock.Endpoints;

/// <summary>
/// Mirrors the Instagram Graph API surface. InstagramSenderApi points its base URL here in
/// Development — zero InstagramSenderApi code changes needed between mock and production.
/// Routes: POST /v25.0/{tenantId}/media | /media_publish | /messages  GET /v25.0/{tenantId}
/// Version is v25.0 — the Graph version IGAutopilot calls in production. Bump both here and
/// InstagramSenderApi's GraphApiBaseUrl together; the mock answers one version only, so a
/// mismatch shows up as a 404 that the sender reports as a generic failure.
/// </summary>
public static class GraphApiEndpoints
{
    public static void MapGraphApiEndpoints(this WebApplication app)
    {
        app.MapPost("/v25.0/{tenantId}/media",         Handle);
        app.MapPost("/v25.0/{tenantId}/media_publish", Handle);
        app.MapPost("/v25.0/{tenantId}/messages",      Handle);
        app.MapGet ("/v25.0/{tenantId}",               Handle);
    }

    private static IResult Handle(string tenantId, TenantStateStore store, HttpContext ctx)
    {
        var state = store.GetOrCreate(tenantId);

        // Serialise all read-modify-write access per tenant — burst scenarios hit the same
        // TenantSimState concurrently and would otherwise corrupt counters/windows.
        lock (state)
        {
            return HandleLocked(tenantId, state, store, ctx);
        }
    }

    private static IResult HandleLocked(string tenantId, TenantSimState state, TenantStateStore store, HttpContext ctx)
    {
        // Auto-unblock when BlockedUntilUtc has elapsed
        if (state.BlockedUntilUtc.HasValue && state.BlockedUntilUtc.Value <= DateTime.UtcNow)
        {
            state.IsBlocked = false;
            state.BlockedUntilUtc = null;
            state.EstimatedTimeToRegainAccessMinutes = 0;
        }

        // FlappingBlock: toggle IsBlocked every FlappingIntervalSeconds using elapsed time
        if (state.ActiveScenario == "FlappingBlock" && state.ScenarioStartedAtUtc.HasValue)
        {
            var elapsed = (DateTime.UtcNow - state.ScenarioStartedAtUtc.Value).TotalSeconds;
            state.IsBlocked = (int)(elapsed / state.FlappingIntervalSeconds) % 2 == 1;
        }

        // PerSecondRateLimit: count calls per 1-second window
        if (state.ActiveScenario == "PerSecondRateLimit")
        {
            var now = DateTime.UtcNow;
            if ((now - state.PerSecondWindowStart).TotalSeconds >= 1.0)
            {
                state.PerSecondWindowStart = now;
                state.PerSecondCallCount = 0;
            }
            state.PerSecondCallCount++;
            state.IsBlocked = state.PerSecondCallCount > state.PerSecondLimit;
        }

        // Always attach rate-limit headers (mirrors real Instagram behaviour)
        ctx.Response.Headers["X-App-Usage"] =
            AppUsageHeaderBuilder.Build(state, store.GlobalAppUsagePct);
        ctx.Response.Headers["X-Business-Use-Case-Usage"] =
            BucUsageHeaderBuilder.Build(tenantId, state);

        if (state.IsBlocked)
        {
            // RetryAfter429 variant: standard HTTP 429 + Retry-After (seconds), no Graph
            // error body — exercises the sender's header-based Retry-After parsing path.
            if (state.RespondWith429)
            {
                ctx.Response.Headers["Retry-After"] = state.RetryAfterSeconds.ToString();
                return Results.Json(new
                {
                    error = new
                    {
                        // Deliberately NOT a rate-limit code (Meta's 1 = "API Unknown"), so this
                        // scenario isolates the Retry-After header path: if the sender backs off
                        // here, it can only be because it parsed the header. 613 used to sit here,
                        // but it became a recognised rate-limit code in the Aug-2026 pass and would
                        // now short-circuit the very path this scenario exists to prove.
                        code    = 1,
                        message = "(#1) An unknown error occurred.",
                        type    = "OAuthException"
                    }
                }, statusCode: 429);
            }

            // Graph API signals rate limiting with HTTP 400 + an error code, not 429.
            return Results.Json(new
            {
                error = new
                {
                    code          = state.ReturnErrorCode,
                    error_subcode = state.ReturnErrorSubcode,
                    message       = $"(#{state.ReturnErrorCode}) Too many API calls. Wait and try again.",
                    type          = "OAuthException"
                }
            }, statusCode: 400);
        }

        // Increment usage percentages to simulate natural quota drain
        state.CallCountPct    = Math.Min(100, state.CallCountPct    + state.AutoIncrementPerCallPct);
        state.TotalTimePct    = Math.Min(100, state.TotalTimePct    + Math.Max(1, state.AutoIncrementPerCallPct / 2));
        state.TotalCpuTimePct = Math.Min(100, state.TotalCpuTimePct + Math.Max(1, state.AutoIncrementPerCallPct / 2));

        // Auto-block when HardBlockAtCallCountPct is reached.
        // Set BlockedUntilUtc so the block auto-recovers like the real API — without it,
        // a hard-blocked tenant stayed blocked forever until a manual /simulator/reset.
        if (state.HardBlockAtCallCountPct.HasValue && state.CallCountPct >= state.HardBlockAtCallCountPct.Value)
        {
            state.IsBlocked = true;
            if (state.EstimatedTimeToRegainAccessMinutes <= 0)
                state.EstimatedTimeToRegainAccessMinutes = 5;
            state.BlockedUntilUtc ??= DateTime.UtcNow.AddMinutes(state.EstimatedTimeToRegainAccessMinutes);
        }

        return Results.Ok(new { id = $"mock-{Guid.NewGuid():N}" });
    }
}
