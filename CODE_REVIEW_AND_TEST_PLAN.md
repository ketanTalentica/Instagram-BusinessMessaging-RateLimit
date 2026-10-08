# Code Review, Fixes & Test Plan — RateLimit Solution

Reviewed: 2026-07-06. All fixes listed below are applied and verified by building the
solution and running both flows end-to-end (results in §5).

---

## 1. Component Inventory

### Flow 1 — Outbound (protecting us from Instagram's rate limits)

| Component | Project | Purpose |
|---|---|---|
| `SendController` | InstagramSenderApi | `POST /send` — accepts a job, enqueues it, returns 202 (or 429 when the tenant queue is full) |
| `SendQueueWorker` | InstagramSenderApi | One bounded `Channel<SendJob>` per tenant; strict per-tenant ordering; pauses/re-queues on rate limit |
| `InstagramThrottleGuard` (`IOutboundGate` x2) | InstagramSenderApi | Runs the pre-flight gates in `GateOrder` and waits out what they return — delays when usage ≥ 80 %, throws `TenantBlockedException` when hard-blocked |
| `TenantRateLimitService` | InstagramSenderApi | Facade over SQL state with a 10-s memory cache; computes throttle delay; block set/clear/preserve semantics |
| `SqlTenantRateLimitRepository` | InstagramSenderApi | Dapper/LocalDB persistence — state survives restarts; auto-creates DB + table on startup |
| `InstagramRateLimitHandler` | InstagramSenderApi | `DelegatingHandler` — parses `X-App-Usage` / `X-Business-Use-Case-Usage` headers and Graph error bodies (codes 4/17/32/613/80001/80002) after every response |
| `InstagramResiliencePipeline` | InstagramSenderApi | Polly v8: retry → circuit breaker → per-attempt timeout; retry delay honours `estimated_time_to_regain_access` |
| `InstagramClient` | InstagramSenderApi | Typed HttpClient; executes each call inside that tenant's own pipeline; fresh request per attempt |
| `GraphApiEndpoints` | InstagramGraphMock | Fake Graph API surface (`/v25.0/{tenantId}/media`, `/messages`, …) returning real-shaped headers and 400+OAuthException error bodies |
| `SimulatorControlEndpoints` | InstagramGraphMock | `/simulator/*` — configure tenants, block, reset, apply named scenarios (GradualApproach, SuddenBlock, FlappingBlock, MultiTenantMix, RecoveryTest, PerSecondRateLimit) |
| `TenantStateStore` / `TenantSimState` | InstagramGraphMock | Per-tenant mutable simulation state |

### Flow 2 — Inbound (protecting our public webhook from abuse)

| Component | Project | Purpose |
|---|---|---|
| `InboundRateLimitMiddleware` | WebhookIngestApi | Intercepts every request before routing; maps denials to 403/401/411/413/429 with `Retry-After` + `X-RateLimit-*` headers |
| `InboundRateLimitPipeline` (`IInboundRule` x8) | WebhookIngestApi | Fail-fast rule pipeline, ordered by `RuleOrder`: block list → allow list → Content-Length/size → HMAC → global window → per-IP window → per-client window → per-client concurrency |
| `InMemoryRateLimitStore` (`IRateLimitStore`) | WebhookIngestApi | Per-key counters; the mechanism comes from each call's `RateLimitPolicy` (`ICountingStrategy`: sliding window by default, fixed window, token bucket); Redis-swappable via one DI line |
| `WebhookResiliencePipeline` | WebhookIngestApi | Polly timeout + circuit breaker for downstream processing of *accepted* webhooks (wired but downstream is still a TODO) |
| `WebhookHttpSender` | WebhookTrafficSimulator | Builds valid/invalid HMAC, fake `X-Forwarded-For`, `X-Webhook-Source` |
| Scenarios (`IScenario`) | WebhookTrafficSimulator | SteadyTraffic, BurstSingleIp, DDoSMultiIp, GlobalFlood, OversizedPayload, InvalidSignature, SlowLoris, MixedAttack |
| `ResultCollector` / `ScenarioResult` | WebhookTrafficSimulator | Thread-safe aggregation: status counts, latency, Retry-After values observed |

---

## 2. Defects Found & Fixed

### Flow 1 — InstagramSenderApi

