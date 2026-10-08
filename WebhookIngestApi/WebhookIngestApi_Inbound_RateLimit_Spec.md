# WebhookIngestApi — Inbound Rate Limiting Spec

**Purpose:** Protect a publicly-exposed webhook endpoint from DDoS attacks, malicious oversized payloads,
and abusive senders using an ordered `IInboundRule` pipeline (`InboundRateLimitPipeline`) behind
`InboundRateLimitMiddleware`.
Uses Polly v8 (`Microsoft.Extensions.Resilience`) with an `IRateLimitStore` abstraction so Redis
can be swapped in without code changes once deployment topology is decided.

---

## NuGet Packages

| Package | Purpose |
|---|---|
| `Polly` | Core resilience policies |
| `Microsoft.Extensions.Resilience` | Polly v8 DI integration, `ResiliencePipelineBuilder` |
| `Microsoft.Extensions.Caching.Memory` | `IMemoryCache` backing for `InMemoryRateLimitStore` |
| `Microsoft.AspNetCore.App` (framework ref) | ASP.NET Core 9 Web API |

---

## Rate Limiting Parameters — All Production-Grade Dimensions

| # | Dimension | Mechanism | Default Threshold (configurable) |
|---|---|---|---|
| 1 | **HMAC Signature** | Middleware pre-check, runs before any rate counter | Missing/invalid → 401 |
| 2 | **Payload size** | `Content-Length` header + stream guard | > 1 MB → 413 |
| 3 | **IP block/allow list** | In-memory `HashSet`, checked first after signature | Blocked → 403 immediately |
| 4 | **Global window** | `IRateLimitStore`, key = `"global"` | 1000 req/min total |
| 5 | **Per-IP window** | `IRateLimitStore`, key = `ip:{remoteIp}` | 60 req/min per IP |
| 6 | **Per-client window** | `IRateLimitStore`, key = `client:{clientId}` | 100 req/min per API key |
| 7 | **Concurrency limiter** | `SemaphoreSlim` per client-id | 10 simultaneous per client |
| 8 | **Burst tolerance** | Configured `BurstSize`, added to the scope's allowance | +20 burst above steady rate |
| 9 | **`Retry-After` response header** | Set on every 429 response | Seconds until window resets |
| 10 | **Structured logging on every limit hit** | `ILogger` | `{ClientId, Ip, DenialReason, Path, RetryAfter}` |
| 11 | **Content-Length required** | Body methods without `Content-Length` (chunked) rejected before any body read — otherwise the HMAC step would buffer an unbounded stream | Missing → 411 |
| 12 | **Spoof-proof IP identity** | `TrustForwardedFor` option — `X-Forwarded-For` honoured **only** when explicitly enabled (trusted reverse proxy in front); otherwise socket `RemoteIpAddress` | default `false` |
| 13 | **Key hygiene** | Attacker-controlled header values truncated to 64 chars before becoming rate-limit keys; idle counters/semaphores pruned past 10 k tracked keys | prevents unbounded memory |

**Evaluation order (fail-fast, cheapest checks first):**
Block list → Allow list → Content-Length/Payload size → HMAC (body methods only) → Global → IP → Client → Concurrency

Each check is an `IInboundRule` and the order comes from its `Order` property, not from DI
registration — see `Rules/` below. Layers 4-6 are all `SlidingWindow` by default; each names its own
mechanism (`GlobalAlgorithm` / `PerIpAlgorithm` / `PerClientAlgorithm`), so a scope can be moved to
`FixedWindow` or `TokenBucket` without touching the other two or any rule code.

---

## Project Structure

