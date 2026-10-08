# Integration Plan — Rate-Limiting Layer into IGAutopilot (concrete, code-verified)

**Status: PLAN ONLY — no code changes.**
Based on an actual scan of `TMExtensions\IGAutopilot\Codebase\src` (2026-07-07):
`IGAutopilot.Ingest.Api`, `IGAutopilot.Worker`, `IGAutopilot.Infrastructure` (plus `Admin.Api`
and `Core` where they intersect). All file/line references verified against that code.

> **Re-verified 2026-07-31** against TMExtensions `e6010d164` (TK-8475 "GUID key restructure +
> drop EF Core migrations") plus the staged TK-8475 phase-2 working tree. IGAutopilot moved
> materially in the interim: **net10.0**, **Flyway owns the schema (EF migrations deleted)**,
> `[meta.ig]` schema + `Id`/`UniqueId` key split, house audit columns via a `SaveChangesInterceptor`,
> per-env config moved to `Config\{env}.appsettings.json`, `Worker.cs` → `Workers\Worker.cs`, and
> **the built-in .NET rate limiter is now in use in both APIs**. Line references, the DB items
> (O2/O2a) and the framework target below are updated accordingly; two new deltas (**O12**, **I12**)
> and the cross-workstream reconciliation are in [§7](#7-drift-re-check-2026-07-31).
>
> **Re-verified 2026-08-06** against Meta's [rate-limiting doc](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/)
> re-read in full **plus** the IGAutopilot send path. Structural finding: they send with a **Page
> Access Token**, so **BUC rate limits apply and Platform limits do not** — this invalidates the
> stated rationale of two existing items and exposes limit classes we read but never enforce. Nine
> new deltas (**O13–O21**), the per-account limit map, the header-semantics table and the code
> evidence are in [§8](#8-buc-correctness-pass-2026-08-06). No IGAutopilot code was touched.

---

## 1. What IGAutopilot has today (as scanned)

### Outbound send path (Worker → Meta)

```
RabbitMQ (comment/message queues)
  → Workers\Worker.cs   per-type consumer loops (MessageWorkerCount=200, CommentWorkerCount=10)
                        → IInstagramService.SendMessageAsync(accountId, recipientId, msg)
  → InstagramService.cs (Infrastructure/Services)
      :51   SendMessageAsync entry point
      :161  CheckRateLimitAsync — self-imposed 200/hour/account counter (:48 reads
            Instagram:RateLimitPerHour), atomic EF ExecuteUpdate on RateLimitTracking (well done)
      :374  SendMessageToInstagramAsync → POST graph.facebook.com/{v25.0}/me/messages, Bearer token
      :478  rate-limit detection = HTTP 429 ONLY
      :1179 GraphErrorDetails parser (message / code / error_subcode / type) — added by TK-8672;
            reuse it, do not write a second Graph error parser
  HttpClient wiring: Worker/Program.cs:149–181 (same block again in Admin.Api/Program.cs:159–181)
      AddResilienceHandler("instagram-pipeline")  Worker :154 / Admin.Api :164
        :158–161  Retry ×3 on 429 ONLY (exp backoff + jitter); predicate :161
        :172–175  CircuitBreaker on 5xx only (:175) — ONE breaker shared by ALL accounts
  On rate-limited result: Workers\Worker.cs:605–611 (comments) / :1073–1074 (messages)
      → row marked CommentStatus.RateLimited / MessageStatus.RateLimited → ACK → **never retried**
      (:545 / :983 already tag the OTel activity with instagram.rate_limited)
```

### Inbound webhook path (Meta → Ingest)

```
POST /api/webhooks/instagram → WebhooksController.ReceiveWebhook (Ingest.Api\Controllers)
  :38  Instagram:EnableSignatureVerification (fail-closed since FIX-73)
  :76  empty-body check         :83  1 MB Content-Length guard (413)
  :99  HMAC X-Hub-Signature-256 vs FbAppSecret; :137 verify helper;
       :163 CryptographicOperations.FixedTimeEquals — zero-alloc, constant-time (FIX-74),
       replay-protected (FIX-75)
  :119 IngestRouter → per-server RabbitMQ queue (igautopilot.incoming.raw)
Ingest.Api\Program.cs pipeline: :229 UseExceptionHandler … :298 UseAuthentication
  :299 UseAuthorization  :302 UseRateLimiter   ← built-in limiter, see below
  :49–61 AddRateLimiter: ONE named policy "data-deletion-status" (10/min per IP, FIX-86);
         deliberately NOT global, so the webhook hot path is untouched
  :307 GET /health   :322 GET /health/threadpool
Worker: IngestWorker.cs consumes raw envelopes → WebhookService → processors → TVP batch writer
Middleware/WebhookSignatureMiddleware.cs is fully commented out (dead code); only
InstagramAuthSettings survives in that file.
```

### Facts that shape the plan

| Fact | Consequence |
|---|---|
| All 6 projects target **net10.0** (was net8.0 at the 07-07 scan; TK-8629 moved the feature to .NET 10). Polly + `Microsoft.Extensions.Http.Resilience` already referenced, versions pinned via `packages.lock.json` | Retarget our components **net10.0**, not net8.0. Easier than planned: net10 ⊇ net9, so A6's one net8 substitution (`LoadIntoBufferAsync(CancellationToken)`) is no longer required (harmless either way). `ResiliencePipelineRegistry<T>` is available. Restore must respect the lock files — adding a package version they don't have will fail the build, so add none |
| Persistence is **EF Core as an O/RM only** — `ApplicationDbContext` with `HasDefaultSchema("meta.ig")` in **TailoredmailDB** (`ConnectionStrings:DefaultConnection`); 14 DbSets. **Flyway owns the schema**: the `Migrations/` folder and `Microsoft.EntityFrameworkCore.Design` were deleted in TK-8475, so `dotnet ef migrations add` does not work here by design | Re-home `TenantRateLimitState` as an EF entity in `[meta.ig]` + a **Flyway release/rollback pair** (O2/O2a below). `ExcludeFromMigrations()` is now unnecessary — EF has no migration pipeline left to exclude it from. No runtime `CREATE TABLE`, no new Dapper dependency |
| **The built-in .NET rate limiter is already adopted inbound**: Admin.Api has a *global* partitioned limiter (FIX-79, 300/min keyed on the `newsletterId` claim, else IP, `UseRateLimiter` after auth) and Ingest.Api has one narrow named policy (`data-deletion-status`, 10/min/IP, FIX-86). `TRD.md` §9.4 + `PROJECT_PLANNING.md` P-3 own that design | Two mechanisms now exist in the same space — **new delta I12**. Do not touch either registration; our middleware covers only the webhook path and sits *before* `UseAuthentication`/`UseRateLimiter`, so both coexist. Generic API throttling stays theirs |
| House column conformance is now enforced: `Id INT IDENTITY(1001,1)` PK + GUID `UniqueId`, `CreatedDate`/`ModifiedDate`/`CreatedBy`/`ModifiedBy` stamped by a scoped `AuditStampingInterceptor` (`IAuditableEntity`), and an inert reserved `Active` column — but **only on the 3 "A-tables"**; the 12 log/runtime/derived "Category-B" tables keep a plain `CreatedAt` and no audit columns | `TenantRateLimitState` is **Category-B** (runtime state, written by the Worker with no HTTP actor). Decision recorded in O2: keep the natural string key, no `Id`/`UniqueId`/audit columns/`Active`, do not implement `IAuditableEntity`. Pre-empts a house-style review objection |
| The three services **share one `ApplicationDbContext`** and must be rebuilt + deployed in a single window when Core/Infrastructure change (TK-8475's stated risk) | Adding our entity in P0 is exactly that kind of change: Worker + Ingest + Admin.Api ship together even though only the Worker enforces. Added to P0 exit criteria |
| A Graph error parser already exists — the **type** `GraphErrorDetails` (message / code / `error_subcode` / type / `fbtrace_id`) lives in `IGAutopilot.Core/Exceptions/GraphApiException.cs:14`, the **parser** `ParseGraphError` in `InstagramService.cs:1185` (call sites `:463`, `:1163`), surfaced to the API by TK-8706 via `ConversationsController.ToGraphErrorResponse`. OpenTelemetry is wired (`AddHttpClientInstrumentation`, `instagram.rate_limited` activity tags) | Reuse both rather than duplicating: our handler consumes `GraphErrorDetails` for the 4/17/32/613/80001/80002 classification — **do not port our `InstagramErrorResponse`**, their record already carries the `error_subcode` we need for 613/1996 — and emits usage-%/block-state/gate-wait as activity tags, not just logs. Verified 2026-08-04: they still have **no** usage-header parsing anywhere in `src` (no `X-App-Usage` / BUC match), so the observation layer remains entirely ours |
| Their **own** register now delegates the rate-limit class to this plan: `NFR-PROD-READINESS-TODO` FIX-48 scoped down, `System-Design-ToDo` + `PERF-TODO` record that **D1/O8 supersedes Spec 03 / ADR-0008** for the rate-limit case, and the secrets side-finding is canonical there as OBS Ingest **FIX-35** | The reconciliation is bilateral — [§7](#7-drift-re-check-2026-07-31) records our half so the two registers stay consistent |
| Tenant identity = `InstagramAccountId` (**Guid**) → `account.InstagramUserId` (Meta id) | Our `tenantId` string key = the Guid; the handler needs it passed via `HttpRequestMessage.Options` from `SendMessageToInstagramAsync` |
| **Multi-server**: appsettings.m1/m2/m7/m9, `IngestRouting` fan-out | SQL-backed outbound state already works cross-server; in-memory inbound counters are per-node (per-node limits until Redis) |
| Conditional DI pattern already in house style (`Instagram:TestMode`, Worker/Program.cs:121) | Our config toggles follow the exact same pattern — cheap and familiar |
| Rate-limit detection is **429-only** (`InstagramService.cs:478`) but Meta signals limits as **HTTP 400 + OAuthException codes 4/17/32/613/80001/80002** and via the `X-Business-Use-Case-Usage` header | **Today real Meta rate-limit responses are classified as generic failures**, retry never fires on them, and no header state is captured. This is the single biggest win of the integration |
| Sends go to `POST /{v}/me/messages` with a **Page Access Token in the `Authorization: Bearer` header** (`InstagramService.cs:391`, `:394`, `:429`) | **BUC limits apply; Platform limits do not** — *"requests made with system user or page access tokens are subject to Business Use Case Rate Limits"* and *"If both Platform and Business Use Case rate limits can be applied to a request, BUC rate limits will be applied."* Consequence: `X-App-Usage` is normally **absent** on this path, so the O10 app row is not a live budget — see [§8](#8-buc-correctness-pass-2026-08-06) and **O21** |
| **Private Replies are in active use**: `recipient = { comment_id }` when `PrivateReplyToCommentId` is set (`InstagramService.cs:401–412`, documented at `Core/Messaging/IgMessageRecipient.cs:14`), driven by the comment queue (`CommentWorkerCount: 10`) | Private Replies carry their **own** BUC caps — **750/hour** per account for post/reel comments, 100/s for Live comments — a limit class the components do not model. Their 200/h counter incidentally bounds it today; **O17** makes the coupling explicit |
| `GetConversationsAsync` calls `/{page-id}/conversations` (`InstagramService.cs:718`) from **Admin.Api** (`ConversationsController.cs:75`) — and does **not** pass through `CheckAndIncrementRateLimitAsync` (only `SendMessageAsync:83` does) | The Conversations API cap is **2 calls/s per account**, the tightest messaging limit, and it is called from the one service D4 leaves guard-free. **O18** amends D4 |
| The Worker is deployed to **4 production servers** (`Config\{m1,m2,m7,m9}.appsettings.json`, each `MessageWorkerCount: 200`) | SQL-backed usage state is shared correctly, but a **process-local dispatch window is not**: the per-second gate would allow up to **4× Meta's cap** (Conversations 2/s → 8/s). **O16** |
| Their self-imposed cap is **200 calls/hour per account**, SQL-backed and race-safe (`ExecuteUpdateAsync` at `:220`), **fixed** window (`ResetAt = now.AddHours(1)`), applied only in `SendMessageAsync` | Keep it — but its rationale in O6 was wrong (it does not protect an `X-App-Usage` budget). Its real value: it is the only hourly-class guard in the system, and at 200/h it sits **below** the 750/h Private Reply cap. Raising `Instagram:RateLimitPerHour` above 750 re-opens that limit — recorded in **O17** |
| `GraphErrorDetails` (`Core/Exceptions/GraphApiException.cs:14`) carries Message / Code / Subcode / Type / FbTraceId / UserTitle / UserMessage / HttpStatus — but **no `error_data.estimated_time_to_regain_access`** | A12.5's "reuse their record, don't port ours" holds for classification but is **incomplete for the block window**: the record needs one additive nullable field. **O20** |
| Rate-limited sends dead-end as a status row (`MessageStatus.RateLimited`), ACKed, never re-driven | Need a re-drive mechanism (decision D1 below) |
| One circuit breaker for all accounts (5xx-only) | Acceptable for 5xx (Meta down = down for everyone) but rate-limit isolation must be per account — our per-tenant pipeline supplies that |
| `MockInstagramService` bypasses HTTP entirely | Our `InstagramGraphMock` complements it: it exercises the real HTTP path incl. headers/error bodies — use it for integration testing the new layer |
| Ingest's **webhook path** still has no rate limiting (no global/per-IP/concurrency caps, no block list, no Retry-After) — the one `data-deletion-status` policy is deliberately scoped away from it | Our inbound layer still fills that gap; their HMAC + 1 MB guard stay |
| **No edge/WAF layer is documented anywhere** — `DeploymentPrerequisites.md`, `Setup-OPS.md` and `SECURITY-REVIEW.md` contain no mention of Cloudflare, nginx, ARR or reverse-proxy request limits; deployment is IIS via `deploy-igapps.ps1` onto per-env servers (`local`/`dev`/`qa`/`m1`/`m2`/`m7`/`m9`/`video`) | I10 and I11 are genuine open gaps on their side, not duplicates. It also settles the I11 trade-off: with no edge tier today, the middleware's `Global`/`PerIp` windows are the *only* volumetric defence, so they ship enabled rather than as a backstop |

> ⚠ Side-finding: the committed `Instagram:FbAppSecret` / `Encryption:Key` are **already tracked on
> their side as OBS Ingest FIX-35** (canonical there) — raise as a pointer to FIX-35, not as a new
> finding. Note the file moved: per-env config is now `src\IGAutopilot.Worker\Config\{env}.appsettings.json`.

---

## 2. What we integrate, component by component

Packaging decision (fits house style — no new solution plumbing):
- **Outbound components → `IGAutopilot.Infrastructure\Services\RateLimiting\`** (shared by Worker *and* Admin.Api, which registers the same `InstagramService` for posts/stories calls that also consume quota).
- **Inbound components → `IGAutopilot.Ingest.Api\RateLimit\`** (used only there).
- Components come from the `RateLimit` solution, retargeted **net10.0**, namespaces renamed to `IGAutopilot.*`, adding no NuGet package they don't already carry (`packages.lock.json` is pinned). The simulators (`InstagramGraphMock`, `WebhookTrafficSimulator`) stay in the RateLimit solution as the test harness.

### 2.1 Outbound — exact insertion points

| # | Where (file:line) | Change |
|---|---|---|
| O1 | `Infrastructure` (new) | Bring over: `InstagramRateLimitHandler`, `InstagramResiliencePipeline`, `InstagramThrottleGuard`, `TenantRateLimitService`, `TenantBlockedException`, models (`AppUsageHeaders`, `BucUsageEntry`, `InstagramErrorResponse`, `TenantRateLimitState`) |
| O2 | `ApplicationDbContext.cs` (+ Flyway pair, O2a) | `TenantRateLimitState` entity in the default `[meta.ig]` schema. Repository reimplemented on **EF via `IDbContextFactory<ApplicationDbContext>`** (our services are singletons; their DbContext is scoped) — replaces the Dapper repo and the runtime `EnsureTableExistsAsync`. **Key: keep our `NVARCHAR(128)` natural key**, not their `Id`/`UniqueId` pattern — it must also hold the synthetic `app:{FbAppId}` budget row (O10), so it is deliberately *not* an FK to `InstagramAccounts`. **Category-B table** (runtime state, Worker-written, no HTTP actor): no `Id`/`UniqueId`, no `CreatedBy`/`ModifiedBy`, no reserved `Active`, does **not** implement `IAuditableEntity` — so `AuditStampingInterceptor` never touches it. `ExcludeFromMigrations()` is not needed (no EF migration pipeline exists post-TK-8475) |
| O2a | `Codebase\SQLDB\ReleaseScripts\` + `RollbackScripts\TailoredMailDB\` | **Flyway release + rollback pair** per the house `flyway-release-script` skill: `V{yyyy.MM.dd.HHmm}__TK-XXXX_Create_TenantRateLimitState.sql` and the matching `R{same-stamp}__…sql`. Targets **TailoredmailDB**, schema `[meta.ig]`. Needs a ticket number allocated. Our existing draft (`docs/release-scripts/001_Create_TenantRateLimitState.sql`) has the right table shape but the **wrong name, schema and process** — reshape it and **write the missing rollback script** (a V without an R is not deployable here). Any sproc we ever add follows the `Instagram_*` convention |
| O3 | `Worker\Program.cs:149` (and `Admin.Api\Program.cs:159`) | On the existing `AddHttpClient<IInstagramService, InstagramService>` chain: add `.AddHttpMessageHandler<InstagramRateLimitHandler>()`. **Keep the handler on unconditionally** — it only *observes* (parses headers, persists state) and gives immediate visibility even before enforcement is enabled |
| O4 | Same block, `AddResilienceHandler("instagram-pipeline")` (Worker `Program.cs:154–181`, retry predicate at `:161`, breaker predicate at `:175`; Admin.Api `:164–181`) | When `RateLimiting:Outbound:Enabled`: keep their **5xx circuit breaker** exactly as is (shared breaker is right for "Meta is down"), but **remove their 429-only retry** — retries move to the per-account pipeline (O5) which understands 400+code 4/17/32/613/80001/80002, honours `estimated_time_to_regain_access` (≤ 2 min inline), and never retries hard blocks. Flag off → block unchanged. **Coordinate with FIX-48**: it is scheduled to *add* 5xx/network/timeout retry + 401/code-190 token refresh to this same block, so agree the split before either lands or the result is two retry layers |
| O5 | `InstagramService.SendMessageToInstagramAsync` (`:374`) | Set the account id on `request.Options` (so the handler can key state); when enabled, execute the send inside the per-account pipeline from `ResiliencePipelineRegistry<string>` (fresh request per attempt — note current code builds the request once; the per-attempt factory pattern from our `InstagramClient` transfers directly). **Fix the real defect at `:478`** — `rateLimited` is `StatusCode == TooManyRequests` only, so 400+code 4/17/32/613/80001/80002 is classified as a generic failure; classify via the existing `GraphErrorDetails` parser (`:1179+`) rather than adding a second parser |
| O6 | `InstagramService.SendMessageAsync` (`:51`) | When enabled, call `InstagramThrottleGuard.EnforceAsync(accountId)` **before** the send: proactive delay ≥ 80 % usage; `TenantBlockedException` when `BlockedUntilUtc` is active → return `InstagramSendResult(RateLimitExceeded: true, RetryAfter: …)` **without any HTTP call**. See **O12** — the guard must not park a RabbitMQ consumer slot for a long delay. Their existing 200/h `RateLimitTracking` counter stays as a complementary self-imposed cap (`:161` check, `:48` limit, atomic increment at `:220`, called from `SendMessageAsync:83`) — ~~it protects the app-level `X-App-Usage` budget~~ **corrected 2026-08-06:** it protects nothing at app level (Page token ⇒ no Platform limit); its actual value is that it is the system's only **hourly-class** guard and, at 200/h, sits below the 750/h Private Reply cap (**O17**). Ours tracks Meta's real per-account BUC state |
| O7 | `IInstagramService.cs:95–99` (`InstagramSendResult`) | Additive, non-breaking: append `TimeSpan? RetryAfter = null` so callers learn *when* to retry, not just that they can't. Note it is a **positional `record`** whose 4th member `RateLimitExceeded = false` already exists — append as the 5th parameter so existing positional call sites keep compiling |
| O8 | `Workers\Worker.cs:605–611` (comments) and `:1073–1074` (messages) | Today `RateLimited` is terminal. Re-drive per decision **D1** (scheduled re-drive worker that re-queues rows whose account's `BlockedUntilUtc` has passed — no broker changes, uses existing status columns). Their register already records that **D1/O8 supersedes Spec 03 / ADR-0008 for the rate-limit case**, while the DLX topology stays for non-rate-limit redelivery (FIX-53) — see [§7](#7-drift-re-check-2026-07-31) |

Rate-limit **detection** fix that falls out automatically: the handler flags 400-with-code-4/17/32/613/80001/80002 *and* 429, populating `RateLimitExceeded` correctly for the first time (today `:444` only checks 429, so real Meta limit responses take the generic-failure branch).

### 2.2 Inbound — exact insertion points

| # | Where | Change |
|---|---|---|
| I1 | `Ingest.Api\RateLimit\` (new) | Bring over: `InboundRateLimitOptions`, `IRateLimitStore` + `InMemoryRateLimitStore`, `LimitDecision`, `InboundRateLimitPipeline`, `InboundRateLimitMiddleware` |
| I2 | `Ingest.Api\Program.cs`, before `var app = builder.Build()` — alongside the existing `AddRateLimiter` block at `:49–61` | Bind options + register store/service (singletons). **Leave their `AddRateLimiter` untouched** (I12) |
| I3 | `Ingest.Api\Program.cs`, before `UseAuthentication` (`:298`) — therefore also before `UseAuthorization` (`:299`) and their `UseRateLimiter` (`:302`) | `app.UseMiddleware<InboundRateLimitMiddleware>()` when `RateLimiting:Inbound:Enabled`. Ordering is deliberate: the webhook path is anonymous, so there is nothing to gain from running after auth, and cheap counters should reject before auth/HMAC work happens. Their `UseRateLimiter` keeps serving the `data-deletion-status` policy downstream — both run, on disjoint paths. Middleware enhancement needed: **`ExcludedPaths`** option covering `/health`, `/health/threadpool`, swagger, and the GET `hub.challenge` verification handshake (GETs must never be blocked or Meta re-verification breaks) |
| I4 | HMAC | **Disable our signature check** (`HmacValidationEnabled: false`). Their `Ingest.Api\Controllers\WebhooksController.cs` HMAC (`:99` header, `:137` verify, `:163` `FixedTimeEquals`) stays the single implementation — since the 07-07 scan it has also been hardened to fail-closed (FIX-73), constant-time (FIX-74) and replay-protected (FIX-75), which strengthens this decision. Consequence: ordering becomes rate-limit-before-HMAC — the right trade for a DDoS-facing endpoint (counters are cheaper than HMAC+body-read under attack). No `EnableBuffering` needed since we skip body reads |
| I5 | Payload guards | Keep both: our middleware's 411/413 pre-checks run first (cheap header checks); their in-controller checks (`:76`/`:83`) remain as second line. Zero conflict |
| I6 | Client identity | Meta sends no `X-Api-Key`/`X-Webhook-Source` → clientId falls back to IP, making per-client ≡ per-IP for real traffic. That's fine; concurrency + global still add value. `TrustForwardedFor` per decision **D3** (IIS/ARR sub-path deployment implies a proxy sets XFF) |
| I7 | Thresholds | Do **not** ship our demo defaults. Meta delivers webhooks in bursts from few egress IPs — `PerIpLimitPerMinute: 60` would throttle legitimate Meta traffic. Starting point per decision **D2**: Global 5000/min, per-IP 2000/min, concurrency 100, informed by a week of `X-RateLimit-Remaining` observation with `Enabled:false` + counters-in-log-only mode |
| I8 | Multi-node ingest | `IngestRouting` implies possibly N ingest nodes → in-memory counters are per-node (limits are ×N). Acceptable initially (document); `RedisRateLimitStore : IRateLimitStore` is the one-line-DI upgrade when needed |

### 2.3 Configuration (single new section, both apps)

Per-env config moved in the staged TK-8475 phase-2 work: values go into
`src\<Project>\Config\{local,dev,qa,m1,m2,m7,m9,video}.appsettings.json`, which
`deploy-igapps.ps1` swaps in per server — so this section is added to **every** env file for the
Worker and Ingest (plus Admin.Api for the observe-only handler), not to one `appsettings.json`.
`Deliverables\CONFIGURATION.md` is updated in the same change.

```json
"RateLimiting": {
  "Outbound": {                       // Worker + Admin.Api
    "Enabled": false,                 // master: guard + per-account pipeline + retry rewire
    "ProactiveThrottleEnabled": true, // 80 % header-based slowdown (sub-flag)
    "ThrottleThresholdPct": 80,       // O13 tier 1 — start spreading
    "NearStopThresholdPct": 95,       // O13 tier 2 — long delay
    "HardStopThresholdPct": 100,      // O13 tier 3 — stop calling (Meta's explicit guidance)
    "EnforceTimeAndCpuUsage": true,   // O13: throttle on max(call_count, total_time, total_cputime)
    "BucWindowHours": 24,             // O14: real BUC window; was hard-coded to 1 h
    "CallsPerPercentUnit": 48,        // O14: was a magic number inside the delay formula
    "BlockBufferMinutes": 1,
    "BlockBufferSeconds": 0,          // O19: set >0 for sub-minute blocks (Retry-After in seconds)
    "MaxInlineRetryMinutes": 2,
    "MaxInlineDelaySeconds": 5,       // O12: beyond this, shed instead of parking a consumer slot
    "PerSecondDispatchLimit": 100,                 // O9 sliding-window log — text/links/reactions/stickers
    "PerSecondMediaDispatchLimit": 10,             // audio/video sends (Meta's own figure)
    "PerSecondConversationsDispatchLimit": 2,      // Conversations API — O18: unguarded in Admin.Api
    "PerSecondPrivateReplyLiveLimit": 100,         // O17: Private Reply on IG Live comments
    "PrivateReplyPerHourLimit": 750,               // O17: Private Reply on post/reel comments
    "PerSecondUnclassifiedDispatchLimit": 2,       // unrecognised endpoint → tightest cap
    "DispatchGateScope": "Node",      // O16: Node = per-process windows (÷ node count!); Cluster = shared store
    "AppBlockMaxMinutes": 60,         // O21: cap the blast radius of a fleet-wide app block
    "AppId": ""                       // O10 app row key app:{FbAppId} — blocks only; see the O10 correction
  },
  "Inbound": {                        // Ingest.Api
    "Enabled": false,                 // master: whole middleware
    "HmacValidationEnabled": false,   // stays false — controller owns HMAC
    "TrustForwardedFor": true,        // confirmed: Ingest.Api sits behind IIS/ARR (D3)
    "ExcludedPaths": [ "/health", "/swagger" ],
    "GlobalLimitPerMinute": 5000, "PerIpLimitPerMinute": 2000,
    "MaxConcurrencyPerClient": 100, "MaxPayloadBytes": 1048576,
    "BlockedIps": [], "AllowedIps": []
  }
}
```

Toggle cost: **low, ~a day including tests** — conditional DI is already their pattern
(`Instagram:TestMode`), and both flows hang off single seams. Read via `IOptionsMonitor` so
inbound flags flip on config reload without restart; outbound `Enabled` needs service restart
(HttpClient pipeline is built once) — acceptable, same as their TestMode.

---

## 3. Sequenced rollout

| Phase | Content | Exit criteria |
|---|---|---|
| P0 | Decisions D1–D4 signed off; ticket number allocated for the Flyway pair; retarget our components **net10.0** + namespace rename; EF entity (Category-B, `[meta.ig]`) + **Flyway V/R pair** (O2a) | Solution builds with no added NuGet versions; V script applied to a dev DB **and R script verified to return it cleanly**; Worker + Ingest + Admin.Api rebuilt together (shared `DbContext`) |
| P1 | **Observe-only**: O1–O3 merged (handler on, everything else off), I1–I3 merged with `Enabled:false` | Per-account usage % visible in `TenantRateLimitState` from live traffic; zero behaviour change confirmed |
| P2 | Outbound enforcement in staging: O4–O7 behind flag; drive with `InstagramGraphMock` scenarios (`GradualApproach`, `SuddenBlock`, `MultiTenantMix`, `RecoveryTest`) pointed at via `Instagram:GraphApiUrl` | Blocked account pauses, others unaffected; recovery automatic; no double-retry (their 429 retry removed under flag) |
| P3 | Re-drive worker (O8/D1); update **ADR-0008** to "superseded for the rate-limit case" and close the Spec 03 reconciliation note in `System-Design-ToDo.md` | `RateLimited` rows re-sent after block expiry; no double-sends (their ResponseMessageId checkpoint already guards this); exactly one re-drive mechanism exists for rate limits |
| P4 | Inbound enforcement: thresholds from P1 observation, `WebhookTrafficSimulator` scenarios against staging ingest | Attack scenarios rejected with correct codes; steady Meta-shaped traffic 100 % passed |
| P5 | Production: enable inbound first (monitor 429/403 rates ≈ 0 for legit traffic), then outbound proactive throttle for one pilot account, then all | Rollback at any step = flag off |

---

## 4. Decisions (confirmed 2026-07-07)

| # | Decision | Resolution |
|---|---|---|
| **D1** | Re-driving rate-limited sends (today they dead-end as status rows) | ✅ **`RateLimitRetryWorker`** in `IGAutopilot.Worker`: periodically scans `Status=RateLimited` rows whose account is no longer blocked, re-enqueues to the existing processing path. No broker changes; bounded attempts; the `ResponseMessageId` checkpoint prevents double-sends. Becomes Phase P3 |
| **D2** | Inbound thresholds for Meta-shaped traffic | ✅ **Observe first**: P1 ships with `Enabled:false` + counters in observe/log-only mode for ~a week; thresholds set from measured peaks ×5 headroom |
| **D3** | Ingest topology | ✅ **Behind IIS/ARR proxy, single node** → `TrustForwardedFor: true` (proxy is the trusted hop); in-memory counters are exact; **Redis store deferred** (I8 becomes a documented future note, not a work item) |
| **D4** | Admin.Api scope | ✅ **Observe-only**: Admin.Api gets the header-parsing handler only (records shared per-account state, no enforcement). Its calls benefit indirectly since Worker respects the shared state; guard added later only if fetch volume warrants. **⚠ Amended 2026-08-06 — reopen at P2 review:** Admin.Api is the *only* caller of `/{page-id}/conversations`, whose cap is **2 calls/s per account** and which bypasses even their own 200/h counter. "Fetch volume warrants it" is now a measurable condition, not a hypothetical — see **O18** |

---

## 5. Coverage assessment (July 2026 Meta behaviour) & plan deltas

The layer is **both preventive and reactive** by design:

- **Prevention (Layer 1):** every response's `X-App-Usage` / `X-Business-Use-Case-Usage` headers
  are recorded per Instagram account; from 80 % usage the throttle guard *spreads* the remaining
  quota so the wall is never hit. This runs per **Instagram account** (`InstagramAccountId` is the
  state key), not per workspace/tenant.
- **Reaction (Layer 2):** if a limit is hit anyway (shared app budget, external consumers of the
  same account, Meta tightening limits), the error/ETA is parsed, `BlockedUntilUtc` is persisted,
  the account's sending pauses, and sends re-drive after expiry. **No polling while blocked** —
  Meta's guidance is that continued calls extend the cooldown, so resumption is time-based
  (ETA + 1 min buffer) and the next real response re-syncs the true state.

Honest gap list — limit classes originally not covered, added as plan items.
**Status update 2026-07-07: O9, O10 and O11 are IMPLEMENTED and live-verified in the RateLimit
workspace** (components + mock scenarios `RetryAfter429` / `SharedAppBudget`; evidence in
`docs/DEMO.md` §4) — they transfer into IGAutopilot automatically with O1. I9/I10/I11 remain
deployment/rollout items executed during P4/P5. **Added 2026-07-31 from the drift re-check:
O12 (a workspace code change, Part A) and I12 (a positioning/reconciliation item).**

| # | Gap (edge case) | Delta work item |
|---|---|---|
| O9 | **Per-second messaging caps** (100 calls/s/account text, 10/s audio-video, 2/s conversations). Usage headers reflect budget windows, not instantaneous rates — a fast worker fleet can breach the per-second cap while headers still read 5 % | Client-side **dispatch cap per account** (sliding-window log; a token bucket until Oct 2026) in the send path (defaults 100/s text, configurable). This is *prevention* for the per-second class; the reactive path (error code 17/613) remains the safety net |
| O10 | **App-level `X-App-Usage` is a shared budget** (200 × DAU/hour across ALL accounts on the FB app), but current state is per-account only — one account observing 90 % app usage doesn't slow the others even though the budget is shared | Record `X-App-Usage` under a separate **global state row** (key `app:{FbAppId}`); throttle guard checks `max(accountPct, appPct)` before every send. **Corrected 2026-08-06:** the *percentage* half of this is **inert on the send path** — Page tokens mean BUC replaces Platform limits, so `X-App-Usage` is normally absent and the row stays at 0. Keep the code (correct if a User-token path ever appears, and harmless), but the app row's live purpose is now only to carry **app-flagged blocks** (613 / 613-1996). Do not present it as active app-budget coverage. See **O21** |
| O11 | Standard **`Retry-After` header on 429** is not parsed (only the error-body ETA and BUC header are) | Handler additionally honours `Retry-After` (seconds/date forms) as a block source |
| O12 | **Our guard waits on the caller's thread** — `InstagramThrottleGuard.EnforceAsync` does `await Task.Delay(delay)` for the proactive throttle (`InstagramThrottleGuard.cs:50`) and then `PerSecondDispatchGate.WaitAsync`. In our own solution the caller is a `Channel` consumer, so parking it is free. **In IGAutopilot the caller is a RabbitMQ consumer holding a prefetched, unacked message** — parking it is precisely the failure ADR-0008 was written to eliminate (Polly's 14 s inline sleeps × 200 workers locking the fleet). Our delays can exceed that: the A2 shared-budget demo measured a **9,375 ms** proactive delay | **Part A code change (do before P2, in the RateLimit workspace):** add `MaxInlineDelaySeconds` (default 5). If the computed proactive delay *or* the dispatch-gate wait exceeds it, do not sleep — throw/return the block outcome immediately so the send becomes `RateLimitExceeded` + `RetryAfter` → `Status=RateLimited` → the O8 re-drive worker picks it up after expiry. Short waits stay inline (cheaper than a DB round-trip through the re-drive path). This makes O6 + O8 the same mechanism at two timescales and keeps consumer slots free, satisfying ADR-0008's intent without its DLX topology |
| O13 | **We read three usage dimensions and enforce one.** `call_count`, `total_time` and `total_cputime` are all persisted, but [`TenantRateLimitService.cs:108`](../InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L108) computes `effectivePct` from `MaxCallCountPct` alone — and Meta states *"When `total_cputime` reaches 100, calls may be throttled"* / *"When `total_time` reaches 100…"*. Worse, at 100 % the method still returns only a **delay clamped to 60 s** ([`:118`](../InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L118)) and keeps calling, against Meta's *"When the limit has been reached, stop making API calls"* | **Part A.** Extract the decision into an `IThrottlePolicy` (`Decide(usage, appUsage) → Pass \| Delay(t) \| Stop`) and leave `TenantRateLimitService` as the state facade it already is — **SRP**: the service caches and persists, the policy decides. Then inside the policy: `effectivePct = max(callCount, totalTime, totalCpu)` across both rows, and tiered outcomes — `≥ ThrottleThresholdPct` delay, `≥ NearStopThresholdPct` (95) long delay, `≥ HardStopThresholdPct` (100) **`TenantBlockedException`, no HTTP call**. New tiers/strategies arrive as another `IThrottlePolicy` (**OCP**) instead of more branches in the service, and the policy is unit-testable with no DB (**DIP** — it takes state, not a repository) |
| O14 | **The pacing horizon is wrong by 24×.** The spread formula is `3_600_000 / (remainingPct × 48)` — a hard-coded **one-hour** window. Every BUC budget on our surface is **24 h** (`instagram` = 4800 × impressions/24 h; `messenger` = 200 × engaged users/24 h). At 80 % the current formula permits ≈960 calls/hour, i.e. it spends the remaining 20 % of a 24-hour budget inside an hour and then falls back on the reactive block. The only hourly BUC limit we touch is Private Replies (**O17**) | **Part A.** Move the window and the magic `48` into `OutboundRateLimitOptions` (`BucWindowHours: 24`, `CallsPerPercentUnit`) and let the policy from O13 pick the window **per BUC type** (24 h for `instagram`/`messenger`, 1 h for the private-reply class). Honest limitation to state in the spec: Meta reports no time-remaining-in-window, so pacing is necessarily an estimate — which is exactly why the O13 hard stop at 100 % matters more than a finer formula |
| O15 | **The BUC `type` is parsed and thrown away.** [`InstagramRateLimitHandler.cs:115–122`](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L115-L122) folds every business-id and every `type` into one `Math.Max`. Two costs: we cannot say *which* budget is hot (`instagram` vs `messenger`), and a `messenger`-type block stops `instagram`-type calls for that account and vice versa — **over-blocking is lost deliverability**, not a safe default | **Part A.** Make the state key `{accountId}` + `{bucType}` (pipe-joined) rather than adding columns: one row shape, `ITenantRateLimitRepository` unchanged, and the `app:{AppId}` row keeps working as a degenerate case. `GetThrottleDelayAsync` then takes the max across the types that apply to *this* dispatch class. Emit `type` on the OTel tags from the B-P2 telemetry item so "which budget is hot" is answerable in production |
| O16 | **The per-second gate is process-local, and the Worker runs on 4 servers.** `PerSecondDispatchGate` holds windows in a `ConcurrentDictionary` ([`:52`](../InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs#L52)). With `m1`/`m2`/`m7`/`m9` each running `MessageWorkerCount: 200`, the effective cap is **up to 4× Meta's** — the 2/s Conversations cap becomes 8/s, which is the cap most likely to be breached. SQL shares the *percentages*, never the buckets | **Part A seam, Part B deployment decision.** Put the gate behind `IDispatchRateLimiter` (**DIP**) with the current class as `InMemoryDispatchRateLimiter`, exactly mirroring the inbound `IRateLimitStore` seam that already exists — then a cluster-wide implementation is a one-line DI swap, not a rewrite. Add `DispatchGateScope: Node \| Cluster` to make the ×N explicit in config rather than implicit in the deployment. Interim mitigation if a distributed limiter is deferred: divide the configured caps by the node count and record the division in `CONFIGURATION.md`. Coordinate with FIX-52/FIX-62 consumer sizing — the same number is being set twice otherwise |
| O17 | **The Private Reply limit class is not modelled at all.** They send private replies (`recipient = { comment_id }`, `InstagramService.cs:401–412`), which carry **750 calls/hour per account** for post/reel comments and 100/s for Live comments. `DispatchClassifier` sees `/me/messages` and labels them `TextSend` at 100/s ([`DispatchClass.cs:53–56`](../InstagramSenderApi/Instagram/Services/DispatchClass.cs#L53-L56)); nothing enforces the hourly figure | **Part A.** Add `DispatchClass.PrivateReply`, detected from `recipient.comment_id` in the payload — the classifier already reads the payload for the audio/video split, so this is the same mechanism. Replace the `static DispatchClassifier` with `IDispatchClassifier` (**OCP/DIP**) so IGAutopilot can substitute a classifier that knows their payload shape without editing ours, and so it is testable in isolation. Hourly enforcement rides on the same `IDispatchRateLimiter` from O16 with a 3600 s window. **Record the coupling:** their 200/h counter currently keeps this limit unreachable, so raising `Instagram:RateLimitPerHour` above 750 without O17 in place re-opens it |
| O18 | **The tightest cap is unguarded in the service that uses it.** `GetConversationsAsync` (`InstagramService.cs:718`, called from `Admin.Api ConversationsController.cs:75`) hits `/{page-id}/conversations` — **2 calls/s per account** — and bypasses even their own 200/h counter, which only `SendMessageAsync:83` calls. **D4** deliberately gives Admin.Api the observe-only handler and no guard | **Amends D4, Part B.** Either (a) extend Admin.Api to the full guard for the `Conversations` dispatch class only — the guard is already class-aware, so this is configuration plus one `EnforceAsync` call, not new machinery — or (b) accept the risk explicitly in writing, given interactive fetch volume is low and one operator refreshing a list twice a second is the realistic worst case. Recommend (a): the 2/s cap leaves no headroom for a UI that polls. Decide at P2 review; either way D4 stops being silent about it |
| O19 | **Block windows are minute-granular.** `RetryAfterMinutesFromHeader` ceils to minutes ([`InstagramRateLimitHandler.cs:239–247`](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L239-L247)), so `Retry-After: 5` (seconds) becomes a 1-minute block, +1-minute buffer = **a 2-minute stall for a 5-second cooldown**. `BlockedUntilUtc` is a `DateTime` and already carries seconds — only the service API is lossy | **Part A, small.** Carry `TimeSpan` end-to-end (`RecordUsageAsync(..., TimeSpan? blockFor)`), keep the minute-based ETA path unchanged since Meta reports `estimated_time_to_regain_access` in whole minutes, and make the buffer proportional (`BlockBufferSeconds`, or `min(1 min, 20 % of the window)`). Interacts with **O12/A10**: a sub-minute block should shed-and-requeue rather than sleep, but the requeue delay must then be accurate to the second |
| O20 | **`GraphErrorDetails` has no ETA field.** A12.5 concluded "reuse their record, do not port `InstagramErrorResponse`". True for classification — but their record (`Core/Exceptions/GraphApiException.cs:14`) carries no `error_data.estimated_time_to_regain_access`, which is the value that sets the block window | **Part B, additive.** Add one nullable `int? EstimatedTimeToRegainAccessMinutes { get; init; }` to their `GraphErrorDetails` and populate it in `ParseGraphError` (`InstagramService.cs:1185`). It is an init-only record, so this breaks nothing and keeps a single Graph error parser in the codebase — still the right call, just one field short as written |
| O21 | **One code 4 stops every account.** `AppLevelErrorCodes = [4, 613]` blocks the shared app row and `GetThrottleDelayAsync` then throws for *every* tenant (A12.7). Code 4 is the **app-token Platform** limit — with Page tokens on the send path it should be unreachable, so a single stray occurrence (a misconfigured token, another call path, a Meta quirk) halts the entire fleet for the full window | **Part A.** Keep the mechanism — 613/613-1996 genuinely is app-wide — but bound the blast radius: `AppBlockMaxMinutes` (cap the window), and require **either** 613 **or** repeated code-4 evidence within a short window before a global block is set. Log the decision at `Warning` with the count that triggered it, so a fleet-wide stop is never silent |
| I9 | **Meta webhook retry semantics**: if the ingest endpoint 429s/5xxes Meta for a prolonged period, Meta backs off and can eventually **disable the webhook subscription** — over-throttling legit Meta traffic is worse than under-throttling | (a) `AllowedIps` seeded with Meta's published egress CIDR ranges so limits bite non-Meta traffic only; (b) observe-first thresholds (D2); (c) under extreme overload prefer fast-200-and-shed (accept + drop to queue with sampling) over 429 to Meta — documented operational guidance. **Condition added by their review (PERF-TODO, 2026-07-08):** shed mode is an **emergency-only exception** to their standing "never silently drop after 200 OK" rule and requires sampling **plus an explicit operator decision** — it is not an automatic behaviour, and normal-operation guidance is unchanged |
| I10 | Transport-level slow-request attacks (headers trickled byte-by-byte) happen **below** middleware | Not a middleware concern: covered by IIS/ARR request limits + Kestrel `MinRequestBodyDataRate` — verify configured in deployment, note in runbook |
| I11 | **Volumetric DDoS / coarse per-IP flooding is an edge-layer job**, not an app-middleware job — an in-process counter only sees requests that already reached the server, so a flood that saturates bandwidth/connections has done its damage before it can be counted | Deployment item (P5, pairs with I10): if an edge layer is available (Cloudflare, nginx, or at minimum IIS/ARR request-limit rules), configure volumetric absorption + coarse per-IP/connection limits **there** as the first line. The middleware's `Global`/`PerIp` windows then demote to a cheap backstop (keep enabled). Per-client identity limits, HMAC, `hub.challenge` GET bypass, Meta-safe shedding (I9), and observe-only rollout stay in the middleware — no edge product has that application context. The **outbound** system is unaffected: edge products protect *your* endpoints and do nothing about *you* exceeding *Instagram's* limits. Rationale: TRD §4.4. **Re-check note (2026-07-31):** no edge/WAF tier is documented in their deployment or security docs, so today the middleware windows are the only volumetric defence — they ship **enabled**, and the "demote to backstop" step happens only if an edge layer is actually introduced |
| I12 | **A second inbound mechanism now exists in-house**: the built-in .NET rate limiter — a *global* partitioned limiter in Admin.Api (FIX-79: 300/min per `newsletterId` claim, else IP, applied after auth) and a narrow `data-deletion-status` policy in Ingest.Api (FIX-86: 10/min/IP). `TRD.md` §9.4 + `PROJECT_PLANNING.md` P-3 own that design. Two mechanisms in one space invites either duplicate throttling or an argument that our middleware is redundant | **Split by capability, don't merge and don't duplicate.** (a) **Leave both registrations exactly as they are** — Admin.Api generic API throttling stays theirs (D4 already limits us to the observe-only handler there); (b) our middleware covers **only** the webhook path and runs before `UseAuthentication`, so the two never evaluate the same request; (c) the built-in limiter is the right tool for *authenticated, principal-partitioned* API throttling, and cannot do what the webhook path needs: per-client identity from `X-Webhook-Source`/`X-Api-Key`, the unconditional GET `hub.challenge` bypass, `ObserveOnly` rollout, 411/413 payload guards, Meta-safe fast-200-and-shed, block/allow lists, `Retry-After`/`X-RateLimit-*` response headers, and a swappable Redis store for multi-node counting. Same reasoning as I11, one tier down. (d) If per-route tightening under TRD §9.4 later reaches the webhook route, it must be routed through the middleware instead of stacking a second limiter on it |

Adjacent but out of scope (not rate limiting): the 24-hour messaging window / human-agent tag
eligibility rules (their `messaging_type: RESPONSE` handles the standard case), and token
expiry/refresh (their `TokenRefreshService` owns it).

---

## 6. Explicitly out of scope for the merge

- Our `SendQueueWorker`/`SendController`/`InstagramClient` — IGAutopilot already has queueing
  (RabbitMQ + Worker loops) and its own typed client path; only guard/handler/pipeline/state move.
- Our middleware HMAC and `EnableBuffering` — their controller HMAC is the keeper (I4).
- Simulators — remain in the RateLimit solution as the test harness; never deployed.
- Their `AddRateLimiter` registrations in Admin.Api and Ingest.Api — untouched (I12).
- Spec 01 / ADR-0006 per-account queue routing and Spec 02 prefetch tuning — their workstream; this
  plan only supplies the send-side budget they size against (see §7).

---

## 7. Drift re-check (2026-07-31)

IGAutopilot moved between the 07-07 scan and this re-verification. Nothing invalidates the plan's
approach; the corrections are mechanical (references, DB process, framework) plus two new deltas
(**O12**, **I12**) and one **Part A** follow-up.

### 7.1 Stale references corrected above

| Was | Now | Item(s) affected |
|---|---|---|
| net8.0 | **net10.0** (all 6 projects; lock files pinned) | §1 facts, packaging, O1, P0 |
| EF Core migrations own the schema; `ExcludeFromMigrations()` needed | **Flyway owns the schema**; `Migrations/` + `EntityFrameworkCore.Design` deleted (TK-8475). Needs a **V + R pair** under `Codebase\SQLDB\`, `[meta.ig]` schema, TailoredmailDB | O2, **O2a (new)**, P0 |
| `Worker.cs:589` / `:1057` | `Workers\Worker.cs:605–611` / `:1073–1074` | O8, §1 diagram |
| `Worker\Program.cs:131`, `:136–162`; `Admin.Api\Program.cs:131` | `:143`, `:148–175` (`:155` retry, `:162` breaker); Admin.Api `:140`/`:145` — **superseded 2026-08-06, see [§8.5a](#85a-line-references-corrected-this-pass)** | O3, O4 |
| `InstagramService` `:70`, `:397–413`, `:444` | `:51` (`SendMessageAsync`), `:374` (`SendMessageToInstagramAsync`), `:478` (429-only detection), `:161`/`:48` (200/h counter), `:1179+` (`GraphErrorDetails`) | O5, O6, §1 diagram |
| `IInstagramService.cs:70` | `:95–99`; `InstagramSendResult` is a positional record that already carries `RateLimitExceeded` | O7 |
| "WebhooksController:100–107" | `Ingest.Api\Controllers\WebhooksController.cs:99`/`:137`/`:163`, now fail-closed + replay-protected (FIX-73/74/75) | I4 |
| `Ingest.Api\Program.cs` ~`:148` / `:241` | registrations by `:49–61`; pipeline `UseAuthentication :298`, `UseAuthorization :299`, `UseRateLimiter :302` | I2, I3 |
| One `appsettings.json` per service | per-env `Config\{env}.appsettings.json` swapped by `deploy-igapps.ps1` | §2.3 |
| Secrets side-finding = new to report | duplicate of their **OBS Ingest FIX-35** (canonical there) | §1 note |

### 7.2 Their register already delegates the rate-limit class to this plan

Verified in their docs — this plan's other half of the reconciliation is now recorded here so the
two registers do not drift apart:

- **`System-Design-ToDo.md` + `PERF-TODO.md`**: *"D1 (block-aware DB re-drive) supersedes Spec 03 for
  the rate-limit case; Spec 03's DLX topology stays relevant for non-rate-limit redelivery (FIX-53).
  Do not implement both."* → our P3 now includes flipping **ADR-0008** (currently "Proposed") to
  superseded-in-part.
- **`NFR-PROD-READINESS-TODO.md` FIX-48 scoped down**: Meta rate-limit detection (400 + code
  4/17/32/613/80001/80002, `Retry-After`, per-account block state, per-second gate) is *ours* via O4/O5/O9–O11;
  FIX-48 keeps only 5xx/network/timeout retry + 401/code-190 token refresh. → O4 carries the
  coordination warning so the two don't produce two retry layers.
- **FIX-52/FIX-62 (consumer sizing)**: they intend to size `WorkerCount`/prefetch against our O9
  per-account per-second gate rather than raw RPS. O9 is the send-side budget for that; the gate's
  configured limit and their worker count must be agreed together.
- **ADR-0006 / Spec 01 (per-account routing)** is a **synergy, not a conflict**: per-account bucket
  queues make our per-account gate and per-account circuit breaker line up with the queue topology
  (one stalled account = one stalled bucket). No change needed on our side.
- **I9 shed mode** is accepted only as an emergency exception requiring sampling + an operator
  decision (recorded in the I9 row).

### 7.3 New work this re-check produced

| Item | Where it lands | Status |
|---|---|---|
| **O12** — cap inline waiting in `InstagramThrottleGuard`/`PerSecondDispatchGate` (`MaxInlineDelaySeconds`) so a long delay sheds to the re-drive path instead of parking a RabbitMQ consumer slot | **Part A** (RateLimit workspace) before P2 — see `TODO List.md` A10 | 🔲 not started |
| **O2a** — Flyway V/R pair replacing the single idempotent script; existing draft needs reshaping and a **rollback script written** | **Part A** draft, ships with P0 | 🔲 draft needs rework |
| **I12** — positioning vs the built-in .NET rate limiter; don't duplicate, don't touch their registrations | Plan/documentation + P4 review | ✅ recorded |
| Reuse `GraphErrorDetails` + emit OTel activity tags instead of a second parser / log-only | O5, and a note on the handler at O1 | ✅ recorded |
| Category-B schema decision for `TenantRateLimitState` (no int key, no audit columns, no `Active`) | O2 | ✅ recorded |

### 7.4 Checked and deliberately not changed

- **Decisions D1–D4 all still hold.** D3 (`TrustForwardedFor: true`, IIS/ARR, Redis deferred) is
  unaffected — deployment is still IIS per-env servers. D4 (Admin.Api observe-only) is *reinforced*
  by FIX-79: generic Admin API throttling is already theirs.
- **I8 (Redis store)** stays a documented future note.
- Their two test projects (`Admin.Api.Tests`, `Infrastructure.Tests`) are **orphaned** — no `.csproj`,
  not in the solution, do not compile (recorded in their own spec's "Not done"). So P2/P3 staging
  validation cannot lean on their test suite; it rides on `InstagramGraphMock` scenarios as planned.

---

## 8. BUC correctness pass (2026-08-06)

Method: Meta's [rate-limiting doc](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/),
[Instagram Platform overview](https://developers.facebook.com/docs/instagram-platform/overview) and
[Messenger Platform rate limits](https://developers.facebook.com/documentation/business-messaging/messenger-platform/overview/rate-limiting)
re-read in full, then every claim checked against the IGAutopilot send path. **No IGAutopilot code
was modified.** Outcome: nine new deltas (**O13–O21**, [§5](#5-coverage-assessment-july-2026-meta-behaviour--plan-deltas)),
three corrections to statements already in this plan (§8.6), one decision reopened (**D4** → O18).

### 8.1 Which limit set governs us — settled

They send with a Page Access Token (`InstagramService.cs:391`, `:429`). Meta:

> "requests made with application or user access tokens are subject to Platform Rate Limits, while
> requests made with system user or page access tokens are subject to Business Use Case Rate Limits."
>
> "If both Platform and Business Use Case rate limits can be applied to a request, BUC rate limits
> will be applied."

So: **BUC applies, Platform does not.** Consequences that matter here — `X-App-Usage` is normally
absent on the send path, the 200 × users/hour Platform figure never governs us, and BUC quota is
scoped **per (app, business-object) pair**, i.e. **per Instagram professional account**. There is no
shared app-wide DM budget: one hot account does not consume another's quota.

### 8.2 Limit map — app level vs per Instagram professional account

| Level | Limit | Window | Applies to us | Enforced today |
|---|---|---|---|---|
| App (Platform) | `200 × users` calls | rolling 1 h | **No** — Page token | n/a (O10 row stays at 0) |
| App (custom) | error **613**, **613/1996** "inconsistent request volume" | Meta-set, no ETA | **Yes** — the only genuinely shared ceiling | ✅ A12.1/A12.2, 15-min floor |
| Per account, BUC `instagram` | `4800 × impressions` calls | rolling **24 h** | Yes | partial — % tracked, paced against the wrong window (**O14**) |
| Per account, BUC `messenger` | `200 × engaged users` calls | rolling **24 h** | Yes | same, and indistinguishable from `instagram` (**O15**) |
| Per account, Send API | **100/s** text, links, reactions, stickers | per second | Yes — the DM path | ✅ A12.3, but process-local (**O16**) |
| Per account, Send API | **10/s** audio or video | per second | Only if they ever send media; today they send text + generic-template attachments, which are text-class | ✅ A12.3 |
| Per account, Conversations API | **2/s** | per second | Yes — Admin.Api | ✅ class exists, **not enforced there** (**O18**) |
| Per account, Private Replies | **100/s** (Live comments) · **750/hour** (post/reel comments) | per second / rolling 1 h | **Yes — in active use** | ❌ not modelled (**O17**) |
| Per account (Messenger doc) | 72,000 messages ⇒ account cannot send/display new messages | unspecified | Unclear trigger | ❌ noted only, §8.9 |
| Self-imposed | **200 calls/hour per account** (`Instagram:RateLimitPerHour`) | fixed 1 h | Theirs, SQL-backed, cluster-wide | ✅ pre-existing, keep |

Two doc conflicts to be aware of: the Instagram Platform page gives Send API text = **100/s** while
the Messenger Platform page gives **300/s for Instagram too**; we use 100 (the safe reading). And no
absolute call ceiling is ever computable — impressions and engaged users are not exposed, and the
headers report **percentages only** (already recorded as A12.4).

### 8.3 Headers — what to read, what each field means, what to do

`X-Business-Use-Case-Usage` — the header that matters for us. Shape:
`{ "<business-object-id>": [ { …one entry per BUC type… } ] }`

| Field | Meaning | Required handling | Today |
|---|---|---|---|
| `type` | which budget: `instagram`, `messenger`, `pages`, … | route the decision and the block to that type | ❌ discarded (**O15**) |
| `call_count` | % of that type's call quota consumed | pace from 80 %, **stop at 100** | partial (**O13**) |
| `total_time` | % of the time quota; throttles at 100 | same treatment as `call_count` | ❌ persisted, never enforced (**O13**) |
| `total_cputime` | % of the CPU quota; throttles at 100 | same treatment as `call_count` | ❌ persisted, never enforced (**O13**) |
| `estimated_time_to_regain_access` | **minutes** until throttling ends; > 0 ⇒ already throttled | hard block for that long, do not probe | ✅ (+1 min buffer) |
| `business-id` (map key) | owning business object | observability | ✅ ignored deliberately |

`X-App-Usage` — `call_count` / `total_time` / `total_cputime`, all %, all throttle at 100. Platform
limits only ⇒ inert on this path (O10 correction). `X-Ad-Account-Usage` — Ads API v3.3 and older,
never relevant to us.

Meta's own guidance, quoted because it is the acceptance criterion for O13: *"When the limit has been
reached, stop making API calls"*; *"Spread out queries evenly … to avoid sending traffic in spikes"*;
monitor the usage header for proximity to the limit; verify the error code to confirm which
throttling type was hit.

### 8.4 Error codes — re-confirmed, with one caveat

The A12 table (`TODO List.md` A12) is correct as written for our surface. Meta's BUC-type mapping:
80000 Ads Insights · 80001 Pages (Page/System-User token) · **80002 Instagram** · 80003 Custom
Audience · 80004 Ads Management · 80005 LeadGen · **80006 Messenger** · 80008 WhatsApp Business
Management · 80009 Catalog Management · 80014 Catalog Batch · 32 Pages (User token) · 4 app token ·
17 user token · 613 custom limit · 613/1996 inconsistent volume.

⚠ **Caveat, recorded honestly:** two passes over the same Meta page returned two different orderings
for 80005/80006. The mapping above is the one carrying an explicit BUC-type column and it agrees with
what [`InstagramRateLimitHandler.cs:29`](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L29)
already assumes. Nothing we ship depends on 80005 (LeadGen — never called), so the ambiguity is
harmless, but re-check against the live page before anyone keys behaviour on it.

### 8.5 Code evidence (IGAutopilot, verified this pass)

| Claim | Evidence |
|---|---|
| Page Access Token, `/me/messages` | `InstagramService.cs:391` (comment), `:394` (URL), `:429` (`Bearer`) |
| Private Replies in active use | `InstagramService.cs:401–412` (`recipient = { comment_id }`), `Core/Messaging/IgMessageRecipient.cs:14`, comment queue `CommentWorkerCount: 10` |
| Conversations edge called from Admin.Api | `InstagramService.cs:718`, `Admin.Api/Controllers/ConversationsController.cs:75` |
| Conversations bypasses their own counter | `CheckAndIncrementRateLimitAsync` is called **only** from `SendMessageAsync:83` |
| 200/h counter is per-account, fixed window, race-safe | `:48` (limit), `:220` (`ExecuteUpdateAsync`), `:249` (`ResetAt = now.AddHours(1)`) |
| Rate-limit detection still 429-only | `InstagramService.cs:478` |
| Retry is 429-only; breaker 5xx-only, shared | `Worker/Program.cs:155–180` |
| 4 Worker servers × 200 message workers | `Worker/Config/{m1,m2,m7,m9}.appsettings.json:15–16` |
| One Guid ↔ one Instagram account | `Core/Entities/InstagramAccount.cs:32` (`InstagramUserId`), `:52` (`FacebookPageId`) — so keying our state on `InstagramAccountId` is correct for per-account BUC caps |
| No usage-header parsing anywhere | grep for `X-App-Usage` / `Business-Use-Case` across `src` → 0 hits (re-confirmed) |
| `GraphErrorDetails` lacks the ETA field | `Core/Exceptions/GraphApiException.cs:14–40`; parser `InstagramService.cs:1185`, call sites `:463` / `:1163` (all three still exact) |

### 8.5a Line references corrected this pass

The resilience-handler block moved ~6 lines since the 07-31 re-check. All references above and in
§1/§2.1 now read:

| Item | Was (07-31) | Now (verified 08-06) |
|---|---|---|
| `AddHttpClient<IInstagramService, InstagramService>` | Worker `:143`, Admin.Api `:140` | Worker **`:149`**, Admin.Api **`:159`** |
| `AddResilienceHandler("instagram-pipeline")` | Worker `:148–175` | Worker **`:154–181`**, Admin.Api **`:164–181`** |
| Retry predicate (429-only) | `:155` | **`:161`** (`AddRetry` at `:158`) |
| Circuit-breaker predicate (5xx-only) | `:162` | **`:175`** (`AddCircuitBreaker` at `:172`) |

Unchanged and re-verified: `InstagramService.cs` `:51` `SendMessageAsync`, `:83` counter call, `:161`
`CheckRateLimitAsync`, `:220` atomic increment, `:374` `SendMessageToInstagramAsync`, `:478` 429-only
detection, `:718` conversations edge, `:1185` `ParseGraphError`; `GraphApiException.cs:14`.

### 8.6 Corrections to statements already in this plan

| Where | Was | Now |
|---|---|---|
| **O6** (§2.1) | their 200/h counter "protects the app-level `X-App-Usage` budget" | It protects nothing at app level — Page token ⇒ no Platform limit. Its real value is being the only **hourly-class** guard, and at 200/h it sits below the 750/h Private Reply cap (**O17**) |
| **O10** (§5) | app row records a live shared 200 × DAU/hour budget | The percentage half is **inert** on the send path; the row's live purpose is carrying **app-flagged blocks** (613 / 613-1996). Keep the code, drop the claim |
| **A12.5 / O5** | reuse `GraphErrorDetails`, port nothing | Still right for classification, but the record has no `estimated_time_to_regain_access` — one additive field needed (**O20**) |

### 8.7 Where the new deltas attach (phases in §3 unchanged)

| Delta | Bucket | Phase | Note |
|---|---|---|---|
| O13, O14, O19, O21 | **Part A** (RateLimit workspace) | before **P2** | Pure component work; verifiable here against `InstagramGraphMock` |
| O15, O17 | **Part A** | before **P2** | O17 needs a new mock scenario (private-reply payload + 750/h) |
| O16 | **Part A** seam + **P5** decision | seam before P2, scope choice at P5 | `IDispatchRateLimiter` now; `Cluster` implementation only if the ÷4 interim is rejected |
| O18 | **Part B** | **P2 review** | Amends D4; one `EnforceAsync` call in Admin.Api, or an explicit written acceptance |
| O20 | **Part B** | **P2** (with O5) | One additive field on their record |

Sequencing recommendation: **O13 first** (largest correctness gain per line — it enforces signals we
already collect), then O17 and O15, then O14 with the tier config, then the rest. O16's abstraction
should land with O13 so the policy and the limiter seams are introduced in one review.

### 8.8 Shape of the changes — keeping the seams SOLID

The nine deltas are deliberately expressed as **three new abstractions plus config**, not as more
branches inside existing classes:

- **`IThrottlePolicy`** — `Decide(accountState, appState, dispatchClass) → Pass | Delay(TimeSpan) | Stop`.
  Takes O13, O14 and the tiering. **SRP:** `TenantRateLimitService` goes back to being a cache +
  persistence facade; the policy owns the arithmetic. **OCP:** a new pacing strategy is a new
  implementation, not another `if` in the service. **Testable with no database** — it receives state,
  not a repository (**DIP**).
- **`IDispatchRateLimiter`** — `TryAcquireAsync(accountId, class) → TimeSpan wait`. Takes O16 and the
  hourly window O17 needs. Current `PerSecondDispatchGate` becomes `InMemoryDispatchRateLimiter`;
  a cluster-wide version is a DI swap. This mirrors the inbound `IRateLimitStore` seam exactly, so the
  solution has **one** pattern for "counter store", not two.
- **`IDispatchClassifier`** — replaces the `static DispatchClassifier`. Takes O17's private-reply
  detection and lets IGAutopilot substitute a classifier that knows their payload shape without
  editing ours (**OCP**), while making the classifier substitutable in tests (**LSP/DIP** — a static
  class is neither).

Unchanged by design: `InstagramRateLimitHandler` keeps **observing only** (parse → persist, never
decide) and `InstagramThrottleGuard` keeps **enforcing only** (ask the policy, wait or throw). That
split is what makes observe-first rollout possible, and none of O13–O21 should blur it. Every new
number goes into `OutboundRateLimitOptions` (§2.3) — no magic constants; the `48` and the
`3_600_000` in the current formula are exactly the kind of literal that hid O14 for a month.

### 8.9 Flagged, not in scope (adjacent, do not silently fix)

- **HTTP 5xx responses from Meta are dropped, not retried** — in *our* pipeline
  [`InstagramResiliencePipeline.cs:42–54`](../InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L42-L54)
  handles transport exceptions and rate-limit results only, so a 500/503 *response* reaches
  `SendQueueWorker`'s "non-retryable" branch and the DM is dropped. Their side has the mirror gap
  (`Worker/Program.cs:161` retries 429 only). **Their FIX-48 owns this** — it is scheduled to add
  5xx/network/timeout retry. Do not fix it inside the rate-limit workstream; O4's coordination note
  already flags the overlap.
- **Access token in a query string** — `GetConversationsAsync` appends `&access_token={accessToken}`
  (`InstagramService.cs:718`), where the send path correctly uses the `Authorization` header. Tokens
  in URLs land in logs and proxy traces. Security finding, not rate limiting; report alongside the
  OBS Ingest FIX-35 pointer.
- **72,000-message Instagram threshold** (Messenger Platform doc) — no representation anywhere, and
  the trigger conditions are not documented precisely enough to model. Note in the runbook.
- **Messaging-window rejections** (code 10 / subcode 2534022, outside the 24-hour window) are
  correctly *not* treated as rate limits by either side; their private-reply path already exists
  precisely because a comment reply is exempt (`InstagramService.cs:397–400`). No change.

**Sources:** [Graph API Rate Limits](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/) ·
[Instagram Platform overview](https://developers.facebook.com/docs/instagram-platform/overview) ·
[Messenger Platform rate limits](https://developers.facebook.com/documentation/business-messaging/messenger-platform/overview/rate-limiting) ·
[Graph API error handling](https://developers.facebook.com/docs/graph-api/guides/error-handling/)
