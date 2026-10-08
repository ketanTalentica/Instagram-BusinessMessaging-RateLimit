# InstagramSenderApi — Outbound Instagram Graph API Rate Limiting Spec

**Purpose:** Prevent hitting Instagram Graph API rate limits across multiple tenants (Instagram accounts)
using a two-layer strategy: proactive header-based throttling (slow down before limits are hit) and
reactive Polly v8 resilience (retry + circuit breaker on 429 / error codes). Per-tenant state is
persisted in SQL Server so limits survive app restarts and can be shared across instances.

---

## NuGet Packages

| Package | Purpose |
|---|---|
| `Polly` | Core resilience policies |
| `Microsoft.Extensions.Resilience` | Polly v8 DI integration |
| `Microsoft.Extensions.Http.Resilience` | Polly integration for HttpClientFactory (as-built: pipelines come from `ResiliencePipelineRegistry`, not `AddResilienceHandler` — see resilience section) |
| `Dapper` | Lightweight SQL Server access for rate-limit state persistence |
| `Microsoft.Data.SqlClient` | SQL Server driver |
| `Microsoft.AspNetCore.App` (framework ref) | ASP.NET Core 9 |

---

## Instagram Graph API Rate Limit Facts (verified from Meta docs)

| Scope | Limit | Header |
|---|---|---|
| Instagram Platform (non-messaging) | `4800 × impressions` calls / 24h rolling, per app-user pair | `X-Business-Use-Case-Usage` |
| Messaging Send API (text/links/stickers) | 100 calls/sec per Instagram professional account | `X-Business-Use-Case-Usage` |
| Messaging Send API (audio/video) | 10 calls/sec per Instagram professional account | `X-Business-Use-Case-Usage` |
| Conversations API | 2 calls/sec per Instagram professional account | `X-Business-Use-Case-Usage` |
| General Graph API calls | 200 × DAU / rolling hour (app-level) | `X-App-Usage` |

### `X-App-Usage` Header (general Graph API calls)
```json
{ "call_count": 28, "total_time": 25, "total_cputime": 25 }
```
All values are **percentages (0–100)**. When any reaches 100, calls are throttled.

### `X-Business-Use-Case-Usage` Header (Instagram Platform + Marketing API)
```json
{
  "{business-object-id}": [{
    "type": "instagram_platform",
    "call_count": 85,
    "total_cputime": 20,
    "total_time": 20,
    "estimated_time_to_regain_access": 0
  }]
}
```
`estimated_time_to_regain_access` is in **minutes**. When > 0, the account is already blocked.

### Error Codes That Mean Rate-Limited (must retry, not fail permanently)

As-built list (`InstagramRateLimitHandler.RateLimitErrorCodes`) — Instagram-relevant codes only; the
level column drives which state row carries the block. Full derivation + Meta doc links: TRD §3.5.

| Code | Sub | Level | Meaning |
|---|---|---|---|
| 4 | — | **app** | Application request limit reached |
| 613 | — | **app** | A custom rate limit has been reached (arrives on 400 as well as 429) |
| 613 | 1996 | **app** | Meta flagged the app's request volume as inconsistent — no ETA supplied, 15-min floor |
| 17 | — | account | User request limit reached (also how a per-second overrun surfaces) |
| 32 | — | account | Page request limit reached (User access token) |
| 80001 | — | account | Too many calls to this Page/account (Page or System-User token) |
| 80002 | — | account | **Instagram BUC limit — the code for standard IG Platform endpoints** |
| 80006 | — | account | Messenger BUC (IG DMs share the `/messages` surface) — handled defensively |

Not handled on purpose: Ads Insights 80000, Custom Audience 80003, Ads Management 80004, LeadGen
80005, WhatsApp 80008, Catalog 80009/80014, Ads-API subcode 2446079 — we never call those surfaces.

**Meta's guidance:** When limit is reached, **stop immediately** — continued calls worsen the cooldown window.

---

## Two-Layer Strategy

```
Layer 1 — PROACTIVE (header-based)
  After every API response:
    → Parse X-App-Usage / X-Business-Use-Case-Usage
    → Persist usage % to SQL (TenantRateLimitState)
    → If usage ≥ 80%: delay future calls (spread remaining quota)
    → If estimated_time_to_regain_access > 0: set BlockedUntil in SQL

Layer 2 — REACTIVE (Polly pipeline, one per tenant)
  On every outbound HTTP call (order outer → inner):
    → RetryStrategy: up to 3 retries on 429 / codes 4,17,32,80001 / transport errors
        delay = estimated_time_to_regain_access + 1 min buffer (if present and ≤ 2 min)
        else exponential backoff with jitter: 2s → 4s → 8s
        blocks LONGER than 2 min are NOT retried inline — the block is already
        persisted to SQL, so the queue re-enqueues and the guard holds the tenant
    → CircuitBreakerStrategy: per-tenant isolation
        FailureRatio 0.8 over 2-min sampling window, min throughput 3, break 5 min
    → TimeoutStrategy: 15s per individual attempt (innermost, never cuts a retry delay)
```