```
WebhookIngestApi/
├── WebhookIngestApi.csproj
├── Program.cs
├── appsettings.json
│
├── RateLimit/
│   ├── InboundRateLimitOptions.cs        ← strongly-typed config (IOptions<T>)
│   ├── IRateLimitStore.cs                ← storage abstraction + RateLimitPolicy (swap Memory ↔ Redis)
│   ├── InMemoryRateLimitStore.cs         ← self-pruning per-key buckets, locking, eviction
│   ├── ClientIdentityResolver.cs         ← IP / client-id derivation, resolved once per request
│   ├── LimitDecision.cs                  ← DenialReason enum + the pipeline's decision record
│   ├── InboundRateLimitPipeline.cs       ← runs the rules in order, returns the first decision
│   ├── InboundRateLimitMiddleware.cs     ← ASP.NET Core middleware, short-circuits on deny
│   │
│   ├── Strategies/                       ← the counting maths, one file per mechanism
│   │   ├── ICountingStrategy.cs          ← called under the key's lock; owns no keys
│   │   ├── SlidingWindowStrategy.cs      ← default: exact count over the trailing window
│   │   ├── FixedWindowStrategy.cs        ← constant memory, boundary burst
│   │   └── TokenBucketStrategy.cs        ← paces rather than counts
│   │
│   └── Rules/                            ← the pipeline, one file per check
│       ├── IInboundRule.cs               ← contract + RuleOrder (the documented sequence)
│       ├── InboundRequest.cs             ← per-request state shared by the rules
│       ├── BlockedIpRule.cs              ← 403, before the store is ever touched
│       ├── AllowedIpRule.cs              ← the one rule that ends the pipeline with an allow
│       ├── PayloadSizeRule.cs            ← 411 / 413 on the Content-Length header alone
│       ├── HmacSignatureRule.cs          ← 401, before any counter moves
│       ├── CountedLimitRule.cs           ← abstract base + Global / PerIp / PerClient
│       └── ConcurrencyRule.cs            ← SemaphoreSlim per client, released on OnCompleted
│
└── Resilience/
    └── WebhookResiliencePipeline.cs      ← Polly pipeline wrapping downstream job processing

(as-built: no controller — POST /webhook is a minimal-API endpoint in Program.cs, plus a
Development-only POST /webhook/slow used by the SlowLoris concurrency scenario)
```

---

## Component Details

