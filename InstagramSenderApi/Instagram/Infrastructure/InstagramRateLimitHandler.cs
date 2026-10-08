using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using InstagramSenderApi.Instagram.Models;
using InstagramSenderApi.Instagram.Services;

namespace InstagramSenderApi.Instagram.Infrastructure;

/// <summary>
/// DelegatingHandler that runs after every Instagram API response.
/// Parses X-App-Usage and X-Business-Use-Case-Usage headers, updates per-tenant SQL state,
/// and sets request options so the Polly pipeline can read retry delay from the same response.
/// </summary>
public sealed class InstagramRateLimitHandler : DelegatingHandler
{
    // Keys used to pass data between this handler and the Polly retry delegate
    public static readonly HttpRequestOptionsKey<string> TenantIdKey         = new("InstagramTenantId");
    public static readonly HttpRequestOptionsKey<bool>   IsRateLimitedKey    = new("IsRateLimited");
    public static readonly HttpRequestOptionsKey<int>    RetryAfterMinutesKey = new("RetryAfterMinutes");

    /// <summary>
    /// Rate-limit error codes Instagram can return on our call surface, per
    /// developers.facebook.com/docs/graph-api/overview/rate-limiting (read 2026-08-04).
    /// Mandatory: 4 (app), 17 (user), 32 (Page calls, User token), 613 (custom limit),
    /// 80001 (Page calls, Page/System-User token), 80002 (Instagram BUC).
    /// Nice-to-have: 80006 (Messenger BUC) — IG DMs go out on the shared /messages surface.
    /// Ads/WhatsApp/Catalog/LeadGen BUC codes are deliberately absent: we never call those.
    /// </summary>
    private static readonly int[] RateLimitErrorCodes = [4, 17, 32, 613, 80001, 80002, 80006];

    /// <summary>
    /// 613/1996 is not a quota reset — Meta has flagged our request volume as inconsistent.
    /// It arrives with no <c>estimated_time_to_regain_access</c>, so a 1-minute floor would
    /// hammer straight back into the flag; back off substantially instead.
    /// </summary>
    private const int InconsistentVolumeSubcode  = 1996;
    private const int InconsistentVolumeBlockMin = 15;

    /// <summary>
    /// Codes raised at **app level** (L1): the whole FB app is limited, not one account.
    /// 4 = app rate limit; 613 (and 613/1996) = custom/app-flagged limit. Their block must
    /// land on the shared app:{AppId} row — blocking only the account that happened to
    /// receive the response would leave every other account calling under an app-wide limit.
    /// Everything else (17, 32, 80001, 80002, 80006) is account/user scoped.
    /// </summary>
    private static readonly int[] AppLevelErrorCodes = [4, 613];

    private readonly ITenantRateLimitService _rateLimitService;
    private readonly IOptions<OutboundRateLimitOptions> _options;
    private readonly ILogger<InstagramRateLimitHandler> _logger;

    public InstagramRateLimitHandler(
        ITenantRateLimitService rateLimitService,
        IOptions<OutboundRateLimitOptions> options,
        ILogger<InstagramRateLimitHandler> logger)
    {
        _rateLimitService = rateLimitService;
        _options          = options;
        _logger           = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        request.Options.TryGetValue(TenantIdKey, out string? tenantId);

        var response = await base.SendAsync(request, ct);

        if (!string.IsNullOrEmpty(tenantId))
            await ParseAndRecordUsageAsync(tenantId, request, response, ct);

        return response;
    }