### July 2026 coverage additions (plan deltas O9–O11 — implemented & live-verified)

- **O9 `PerSecondDispatchGate`** (`Instagram/Services/`): per-tenant token bucket (reservation-style, balance may go negative) enforced by `InstagramThrottleGuard` as the last pre-flight step. Covers Meta's per-second caps, which usage headers cannot predict — they reflect hourly budget windows, not instantaneous rates. Configured via `RateLimiting:Outbound:PerSecondDispatchLimit` (default 100) *and, since the August 2026 pass below, three further per-class caps*. Verified: 15 sends through a 4/s gate against a 5/s mock cap → zero code-17 errors.
- **O10 shared app budget**: `X-App-Usage` is one budget for ALL accounts on the FB app, so `InstagramRateLimitHandler` mirrors it to a global state row keyed `app:{AppId}` (`RateLimiting:Outbound:AppId`); `TenantRateLimitService.GetThrottleDelayAsync` throttles on `max(tenantPct, appPct)`. Verified: a fresh tenant's first-ever send was delayed 9,375 ms purely from the app row at 92%. *(Superseded in part by the August 2026 pass below: the app row now DOES carry a block, for app-level codes 4 / 613.)*
- **O11 `Retry-After` on HTTP 429**: any 429 is treated as rate-limited even without a recognised Graph error code; `Retry-After` (delta-seconds and HTTP-date forms) is parsed to minutes (ceil, min 1) and merged with the error-body ETA. Verified with the mock's `RetryAfter429` scenario (429 + `Retry-After: 90`, unrecognised code → 2-min block +1 buffer, job re-queued). The mock's body code for this scenario is now Meta's `1` ("API Unknown"); it was 613 until the August-2026 pass made 613 a recognised rate-limit code, which would have let the scenario pass through the code list instead of the header path it exists to test. Re-verified 2026-08-05.
- **Config gate**: `RateLimiting:Outbound:Enabled` switches all guard enforcement off (observe-first rollout); the header-parsing handler always records state regardless.

### August 2026 level-correctness pass (A12 — implemented & live-verified)

Full reference table of Meta's levels/codes with doc links: `docs/TRD_RateLimiting.md` §3.5.