### `InboundRateLimitOptions.cs`
**Purpose:** Strongly-typed options bound from `appsettings.json` under key `"RateLimiting:Inbound"`
(renamed July 2026 from `"WebhookRateLimits"` to match the IGAutopilot integration plan's section shape).

Properties:
- `bool Enabled` — default `true`; master switch, `false` passes every request straight through
- `bool ObserveOnly` — default `false`; counters run and every would-be denial is logged with its
  status code, but nothing is denied (observe-first rollout mode, added July 2026)
- `int GlobalLimitPerMinute` — default 1000
- `int PerIpLimitPerMinute` — default 60
- `int PerClientLimitPerMinute` — default 100
- `int BurstSize` — default 20 (extra burst capacity above steady rate)
- `int WindowSeconds` — default 60
- `long MaxPayloadBytes` — default 1,048,576 (1 MB)
- `int MaxConcurrencyPerClient` — default 10
- `string HmacSecretKey` — **load from env var / Key Vault, never hardcode**; empty = HMAC
  validation disabled (dev only — a loud warning is logged at startup)
- `bool TrustForwardedFor` — default `false`; honour `X-Forwarded-For` only behind a trusted
  reverse proxy. Local config sets `true` so the traffic simulator can fake per-IP identities.
- `HashSet<string> BlockedIps`
- `HashSet<string> AllowedIps` — bypass all limits if IP is in this set
- `List<string> ExcludedPaths` — path prefixes that bypass rate limiting entirely (health probes,
  swagger, …). Independent of this list, **GET requests always bypass** — Meta's `hub.challenge`
  webhook verification handshake is a GET and must never be throttled (added July 2026; both
  bypasses verified to hold while the global window is saturated)

---

### `IRateLimitStore.cs`
**Purpose:** Decouple rate-limit counting from both infrastructure *and* mechanism. Enables the Redis
swap with zero business-logic changes, and lets each scope choose how it is counted.

```csharp
public enum RateLimitAlgorithm { SlidingWindow, FixedWindow, TokenBucket }

public readonly record struct RateLimitPolicy(
    RateLimitAlgorithm Algorithm,
    int                WindowSeconds,
    int                Limit,
    int                BurstSize);

public interface IRateLimitStore
{
    Task<CounterResult> TryAcquireAsync(
        string key,
        RateLimitPolicy policy,
        CancellationToken ct = default);
}

public readonly record struct CounterResult(bool IsAllowed, int Remaining, DateTimeOffset ResetAt);
```

Passing the whole policy rather than the four loose numbers a sliding window happens to need is what
makes the mechanism swappable: `BurstSize` means nothing to a fixed window and is capacity to a token
bucket, and neither the caller nor the store has to care. Nothing is consumed on a denial, whichever
mechanism is in force.

---

### `InMemoryRateLimitStore.cs`
**Purpose:** Default implementation for single-instance deployments.

As-built (deviation from the original IMemoryCache plan — rewritten during review):
- `ConcurrentDictionary<string, Bucket>`; each bucket owns its own lock object (fine-grained locking,
  not a global lock) plus opaque per-key state belonging to whichever algorithm created it
- The store owns keys, locking, eviction and the clock (`TimeProvider`, injected so window expiry is
  testable without sleeping); the `ICountingStrategy` named by the policy owns the maths and is
  always called under the key's lock, so an algorithm never needs to be thread-safe itself
- A key whose scope is reconfigured to a different algorithm has its state rebuilt — a queue of
  timestamps cannot be reinterpreted as a counter
- Sliding window (the default): dequeue timestamps older than `windowSeconds`, then check
  count ≤ limit + burstSize
- **Self-pruning:** past 10 000 tracked keys, buckets idle for > 2 windows are evicted (outside any
  bucket lock, with a `Removed` re-check to avoid racing a concurrent user of the same bucket).
  Rationale: rate-limit keys derive from attacker-controlled values (IPs, `X-Webhook-Source`),
  so an unbounded dictionary is itself a memory-DoS vector.

**Redis upgrade path:** Implement `RedisRateLimitStore : IRateLimitStore` using a Lua script
with `ZADD` + `ZREMRANGEBYSCORE` + `ZCARD` on a sorted set. Change one DI registration line in `Program.cs`.

---

### `InboundRateLimitPipeline.cs` + `Rules/`
**Purpose:** Run the checks in order and return the first terminal decision. Each check is an
`IInboundRule`; the service owns only the sequencing.

Constructor injections: `IEnumerable<IInboundRule>`, `ClientIdentityResolver`, `IOptions<InboundRateLimitOptions>`

```csharp
public interface IInboundRule
{
    int    Order { get; }     // see RuleOrder — the documented sequence
    string Name  { get; }
    ValueTask<LimitDecision?> EvaluateAsync(InboundRequest ctx, CancellationToken ct = default);
    // null = pass to the next rule; non-null = terminal (deny, or the allow-list bypass)
}
```

**Order is a security property, not a wiring detail** — the cheap no-I/O checks must run before the
store is touched, and HMAC must run before any counter is incremented so forged traffic cannot spend
a real client's quota. It therefore lives in each rule's `Order` (constants in `RuleOrder`) and is
sorted once in the service constructor, rather than depending on DI registration order: a property
can be asserted in a test, registration order cannot. Identity is resolved once, up front, so no rule
can re-derive it from a different header.

Adding a limit is a new rule class plus one DI line; the service does not change.

```csharp
public Task<LimitDecision> EvaluateAsync(HttpContext context, CancellationToken ct);

public record LimitDecision(
    bool IsAllowed,
    DenialReason DenialReason,   // None | Blocked | PayloadSize | LengthRequired | Signature | Global | Ip | Client | Concurrency
    int RetryAfterSeconds,
    int Remaining);
```

Rule sequence (as-built, `RuleOrder` value in brackets):
1. `BlockedIpRule` [100] — `BlockedIps` lookup → deny with `DenialReason.Blocked`
2. `AllowedIpRule` [200] — `AllowedIps` lookup → allow immediately (bypass all remaining checks)
3. `PayloadSizeRule` [300] — body method (POST/PUT/PATCH) without `Content-Length` → deny with
   `DenialReason.LengthRequired` (a chunked body would otherwise bypass the size guard and get fully
   buffered by the HMAC step); then `Content-Length` vs `MaxPayloadBytes` → `DenialReason.PayloadSize`
4. `HmacSignatureRule` [400] — HMAC-SHA256 of `X-Hub-Signature-256` using
   `CryptographicOperations.FixedTimeEquals` → deny with `DenialReason.Signature` — **body methods
   only** (GET health probes carry no signed payload); skipped entirely when `HmacSecretKey` is
   empty (startup warning)
5. `GlobalLimitRule` [500] — `TryAcquireAsync("global", policy)` → deny with `DenialReason.Global`
6. `PerIpLimitRule` [600] — `TryAcquireAsync("ip:{ip}", policy)` → deny with `DenialReason.Ip`
   (IP per `TrustForwardedFor` rule; no burst allowance for this scope)
7. `PerClientLimitRule` [700] — `TryAcquireAsync("client:{clientId}", policy)` → deny with
   `DenialReason.Client` (clientId from `X-Webhook-Source` / `X-Api-Key`, truncated to 64 chars, IP
   fallback). Runs last of the three, so its `Remaining` is what the caller is told
8. `ConcurrencyRule` [800] — acquire a `SemaphoreSlim` for `clientId` (stored in a
   `ConcurrentDictionary`, pruned when idle past 10 k clients) → deny with `DenialReason.Concurrency`
   if `WaitAsync(0)` fails

The `SemaphoreSlim` is released via `Response.OnCompleted` after the response finishes streaming
(not when the evaluation method returns) — it is the one rule holding a resource past its own
return, which is what bounds slow-drip traffic that every window happily allows.

---

### `InboundRateLimitMiddleware.cs`
**Purpose:** ASP.NET Core middleware that intercepts all requests before they reach controllers.

- Reads `X-Webhook-Source` or `X-Api-Key` as `clientId`; falls back to remote IP if absent
- Calls `InboundRateLimitPipeline.EvaluateAsync`
- **On deny:** short-circuits with appropriate HTTP status code + response headers + JSON body:
  ```json
  { "error": "rate_limit_exceeded", "retryAfterSeconds": 30 }
  ```
  Response headers set: `Retry-After`, `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`
- **On allow:** calls `await next(context)` inside a `try/finally` that releases the concurrency semaphore

HTTP status codes returned:
| Deny reason | Status |
|---|---|
| Blocked IP | 403 Forbidden |
| Invalid signature | 401 Unauthorized |
| Missing Content-Length (body method) | 411 Length Required |
| Payload too large | 413 Payload Too Large |
| Rate limit hit (global/IP/client/concurrency) | 429 Too Many Requests |

---

### `WebhookResiliencePipeline.cs`
**Purpose:** Polly v8 resilience pipeline that wraps **downstream job processing** of an accepted
webhook (database writes, queue dispatch, etc.) — not the rate limiting itself.

```
Timeout(5 seconds)
→ CircuitBreaker(failureRatio: 0.5, minThroughput: 10, sampling: 30s, break: 15s)
```

Registered as a named pipeline: `services.AddResiliencePipeline("webhook-processing", builder => ...)`
Used inside `WebhookController` via `ResiliencePipelineProvider<string>`.

---

### `Program.cs` — Middleware Pipeline Order

```csharp
// Order is critical — EnableBuffering must precede the middleware so the body can be
// read for HMAC and rewound; the rate limiter must precede endpoint execution
app.Use(async (ctx, next) => { ctx.Request.EnableBuffering(); await next(ctx); });
app.UseMiddleware<InboundRateLimitMiddleware>();
app.MapPost("/webhook", ...);                       // minimal API endpoint
if (app.Environment.IsDevelopment())
    app.MapPost("/webhook/slow", ...);              // 8-s hold, SlowLoris scenario only
```

---

### `appsettings.json` Shape

```json
{
  "RateLimiting": {
    "Inbound": {
      "Enabled": true,
      "ObserveOnly": false,
      "GlobalLimitPerMinute": 1000,
      "PerIpLimitPerMinute": 60,
      "PerClientLimitPerMinute": 100,
      "BurstSize": 20,
      "WindowSeconds": 60,
      "MaxPayloadBytes": 1048576,
      "MaxConcurrencyPerClient": 10,
      "HmacSecretKey": "<<USE-ENV-VAR-OR-KEY-VAULT>>",
      "TrustForwardedFor": false,
      "BlockedIps": [],
      "AllowedIps": [],
      "ExcludedPaths": ["/health"]
    }
  }
}
```

**As-built local config:** the checked-in `appsettings.json` carries a dev-only placeholder secret
(`local-dev-secret-do-not-use-in-prod`, matching `Simulator:HmacSecret` in WebhookTrafficSimulator)
and `TrustForwardedFor: true` so all simulator scenarios exercise the real code paths locally.
Both values MUST be replaced/disabled for production.

---

## Security Notes

1. `HmacSecretKey` must **never** be committed to source control — use `dotnet user-secrets` locally, environment variable or Azure Key Vault in production
2. HMAC comparison uses `CryptographicOperations.FixedTimeEquals` to prevent timing-based attacks
3. IP extraction: `X-Forwarded-For` is honoured **only** when `TrustForwardedFor = true` (behind a trusted reverse proxy that overwrites the header). Default is the socket `RemoteIpAddress` — otherwise any direct caller can rotate fake XFF values and bypass the per-IP limit. Longer term, replace with ASP.NET's `ForwardedHeadersMiddleware` + `KnownProxies`.
4. Rate-limit keys must not include sensitive data — use hashed client IDs as keys if client IDs are PII
5. `BlockedIps` should be externally configurable (feature flag / config reload) so you can block new attackers without redeployment

---

## Positioning — Edge Layer (nginx / Cloudflare) vs This Middleware

This middleware is the **identity-aware second line**, not a DDoS shield. In production it
should sit behind an edge layer (Cloudflare, nginx, or IIS/ARR request limits), and the split
of responsibilities is deliberate:

**What belongs at the edge (and why this middleware cannot do it):**

- **Volumetric DDoS** — an in-process counter only sees requests that already reached Kestrel.
  If the attack saturates bandwidth or the connection pool, counting them afterwards doesn't
  help. Edge networks absorb this before it touches the server.
- **Coarse per-IP / connection limits** — nginx `limit_req`/`limit_conn` and Cloudflare
  rate-limiting rules do this in optimized native code at the edge, far cheaper than any
  managed-code middleware.
- **Slow-transport attacks** (headers trickled byte-by-byte) — these happen **below** the
  middleware; covered by IIS/ARR request limits + Kestrel `MinRequestBodyDataRate`
  (integration plan item I10).

**What only this middleware can do (edge products have no application context):**

- **Per-client limits** keyed on `X-Webhook-Source`/`X-Api-Key` — the edge sees IPs, not your
  tenant identity; many clients can share an IP (and one client can span many).
- **HMAC `X-Hub-Signature-256` validation** with the app secret, constant-time compare.
- **Meta webhook semantics** — the unconditional GET bypass so `hub.challenge` verification is
  never throttled, and fast-200-and-shed under overload instead of prolonged 429s to Meta
  (which risk webhook-subscription disable — an edge rule would happily 429 Meta).
- **Observe-only rollout** integrated with the application's own logging and thresholds.
- **Payload semantics** — the 411 chunked-bypass guard and 413 size check tied to HMAC
  buffering behaviour.

**Consequence:** with an edge layer in front, the `Global` and `PerIp` windows here demote to a
cheap backstop (keep them on — they cost little and cover gaps in edge configuration), while
`PerClient`, HMAC, concurrency, and payload checks remain the primary implementation. The
outbound system (`InstagramSenderApi`) is untouched by this discussion — no edge product
addresses *outgoing* Graph-API-limit compliance.

---

## Open Decision — Redis

When deployment topology is confirmed (single instance vs. distributed pods/containers):
- Implement `RedisRateLimitStore : IRateLimitStore` using `IConnectionMultiplexer` + Lua atomic script
- Change **one line** in `Program.cs`: `services.AddSingleton<IRateLimitStore, RedisRateLimitStore>()`
- No changes to `InboundRateLimitPipeline`, middleware, or options

---

## Verification Checklist

- [x] Burst from same IP → first 60 return 202, remainder 429 with `Retry-After` *(verified 2026-07-06: 60×202 + 140×429)*
- [ ] POST with `Content-Length` > 1 MB → 413 before any rate-limit counter increments
- [x] POST with invalid `X-Hub-Signature-256` → 401, no counter incremented *(verified: 30×401)*
- [ ] POST from a blocked IP → 403 immediately, no counter or HMAC lookup
- [ ] Global limit hit by requests spread across multiple IPs → 429 on all new requests
- [x] Concurrency: 15 simultaneous slow requests from same client → 10×202, 5×429 *(verified; requires Development env for `/webhook/slow`)*
- [ ] Allowed IP bypasses all rate limits
- [x] `Retry-After`, `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` headers present on every 429 *(verified)*
- [ ] POST without `Content-Length` (chunked) → 411, body never read
- [ ] With `TrustForwardedFor=false`, rotating `X-Forwarded-For` values does NOT create distinct per-IP buckets
- [ ] Swap `InMemoryRateLimitStore` with a mock `IRateLimitStore` in unit tests → all service tests pass unchanged
- [ ] Structured log entry emitted with `ClientId`, `Ip`, `DenialReason`, `Path` on every limit hit

---

## Real-Time Simulator — `WebhookTrafficSimulator` Project

**Purpose:** A companion ASP.NET Core 9 Minimal API that fires controlled HTTP traffic patterns
directly at the running WebhookIngestApi, proving every rate-limit layer triggers under realistic load.
It acts as the "attacker + legitimate sender" rolled into one controllable harness.

### How It Fits Into the System

```
[WebhookTrafficSimulator]  ──HTTP──►  [WebhookIngestApi]  ──job──►  [Your downstream processor]
     ↑
Developer triggers scenario via
POST /simulator/run or CLI args
```

### Project Structure

```
WebhookTrafficSimulator/
├── WebhookTrafficSimulator.csproj          ← Minimal API, net9.0, HttpClient + Bogus (fake payload gen)
├── Program.cs                       ← maps /simulator/* endpoints, DI wiring
├── SimulatorOptions.cs              ← target URL, HMAC secret, default durations (from appsettings)
│
├── Scenarios/
│   ├── IScenario.cs                 ← interface: RunAsync(ScenarioContext) → ScenarioResult
│   ├── SteadyTrafficScenario.cs     ← N senders × 1 req/sec, valid HMAC, small payload
│   ├── BurstSingleIpScenario.cs     ← 1 IP fires MaxRequests in BurstWindowSeconds
│   ├── DDoSMultiIpScenario.cs       ← VirtualIpCount fake IPs each firing at PerIpLimit - 1
│   ├── OversizedPayloadScenario.cs  ← sends configurable MB payloads
│   ├── InvalidSignatureScenario.cs  ← correct rate, deliberately wrong HMAC
│   ├── SlowLorisScenario.cs         ← ConcurrentCount slow requests held open simultaneously
│   ├── GlobalFloodScenario.cs       ← enough virtual IPs to push past GlobalLimitPerMinute
│   └── MixedAttackScenario.cs       ← parallel mix of Burst + DDoS + InvalidSig at same time
│
├── Infrastructure/
│   ├── WebhookHttpSender.cs         ← HttpClient wrapper; builds valid/invalid HMAC, sets headers
│   ├── VirtualIpRotator.cs          ← cycles through fake X-Forwarded-For values per scenario
│   └── PayloadFactory.cs           ← generates JSON payloads of configurable byte sizes
│
└── Models/
    ├── ScenarioRequest.cs           ← { ScenarioName, DurationSeconds, Concurrency, ... }
    ├── ScenarioContext.cs           ← runtime state passed into each scenario
    └── ScenarioResult.cs           ← per-status-code counts, avg latency, Retry-After values seen
```

### Simulator REST Endpoints (`Program.cs`)

| Method | Route | Description |
|---|---|---|
| `GET` | `/simulator/scenarios` | Lists all available scenario names and their parameters |
| `POST` | `/simulator/run` | Body: `ScenarioRequest` → runs scenario, returns `ScenarioResult` |
| `POST` | `/simulator/run-all` | Runs every scenario sequentially, returns full report |
| `GET` | `/simulator/health` | Confirms simulator is up and can reach the target WebhookIngestApi URL |

All endpoints return JSON. `POST /simulator/run` is synchronous — it blocks until the scenario
completes (or `DurationSeconds` elapses), then returns the result. Use a generous HTTP timeout
on your client (e.g. Postman timeout = 300 s).

### Scenario Definitions

| Scenario | What It Sends | What You Expect to See |
|---|---|---|
| `SteadyTraffic` | 5 senders × 1 req/10 sec, valid HMAC, 1 KB payload | All 200 — baseline pass |
| `BurstSingleIp` | 1 IP, 200 requests in 10 seconds | First ~60: 200; rest: 429 with `Retry-After` |
| `DDoSMultiIp` | 50 virtual IPs, each firing 25 req/min | Global ceiling hit; new requests 429 across all IPs |
| `OversizedPayload` | 10 requests with 2 MB+ body | All 413; no rate counter incremented |
| `InvalidSignature` | 30 req at valid rate, wrong HMAC | All 401; counters not consumed |
| `SlowLoris` | 15 concurrent requests, each sleeps 8 s server-side | First 10: 200; 11th–15th: 429 (concurrency limit) |
| `GlobalFlood` | 120 virtual IPs × 15 req/min | Total > GlobalLimitPerMinute → 429 on all new IPs |
| `MixedAttack` | Burst + DDoS + InvalidSig fired in parallel | Each category returns its own correct status code |

### `ScenarioResult` Shape (JSON response)

```json
{
  "scenario": "BurstSingleIp",
  "durationSeconds": 10,
  "totalSent": 200,
  "statusCounts": { "200": 62, "429": 138 },
  "averageLatencyMs": 14,
  "retryAfterValuesSeen": [30, 30, 30],
  "rateLimitHeadersObserved": {
    "X-RateLimit-Limit": "60",
    "X-RateLimit-Remaining": "0",
    "X-RateLimit-Reset": "2026-06-19T12:01:00Z"
  },
  "errors": []
}
```

### `SimulatorOptions` (`appsettings.json` shape)

```json
{
  "Simulator": {
    "TargetBaseUrl": "http://localhost:5001",
    "WebhookPath": "/webhook",
    "HmacSecret": "<<same-secret-as-WebhookIngestApi>>",
    "DefaultDurationSeconds": 30,
    "VirtualIpCount": 50,
    "OversizedPayloadMb": 2
  }
}
```

### HMAC Signing in `WebhookHttpSender`

- For valid-signature scenarios: compute `HMAC-SHA256(body, HmacSecret)`, set `X-Hub-Signature-256: sha256={hex}`
- For invalid-signature scenarios: set a deliberately wrong hex string
- `X-Webhook-Source` header is set to a deterministic `client-{i}` string for per-client rate limit testing
- `X-Forwarded-For` is set to a fake IP from `VirtualIpRotator` for multi-IP scenarios

### NuGet Packages

| Package | Purpose |
|---|---|
| `Bogus` | Generates realistic fake JSON webhook payloads |
| `Microsoft.AspNetCore.App` | Minimal API host |

### Running the Simulator

```bash
# 1. Start WebhookIngestApi
cd WebhookIngestApi && dotnet run

# 2. Start WebhookTrafficSimulator in a second terminal
cd WebhookTrafficSimulator && dotnet run

# 3. Trigger a scenario (Postman or curl)
curl -X POST http://localhost:5010/simulator/run \
     -H "Content-Type: application/json" \
     -d '{"scenarioName":"BurstSingleIp","durationSeconds":15}'

# 4. Run all scenarios at once for a full validation report
curl -X POST http://localhost:5010/simulator/run-all
```

### What the Results Prove

| Result observation | Rate-limit layer it validates |
|---|---|
| 429 after ~60 requests from same IP | Per-IP sliding window |
| 413 on oversized body, counter not incremented | Payload size guard |
| 401 on bad HMAC, counter not incremented | Signature check |
| 429 when global total crosses 1000/min across many IPs | Global sliding window |
| 429 on 11th concurrent slow request from same client | Concurrency limiter (`SemaphoreSlim`) |
| 200 for all requests from an IP in `AllowedIps` | Allow-list bypass |
| `Retry-After` header present and non-zero on every 429 | Response header correctness |
