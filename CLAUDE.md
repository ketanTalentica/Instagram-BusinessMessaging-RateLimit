# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Execution Rules — read before starting any work

Adopt the structural operating discipline of a frontier Fable model. These rules govern *how* work
is done here and override default behaviour.

- **PLAN GATE** — Do not write or edit any code until a written plan exists containing the goal,
  unknowns, success criteria, and step order.
- **SCOPE FENCE** — Do exactly what was asked. If you see adjacent problems or bugs, flag them, but
  NEVER silently fix them. Do not design for hypothetical future requirements.
- **LIVE STATE TRUTH** — Assume documentation is stale. Verify against the live system and actual
  files before asserting anything that matters.
- **ADVERSARIAL VERIFY** — Refute your own work before presenting it. Verify each stage against a
  check that can actually fail (running a test, checking if a file exists) rather than just saying
  "it looks right".
- **RUTHLESS EDITOR** — Lead with the outcome. Your first sentence after finishing should answer
  "what happened." Cut 30% of your explanation with zero information loss.
- **MEMORY HYGIENE** — Do not re-litigate established facts. Store one clear lesson/fact per file
  when asked to remember something.
- **ZERO FILLER** — Strip all conversational pleasantries ("Here is the code", "I hope this helps",
  "Let me know"). Output only the necessary commands, the code blocks, and a maximum 2-sentence
  technical summary of what changed.

## What This Is

A .NET 9 solution demonstrating two independent rate-limiting systems, each paired with a companion simulator for end-to-end testing without external dependencies:

1. **Outbound rate limiting** — `InstagramSenderApi` (protects against Instagram Graph API limits) + `InstagramGraphMock` (impersonates the Graph API, returns configurable rate-limit headers and errors)
2. **Inbound rate limiting** — `WebhookIngestApi` (protects a public webhook endpoint from DDoS/abuse) + `WebhookTrafficSimulator` (fires attack/traffic scenarios at the proxy)

Unit tests live in `RateLimit.Tests` (xUnit, `dotnet test RateLimit.sln`) and cover the contracts and their maths — store algorithms, the inbound rule pipeline's order and short-circuits, identity resolution, the outbound gates, throttle/block state, dispatch classification. They do not cover the HTTP handler, the Polly pipeline or `SendQueueWorker`; those are still only exercised end to end by `run-all.bat`, which remains the regression net for observed behaviour.

Each system's design doc is authoritative and detailed — read it before changing that system:
- `docs/TRD_RateLimiting.md` — overall TRD: architecture, business rules, production integration plan
- `InstagramSenderApi/InstagramSenderApi_Outbound_RateLimit_Spec.md`
- `WebhookIngestApi/WebhookIngestApi_Inbound_RateLimit_Spec.md`
- `CODE_REVIEW_AND_TEST_PLAN.md` — review findings, verified test evidence, unit-test blueprint
- `docs/DEMO.md` — step-by-step standalone demo runbook with expected outputs
- `docs/INTEGRATION_PLAN_IGAutopilot.md` + `TODO List.md` — production integration plan and its working checklist; `docs/release-scripts/` holds the SQL release scripts for that integration (no EF migrations there)

## Commands

```powershell
dotnet build RateLimit.sln

# Run a service (see port note below)
dotnet run --project InstagramSenderApi --no-launch-profile
```

**Prefer the runner for anything demo- or verification-shaped.** `run-all.bat` (logic in
`scripts/Demo.ps1`) stops leftovers, builds, starts LocalDB + all four services against the local
mock, and drives every `docs/DEMO.md` use case with PASS/FAIL assertions read back from the service
logs and the SQL rows:

```powershell
.\run-all.bat              # interactive menu: pick a use case by id (2.1 ... 3.3)
.\run-all.bat outbound     # 2.1-2.8 unattended + PASS/FAIL summary
.\run-all.bat inbound      # 3.1-3.3
.\run-all.bat 2.5          # single use case by id
.\stop-all.bat             # stop all services (required before an IDE build — see below)
```

Each run opens with a plain-language briefing (direction, sender, receiver) and closes by asking
which use case to run next. Every outbound reset restarts the sender — per-tenant queue pauses and
the 10-second state cache live in memory and survive a store reset, which is what makes a repeated
demo silently do nothing. If a service cannot be killed, the runner aborts instead of testing stale
code.

**Build gotcha:** a running service locks its own binary, so `dotnet build` fails with MSB3021 while
any service (or an orphan from an earlier run) is alive. `stop-all.bat` clears them; the runner does
it automatically before every build. Service stdout goes to `logs\<service>.log`.