- **Codes recognised** (`InstagramRateLimitHandler.RateLimitErrorCodes`): `4, 17, 32, 613, 80001, 80002, 80006`. **80002 is the Instagram BUC code** — previously only its Page-token sibling 80001 was handled, so a real Instagram block was classified as a generic 400: no block persisted, no retry. `613` also arrives on HTTP 400, not only 429.
- **`error_subcode` parsed**: `613/1996` ("inconsistent behavior in the API request volume of your app") carries no `estimated_time_to_regain_access`; it takes a **15-minute** block floor instead of the 1-minute default, because retrying in a minute walks straight back into the flag.
- **Blocks routed by level**: `AppLevelErrorCodes = [4, 613]` block the shared `app:{AppId}` row; account-level codes block the tenant row. `GetThrottleDelayAsync` throws `TenantBlockedException` for *any* tenant while the app row is blocked — previously one unlucky account absorbed an app-wide limit while every other account kept calling. A success clears an account block but **never** an app block (one account recovering does not prove a shared budget recovered).
- **L1/L2 figures kept apart**: `X-App-Usage` is recorded only on the app row, BUC figures only on the tenant row; the tenant row is written only when account-level evidence exists, so an app-usage-only response cannot zero a real per-account figure. Throttling is unchanged — the guard still takes `max(tenantPct, appPct)`.
- **Per-second caps per call class** (`DispatchClassifier` + one bucket per tenant *and* class): text/links/reactions/stickers `PerSecondDispatchLimit` 100/s, audio/video `PerSecondMediaDispatchLimit` 10/s, Conversations `PerSecondConversationsDispatchLimit` 2/s, unclassified `PerSecondUnclassifiedDispatchLimit` 2/s. Text and audio/video share the `/messages` endpoint, so the class comes from `message.attachment.type` in the payload, not the URL alone.
- **Budget ceilings are not knowable**: Meta's Instagram allowance is `4800 × impressions` over 24 h and only a *percentage* is ever reported, so no absolute per-account call budget can be pre-computed — the 80 % threshold is the primary defence, not a refinement of a known quota.
- **New mock scenarios**: `InstagramBucBlock` (80002), `CustomRateLimit613` (613/1996), `AppLevelBlock` (4) — all on HTTP 400 with no `Retry-After`, so only the code list can recognise them.
- **net8.0 note**: components compile on net8.0 (IGAutopilot's target). `HttpContent.LoadIntoBufferAsync(CancellationToken)` is .NET 9-only — the handler deliberately uses the parameterless overload.

---

## Project Structure

```
InstagramSenderApi/
├── InstagramSenderApi.csproj
├── Program.cs
├── appsettings.json
│
├── Instagram/
│   ├── Models/
│   │   ├── AppUsageHeaders.cs              ← maps X-App-Usage JSON
│   │   ├── BucUsageEntry.cs                ← maps one entry in X-Business-Use-Case-Usage
│   │   └── TenantRateLimitState.cs         ← persisted per-tenant state (SQL row)
│   │
│   ├── Infrastructure/
│   │   ├── ITenantRateLimitRepository.cs   ← SQL persistence abstraction
│   │   ├── SqlTenantRateLimitRepository.cs ← Dapper implementation (auto-creates DB + table)
│   │   ├── InstagramRateLimitHandler.cs    ← DelegatingHandler: parses headers post-response
│   │   └── InstagramResiliencePipeline.cs  ← Polly pipeline factory (per-tenant keyed)
│   │
│   ├── Client/
│   │   └── InstagramClient.cs              ← typed HttpClient; runs calls in per-tenant pipeline
│   │
│   ├── Services/
│   │   ├── TenantRateLimitService.cs       ← facade: RecordUsageAsync, GetThrottleDelayAsync
│   │   ├── IOutboundGate.cs                ← gate contract + GateOrder + OutboundDispatch
│   │   ├── InstagramThrottleGuard.cs       ← runs the gates in order and does the waiting
│   │   ├── HeaderUsageThrottleGate.cs      ← proactive delay from the persisted usage rows
│   │   ├── PerSecondDispatchGate.cs        ← token bucket per tenant per dispatch class
│   │   ├── DispatchClass.cs                ← the classes + DispatchClassifier
│   │   └── TenantBlockedException.cs       ← thrown when tenant is hard-blocked
│   │
│   └── Workers/
│       ├── SendJob.cs                      ← queued job record
│       └── SendQueueWorker.cs              ← IHostedService: per-tenant Channel<SendJob>
│
└── Controllers/
    └── SendController.cs                   ← POST /send — enqueues jobs to SendQueueWorker
```

---

## Component Details

### `AppUsageHeaders.cs`
**Purpose:** Deserialise `X-App-Usage` response header.

```csharp
public record AppUsageHeaders(int CallCount, int TotalTime, int TotalCpuTime);
// JSON property names: "call_count", "total_time", "total_cputime"
```

---

### `BucUsageEntry.cs`
**Purpose:** Deserialise one entry inside the `X-Business-Use-Case-Usage` response header.

```csharp
public record BucUsageEntry(
    string Type,
    int CallCount,
    int TotalCpuTime,
    int TotalTime,
    int EstimatedTimeToRegainAccessMinutes);
// JSON: "type", "call_count", "total_cputime", "total_time", "estimated_time_to_regain_access"
```

---

### `TenantRateLimitState.cs`
**Purpose:** Per-tenant state row persisted in SQL Server. Loaded on startup and refreshed after every response.

```csharp
public class TenantRateLimitState
{
    public string TenantId { get; set; }
    public int MaxCallCountPct { get; set; }     // worst % across all BUC entries
    public int MaxTotalTimePct { get; set; }
    public int MaxTotalCpuTimePct { get; set; }
    public DateTime? BlockedUntilUtc { get; set; } // null = not currently blocked
    public DateTime LastUpdatedUtc { get; set; }
}
```

---

### SQL Server Schema

```sql
CREATE TABLE TenantRateLimitState (
    TenantId           NVARCHAR(128)  NOT NULL PRIMARY KEY,
    MaxCallCountPct    INT            NOT NULL DEFAULT 0,
    MaxTotalTimePct    INT            NOT NULL DEFAULT 0,
    MaxTotalCpuTimePct INT            NOT NULL DEFAULT 0,
    BlockedUntilUtc    DATETIME2      NULL,
    LastUpdatedUtc     DATETIME2      NOT NULL
);
```

Upsert pattern: `MERGE INTO TenantRateLimitState USING ... ON TenantId = @TenantId WHEN MATCHED ... WHEN NOT MATCHED ...`

---

### `ITenantRateLimitRepository.cs`
**Purpose:** Abstraction over SQL persistence so unit tests can mock it.

```csharp
public interface ITenantRateLimitRepository
{
    Task<TenantRateLimitState?> GetAsync(string tenantId, CancellationToken ct = default);
    Task UpsertAsync(TenantRateLimitState state, CancellationToken ct = default);
}
```

---

### `SqlTenantRateLimitRepository.cs`
**Purpose:** Dapper-based implementation of `ITenantRateLimitRepository`.

- `GetAsync`: `SELECT` by `TenantId`
- `UpsertAsync`: `MERGE` statement (atomic, no race condition)
- Connection string loaded from `IConfiguration["ConnectionStrings:SenderDb"]`

---

### `InstagramRateLimitHandler.cs`
**Purpose:** `DelegatingHandler` registered on the typed Instagram `HttpClient`. Runs automatically
after every response — parses rate-limit headers and updates SQL state.

Workflow per response:
1. Read `X-App-Usage` header → deserialise to `AppUsageHeaders`
2. Read `X-Business-Use-Case-Usage` header → deserialise to `Dictionary<string, BucUsageEntry[]>`
3. Take the **worst-case percentage** across all metrics and all BUC entries
4. If `EstimatedTimeToRegainAccessMinutes > 0` → set `BlockedUntilUtc = UtcNow + minutes + 1 min buffer`
5. Call `TenantRateLimitService.RecordUsageAsync(tenantId, parsedState)`
6. `tenantId` is retrieved from `HttpRequestMessage.Options` (set by caller before each request)

---

### `InstagramResiliencePipeline.cs`
**Purpose:** Polly v8 resilience pipeline factory. One pipeline instance **per tenant** (keyed by `tenantId`)
so one tenant's circuit breaker tripping does **not** affect other tenants.

```
Pipeline composition (outer to inner — Polly executes first-added as outermost):
  1. RetryStrategy          — max 3 retries
       handle: HttpRequestException, TimeoutRejectedException, BrokenCircuitException,
               responses flagged rate-limited by InstagramRateLimitHandler
               (HTTP 429 or OAuthException codes 4/17/32/613/80001/80002)
       delay:  estimated_time_to_regain_access + 1 min buffer when present AND ≤ 2 min;
               longer blocks are NOT retried inline (queue + throttle guard own the wait,
               per Meta's "stop immediately" guidance)
               else exponential backoff with jitter: 2s, 4s, 8s
       on retry: dispose the previous attempt's response
  2. CircuitBreakerStrategy — per-tenant isolation
       FailureRatio 0.8, MinimumThroughput 3, SamplingDuration 2 min, BreakDuration 5 min
  3. TimeoutStrategy        — 15 seconds per individual attempt
```

**As-built registration (deviation from original plan):** the pipeline is *not* attached via
`AddResilienceHandler` — that API builds one shared pipeline per named HttpClient, which would
make **all tenants share one circuit breaker**. Instead `InstagramClient` resolves a per-tenant
pipeline lazily from a singleton `ResiliencePipelineRegistry<string>` (key `instagram:{tenantId}`)
and executes each call inside it, building a **fresh `HttpRequestMessage` per attempt**
(HttpClient forbids re-sending a message, and per-attempt requests keep the rate-limited
flags attempt-scoped).

---

### `TenantRateLimitService.cs`
**Purpose:** Facade that coordinates header parsing results, SQL persistence, and throttle decisions.
Used by `InstagramRateLimitHandler`, `InstagramThrottleGuard`, and `SendQueueWorker`.

```csharp
public interface ITenantRateLimitService
{
    // estimatedBlockMinutes semantics: >0 set block (+1 min buffer) · 0 clear block
    // (a successful call proves recovery) · null preserve existing block (an unrelated
    // failure must never unblock a tenant)
    Task RecordUsageAsync(string tenantId, int maxCallCountPct, int maxTotalTimePct,
        int maxTotalCpuTimePct, int? estimatedBlockMinutes, CancellationToken ct = default);
    Task<TenantRateLimitState?> GetStateAsync(string tenantId, CancellationToken ct = default);
    Task<TimeSpan?> GetThrottleDelayAsync(string tenantId, CancellationToken ct = default);
}
```

`GetThrottleDelayAsync` logic:
- Load state from repository (with short in-memory cache, 10-second TTL)
- If `BlockedUntilUtc > UtcNow` → throw `TenantBlockedException(remaining)` — caller must not call
- If `MaxCallCountPct >= 80` → return calculated delay to spread remaining quota over rest of window
  (clamped 200 ms – 60 s; observed in practice: 3.75 s at 80 % rising to 15 s at 96 %)
- Else → return `null` (no delay needed)

The handler only calls `RecordUsageAsync` when it actually observed a signal (usage headers or a
rate-limit error body) — a header-less 500 must not wipe persisted state to zeros. A rate-limit
error with **no** ETA still applies a 1-minute minimum block (Meta: stop immediately).

---

### `IOutboundGate.cs` + `InstagramThrottleGuard.cs`
**Purpose:** Pre-flight checks run **before** any HTTP call leaves the application.
Prevents burning API quota when we already know we're near the limit.

Each reason a call might have to wait is an `IOutboundGate`. A gate computes the wait; the guard
decides to sleep it. That split is deliberate: it keeps every gate unit-testable without a clock or a
real minute of wall time, and keeps "is waiting even the right answer" in one place.

```csharp
public interface IOutboundGate
{
    int    Order { get; }     // see GateOrder
    string Name  { get; }
    ValueTask<TimeSpan> GetDelayAsync(OutboundDispatch dispatch, CancellationToken ct = default);
}

public async Task EnforceAsync(string tenantId, DispatchClass cls = DispatchClass.Unclassified,
                               CancellationToken ct = default)
{
    if (!_options.Value.Enabled) return;

    foreach (var gate in _gates)                       // sorted by Order in the constructor
    {
        var delay = await gate.AcquireAsync(new OutboundDispatch(tenantId, cls), ct);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct);               // caller's token respected for cancellation
    }
}
```

As-built gates, in order:

| Order | Gate | Wait it returns |
|---|---|---|
| 100 | `HeaderUsageThrottleGate` | the proactive delay from `GetThrottleDelayAsync` (headers, `max(tenantPct, appPct)` ≥ 80%) |
| 200 | `PerSecondDispatchGate` | the token-bucket deficit for this tenant *and dispatch class* |

`PerSecondDispatchGate` is last by design: its token is for dispatching *now*, so any earlier gate's
wait must already have elapsed, or the token is spent on a call that has not happened yet.

If the tenant is hard-blocked (`BlockedUntilUtc > UtcNow` on **either** the account row or the shared
app row), `HeaderUsageThrottleGate` lets `TenantBlockedException(retryAfter)` propagate instead of
returning a delay — a block measured in minutes is not something to sleep through on a worker thread,
so the `SendQueueWorker` re-enqueues the job instead.

---

### `SendQueueWorker.cs`
**Purpose:** `IHostedService` background worker that drains a per-tenant bounded channel of send jobs.
Ensures message ordering per tenant and handles re-queuing on throttle.

Key design decisions:
- One `Channel<SendJob>` per tenant (`ConcurrentDictionary<string, Channel<SendJob>>`), capacity 500
- **Enqueue is non-blocking (`TryEnqueue` / `TryWrite`)** — awaiting space in the channel from the
  processor's own re-enqueue path would deadlock (the processor is the channel's only reader).
  A full channel surfaces as HTTP 429 at the controller instead of an indefinitely-hanging request.