| # | Severity | Defect | Fix |
|---|---|---|---|
| 1 | **Critical** | Circuit breaker was **shared by all tenants** — `AddResilienceHandler` builds one pipeline per named client, so tenant A tripping the breaker blocked tenants B and C, contradicting the spec's isolation guarantee | `InstagramClient` now executes each call inside a per-tenant pipeline from `ResiliencePipelineRegistry<string>` (key `instagram:{tenantId}`) |
| 2 | **Critical** | Polly strategy order was inverted: `AddTimeout(15 s)` was added first = **outermost**, so the 15-s cap covered the whole retry chain — any retry honouring a 5-min `estimated_time_to_regain_access` was killed by `TimeoutRejectedException` | Order is now Retry → CircuitBreaker → Timeout; 15 s applies per attempt |
| 3 | High | `IsRateLimitedKey` was set on the request options on failure and **never cleared**, so after one rate-limited response every later success on that request was classified as a failure | Each retry attempt now builds a fresh `HttpRequestMessage` (also required because HttpClient forbids re-sending a message) |
| 4 | High | Worker **dropped** rate-limited jobs: a 400/429 result was only logged. The user requirement is "pause the queue / act on headers" | Worker now pauses the tenant loop for the advertised retry window, then re-queues the job with an incremented attempt count (max 5) |
| 5 | High | Worker re-queue used `await WriteAsync` into its **own full channel** — the processor is the channel's only reader, so a full channel would deadlock that tenant forever | Non-blocking `TryEnqueue`; controller returns 429 + `Retry-After` when a tenant queue is full instead of hanging the HTTP request |
| 6 | Medium | Any response without usage headers (e.g. a 500, or a network-level body) **wiped persisted state to zeros and cleared an active block** | Handler skips recording when nothing was observed; block semantics are now explicit: `>0` set, `0` clear (successful call proves recovery), `null` preserve |
| 7 | Medium | Rate-limit error without an ETA left the tenant unblocked → queue hammered a blocked account (Meta: "stop immediately") | 1-minute minimum block applied on any rate-limit error code |
| 8 | Medium | Long blocks were retried inline (sleeping minutes inside Polly) | Retry only sleeps for windows ≤ 2 min; longer blocks fail fast — the block is already persisted, so the guard + queue own the wait |
| 9 | Medium | Startup crashed on a fresh machine: `EnsureTableExistsAsync` created the table but **not the database** (SQL error 4060) | Creates `SenderDB` via master when missing |
| 10 | Low | Dapper calls ignored `CancellationToken` | Passed via `CommandDefinition` |
| 11 | Low | `BrokenCircuitException` / `TimeoutRejectedException` escaped to the worker as generic errors (job dropped) | `InstagramClient` maps them to failure results (`IsRateLimited = true` for open circuit → re-queue) |

### Flow 1 — InstagramGraphMock

| # | Severity | Defect | Fix |
|---|---|---|---|
| 12 | Medium | Per-tenant state mutated with **no synchronisation** — concurrent bursts corrupt counters and the per-second window | All read-modify-write now under `lock (state)` |
| 13 | Medium | A hard block (`HardBlockAtCallCountPct`) never set `BlockedUntilUtc` → tenant stayed blocked **forever** until manual reset, so InstagramSenderApi auto-recovery could never be demonstrated | Hard block now sets `BlockedUntilUtc = now + EstimatedTimeToRegainAccessMinutes` |
| 14 | Low | PerSecondRateLimit scenario returned code 80001 | Returns code 17 ("User request limit reached") per the spec |

### Flow 2 — WebhookIngestApi