**Port gotcha:** All inter-service wiring uses the `Urls` value in each project's `appsettings.json`, but `dotnet run` with the default launch profile applies `launchSettings.json` ports (5070/5007/5257/5248), which breaks that wiring. Use `--no-launch-profile` (and set `ASPNETCORE_ENVIRONMENT=Development` yourself when needed, e.g. for InstagramSenderApi's mock-server base URL override or WebhookIngestApi's `/webhook/slow` endpoint).

| Project | Port (appsettings `Urls`) |
|---|---|
| InstagramSenderApi | 5001 |
| WebhookIngestApi | 5002 |
| WebhookTrafficSimulator | 5010 |
| InstagramGraphMock | 5020 |

### Exercising the outbound system
```powershell
# Start InstagramGraphMock, then InstagramSenderApi (Development env points GraphApiBaseUrl at localhost:5020)
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"GradualApproach","tenantId":"tenant-1"}'
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-1","payload":{},"targetEndpoint":"/v25.0/tenant-1/messages"}'
curl http://localhost:5020/simulator/state/tenant-1
```
Predefined mock scenarios: `GradualApproach`, `SuddenBlock`, `FlappingBlock`, `MultiTenantMix`, `RecoveryTest`, `PerSecondRateLimit`, `RetryAfter429` (HTTP 429 + `Retry-After` header, unrecognised error code), `SharedAppBudget` (store-level X-App-Usage override shared by all tenants), `InstagramBucBlock` (error 80002 — the Instagram BUC code — on HTTP 400, no `Retry-After`), `CustomRateLimit613` (error 613 subcode 1996, no `estimated_time_to_regain_access`), `AppLevelBlock` (error 4 — app-level, so the block must land on the shared `app:{AppId}` row and hold *every* tenant). Control surface: `/simulator/state`, `/simulator/configure/{tenantId}`, `/simulator/block/{tenantId}`, `/simulator/app-usage`, `/simulator/reset`.

### Exercising the inbound system
```powershell
# Start WebhookIngestApi, then WebhookTrafficSimulator
curl -X POST http://localhost:5010/simulator/run -H "Content-Type: application/json" -d '{"scenarioName":"BurstSingleIp","durationSeconds":15}'
curl -X POST http://localhost:5010/simulator/run-all
```
Scenarios (registered in `WebhookTrafficSimulator/Program.cs`): `SteadyTraffic`, `BurstSingleIp`, `DDoSMultiIp`, `OversizedPayload`, `InvalidSignature`, `SlowLoris`, `GlobalFlood`, `MixedAttack`. `/simulator/run` blocks until the scenario finishes — use a generous client timeout.

## Architecture

### InstagramSenderApi — outbound (two-layer strategy)

- **Layer 1, proactive:** `InstagramRateLimitHandler` (a `DelegatingHandler` on the typed `HttpClient`) parses `X-App-Usage` / `X-Business-Use-Case-Usage` headers from every response and persists worst-case usage % per tenant to SQL via `TenantRateLimitService` → `SqlTenantRateLimitRepository` (Dapper). X-App-Usage is recorded **only** on a global `app:{AppId}` row (the budget is shared by all accounts on the FB app) and per-account BUC figures **only** on the tenant row — the two levels are never mixed. App-level error codes (4, 613) block the app row, not the account that happened to receive them; account-level codes (17, 32, 80001, 80002, 80006) block the tenant row. Pre-flight is a list of `IOutboundGate` implementations that `InstagramThrottleGuard.EnforceAsync` runs in ascending `Order`: each gate returns the wait it requires and the guard does the waiting, so a gate needs no clock and is unit-testable. `HeaderUsageThrottleGate` throttles on `max(tenantPct, appPct)` ≥ 80% and lets `TenantBlockedException` propagate when **either** row's `BlockedUntilUtc` is in the future; `PerSecondDispatchGate` runs **last** (its token is for dispatching *now*, so earlier waits must already have elapsed) as a token bucket per tenant **per dispatch class** — Meta's per-second caps differ by call class: text 100/s, audio/video 10/s, Conversations 2/s, unclassified 2/s; `DispatchClassifier` picks the class from the endpoint plus the payload's attachment type and it travels to the gate on an `OutboundDispatch`. All guard enforcement is gated by `RateLimiting:Outbound:Enabled`; the handler always observes.
- **Layer 2, reactive:** `InstagramResiliencePipeline` (Polly v8 via `AddResilienceHandler`) — 15s timeout, 3 retries on 429/Graph error codes 4/17/32/613/80001/80002 (delay from `estimated_time_to_regain_access` when present, else exponential backoff), per-tenant circuit breaker so one tenant tripping doesn't affect others. A plain HTTP 429 is also treated as rate-limited, honouring the standard `Retry-After` header (delta-seconds or HTTP-date).
- **Flow:** `POST /send` (`SendController`) → per-tenant bounded `Channel<SendJob>` in `SendQueueWorker` (singleton + hosted service; re-enqueues jobs on `TenantBlockedException`) → `InstagramClient` → handler + Polly pipeline. The tenant ID travels on `HttpRequestMessage.Options`.
- **Persistence:** SQL Server LocalDB (`(localdb)\mssqllocaldb`, database `SenderDB`, connection string `SenderDb`). The `TenantRateLimitState` table is auto-created at startup via `EnsureTableExistsAsync`. Jobs themselves are in-memory only (lost on restart — known open decision; outbox pattern is the documented upgrade path).