- Per-tenant processing loop:
  1. Call `InstagramThrottleGuard.EnforceAsync` (delays or throws if blocked)
  2. Execute HTTP call via `InstagramClient` (per-tenant Polly pipeline + `InstagramRateLimitHandler`)
  3. On `TenantBlockedException`: `await Task.Delay(retryAfter)` (pauses only this tenant's queue),
     then re-enqueue with incremented attempt count
  4. On a rate-limited result (Polly retries exhausted or circuit open): pause for the advertised
     window (ETA + 1 min, or 30 s default), then re-enqueue — the job is **not** dropped
  5. Jobs are dropped only when non-retryable (e.g. 404) or after 5 attempts (logged as error)
- Jobs are **not** persisted to SQL — they live in-memory. See Open Decision below.

```csharp
public record SendJob(string TenantId, string TargetEndpoint, JsonElement Payload, int AttemptCount = 0);
```

---

### `SendController.cs`
**Purpose:** REST endpoint that accepts outbound send requests and enqueues them to `SendQueueWorker`.

```
POST /send
Body: { "tenantId": "...", "payload": {...}, "targetEndpoint": "..." }
Response: 202 Accepted (job enqueued) | 429 + Retry-After (tenant channel full)
        | 400 (validation error)      | 503 (channel closed / shutting down)
```

---

### `Program.cs` — DI Wiring Summary

```csharp
// SQL repository (opens a connection per call — safe as singleton)
services.AddSingleton<ITenantRateLimitRepository, SqlTenantRateLimitRepository>();

// Rate limit service (with short in-memory cache layer) + throttle guard
services.AddSingleton<ITenantRateLimitService, TenantRateLimitService>();
services.AddSingleton<InstagramThrottleGuard>();

// Pre-flight gates. Execution order comes from each gate's Order property, not these lines.
// PerSecondDispatchGate is resolved through its concrete registration so the guard and anything
// else share one instance — the token buckets are the state.
services.AddSingleton<PerSecondDispatchGate>();
services.AddSingleton<IOutboundGate, HeaderUsageThrottleGate>();
services.AddSingleton<IOutboundGate>(sp => sp.GetRequiredService<PerSecondDispatchGate>());

// DelegatingHandler (transient)
services.AddTransient<InstagramRateLimitHandler>();

// Per-tenant Polly pipelines are created lazily from this registry by InstagramClient
// (AddResilienceHandler would share one circuit breaker across all tenants)
services.AddSingleton<ResiliencePipelineRegistry<string>>();

// Typed HttpClient with header-parsing handler; resilience applied inside InstagramClient
services.AddHttpClient<IInstagramClient, InstagramClient>()
        .AddHttpMessageHandler<InstagramRateLimitHandler>();

// Background worker — singleton + hosted service (controller injects it to enqueue)
services.AddSingleton<SendQueueWorker>();
services.AddHostedService(sp => sp.GetRequiredService<SendQueueWorker>());

// Startup: repository.EnsureTableExistsAsync() — creates the SenderDB database (via master)
// and the TenantRateLimitState table when missing, so first run works on a clean machine.
```

---

### `appsettings.json` Shape

```json
{
  "ConnectionStrings": {
    "SenderDb": "Server=(localdb)\\mssqllocaldb;Database=SenderDB;..."
  },
  "Instagram": {
    "GraphApiBaseUrl": "https://graph.facebook.com/v25.0/"
  }
}
```

**As-built note:** only `GraphApiBaseUrl` and the connection string are configuration today.
Threshold (80 %), block buffer (1 min), retry attempts (3), channel capacity (500) and max
job attempts (5) are compile-time constants — promoting them to `IOptions<InstagramOptions>`
is a planned improvement (see integration plan). `appsettings.Development.json` overrides
`GraphApiBaseUrl` to `http://localhost:5020/v25.0/` (the InstagramGraphMock).

---

## Security Notes

1. Instagram access tokens are per-tenant secrets — store in SQL (encrypted column) or Key Vault; **never** in `appsettings.json`
2. `TenantId` used as a Polly pipeline key and SQL primary key — validate it is a known tenant ID before use to prevent key injection
3. `estimated_time_to_regain_access` from the API response must be treated as advisory only — add a 1-minute buffer to avoid immediate re-block
4. Log rate-limit state changes but **never log access tokens** — scrub `Authorization` header from diagnostic logs

---

## Open Decision — Queue Persistence (Outbox Pattern)

The current spec uses an **in-memory** `Channel<SendJob>`. Jobs are lost on app restart.

**Recommended for production:** persist jobs to a `SendJobs` SQL table (outbox pattern):
- `INSERT` into `SendJobs` on `POST /send` (same transaction as business data if applicable)
- `SendQueueWorker` reads from DB instead of in-memory channel
- On completion, `DELETE` or mark `Status = 'Sent'`
- Restart-safe, auditable, and enables dead-letter handling

Implement this when reliability requirements are confirmed.

---

## Verification Checklist

- [x] Send request; mock response with `X-App-Usage: {"call_count":85,...}` → `TenantRateLimitState.MaxCallCountPct == 85` persisted to SQL *(verified 2026-07-06)*
- [x] Load state with `BlockedUntilUtc` in the future → `InstagramThrottleGuard` throws `TenantBlockedException` without making any HTTP call
- [x] Error body with `"code": 80001` and ETA 5 min → block persisted (ETA + 1 min buffer); ETA > 2 min is **not** retried inline (as-built deviation: the queue owns long waits — Meta says stop calling); ETAs ≤ 2 min are retried with that delay
- [ ] Circuit breaker trips for Tenant A → Tenant B continues processing normally (isolation is per pipeline key; scripted multi-tenant test pending)
- [x] Rate-limited job is re-queued after the advertised pause — no silent drop *(verified: 6-min pause + re-enqueue observed)*
- [x] `POST /send` on a full tenant channel → 429 + `Retry-After` (never hangs the HTTP request)
- [ ] App restart with in-flight jobs → jobs are lost (expected with current in-memory design; accept until outbox is implemented)
- [x] SQL upsert is idempotent — MERGE by TenantId
- [x] `GetThrottleDelayAsync` at 79% usage → no delay; at 80% → delay calculated (3.75 s observed, rising with usage); at blocked state → exception
- [x] Response without usage headers / non-rate-limit error → persisted state is NOT wiped, active block is preserved
- [x] Fresh machine startup → `SenderDB` database + table auto-created

---

## Real-Time Simulator — `InstagramGraphMock` Project

**Purpose:** A companion ASP.NET Core 9 Minimal API that impersonates the Instagram Graph API.
It serves the same URL structure as the real API, returns configurable `X-App-Usage` /
`X-Business-Use-Case-Usage` headers, and emits 429 + error bodies on demand — so the InstagramSenderApi's
proactive throttling and Polly pipeline can be exercised end-to-end without touching real Instagram.

### How It Fits Into the System

```
[InstagramSenderApi]
    │
    │ HttpClient (base URL overridden to mock server in dev/test)
    ▼
[InstagramGraphMock]  ←── Developer sets scenarios via POST /simulator/*
    │  returns X-App-Usage, X-Business-Use-Case-Usage, 429, error codes
    │
    ▼
  (real Instagram Graph API in production — same URL surface)
```

Only one config line changes between dev and prod:
`appsettings.Development.json` → `"GraphApiBaseUrl": "http://localhost:5020"`
`appsettings.Production.json` → `"GraphApiBaseUrl": "https://graph.facebook.com/v25.0"`

### Project Structure

```
InstagramGraphMock/
├── InstagramGraphMock.csproj        ← Minimal API, net9.0, no external dependencies
├── Program.cs                        ← maps /v25.0/* (Graph API surface) + /simulator/* (control)
│
├── State/
│   ├── TenantSimState.cs             ← mutable per-tenant mock state
│   └── TenantStateStore.cs           ← ConcurrentDictionary<tenantId, TenantSimState>; thread-safe
│
├── Endpoints/
│   ├── GraphApiEndpoints.cs          ← fake /v25.0/{tenantId}/media, /messages, /media_publish
│   └── SimulatorControlEndpoints.cs  ← /simulator/* control surface
│
└── HeaderBuilders/
    ├── AppUsageHeaderBuilder.cs      ← serialises TenantSimState → X-App-Usage JSON string
    └── BucUsageHeaderBuilder.cs      ← serialises TenantSimState → X-Business-Use-Case-Usage JSON
```

### `TenantSimState.cs` — Per-Tenant Mutable State

```csharp
public class TenantSimState
{
    public int CallCountPct { get; set; }           // current usage %, 0–100
    public int TotalTimePct { get; set; }
    public int TotalCpuTimePct { get; set; }
    public int AutoIncrementPerCallPct { get; set; } = 2;  // how much each call raises the %
    public bool IsBlocked { get; set; }
    public int EstimatedTimeToRegainAccessMinutes { get; set; }
    public int? HardBlockAtCallCountPct { get; set; }      // auto-block when % hits this value
    public int? ReturnErrorCode { get; set; }              // 4, 17, 32, or 80001
    public DateTime? BlockedUntilUtc { get; set; }         // auto-unblock when time passes
}
```

### Fake Graph API Endpoints (`GraphApiEndpoints.cs`)

These mirror the real Instagram Graph API surface so InstagramSenderApi needs zero code changes:

| Method | Route | Simulates |
|---|---|---|
| `POST` | `/v25.0/{tenantId}/media` | Create media container |
| `POST` | `/v25.0/{tenantId}/media_publish` | Publish media container |
| `POST` | `/v25.0/{tenantId}/messages` | Send message (messaging API) |
| `GET` | `/v25.0/{tenantId}` | Basic account read (for health checks) |

**Per-request behaviour** (all steps run under a per-tenant lock — concurrent bursts must not
corrupt counters or the per-second window):
1. Look up `TenantSimState` for `tenantId` (create default state if first call)
2. If `BlockedUntilUtc` has passed → auto-clear `IsBlocked`, reset `EstimatedTimeToRegainAccessMinutes`
3. If `IsBlocked` → return `{ "error": { "code": {ReturnErrorCode}, "message": "Rate limit..." } }` with HTTP 400 (Graph API uses 400, not 429, with error codes)
4. Increment `CallCountPct += AutoIncrementPerCallPct` (capped at 100)
5. If `CallCountPct >= HardBlockAtCallCountPct` → flip `IsBlocked = true` **and set
   `BlockedUntilUtc = now + EstimatedTimeToRegainAccessMinutes` (default 5)** so the block
   auto-recovers like the real API — without this a hard-blocked tenant stayed blocked forever
6. Add `X-App-Usage` and `X-Business-Use-Case-Usage` headers
7. Return `200 OK` with minimal success body (`{ "id": "mock-{guid}" }`)

`PerSecondRateLimit` scenario returns error code **17** (user request limit), not 80001.

### Simulator Control Endpoints (`SimulatorControlEndpoints.cs`)

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/simulator/state/{tenantId}` | View current `TenantSimState` for a tenant |
| `GET` | `/simulator/state` | View all tenant states |
| `POST` | `/simulator/configure/{tenantId}` | Set any `TenantSimState` fields (partial update) |
| `POST` | `/simulator/block/{tenantId}` | Immediately block a tenant for N minutes |
| `POST` | `/simulator/reset/{tenantId}` | Reset one tenant to default state (0%, unblocked) |
| `POST` | `/simulator/reset` | Reset all tenants |
| `POST` | `/simulator/scenario` | Apply a named predefined scenario (see below) |

### Predefined Scenarios (`POST /simulator/scenario`)

Body: `{ "name": "GradualApproach", "tenantId": "tenant-1" }`

| Scenario Name | What It Configures | What InstagramSenderApi Should Do |
|---|---|---|
| `GradualApproach` | Start at 0%, `AutoIncrementPerCallPct = 5`, `HardBlockAtCallCountPct = 100` | Proactive delay kicks in at 80%; circuit breaker never trips |
| `SuddenBlock` | Normal until call #20, then `IsBlocked = true`, `EstimatedTimeToRegainAccess = 5` | Polly retries with 5-min delay, circuit breaker trips after 3 failures |
| `FlappingBlock` | Background thread toggles `IsBlocked` every 30 seconds | Circuit breaker half-opens and recovers periodically |
| `MultiTenantMix` | Tenant A: 10%, Tenant B: 75%, Tenant C: blocked | Tenant isolation: A processes normally, B is throttled, C is circuit-broken |
| `RecoveryTest` | Block tenant, set `BlockedUntilUtc = now + 2 min` | After 2 min, mock auto-recovers; InstagramSenderApi resumes without manual intervention |
| `PerSecondRateLimit` | Track calls per second; return error code `17` if >100 in 1 second | `SendQueueWorker` respects per-second cap; no more than 100 jobs/sec dispatched |

### Header Output Examples

`X-App-Usage` (when `CallCountPct = 72`):
```json
{ "call_count": 72, "total_time": 35, "total_cputime": 30 }
```

`X-Business-Use-Case-Usage` (when `CallCountPct = 85`, `EstimatedTimeToRegainAccess = 0`):
```json
{
  "tenant-1": [{
    "type": "instagram_platform",
    "call_count": 85,
    "total_cputime": 40,
    "total_time": 38,
    "estimated_time_to_regain_access": 0
  }]
}
```

`X-Business-Use-Case-Usage` (when blocked, `EstimatedTimeToRegainAccess = 5`):
```json
{
  "tenant-1": [{
    "type": "instagram_platform",
    "call_count": 100,
    "total_cputime": 100,
    "total_time": 100,
    "estimated_time_to_regain_access": 5
  }]
}
```

### Running the Simulator

```bash
# 1. Start InstagramGraphMock
cd InstagramGraphMock && dotnet run   # listens on http://localhost:5020

# 2. Override base URL in InstagramSenderApi (Development only)
# appsettings.Development.json: "GraphApiBaseUrl": "http://localhost:5020"

# 3. Start InstagramSenderApi
cd InstagramSenderApi && dotnet run             # listens on http://localhost:5001

# 4. Set up a scenario on the mock server
curl -X POST http://localhost:5020/simulator/scenario \
     -H "Content-Type: application/json" \
     -d '{"name":"GradualApproach","tenantId":"tenant-1"}'

# 5. Send jobs to InstagramSenderApi and watch it throttle proactively
curl -X POST http://localhost:5001/send \
     -H "Content-Type: application/json" \
     -d '{"tenantId":"tenant-1","payload":{},"targetEndpoint":"/v25.0/tenant-1/messages"}'

# 6. Watch current mock state
curl http://localhost:5020/simulator/state/tenant-1
```

### What the Results Prove

| Observation in InstagramSenderApi logs / SQL | Rate-limit component it validates |
|---|---|
| `TenantRateLimitState.MaxCallCountPct` increases with each mock response | `InstagramRateLimitHandler` correctly parses headers |
| InstagramSenderApi delays calls after `CallCountPct` crosses 80% | `InstagramThrottleGuard` proactive throttle |
| No HTTP call made when `BlockedUntilUtc` is in the future | `InstagramThrottleGuard` hard-block check |
| Polly retries 3× with delay matching `EstimatedTimeToRegainAccessMinutes` | `InstagramResiliencePipeline` retry delay logic |
| Tenant B circuit-broken; Tenant A continues at full speed | Per-tenant circuit breaker isolation |
| After `BlockedUntilUtc` elapses, jobs resume automatically | `SendQueueWorker` re-queue + auto-recovery |
| SQL `BlockedUntilUtc` is set after first blocked response | `InstagramRateLimitHandler` → `TenantRateLimitService.RecordUsageAsync` |

### NuGet Packages

| Package | Purpose |
|---|---|
| `Microsoft.AspNetCore.App` | Minimal API host — **no other dependencies needed** |