| # | Severity | Defect | Fix |
|---|---|---|---|
| 15 | **Critical** | `X-Forwarded-For` was trusted **unconditionally**. On a directly-exposed endpoint any attacker rotates fake XFF values and bypasses the per-IP limit entirely — the DDoSMultiIp simulator literally exploits this | New `TrustForwardedFor` option (default **false** → socket IP). `true` only in local config so the simulator works; documented as proxy-only for production |
| 16 | High | Requests **without `Content-Length`** (chunked) skipped the 1 MB size guard, then the HMAC step buffered the entire stream → memory-exhaustion vector | Body-carrying methods without `Content-Length` are rejected 411 before any body read |
| 17 | High | Shipped config had an **empty HMAC secret**, which silently disables signature validation — the InvalidSignature scenario was passing 202s, not 401s | Matching dev secret in both configs; loud startup warning whenever HMAC is disabled |
| 18 | Medium | Two **unbounded dictionaries** keyed by attacker-controlled strings (per-key locks in the store; per-client semaphores) — rotating `X-Webhook-Source` values grows memory without limit | Store rewritten with self-pruning buckets (idle > 2 windows evicted past 10 k keys); concurrency slots pruned when idle; header-derived keys capped at 64 chars |
| 19 | Medium | The store's lock dictionary + `IMemoryCache` combo could desync (cache entry evicted while lock remains; `GetOrCreate` not atomic → two queues for one key) | Single `ConcurrentDictionary<string, Bucket>` with per-bucket lock and a removed-flag re-check |
| 20 | Low | HMAC was demanded on GET requests (health probes 401'd once a secret is set) | HMAC enforced on body methods only |

### Not changed — deliberate trade-offs to be aware of

- **Check order (HMAC before counters):** invalid-signature floods don't consume rate quota, but each one costs a body read + HMAC. The alternative (per-IP counter first) is cheaper under attack but lets bad signatures consume quota. Current order matches the spec; revisit if CPU-bound floods become a concern (a cheap pre-HMAC per-IP "suspicion" counter is the usual hybrid).
- **Counters increment before later checks deny:** a request denied at per-client still consumed one global + one per-IP slot. Acceptable and common; atomic multi-key checks need the Redis/Lua store.
- **In-memory job queue:** jobs are lost on restart. The spec already documents the outbox-pattern upgrade (§4).

---

## 3. How the Two Flows Now Behave (verified)

### Flow 1 — InstagramSenderApi vs InstagramGraphMock

```
POST /send → per-tenant channel → ThrottleGuard (delay ≥80 %, throw if blocked)
  → per-tenant Polly (retry → breaker → 15 s/attempt) → HttpClient
  → InstagramRateLimitHandler parses headers/error body → SQL + cache state
429/400 + code 4/17/32/613/80001/80002 → block persisted (ETA + 1 min buffer)
  → worker pauses that tenant's queue, re-queues job → auto-resumes after window
```

### Flow 2 — WebhookTrafficSimulator vs WebhookIngestApi

```
Request → middleware → block list → allow list → 411/413 size guards → HMAC(401)
  → global window(429) → per-IP window(429) → per-client window(429)
  → concurrency semaphore(429) → /webhook endpoint (202)
Every 429 carries Retry-After, X-RateLimit-Limit/Remaining/Reset.
```

### 3.1 July-2026 additions — verified 2026-07-07 (full commands + outputs in `docs/DEMO.md`)

| Feature | Test | Observed |
|---|---|---|
| Per-second dispatch gate (O9) | 15 sends, sender gate 4/s vs mock `PerSecondRateLimit` cap 5/s | 15/15 `Sent job`, mock `isBlocked:false`, `callCountPct:30` (exactly 15 calls), zero code-17 errors |
| `Retry-After` on 429 (O11) | mock `RetryAfter429` (429 + `Retry-After: 90`, unrecognised code — `1`, was 613 until 613 became recognised in the Aug-2026 pass) | log `HTTP 429 … Retry-After → 2 min`; block persisted +1 min buffer; queue paused 3 min, job re-queued; correctly no inline retry (3 min > 2-min cap) |
| Shared app budget (O10) | mock `SharedAppBudget` (global X-App-Usage 92 %) | fresh tenant-light's **first** send delayed 9 375 ms off the `app:local-dev-app` SQL row (own usage 5 %); row confirmed via sqlcmd |
| Inbound regression | `BurstSingleIp` 15 s, enforce mode | exactly 60 × 202 + 140 × 429, `Retry-After: 59` (unchanged from prior evidence) |
| Global window | `GlobalFlood` 32 s (960 req) | 780 × 202 + 180 × 429 at 1000/min, `X-RateLimit-Remaining: 0` |
| ExcludedPaths + GET bypass (I3 prep) | during saturated global window | `POST /health` (unsigned) → 200; `GET /anything` → 404 (routed, never throttled) |
| Observe-only mode | `BurstSingleIp` with `ObserveOnly=true` | 200 × 202 (nothing denied) + 142 × `OBSERVE ONLY: would deny Ip → 429` log lines |
| net8.0 compatibility | scratch classlib linking all transferable sources | compiles clean on net8.0 (Dapper 2.1.79, SqlClient 7.0.1, Polly.Core 8.6.5) after replacing the .NET-9-only `LoadIntoBufferAsync(ct)` overload |

---

## 4. Improvement Plan (recommended order)

1. **Durable outbox for `SendJob`** — SQL `SendJobs` table (Status: Pending/Sent/Dead), worker reads oldest-pending per tenant; survives restarts, enables dead-lettering. (The spec already flags this.)
2. ~~**Per-second dispatch cap**~~ — **DONE (2026-07-07)**: `PerSecondDispatchGate` token bucket per tenant, enforced by `InstagramThrottleGuard`; see §3.1 evidence below.
3. **`RedisRateLimitStore`** for WebhookIngestApi when it goes multi-instance (Lua `ZADD`+`ZREMRANGEBYSCORE`+`ZCARD`); the `IRateLimitStore` seam is ready.
4. **Forwarders done properly** — replace the custom XFF parsing with ASP.NET's `ForwardedHeadersMiddleware` + `KnownProxies` when the deployment topology is known.
5. **Wire `WebhookResiliencePipeline`** — `/webhook` still has a TODO where accepted payloads should be dispatched downstream through the registered timeout+breaker pipeline.
6. **Observability** — counters (`throttle_delays_total`, `blocked_tenants`, `denials_by_limit_type`) via `System.Diagnostics.Metrics`; both flows already log structured events.
7. **Config hardening** — move `HmacSecretKey` / connection string to user-secrets/Key Vault; hot-reloadable `BlockedIps` (`IOptionsMonitor`).
8. **Graceful shutdown** — worker currently drops in-flight jobs on host stop; drain channels into the outbox once #1 exists.

---

## 5. Test Plan

### A. Executed during this review (all passing)

| Test | Result |
|---|---|
| Solution builds | 0 warnings, 0 errors |
| InvalidSignature scenario (30 req, bad HMAC) | **30 × 401**, no counters consumed |
| BurstSingleIp (200 req, one IP) | **60 × 202, 140 × 429**, `Retry-After: 58–59`, correct `X-RateLimit-*` |
| SlowLoris (15 held-open req, one client) | **10 × 202, 5 × 429** (concurrency cap) |
| GradualApproach, 25 jobs to tenant-1 | Sends 1–15 full speed; **proactive delays 3.75 s → 15 s** from 80 %; call 20 → code 80001 parsed, `retryAfter=5 min`; **block persisted to SQL** (`BlockedUntilUtc` = ETA + 1 min buffer); job **re-queued** with 6-min pause, not dropped |
| Fresh-machine startup | `SenderDB` + table auto-created on LocalDB |

### A2. Automated re-run of the whole matrix

`run-all.bat outbound` / `run-all.bat inbound` (logic in `scripts/Demo.ps1`) now execute every
`docs/DEMO.md` demo unattended and assert each expected log line, printing a PASS/FAIL summary. Last
full run **2026-08-05 on Graph v25.0: 7/7 outbound, 2/2 asserting inbound, build 0 warnings.** Prefer
this over re-running the matrix by hand — it also resets state, waits out the 10-second state cache
and refuses to start unless the sender is pointed at the local mock.

### B. Manual scenario matrix (run these to demo every layer)

Flow 2 (start WebhookIngestApi with `ASPNETCORE_ENVIRONMENT=Development` for `/webhook/slow`):

| Scenario (`POST :5010/simulator/run`) | Expect |
|---|---|
| `SteadyTraffic` | all 202, zero 429 |
| `DDoSMultiIp` / `GlobalFlood` | 429s appear once global window (1000+20) fills, across all IPs |
| `OversizedPayload` | all 413, no counter consumed |
| `MixedAttack` | 401/429/202 each attributed to the right stream |
| run twice back-to-back | second run still correct after windows slide (60 s) |

Flow 1 (mock + InstagramSenderApi, Development):

| Scenario (`POST :5020/simulator/scenario`) | Expect |
|---|---|
| `SuddenBlock` | 80001 at call ~20; queue pauses; jobs re-queued; resumes after `BlockedUntilUtc` |
| `RecoveryTest` (2 min) | no HTTP call while blocked (guard throws pre-flight); auto-resume ≤ 3 min (ETA + buffer) |
| `MultiTenantMix` + jobs to a/b/c | tenant-a full speed, tenant-b throttled, tenant-c blocked — **a and b unaffected by c** (per-tenant breaker isolation) |
| `FlappingBlock` | breaker opens/half-opens; sends resume during "up" phases |
| `PerSecondRateLimit` | code 17 handled: 1-min minimum block, re-queue, recovery |
| Restart InstagramSenderApi while tenant blocked | first job after restart is *not* sent — block reloaded from SQL |

### C. Unit tests (`RateLimit.Tests`, xUnit — added with the September-2026 interface pass)

`dotnet test RateLimit.sln` — 80 tests, no external dependencies (no SQL, no sockets, no sleeping:
the store takes a `TimeProvider` and the outbound gates return their delay instead of awaiting it).
Test doubles are hand-written (`TestDoubles.cs`); no mocking library was added.

| Class | Covers | Blueprint item |
|---|---|---|
| `InMemoryRateLimitStoreTests` | each algorithm's distinguishing property (sliding ages out per request, fixed window bursts at the boundary, token bucket refills at the nominal rate and consumes nothing on denial), `ResetAt`, key isolation, 500-way concurrent hammering yields exactly `limit + burst`, policy/algorithm switching | 3 |
| `InboundPipelineTests` | rule order; every short-circuit proved by whether the store was touched (blocked IP, allow-list, 411/413, forged HMAC all reach it zero times); per-scope algorithm selection; concurrency slots | 4 |
| `ClientIdentityResolverTests` | `TrustForwardedFor` on/off, first-hop-only parsing, header precedence, 64-char truncation | 4 |
| `TenantRateLimitServiceTests` | delay thresholds and monotonicity; app-vs-account level separation (A12.8); app-level block holds an unrelated tenant; block set (+1 min buffer) / cleared on 0 / preserved on null | 1 |
| `OutboundGateTests` | gate ordering regardless of registration order; per-class and per-tenant bucket isolation; reservation stacking; `TenantBlockedException` for account and app blocks; `Enabled: false` bypass | — |
| `DispatchClassifierTests` | endpoint × payload matrix, including malformed payloads falling back to text rather than throwing | — |

**Still uncovered, and still only exercised end to end by `run-all.bat`:** blueprint items 2
(`InstagramRateLimitHandler` header/error parsing), 5 (`SendQueueWorker`) and 6 (the Polly pipeline).
Those are the three components a unit test would have to fake an `HttpMessageHandler` for.

#### Original blueprint (highest-value first)

1. **`TenantRateLimitServiceTests`** (mock repo): delay null below 80 %; delay at 80/99 %; `TenantBlockedException` when blocked; block **preserve** on `null`, **clear** on `0`, **set +1 buffer** on `>0`.
2. **`InstagramRateLimitHandlerTests`** (fake inner handler): header parsing (worst-case across App + BUC entries); error-code detection incl. 1-min default ETA; *no* recording on signal-free responses; malformed JSON headers ignored.
3. **`InMemoryRateLimitStoreTests`**: allows `limit+burst`, denies next; window slides (fake-able by injecting timestamps or a clock — consider adding `TimeProvider`); `ResetAt` correctness; parallel hammering yields exactly `limit+burst` allows; prune evicts idle keys past threshold.
4. **Pipeline tests** (mock `IRateLimitStore`, now `InboundPipelineTests`): check ordering (blocked IP short-circuits before store is touched; allow-list bypass; 411/413 before HMAC; HMAC before counters); XFF ignored when `TrustForwardedFor=false`; clientId truncation.
5. **`SendQueueWorkerTests`**: per-tenant ordering; `TryEnqueue` returns `Full` at capacity; re-queue increments attempts; drop after max attempts.
6. **Polly pipeline test** (`ResiliencePipeline` + fake handler): 429→429→200 sequence succeeds on 3rd attempt; ETA > 2 min is *not* retried inline; breaker opens after threshold and isolates by key.

Suggested layout: one xUnit project `RateLimit.Tests` referencing InstagramSenderApi + WebhookIngestApi; use `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`) for middleware-level integration tests so B-matrix scenarios also run in CI without real sockets.