### WebhookIngestApi — inbound

- All enforcement happens in `InboundRateLimitMiddleware` → `InboundRateLimitPipeline.EvaluateAsync`, which runs a pipeline of `IInboundRule` implementations (`RateLimit/Rules/`) fail-fast in ascending `Order` — the first rule to return a non-null `LimitDecision` ends it, whether that denies or (allow-list) allows. Order: block list (403) → allow-list bypass → payload size (413/411) → HMAC of `X-Hub-Signature-256` (401) → global window → per-IP window → per-client window (429 + `Retry-After`/`X-RateLimit-*` headers) → per-client `SemaphoreSlim` concurrency cap (`ConcurrencyRule`, released on `Response.OnCompleted`). **The order is a security property, not wiring** — cheap checks before I/O, HMAC before any counter moves so forged traffic cannot spend a real client's quota — which is why it lives in `RuleOrder` rather than in DI registration order. Adding a limit is a new rule class plus one DI line. Before any of that, the middleware bypasses GET requests entirely (Meta's `hub.challenge` verification must never be throttled) and any `ExcludedPaths` prefix; `Enabled: false` disables the middleware and `ObserveOnly: true` computes+logs every would-be denial but passes all traffic (rollout observation mode).
- Counting goes through `IRateLimitStore.TryAcquireAsync(key, policy)`. The `RateLimitPolicy` names the mechanism, so each scope picks its own: `SlidingWindow` (default everywhere — every figure in `docs/DEMO.md` was measured against it), `FixedWindow` (constant memory, boundary burst), `TokenBucket` (paces rather than counts). `InMemoryRateLimitStore` owns per-key buckets in a `ConcurrentDictionary` plus their locking and pruning; the algorithms in `RateLimit/Strategies/` own only the maths and are always called under the key's lock. Switch a scope with `GlobalAlgorithm` / `PerIpAlgorithm` / `PerClientAlgorithm`; a Redis store is still a single DI-line change.
- Client identity comes from `X-Webhook-Source` / `X-Api-Key`, falling back to remote IP (`ClientIdentityResolver`, resolved once per request and shared by every rule); simulators fake IPs via `X-Forwarded-For`.
- `Program.cs` order matters: `EnableBuffering()` must run before the middleware so the body can be read for size/HMAC and rewound for the endpoint.
- Config binds `InboundRateLimitOptions` from the `RateLimiting:Inbound` section (the sender's outbound options bind `RateLimiting:Outbound` — section shapes match the IGAutopilot integration plan). `HmacSecretKey` (proxy) and `Simulator:HmacSecret` (simulator) hold a matching dev-only placeholder secret in checked-in config (empty secret disables HMAC validation entirely and logs a warning); never commit real secrets. `TrustForwardedFor` is `true` in checked-in config so the simulator's fake `X-Forwarded-For` IPs work locally — it must be `false` in production unless behind a trusted reverse proxy, or per-IP limits can be bypassed by header spoofing.

### Simulators

Both simulators are dependency-light minimal APIs. `InstagramGraphMock` keeps mutable per-tenant state in `TenantStateStore` (a `ConcurrentDictionary`) and mirrors the real Graph API URL surface (`/v25.0/{tenantId}/media`, `/messages`, etc.), so switching InstagramSenderApi between mock and production is a single `GraphApiBaseUrl` config change. Note the Graph API signals rate-limit errors with HTTP 400 + error body codes, not 429 — the mock reproduces this. `WebhookTrafficSimulator` scenarios implement `IScenario` and are registered by name in its `Program.cs`; add new attack patterns there.
