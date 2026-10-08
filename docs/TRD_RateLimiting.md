# Technical Requirements & Design Document — Instagram Rate-Limiting Solution

| | |
|---|---|
| Solution | `RateLimit.sln` (.NET 9) |
| Status | Implemented, reviewed, end-to-end verified (2026-07-06) |
| Related docs | [Outbound spec](../InstagramSenderApi/InstagramSenderApi_Outbound_RateLimit_Spec.md) · [Inbound spec](../WebhookIngestApi/WebhookIngestApi_Inbound_RateLimit_Spec.md) · [Code review & test plan](../CODE_REVIEW_AND_TEST_PLAN.md) · [Visual design map](TRD_RateLimiting.html) |

---

## 1. Business Problem

We integrate with Instagram (Meta Graph API) in two directions, and each direction has a distinct
risk we must engineer against:

1. **Outbound** — we send messages/media to Instagram on behalf of many tenants (Instagram
   professional accounts). Meta enforces per-account and per-app rate limits; exceeding them gets
   an account temporarily blocked, and continuing to call while blocked **extends** the cooldown.
   Requirement: never voluntarily exceed a limit, react correctly when limited (pause the tenant's
   queue, honour Meta's recovery window), and never let one tenant's block affect another tenant.

2. **Inbound** — Instagram delivers webhook events (comments, messages, keywords) to a public
   HTTPS endpoint. Anything public gets attacked: floods from one IP, distributed floods, oversized
   bodies, forged payloads, slow-connection exhaustion. Requirement: authenticate and rate-limit
   every request before it costs us resources, and degrade predictably (correct status codes +
   `Retry-After`) rather than falling over.

Both directions are developed against **local simulators** so the full behaviour can be proven
without touching real Instagram or exposing anything publicly.

---

## 2. Naming — Who Is Who

Projects are named so the direction and ownership are readable at a glance. Convention:
**our deployable services end in `Api`; fake external counterparts end in `Mock`/`Simulator`.**

| Project | Was | Side | Role |
|---|---|---|---|
| `InstagramSenderApi` | SenderAPI | **Ours** | Outbound sender — accepts send jobs, queues per tenant, calls the Graph API with rate-limit protection |
| `InstagramGraphMock` | InstagramMockServer | *Fake Instagram* | Impersonates the Graph API surface; returns configurable usage headers, error codes, blocks |
| `WebhookIngestApi` | WebhookProxy | **Ours** | Public inbound webhook receiver, hardened against DDoS/abuse (maps to the production "Ingest API receiver") |
| `WebhookTrafficSimulator` | WebhookSimulator | *Fake Instagram + attackers* | Fires legitimate and hostile traffic patterns at the ingest API and reports what came back |

Call graph:

```
OUTBOUND FLOW                                INBOUND FLOW
=============                                ============
[client] ──POST /send──► InstagramSenderApi  WebhookTrafficSimulator ──POST /webhook──► WebhookIngestApi
                              │  (5001)          (5010, plays Instagram+attackers)          (5002)
                              ▼ Graph calls
                        InstagramGraphMock   In production, real Instagram/Meta calls our
                              (5020)         ingest endpoint; the simulator is dev-only.
In production the sender calls
https://graph.facebook.com/v25.0/ —
one config value switches mock ↔ real.
```

---

## 3. Outbound Design — `InstagramSenderApi`

### 3.1 Request lifecycle

```
POST /send {tenantId, targetEndpoint, payload}
  → SendController: validate → SendQueueWorker.TryEnqueue
      202 enqueued · 429 + Retry-After (tenant channel full) · 503 (shutting down)
  → per-tenant bounded Channel<SendJob> (capacity 500, strict FIFO per tenant)
  → per-tenant processor loop:
      1. InstagramThrottleGuard.EnforceAsync
           usage ≥ 80 %  → sleep computed delay (spreads remaining quota)
           hard-blocked  → TenantBlockedException (no HTTP call is made at all)
      2. InstagramClient.PostAsync — executes inside THAT TENANT'S Polly pipeline:
           Retry(3, ETA-aware) → CircuitBreaker(per tenant) → Timeout(15 s/attempt)
           fresh HttpRequestMessage per attempt
      3. InstagramRateLimitHandler (DelegatingHandler, runs on every attempt's response):
           parse X-App-Usage + X-Business-Use-Case-Usage + error body
           persist worst-case usage % and block window → SQL + 10-s memory cache
      4. outcome:
           success            → next job
           rate-limited       → pause tenant loop for advertised window, re-enqueue (max 5 attempts)
           TenantBlockedException → sleep remaining block, re-enqueue
           non-retryable (404…)   → drop with error log
```

### 3.2 Business rules and their reasons

| Rule | Value | Why |
|---|---|---|
| Proactive throttle threshold | 80 % usage | Start spreading the remaining quota before the wall, so we never *cause* a block; Meta headers are percentages, 80 is the industry-common knee point |
| Block buffer | ETA + 1 minute | `estimated_time_to_regain_access` is advisory; calling at exactly ETA risks an immediate re-block, which extends the cooldown |
| Rate-limit error **without** ETA | 1-minute minimum block | Meta's guidance is "stop immediately"; without a floor the queue would hammer a blocked account |
| Inline retry cap | ETA ≤ 2 min retried in-pipeline; longer → queue owns the wait | Sleeping many minutes inside an HTTP resilience pipeline wastes a worker slot and hides state; the block is already persisted, so the guard enforces it on the next attempt |
| Block clear/preserve semantics | success → clear · rate-limit → set · anything else → preserve | A 500 with no headers must never unblock a tenant or wipe usage state to zeros; only a **successful call** proves access is regained |
| Per-tenant circuit breaker | registry key `instagram:{tenantId}` (ratio 0.8, min 3, sample 2 min, break 5 min) | Tenant isolation is a hard requirement — `AddResilienceHandler` would share one breaker across all tenants (found and fixed in review) |
| Per-tenant FIFO queue | one bounded channel per tenant | Message ordering per account; a paused/blocked tenant stalls only its own lane |
| Non-blocking enqueue | `TryWrite`, 429 on full | The processor re-enqueues into its own channel; awaiting space there deadlocks (it is the only reader). Full queue = explicit backpressure to the caller |

### 3.3 Persistence

- SQL Server (LocalDB in dev), table `TenantRateLimitState` (usage percentages, `BlockedUntilUtc`,
  `LastUpdatedUtc`); MERGE upsert; DB **and** table auto-created at startup.
- State survives restarts: a tenant blocked before a deploy is still blocked after it.
- Jobs are in-memory only — **accepted risk** for this phase; the documented upgrade is a SQL
  outbox (`SendJobs` table) which also fixes graceful-shutdown job loss.

### 3.4 `InstagramGraphMock` behaviour

Mirrors the real API surface (`/v25.0/{tenantId}/media|media_publish|messages`) so switching to
production is one `GraphApiBaseUrl` config change. Faithful details that matter:

- Rate-limit errors come back as **HTTP 400 + OAuthException body** (codes 4/17/32/613/80001/80002), *not*
  429 — matching real Graph API behaviour.
- Usage headers are attached to **every** response; `estimated_time_to_regain_access` appears in
  the BUC header when blocked.
- Blocks auto-expire (`BlockedUntilUtc`), so recovery scenarios run unattended.
- Per-tenant state is mutated under a lock (burst scenarios are concurrent).
- Control surface `/simulator/*`: configure/block/reset per tenant + named scenarios
  (`GradualApproach`, `SuddenBlock`, `FlappingBlock`, `MultiTenantMix`, `RecoveryTest`,
  `PerSecondRateLimit`, `RetryAfter429`, `SharedAppBudget`, `InstagramBucBlock`,
  `CustomRateLimit613`, `AppLevelBlock`).
- `RetryAfter429` answers 429 + `Retry-After` with error code **1** ("API Unknown"), deliberately a
  non-rate-limit code, so that scenario tests the header path and nothing else. It carried 613 until
  613 became a recognised code in the August-2026 pass.
- Unless `/simulator/app-usage` sets a store-level override, `X-App-Usage` is derived from the same
  per-tenant counter as the BUC header — so a scenario that drives one tenant to 100 % also leaves
  the shared app row at 100 % for the next demo (`docs/DEMO.md` §1 covers the reset discipline).

---

## 3.5 Meta rate-limit levels and error codes — reference, coverage and proof

Primary sources (re-read **2026-08-04**; Meta publishes no version history on these pages, so treat
the read date as the currency of everything below):

- [Graph API → Overview → Rate Limits](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/) — the Platform and Business-Use-Case error tables, the three usage metrics, and `estimated_time_to_regain_access`
- [Instagram Platform → Overview → Rate Limits](https://developers.facebook.com/docs/instagram-platform/overview#rate-limits) — the Instagram BUC formula `Calls within 24 hours = 4800 × Impressions`
- [Instagram Platform → Messaging → Sending messages](https://developers.facebook.com/docs/instagram-platform/instagram-api-with-instagram-login/messaging-api) — the per-second Send API caps

### 3.5.1 How many levels

Meta enforces at **four distinct scopes**. They are independent: passing one says nothing about the
others, which is why a single counter cannot model them.

| # | Level | Scope of the budget | Signal Meta gives us | Codes raised at this level | Where we track it |
|---|---|---|---|---|---|
| L1 | **App** | The whole Facebook app — *every* tenant shares one budget | `X-App-Usage` header (`call_count`, `total_time`, `total_cputime`) | 4, 613, 613/1996 | Global `app:{FbAppId}` row — usage *and* blocks, `InstagramRateLimitHandler` |
| L2 | **Instagram account (BUC)** | Per Instagram professional account / business asset, per use case | `X-Business-Use-Case-Usage` header, keyed by business id, incl. `estimated_time_to_regain_access` | 80002 (Instagram), 80006 (Messenger) | Per-tenant row, [:184-185](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L184-L185) |
| L3 | **User / Page token** | Per app-user pair, or per Page when the call uses a Page/System-User token | Same BUC header; distinguished only by the error code | 17, 32, 80001 | Per-tenant row (in our model one tenant = one IG account = one linked Page, so L3 collapses into L2) |
| L4 | **Per-second burst** | Per Instagram account, and different per call class | **Nothing** — no header at all | none of its own; surfaces as a 17 | `PerSecondDispatchGate` sliding-window log, [PerSecondDispatchGate.cs](../InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs) |

L4's documented caps: **Send API text/links/reactions/stickers 100/s**, **Send API audio/video 10/s**,
**Conversations API 2/s**, **Live private replies 100/s** — all per account. Each class gets its own
window, keyed by tenant **and** class, because Meta enforces them as separate limits: 100 text sends
and 2 Conversations calls in the same second are both legal. `DispatchClassifier` picks the class
from the endpoint **plus the payload** — text and audio/video sends share the `/messages` endpoint
and are distinguishable only by `message.attachment.type`.

### 3.5.2 How many codes

Meta's two tables list **18 rows** = **14 distinct error codes** (rows exceed codes because 17 and
613 each appear twice with a subcode, and 17 and 32 appear in both tables). Of those 14, **7 can
occur on the call surface we use** and all 7 are handled; the other 7 belong to products we never
call.

| Code | Sub | Level | Meta's meaning | Handled | Mocked |
|---|---|---|---|---|---|
| 4 | — | **L1 app** | App has reached its rate limit | ✅ | ✅ `AppLevelBlock` (code), `SharedAppBudget` (usage %) |
| 613 | — | **L1 app** | A custom rate limit has been reached | ✅ | ✅ `CustomRateLimit613` |
| 613 | 1996 | **L1 app** | Meta noticed inconsistent behaviour in the app's API request volume | ✅ 15-min floor | ✅ `CustomRateLimit613` |
| 17 | — | **L3 user** | The user whose token is used has reached their rate limit | ✅ | ✅ `PerSecondRateLimit` |
| 32 | — | **L3 page** | Pages API request limit, User access token | ✅ | ⚠️ `/simulator/configure` only |
| 80001 | — | **L3 page** | Page calls with a Page or System-User access token | ✅ | ✅ default + `SuddenBlock` |
| 80002 | — | **L2 account** | **Instagram BUC limit — standard IG Platform endpoints** | ✅ | ✅ `InstagramBucBlock` |
| 80006 | — | L2 account | Messenger BUC (IG DMs share the `/messages` surface) | ✅ defensive | ⚠️ `/simulator/configure` only |
| 80000 / 80003 / 80004 | 2446079 | L2 | Ads Insights / Custom Audience / Ads Management | ❌ N/A | ❌ |
| 80005 | — | L2 | LeadGen | ❌ N/A | ❌ |
| 80008 | — | L2 | WhatsApp Business Management | ❌ N/A | ❌ |
| 80009 / 80014 | — | L2 | Catalog Management / Catalog Batch | ❌ N/A | ❌ |
| 17 | 2446079 | L3 | Ads API v3.3 or older | ❌ N/A | ❌ |

The N/A rows are a deliberate exclusion, not an oversight: we call only IG media, media_publish and
messages endpoints, so an Ads/WhatsApp/Catalog/LeadGen BUC code cannot be produced by our traffic.
Adding them would mean writing block state in response to a code we can never receive.

### 3.5.3 What we implemented, why, and where

| What | Why | Where |
|---|---|---|
| Single recognition list `[4, 17, 32, 613, 80001, 80002, 80006]` | Meta signals limits as **HTTP 400 + error code**, not 429; an unrecognised code takes the generic-failure branch, so no block is persisted and Polly never retries. 80002 was the critical miss — we handled its sibling 80001 instead | [InstagramRateLimitHandler.cs:29](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L29) |
| One list feeds both layers | Polly decides purely from the `IsRateLimitedKey` flag the handler sets, so recognition can never drift between the proactive and reactive layers | set at [:139-140](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L139-L140), read at [InstagramResiliencePipeline.cs:99-101](../InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L99-L101) |
| `error_subcode` parsed | 613/1996 is not a quota reset — it is Meta flagging our *traffic shape*. It arrives with no ETA, so the ordinary 1-minute floor would retry straight back into the flag | [InstagramErrorResponse.cs:23-30](../InstagramSenderApi/Instagram/Models/InstagramErrorResponse.cs#L23-L30) |
| 15-minute block floor for 613/1996 | Chosen deliberately over the 1-minute default; Meta gives no ETA for this code, and the failure it describes is sustained volume, not a momentary spike | [InstagramRateLimitHandler.cs:31-37](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L31-L37), applied at [:134-141](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L134-L141) |
| `estimated_time_to_regain_access` treated as **minutes** | Meta documents it as "time, in minutes, until calls will no longer be throttled". Reading it as seconds would under-wait by 60× | model [BucUsageEntry.cs:11](../InstagramSenderApi/Instagram/Models/BucUsageEntry.cs#L11); +1 min safety buffer at [InstagramResiliencePipeline.cs:108](../InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L108) |
| All three usage metrics tracked, worst-case wins | Meta throttles when **any** of `call_count` / `total_time` / `total_cputime` reaches 100, so tracking only `call_count` would miss a CPU-driven throttle | [:86-88](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L86-L88) and [:106-109](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L106-L109) |
| L1 mirrored to `app:{FbAppId}`, guard throttles on `max(tenantPct, appPct)` | `X-App-Usage` is one budget for every account on the app, so a fresh tenant must still be throttled when the app budget is hot | [:190-194](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L190-L194); guard in [InstagramThrottleGuard.cs:37](../InstagramSenderApi/Instagram/Services/InstagramThrottleGuard.cs#L37) |
| L4 enforced independently of headers | Per-second caps are invisible in the usage headers — by the time a percentage moves, the burst has already been rejected | [PerSecondDispatchGate.cs](../InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs) |
| Mock returns HTTP **400** for code paths | Reproduces the real Graph API convention; a mock that answered 429 would hide exactly the classification bug this system exists to fix | [GraphApiEndpoints.cs:87-96](../InstagramGraphMock/Endpoints/GraphApiEndpoints.cs#L87-L96) |

Live evidence (2026-08-04, `InstagramGraphMock` + `InstagramSenderApi`, real LocalDB rows):

```text
code=80002, subcode=0,    retryAfter=8min  → blocked  8 min (+1 buffer) → re-queued 0:09:00 → BlockedUntilUtc 12:49:46
code=613,   subcode=1996, retryAfter=15min → blocked 15 min (+1 buffer) → re-queued 0:16:00 → BlockedUntilUtc 12:56:46
```

### 3.5.4 What the budget percentages can and cannot tell us

Meta computes the Instagram BUC allowance as **`Calls within 24 hours = 4800 × Impressions`**, and
the usage headers report **only a percentage** — never the ceiling that percentage is a fraction of.
Two consequences that constrain the whole design:

- **No absolute per-account call budget can be pre-computed.** A low-impression account has a small
  ceiling and burns through its percentage far faster than a "200 calls/hour" rule of thumb suggests.
  Any scheduler that hands out a fixed number of calls per account per hour will be wrong for exactly
  the accounts most at risk.
- **The 80 % threshold is therefore the primary defence**, not a refinement of a known budget. It is
  the only signal available before Meta starts rejecting calls, which is why the proactive layer keys
  everything off it (`ThrottleThresholdPct`, [TenantRateLimitService.cs:14](../InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L14)) and why the
  reactive layer must treat every rate-limit code as authoritative when it arrives.

### 3.5.5 Level-correctness — fixed, with the evidence

All three level defects found during the 2026-08-04 audit are fixed. Verified live against
`InstagramGraphMock` with a cleared state table; solution builds with 0 warnings.

| Was wrong | Fix | Proof |
|---|---|---|
| **App-level blocks landed on the account row.** Codes 4 / 613 / 613-1996 mean *the app* is limited, so blocking the one account that received the response left every other account calling into an app-wide limit | `AppLevelErrorCodes = [4, 613]`; the block routes to `AppStateKey`, the account row is left unblocked, and `GetThrottleDelayAsync` now throws for **any** tenant while the app row is blocked | `AppLevelBlock` scenario → `code=4, level=app` → `app:local-dev-app` blocked until 13:53:37, `tenant-guilty` row unblocked at its own 20 %, and a different healthy tenant logged `held by an APP-level block for 00:06:55` **without making a call** |
| **App usage % inflated every account's own figures** — `X-App-Usage` was merged into the same locals persisted to the tenant row, so one hot app budget made all accounts look hot | L1 and L2 maxima kept in separate locals; the account row is written only when account-level evidence exists (`sawBucSignal`, an account-level code, or a success), so an app-usage-only response can no longer overwrite real per-account figures with zeros | `SharedAppBudget` (app 92 %) → `app:local-dev-app` = 92, `tenant-light` = **5** (its own BUC figure). Before the fix the same row read **92** |
| **One per-second cap for four different limits** — 100/s applied to classes Meta caps at 10/s and 2/s | Four configurable caps and a dispatch window per tenant **and** class; `DispatchClassifier` reads the endpoint and the payload attachment type, defaulting to the tightest cap (2/s) when it cannot classify | 6 `/conversations` jobs → `class=Conversations cap=2/s`, 4 waits of ~500 ms. 20 video-attachment sends → `class=MediaSend cap=10/s`, 10 waits. 20 text sends on the **same** `/messages` endpoint in the same burst → **no** gate waits (100/s) |

One deliberate asymmetry: a success clears an **account** block (that account demonstrably recovered)
but never an **app** block. During verification an unrelated tenant's in-flight success wiped a live
app-level block seconds after it was set; the app block now expires only on its own
`BlockedUntilUtc`, because one account succeeding does not prove a shared budget recovered.

Remaining gap: codes **32** and **80006** have no named mock scenario — they need a manual
`/simulator/configure` call with an explicit `ReturnErrorCode`.

---

## 4. Inbound Design — `WebhookIngestApi`

### 4.1 Enforcement chain (fail-fast, cheapest first)

```
request → EnableBuffering → InboundRateLimitMiddleware → InboundRateLimitPipeline:
  1. BlockedIps                → 403
  2. AllowedIps                → bypass everything
  3. no Content-Length (body method) → 411   (chunked bypass of the size guard)
  4. Content-Length > 1 MB     → 413
  5. HMAC X-Hub-Signature-256  → 401   (body methods only; FixedTimeEquals)
  6. global window  1000/min+20 → 429
  7. per-IP window  60/min      → 429
  8. per-client window 100/min+20 → 429  (X-Webhook-Source/X-Api-Key, 64-char cap, IP fallback)
  9. concurrency 10/client      → 429  (SemaphoreSlim, released on Response.OnCompleted)
→ POST /webhook endpoint (202)
Every 429 carries Retry-After + X-RateLimit-Limit/Remaining/Reset.
```

### 4.2 Business rules and their reasons

| Rule | Why |
|---|---|
| HMAC before counters | Unauthenticated junk must not consume legitimate senders' quota. Trade-off: each invalid signature costs one body read + HMAC; if CPU-flood becomes a concern, add a cheap pre-HMAC per-IP counter |
| `TrustForwardedFor` default **false** | `X-Forwarded-For` is attacker-controlled on a directly-exposed endpoint — trusting it lets one host fake unlimited IPs and void the per-IP limit. Enabled only locally (simulator) or behind a trusted proxy |
| 411 on missing Content-Length | A chunked body bypasses the length check and the HMAC step would buffer an unbounded stream — memory exhaustion vector |
| Self-pruning stores | Counter keys and concurrency slots are keyed by attacker-controlled values; unbounded dictionaries are themselves a DoS target. Past 10 k keys, idle entries are evicted |
| Counters increment before later checks may deny | Simpler and atomic-per-counter; a request denied at step 8 consumed a step 6/7 slot. Acceptable; fully atomic multi-key checks arrive with the Redis store |
| `IRateLimitStore` abstraction | Counting is behind one contract, `TryAcquireAsync(key, policy)`. The policy names the mechanism, so each scope picks its own (`SlidingWindow` default, `FixedWindow`, `TokenBucket`) without touching a rule; multi-instance swaps in Redis (Lua `ZADD`/`ZREMRANGEBYSCORE`/`ZCARD`) with one DI line, no logic changes |
| Rule pipeline (`IInboundRule`) | Each check is one class ordered by an `Order` property, not by DI registration. The sequence is a security property (cheap checks before I/O, HMAC before counters), so it must be assertable in a test |

### 4.3 `WebhookTrafficSimulator`

Eight scenarios (`SteadyTraffic`, `BurstSingleIp`, `DDoSMultiIp`, `GlobalFlood`,
`OversizedPayload`, `InvalidSignature`, `SlowLoris`, `MixedAttack`) via `POST /simulator/run`;
each returns status-code counts, latency, and the rate-limit headers observed, so every layer's
trigger is provable from one JSON response. HMAC secret must match the ingest API's
(checked-in dev placeholder does). `SlowLoris` needs the ingest API running with
`ASPNETCORE_ENVIRONMENT=Development` (dev-only `/webhook/slow` endpoint).

### 4.4 Defense in depth — where an edge layer (nginx / Cloudflare) fits

This middleware is **not** the first line of defense against volumetric attacks, and is not
intended to be. A production deployment should pair it with an edge layer; the two solve
different problems:

| Layer | Owns | Why it must live there |
|---|---|---|
| **Edge — Cloudflare / nginx / IIS+ARR in front** | Volumetric DDoS absorption, coarse per-IP rate limits, connection limits, slow-transport (slow-loris) protection, TLS termination, IP reputation / bot filtering | Attack traffic must be dropped **before** it consumes the app server's bandwidth, connections, and thread pool. An in-process counter can only count requests that already reached Kestrel — by then a large flood has done its damage. Edge products do per-IP throttling in optimized native code at a fraction of the cost |
| **Application middleware — this system** | Identity-aware limits (per-*client* keys from `X-Webhook-Source`/`X-Api-Key`, which the edge cannot see as tenant identity), HMAC `X-Hub-Signature-256` validation with the app secret, payload semantics (411/413), the "never throttle Meta's `hub.challenge` GET" rule, observe-only rollout, and Meta-safe shedding (prolonged 429s to Meta risk webhook-subscription disable — an edge rule would happily 429 Meta) | These rules require application context (client identity, shared secret, Meta webhook semantics) that no edge product has |
| **Outbound system (`InstagramSenderApi` components)** | Everything in §3 | Unaffected by this discussion entirely — edge products protect *your* endpoints; they do nothing about *you* exceeding *Instagram's* limits. Usage-header tracking, shared app budget, per-account blocks, and the per-second dispatch gate have no CDN/edge equivalent |

Practical consequence: when an edge layer is present, the middleware's **global** and **per-IP**
windows demote to a cheap second line (keep them enabled — they cost little and catch
whatever the edge is not configured for), while the per-client, HMAC, concurrency, and payload
layers remain the primary implementation. This is reflected in the integration plan
(I9/I10 and the P4/P5 deployment checklist in
[INTEGRATION_PLAN_IGAutopilot.md](INTEGRATION_PLAN_IGAutopilot.md)).

---

## 5. Implementation Summary & Verified Results

The initial implementation was reviewed end-to-end; 20 defects were found and fixed (4 critical:
shared-across-tenants circuit breaker, timeout wrapping retries, rate-limited jobs dropped,
unconditional XFF trust). Full defect list with severities: [CODE_REVIEW_AND_TEST_PLAN.md](../CODE_REVIEW_AND_TEST_PLAN.md).

Live verification (2026-07-06):

| Flow | Observed |
|---|---|
| Outbound, `GradualApproach`, 25 jobs | Full speed to 80 %; proactive delays 3.75 s → 15 s; call 20 → code 80001, ETA 5 min parsed; block persisted to SQL (+1 min buffer); job re-queued after pause — not dropped |
| Inbound, `InvalidSignature` | 30 × 401, counters untouched |
| Inbound, `BurstSingleIp` (200 req) | exactly 60 × 202 + 140 × 429, `Retry-After: 58–59` |
| Inbound, `SlowLoris` (15 concurrent) | exactly 10 × 202 + 5 × 429 |
| Fresh machine | `SenderDB` + table auto-created |

**July 2026 update — coverage deltas O9–O11 implemented and live-verified in this workspace**
(evidence in [DEMO.md](DEMO.md) §4): per-second dispatch cap (`PerSecondDispatchGate`, then a token
bucket per tenant; a sliding-window log per tenant and class since Oct 2026, §8.2), shared app-level budget (global `app:{AppId}` state row; guard throttles on
`max(tenantPct, appPct)`), and `Retry-After` parsing on HTTP 429. Outbound enforcement is now
config-gated (`RateLimiting:Outbound:Enabled`); inbound gained `Enabled` / `ObserveOnly` /
`ExcludedPaths` plus an unconditional GET bypass, and its config section was renamed to
`RateLimiting:Inbound`. I9–I10 remain deployment/rollout items for the integration.

Known gaps (accepted, tracked): in-memory job queue (outbox planned), some sender constants (80 % threshold, cache TTL)
remain compile-time pending an options pass (§6.4). Full July-2026 coverage assessment and the
corresponding work items (O9–O11, I9–I10):
[INTEGRATION_PLAN_IGAutopilot.md §5](INTEGRATION_PLAN_IGAutopilot.md).

---

## 6. Integration Plan — Merging into the Production Solution (PLAN ONLY, no code yet)

> **Superseded by the concrete, code-verified plan:** after scanning the actual IGAutopilot
> codebase (`IGAutopilot.Ingest.Api`, `IGAutopilot.Worker`, `IGAutopilot.Infrastructure`), the
> specific insertion points, packaging, config shape, rollout phases, and open decisions are in
> **[INTEGRATION_PLAN_IGAutopilot.md](INTEGRATION_PLAN_IGAutopilot.md)**. The sections below
> remain as the original generic reasoning.

Target: the real solution containing
**(a)** an Instagram sender service that calls `graph.facebook.com`, and
**(b)** an Ingest API receiver that accepts Instagram/FB webhooks.
Goal: graft this solution's protection layers onto both, switchable **per feature via
configuration**, so the merged code can ship dark and be enabled gradually.

### 6.1 Is the config toggle hard?

**No — it is the easy part.** Both flows already hang off single seams:

- Inbound protection is one middleware registration. A flag around
  `app.UseMiddleware<InboundRateLimitMiddleware>()` (or better: middleware that consults
  `IOptionsMonitor` per request and calls `next()` straight through when disabled) is ~10 lines.
  Per-request checking gives you **runtime** on/off without restart.
- Outbound protection is two seams: the pre-flight guard call and the pipeline-wrapped client.
  A flag makes the guard return immediately and the client call `SendAsync` without the pipeline.
  The `DelegatingHandler` can stay attached even when disabled (parsing headers is cheap and
  harmless — it only *records*; worth keeping on always for observability).

Estimated toggle work is a day including tests; the real effort is the merge itself (§6.3).

### 6.2 Proposed configuration shape

```json
{
  "RateLimiting": {
    "Outbound": {
      "Enabled": true,                  // master switch: guard + Polly pipeline
      "ProactiveThrottleEnabled": true, // 80 % header-based slowdown
      "ThrottleThresholdPct": 80,
      "BlockBufferMinutes": 1,
      "MaxInlineRetryMinutes": 2,
      "PerTenantChannelCapacity": 500,
      "MaxJobAttempts": 5
    },
    "Inbound": {
      "Enabled": true,                  // master switch: whole middleware
      "HmacValidationEnabled": true,    // separable — teams often stage HMAC first
      "TrustForwardedFor": false
      // + existing WebhookRateLimits values (limits, windows, lists)
    }
  }
}
```

Read via `IOptionsMonitor<T>` so flags flip on config reload (App Configuration / Key Vault /
appsettings reload-on-change) without a deploy. Constants currently hardcoded (threshold, buffer,
capacities) get promoted into these options as part of the merge — small, mechanical.

### 6.3 Merge steps

**Phase 0 — discovery (needs the target solution; point me at it).** Confirm: target framework
(this code is net9.0; the rate-limit logic itself is net6-compatible if needed), how the sender
issues Graph calls (HttpClientFactory? raw HttpClient? SDK?), what "tenant" is keyed by there,
existing queueing (if it has its own queue, our worker is *not* merged — only guard + pipeline +
handler wrap its send path), existing auth on the ingest endpoint, and whether ingest runs
multi-instance (decides Redis store timing).

**Phase 1 — extract a shared library.** Move the reusable core out of the demo apps into e.g.
`RateLimiting.Instagram` (handler, pipeline factory, guard, tenant state service/repository,
models) and `RateLimiting.Ingress` (options, store abstraction + in-memory store, service,
middleware). The demo apps then reference the libraries — proving the extraction — and the
production solution references the same packages/projects. Simulators stay here as the test
harness.

**Phase 2 — outbound graft.** In the real sender: register repository/service/guard/registry +
`AddHttpMessageHandler<InstagramRateLimitHandler>()` on its Graph HttpClient; set the tenant id on
`HttpRequestMessage.Options` where requests are built; wrap its send call in the per-tenant
pipeline; honor `TenantBlockedException`/rate-limited results in *its* retry/queue semantics.
Point its SQL connection at the production DB (table auto-creates). If it lacks queueing and wants
ours, the worker + controller move into the shared library too.

**Phase 3 — inbound graft.** In the real Ingest API: bind options, register store + service, add
`EnableBuffering` + middleware **before** its existing pipeline; map its real client-identity
header into `GetClientId`; wire its HMAC secret (it likely already validates Meta's
`X-Hub-Signature-256` — if so, keep exactly one implementation: prefer ours for the fail-fast
ordering, delete the duplicate). Set `TrustForwardedFor` per its actual topology (ALB/nginx → true
with known proxies; direct → false).

**Phase 4 — rollout.** Ship with `Enabled: false` (verify zero behaviour change) → enable inbound
in staging and replay simulator scenarios against it → enable inbound in prod (monitor 401/429
rates) → enable outbound proactive throttle for one pilot tenant → all tenants. Rollback at every
step = flip the flag back.

### 6.4 Risks / decisions to settle during Phase 0

1. **Double protection:** if the real sender already has retry logic, layering ours on top
   multiplies retries — one owner must win (ours, with theirs disabled behind the same flag).
2. **Tenant identity mapping** between their model and `tenantId` (our SQL key + pipeline key).
3. **Shared state store:** multiple sender instances share SQL state correctly, but per-instance
   circuit breakers/counters mean limits are per-pod for the ingest side until the Redis store
   is implemented — decide whether that's acceptable at current scale.
4. **Secrets:** production HMAC secret and connection strings go to Key Vault/user-secrets; the
   dev placeholders in this repo must never travel into the merged solution's config.

---

## 7. Document Map

| Document | Contents |
|---|---|
| `docs/TRD_RateLimiting.md` (this) | Requirements, architecture, business rules, integration plan |
| `InstagramSenderApi/InstagramSenderApi_Outbound_RateLimit_Spec.md` | Outbound component-level spec (synced to as-built code) |
| `WebhookIngestApi/WebhookIngestApi_Inbound_RateLimit_Spec.md` | Inbound component-level spec (synced to as-built code) |
| `CODE_REVIEW_AND_TEST_PLAN.md` | Full defect list from review, executed test evidence, unit-test blueprint |
| `CLAUDE.md` | Working notes for AI-assisted development (ports, commands, gotchas) |

Section 8 below maps every use case to the counting mechanism behind it and why that mechanism
is the correct one for that scope — including where this solution deliberately departs from the
textbook five.

---

## 8. Algorithm Choice per Use Case — and how it compares with the standard five

The textbook treatment of rate limiting (e.g. https://www.adeshgg.in/blog/rate-limiting) presents
five mechanisms — token bucket, leaky bucket, fixed window, sliding window log, sliding window
counter — and a decision tree ending in "sliding window counter is the production default". That
framework is sound, and it is written entirely from the **inbound** seat: you own the limit, so the
only question is whether to admit or reject. It has no answer for the **outbound** seat, where
someone else owns the limit, rejecting our own work is pointless, and the authority publishes its
own usage figures. Roughly half the mechanisms below are therefore not on its list at all.

### 8.1 Inbound — `WebhookIngestApi`

Our `SlidingWindow` is that article's **sliding window log** (a queue of timestamps per key,
`Strategies/SlidingWindowStrategy.cs`). All three counted scopes default to it.

| Demo | Layer | Limit | Mechanism | Why this is the right choice here |
|---|---|---|---|---|
| 3.1 `GlobalFlood` | Global ceiling | 1000/min + 20 burst, **one key** | Sliding window log | Key cardinality is 1. The only argument against a log is memory, and at a single key holding ≤1020 timestamps that argument disappears — so take the exact answer for free. A boundary burst here would be a doubled whole-service ceiling |
| 3.1 `BurstSingleIp`, `DDoSMultiIp` | Per source IP | 60/min, **burst 0** | Sliding window log | An abuse control, not a fairness quota. The fixed-window boundary burst is *tunable by the attacker* — cluster at the seam and get 2x — which is precisely the traffic this layer exists to stop. Burst 0 is deliberate: an unidentified caller gets the flat documented rate |
| 3.1 `SteadyTraffic`, `MixedAttack` | Per client identity | 100/min + 20 burst | Sliding window log | Identity is known (the rule runs after HMAC), so this is fairness between honest senders. `BurstSize` supplies the headroom a legitimately bursty sender needs **without** switching mechanism — token-bucket depth, window exactness. Runs last of the three, so its `Remaining` is what the caller is told |
| 3.1 `SlowLoris` | Concurrency | 10 in-flight per client | `SemaphoreSlim`, released on `Response.OnCompleted` — **not a rate algorithm** | One request every 30 s satisfies every window in the standard five. Only a concurrency cap bounds connections *held open*. This is the gap in the textbook framework, not in the design |
| 3.1 `OversizedPayload` | Payload size | 1 MB → 413, no length → 411 | Guard, no counter | The cost is in the bytes, not the count. Refusing on `Content-Length` before a byte is read is cheaper than any algorithm can be |
| 3.1 `InvalidSignature` | HMAC | 401 | Guard, ordered **before every counter** | Security property: forged traffic must not spend a real client's quota on its way to being rejected |
| — | Block / allow list | — | Set membership, O(1) | Cheapest check first; a banned host never reaches the store |
| 3.2 | GET + `ExcludedPaths` bypass | — | No limiter at all | Meta's `hub.challenge` verification is a GET. Throttling it breaks re-verification and the subscription can be disabled |
| 3.3 | Observe-only | — | Same algorithms, denial logged not applied | The rollout step the textbook omits: measure real traffic for a week before picking thresholds |

### 8.2 Outbound — `InstagramSenderApi`

| Demo | Layer | Mechanism | Why this is the right choice here |
|---|---|---|---|
| 2.1, 2.8 | Per-second cap, per tenant **per dispatch class** (text 100/s, media 10/s, Conversations 2/s) | **Reservation** sliding-window log returning a delay: the last N dispatch times per key, next slot = max(now, oldest + 1 s) (`PerSecondDispatchGate`) | We are the client: rejecting our own send is strictly worse than pacing it, so the gate returns a wait rather than a verdict. A sliding log is the one mechanism here whose guarantee matches the limit's wording — never more than N in **any** one-second span, cold start included — while still letting a burst up to N leave at once. It replaced a reservation token bucket (Oct 2026): a full bucket refilling at the cap admits capacity + rate − 1 calls in one second (7 for a 4/s gate), which is what failed demo 2.1, and shrinking the capacity would have smoothed every legitimate burst. Cost: N timestamps per key instead of two values |
| 2.3, 2.5, 2.6, 2.7 | Usage-% throttle and hard block | **Adaptive feedback throttle** — none of the standard five. Threshold 80% of `max(tenantPct, appPct)`, delay `3_600_000 / (remainingPct x 48)` ms clamped to 200 ms–60 s | Meta publishes the ground truth in `X-App-Usage` / `X-Business-Use-Case-Usage`. A local counter is a model of someone else's budget, and it is blind to the other instances of our own service, to other processes on the same app, and to the 24 h BUC window. Reading the authority's number beats simulating it. The clamp is the safety rail on a hyperbola |
| 2.2 | Reactive recovery | Polly: exponential backoff **with jitter**, `Retry-After` / `estimated_time_to_regain_access` honoured, per-tenant circuit breaker, 15 s per-attempt timeout | Layer 1 is a prediction, so layer 2 exists for when the prediction is wrong. Jitter is not decoration: without it, N instances that backed off together retry together and re-create the burst they were avoiding |
| all | Work admission | Per-tenant bounded `Channel<SendJob>` | The queue half of a leaky bucket, in front of everything, so backpressure exists before any gate is consulted |

### 8.3 Deliberate gaps

- **No sliding window counter, no leaky-bucket strategy.** The textbook production default is not
  implemented. Defensible today — single instance, in-memory, small limits, self-pruning keys — but
  when inbound goes multi-instance on Redis, **per-IP is the scope where log memory bites** at
  internet key cardinality. The fix is one config value (`PerIpAlgorithm`) plus a fourth strategy
  file; no rule changes.
- **Cold-start overshoot — fixed (Oct 2026).** The outbound gate used to be a token bucket that
  started full, so a restart could emit capacity + rate − 1 calls in the first second (7 for a 4/s
  gate), which is what failed demo 2.1. It is the textbook token-bucket trade-off — "can allow
  instantaneous bursts that overwhelm downstream services" — arriving exactly as predicted. The gate
  is now a sliding-window log (§8.2), which caps **any** one-second span, cold start included; the
  test `A_cold_start_never_lets_more_than_the_cap_out_in_one_second` holds it there. What remains
  is network jitter between the gate and the wire, which is why the demo gate runs one below the
  mock's cap (`docs/DEMO.md` §4).
- **Token bucket and leaky bucket are the same maths.** Presenting them as separate algorithms is a
  common simplification. The real difference is what happens on overflow: reject (token) or
  queue/delay (leaky). The same split applies to any counter: inbound `TokenBucket` rejects on
  overflow, while the outbound gate — a sliding-window log — returns a delay instead, which is why it
  is listed above as a shaper rather than a limiter.