    private async Task ParseAndRecordUsageAsync(
        string tenantId,
        HttpRequestMessage request,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        // L1 (app) and L2 (account) figures are kept strictly apart: folding the shared
        // X-App-Usage percentages into the tenant's own row would make every account look
        // as hot as the app budget, destroying per-account observability. The guard already
        // takes max(tenantPct, appPct) across the two rows, so throttling is unaffected.
        int maxCallCount = 0, maxTotalTime = 0, maxTotalCpu = 0, estimatedMinutes = 0;
        var sawUsageSignal = false;   // any signal at all — gates the early return
        var sawBucSignal   = false;   // account-level (L2) evidence specifically
        AppUsageHeaders? appUsage = null;

        // Parse X-App-Usage → L1 only
        if (response.Headers.TryGetValues("X-App-Usage", out var appUsageValues))
        {
            try
            {
                var usage = JsonSerializer.Deserialize<AppUsageHeaders>(appUsageValues.First());
                if (usage is not null)
                {
                    sawUsageSignal = true;
                    appUsage       = usage;
                }
            }
            catch (JsonException) { /* malformed header — ignore */ }
        }

        // Parse X-Business-Use-Case-Usage
        if (response.Headers.TryGetValues("X-Business-Use-Case-Usage", out var bucValues))
        {
            try
            {
                var bucData = JsonSerializer.Deserialize<Dictionary<string, BucUsageEntry[]>>(bucValues.First());
                if (bucData is not null)
                {
                    sawUsageSignal = true;
                    sawBucSignal   = true;
                    foreach (var entries in bucData.Values)
                    foreach (var entry in entries)
                    {
                        maxCallCount     = Math.Max(maxCallCount,     entry.CallCount);
                        maxTotalTime     = Math.Max(maxTotalTime,     entry.TotalTime);
                        maxTotalCpu      = Math.Max(maxTotalCpu,      entry.TotalCpuTime);
                        estimatedMinutes = Math.Max(estimatedMinutes, entry.EstimatedTimeToRegainAccessMinutes);
                    }
                }
            }
            catch (JsonException) { /* malformed header — ignore */ }
        }

        // On error response: buffer body, check for rate-limit error codes
        var isRateLimitError = false;
        var isAppLevelBlock  = false;
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                // Parameterless on purpose: the CancellationToken overload is .NET 9+ and
                // these components must also compile on net8.0 (IGAutopilot's target).
                await response.Content.LoadIntoBufferAsync();
                var body  = await response.Content.ReadAsStringAsync(ct);
                var error = JsonSerializer.Deserialize<InstagramErrorResponse>(body);

                if (error?.Error is not null && RateLimitErrorCodes.Contains(error.Error.Code))
                {
                    isRateLimitError = true;
                    isAppLevelBlock  = AppLevelErrorCodes.Contains(error.Error.Code);

                    // A rate-limit error without an ETA still means "stop calling" (Meta guidance)
                    // — apply a 1-minute minimum block so the queue backs off instead of hammering.
                    var floorMinutes = error.Error.Subcode == InconsistentVolumeSubcode
                        ? InconsistentVolumeBlockMin
                        : 1;
                    var retryMinutes = Math.Max(floorMinutes,
                        Math.Max(estimatedMinutes, error.Error.EstimatedTimeToRegainAccessMinutes));
                    request.Options.Set(IsRateLimitedKey,    true);
                    request.Options.Set(RetryAfterMinutesKey, retryMinutes);
                    estimatedMinutes = retryMinutes;

                    // Escalate the percentage on the row that actually owns this limit
                    if (isAppLevelBlock)
                        appUsage = new AppUsageHeaders(
                            Math.Max(appUsage?.CallCount    ?? 0, 100),
                            Math.Max(appUsage?.TotalTime    ?? 0, 0),
                            Math.Max(appUsage?.TotalCpuTime ?? 0, 0));
                    else
                        maxCallCount = Math.Max(maxCallCount, 100);

                    _logger.LogWarning(
                        "Rate-limit error for tenant {TenantId}: code={Code}, subcode={Subcode}, " +
                        "level={Level}, retryAfter={Minutes}min",
                        tenantId, error.Error.Code, error.Error.Subcode,
                        isAppLevelBlock ? "app" : "account", retryMinutes);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not parse Instagram error body for tenant {TenantId}", tenantId);
            }
        }

        // Standard HTTP 429 — some Graph endpoints (and any intermediary) signal with
        // 429 + Retry-After instead of the 400 + error-code convention. Honour both the
        // delta-seconds and HTTP-date forms of the header; without it, back off 1 minute.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var headerMinutes = RetryAfterMinutesFromHeader(response);
            var retryMinutes  = Math.Max(Math.Max(1, estimatedMinutes), headerMinutes);

            isRateLimitError = true;
            request.Options.Set(IsRateLimitedKey,     true);
            request.Options.Set(RetryAfterMinutesKey, retryMinutes);
            estimatedMinutes = retryMinutes;
            maxCallCount     = Math.Max(maxCallCount, 100);

            _logger.LogWarning(
                "HTTP 429 for tenant {TenantId}: Retry-After → {Minutes} min", tenantId, retryMinutes);
        }

        // Nothing observed (no usage headers, no rate-limit error) — do not overwrite
        // previously persisted state with zeros.
        if (!sawUsageSignal && !isRateLimitError)
            return;

        // estimatedBlockMinutes semantics (see ITenantRateLimitService):
        //   > 0  → set/extend block    0 → clear block (proven recovered)    null → preserve
        int? blockMinutes = estimatedMinutes > 0 ? estimatedMinutes
                          : response.IsSuccessStatusCode ? 0
                          : null;

        // Route the block to the level that owns it. An app-level code (4 / 613) blocks the
        // shared app row, not this account — the account did nothing wrong and its own
        // budget is untouched.
        int? tenantBlockMinutes = isAppLevelBlock ? null : blockMinutes;

        // Deliberately asymmetric with the account row: a success on ONE account does not
        // prove the shared app budget recovered, and clearing the app block would re-open
        // the floodgates for every account. It expires on its own BlockedUntilUtc instead.
        // (Observed: without this, an in-flight success from another tenant wiped a live
        // app-level block seconds after it was set.)
        int? appBlockMinutes = isAppLevelBlock ? blockMinutes : null;

        // Write the account row only when there is account-level evidence. Writing it from
        // an X-App-Usage-only response would persist zero percentages over the account's
        // real figures (A12.8).
        if (sawBucSignal || (isRateLimitError && !isAppLevelBlock) || response.IsSuccessStatusCode)
            await _rateLimitService.RecordUsageAsync(
                tenantId, maxCallCount, maxTotalTime, maxTotalCpu, tenantBlockMinutes, ct);

        // X-App-Usage is one budget shared by every account on the FB app — mirror it to
        // the global app:{AppId} row so all tenants' throttle guards see it, and carry the
        // block here when Meta limited the app rather than the account.
        // Only when we actually have app-level figures (the app-level error path above
        // synthesises them), so a header-less response never zeroes the shared row.
        if (appUsage is not null)
            await _rateLimitService.RecordUsageAsync(
                _options.Value.AppStateKey,
                appUsage.CallCount, appUsage.TotalTime, appUsage.TotalCpuTime,
                appBlockMinutes, ct);
    }

    private static int RetryAfterMinutesFromHeader(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes));
        if (retryAfter?.Date is { } date)
            return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalMinutes));
        return 0;
    }
}
