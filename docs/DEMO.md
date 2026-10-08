# Standalone Demo Runbook

End-to-end demo of both rate-limiting systems using only this workspace — the simulators stand in for Meta and for attackers. **No IGAutopilot, no internet, no real Graph API.** Expected outputs are actual observed values.

Prerequisites: .NET 9 SDK, SQL Server LocalDB (`(localdb)\mssqllocaldb` — the sender creates `SenderDB` automatically), `curl`.

Graph version: **v25.0**. The mock serves that one version only ([GraphApiEndpoints.cs:15-18](../InstagramGraphMock/Endpoints/GraphApiEndpoints.cs#L15-L18)), and the sender's Development base URL points at it, so a version bump is two files in lockstep — a mismatch shows up as a 404 the sender reports as a generic failure.

## 0. The fast path — `run-all.bat`

Everything below is automated. [run-all.bat](../run-all.bat) stops leftovers, builds, starts LocalDB and all four services, proves the sender is wired to the mock, then opens a menu:

```powershell
.\run-all.bat              # interactive menu (recommended)
.\run-all.bat outbound     # 2.1-2.8 back to back, defaults, PASS/FAIL summary
.\run-all.bat inbound      # 3.1-3.3
.\run-all.bat 2.5          # one use case by id (an ordinal like 5 also works)
.\run-all.bat attach       # menu only, against services already running
.\stop-all.bat             # stop everything (run before an IDE build — see below)
```

**You pick use cases by id** — `2.1`, `2.2`, … `3.3`, the same ids used in this document. Nothing
else runs.

**Before each run** the console states, in plain language, what the use case is, the direction, and
who calls whom:

```text
==========================================================================
  USE CASE 2.7 - Our whole app is banned - EVERY account must stop
==========================================================================
  Direction : OUTBOUND - we call Instagram
  Sender    : InstagramSenderApi     - OUR app that sends to Instagram      (:5001)
  Receiver  : InstagramGraphMock     - stands in for Instagram              (:5020)
  In plain words:
     One account gets the error, but the ban covers the whole app.
     An unrelated, perfectly healthy account is stopped before it even dials out.
  Detail    : Error 4 must land on the shared app row, not on the account that received it.
```

**After each run** it asks what to do next — Enter takes the immediate next use case, or type any id
to jump:

```text
  Next use case: 2.8 - Video is capped 10x tighter than text - same endpoint
  [Enter] run it   |   or type a use case id (2.1 - 3.3)   |   m = menu   |   q = quit
```

Every knob is prompted with the documented value as the default (tenant id, block minutes, caps,
durations), so pressing Enter reproduces this runbook exactly. Each run then resets state, executes,
reads the service log back and prints **PASS/FAIL per expected line** plus the `TenantRateLimitState`
rows. Logic lives in [scripts/Demo.ps1](../scripts/Demo.ps1).

What the runner handles that manual steps get wrong:

- **Restarts the sender as part of every outbound reset.** Two kinds of sender state survive a store
  reset: the 10-second cache *and* per-tenant queue pauses that last minutes. Without the restart,
  running 2.5 twice inside its own 9-minute block window looks broken — `/send` returns 202 and then
  nothing happens, because the job sits behind a tenant that is still sleeping. Restarting also
  empties the cache, which is why the runner needs no 11-second wait.
- **Applies per-demo settings and restores defaults after** (2.1 needs a low text-class gate, 3.3
  needs `ObserveOnly`), so no override leaks into the next use case.
- **Kills orphaned service processes before building.** A running service locks its own binary and
  `dotnet build` fails with MSB3021 — the most common local build failure here. If a process cannot
  be killed, the runner **stops** rather than demo against stale code; it prints the PID and the
  reason.
- **Refuses to run if the sender is not pointed at the local mock** (`Assert-MockOnly`), then proves
  it by sending one probe call and reading it back off the mock (`Test-Wiring`). There are no
  credentials anywhere in this solution, so a real Graph call could not authenticate even if
  misconfigured, but the guard fails closed rather than relying on that.

**Visual report — one HTML file per session.** Every runner session writes
`reports\run-<yyyyMMdd-HHmmss>.html` (gitignored), rewritten after each use case so an interrupted
session still leaves a complete file. It is self-contained (data embedded, inline SVG, no CDN) and
opens offline. Per use case it draws what the run actually produced:

| Id | Chart |
|---|---|
| 2.1 | calls in any rolling second against the mock cap and our gate; error responses in red |
| 2.2, 2.5, 2.6, 2.7 | first seconds per tenant/app lane, then the block windows split into Meta's wait, the +1 min buffer and the queue pause, ending on `BlockedUntilUtc` from SQL |
| 2.3 | usage per stored row against the 80 % line, plus the throttle delay the fresh tenant took |
| 2.8 | three bursts on one timeline with gate waits, and a per-class pacing table |
| 3.1, 3.3 | responses per second by status; in 3.3 a line for what enforce mode would have denied; denial reasons |
| 3.2 | the rule pipeline with this run's two requests drawn through their bypass |

Assertions, parameters, SQL rows, mock state and the raw log slices sit under each chart. Two
honest limits: the runner stops watching after a few seconds, so block windows beyond that point
are drawn faded as *scheduled* rather than observed; and request/response pairing is only drawn
where a single tenant had calls in flight. The timeline needs timestamps, so the runner starts every
service with `Logging__Console__FormatterOptions__TimestampFormat` / `SingleLine` set by environment
variable — checked-in appsettings are unchanged, and attach mode (`-SkipStart`) has no timelines.
Template: [scripts/report-template.html](../scripts/report-template.html).

Service output goes to `logs\<service>.log` (gitignored). Menu option **L** opens live tail windows;
**S** shows port/row status; **R** resets outbound state; **B** stops, rebuilds and restarts;
**A**/**I** run all outbound/inbound use cases unattended.

## Presenting this — running order and narration

Ids are never renumbered: `2.1`–`3.3` are the same strings in this document, in the `run-all.bat`
menu and in `$DemoCatalog` ([scripts/Demo.ps1:57-158](../scripts/Demo.ps1#L57)). Present in the
order below, not in numeric order.

### Rehearse, then present

```powershell
.\run-all.bat outbound     # 2.1-2.8 unattended + PASS/FAIL summary   (~3 min, measured)
.\run-all.bat inbound      # 3.1-3.3                                  (~2 min)
```

Do this before the session. It proves the build is green today and leaves `logs\*.log` as fallback
evidence if a live run misbehaves. Then present from the interactive menu — that is where the
plain-language briefing and the "next use case" prompt live:

```powershell
.\run-all.bat              # pick ids in the act order below; Enter accepts every documented default
```

Keep menu option **L** (live log tail windows) on a second screen. The sender log *is* the demo —
`/send` only ever returns `202`, so an audience watching HTTP responses sees nothing happen.

### The arc — four acts, one claim each

Open by naming the two directions: **outbound** protects Instagram from us, **inbound** protects us
from the internet. They share no code and are demonstrated by different pairs of services.

| Act | Use cases | The one sentence to say |
|---|---|---|
| 1. Don't get blocked | 2.1, 2.8 | "We pace ourselves client-side, and the cap depends on *what* we send — same `/messages` endpoint, video 10/s, text 100/s, chat lists 2/s." |
| 2. When blocked, obey correctly | 2.2, 2.5, 2.6 | "Three ways Meta says *stop*, each read from a different place: a `Retry-After` header, a BUC code's ETA, and a subcode carrying no wait time at all." |
| 3. Blast radius | 2.3, 2.7 | "The quota is shared by every account on the app. A healthy account that never made a call gets held — and the account that *received* the error is not punished for it." |
| 4. Inbound | 3.1 (BurstSingleIp, then GlobalFlood), 3.2, 3.3 | "Bounded accepts, everything else 429 with `Retry-After` — then the two things that must never be throttled, then the rollout switch." |

Act 3 is the one to slow down on: it is the only part that cannot be inferred from reading the
config, and 2.7's SQL rows are the proof, not the log lines. Put the `SELECT` output on screen.

Close on **3.3**, not on an attack scenario. It answers "how dare you switch this on in
production": log every would-be denial for a week, choose thresholds from real traffic, then
enforce — rollout step P1 in [INTEGRATION_PLAN_IGAutopilot.md](INTEGRATION_PLAN_IGAutopilot.md).

**Skip 2.4 live** (five classic scenarios, watch-only, nothing asserted). Mention it exists.

### What each use case proves

| Id | What the case is | Asserted by the runner |
|---|---|---|
| 2.1 | We pace ourselves under Meta's per-second cap, so the cap is never hit | yes |
| 2.2 | A 429 with an unrecognised error code is still obeyed — via the `Retry-After` header alone | yes |
| 2.3 | One quota is shared by all accounts; a fresh account's *first* call is already throttled, yet each row keeps its own percentage | yes |
| 2.4 | Classic scenarios (gradual approach, sudden block, flapping, multi-tenant mix, recovery) | no — watch only |
| 2.5 | Instagram's real BUC code 80002 arrives on HTTP 400 with no header; only the code list can classify it | yes |
| 2.6 | 613/1996 gives no wait time at all, so we supply a 15-minute floor — and it is app-scoped | yes |
| 2.7 | An app-level ban stops *every* account, including one that never called | yes |
| 2.8 | Per-second caps differ by call class on the same endpoint | yes |
| 3.1 | Attack traffic (flood, DDoS, oversized, bad signature, slow-loris) is bounded | no — prints observed counts against an expected shape |
| 3.2 | Health checks and Meta's GET handshake are never blocked, even mid-attack | yes |
| 3.3 | Observe-only computes every denial and blocks nothing | yes |

Nine of the eleven carry PASS/FAIL assertions. Do not claim 11/11 — 2.4 and 3.1 print observations,
and someone will check.

### Three things to get right while narrating

- **3.1's split is not a fixed number.** `{"202":60,"429":140}` and `{"202":47,"429":153}` are both
  correct — it depends where the burst lands inside the sliding minute. Say this *before* the numbers
  appear, or it reads as a regression.
- **2.7's evidence is the DB, not the log.** Block on `app:local-dev-app`; `tenant-guilty` at its own
  20 % and *unblocked*; no `tenant-innocent` row at all, because the guard threw before the network.
- **Nothing can reach real Meta.** `Assert-MockOnly` refuses to start unless the sender's base URL is
  `localhost:5020`, `Test-Wiring` proves the mock received a call, and there are no credentials in
  the solution. State this up front rather than when someone asks.

## 1. Start the services manually (PowerShell, one terminal each)

```powershell
cd "d:\AI Native Assignments\RateLimit"
dotnet build RateLimit.sln

sqllocaldb start mssqllocaldb    # the sender exits at startup if LocalDB is stopped

$env:ASPNETCORE_ENVIRONMENT = 'Development'   # set in EACH terminal

# Terminal 1 — Graph API mock (port 5020)
dotnet run --project InstagramGraphMock --no-launch-profile

# Terminal 2 — Sender API (port 5001). For demo 2.1 use a low text-class gate:
$env:RateLimiting__Outbound__PerSecondDispatchLimit = '4'
dotnet run --project InstagramSenderApi --no-launch-profile

# Terminal 3 — Webhook ingest API (port 5002)
dotnet run --project WebhookIngestApi --no-launch-profile

# Terminal 4 — Traffic simulator (port 5010)
dotnet run --project WebhookTrafficSimulator --no-launch-profile
```

`--no-launch-profile` is required — launch-profile ports break the inter-service wiring (see CLAUDE.md).

### Resetting between outbound demos

Two independent stores must be cleared, and one cache must be waited out. (`run-all.bat` does all of
this for you, and restarts the sender on top — see §0.)

```powershell
curl -X POST http://localhost:5020/simulator/reset                     # mock per-tenant state
curl -X POST http://localhost:5020/simulator/app-usage -H "Content-Type: application/json" -d '{"pct":null}'   # clear the shared X-App-Usage override
sqlcmd -S "(localdb)\mssqllocaldb" -d SenderDB -Q "DELETE FROM TenantRateLimitState;"
Start-Sleep 11    # TenantRateLimitService caches state for 10 s — without this wait the
                  # sender still sees the pre-reset percentages and blocks
```

**Why this matters now:** a stale `app:{AppId}` row at 100 % throttles *every* tenant (that is the
point of demo 2.3), and since app-level blocks are honoured across tenants (demo 2.7), a leftover app
block will make unrelated demos look broken. Reset before each outbound demo, or use fresh tenant ids
and accept the app row's carry-over. Skipping the 11-second wait is the single most common cause of a
demo "not reproducing".

**Third kind of state, and no API clears it:** the sender's queue holds per-tenant pauses that last
as long as the block (9 minutes for demo 2.5). Re-running a demo for the *same tenant id* inside that
window returns 202 from `/send` and then does nothing visible — the job is queued behind a sleeping
tenant. Restart the sender, or use a fresh tenant id.

**Mock artifact to know about:** unless you set a global override via `/simulator/app-usage`, the mock
derives `X-App-Usage` from the *same* per-tenant counter it uses for the BUC header
([AppUsageHeaderBuilder.cs:16](../InstagramGraphMock/HeaderBuilders/AppUsageHeaderBuilder.cs#L16)). So any scenario that drives one tenant to 100 % — 2.5 and 2.7 both do —
also leaves the shared app row at 100 %, and the *next* demo opens with
`Proactive throttle … delaying 60000 ms` before anything else happens. Observed exactly this while
verifying 2.6. Either reset in between, or wait the delay out.

---

## 2. Outbound demos (Sender → Graph mock)

### 2.1 Per-second dispatch gate (prevention of per-second caps)

**What it is:** Meta caps calls per second per account. We never let a call reach the network faster
than our own cap, so Meta never has to refuse one.

**How it's implemented:** the pre-flight sequence is a list of
[IOutboundGate](../InstagramSenderApi/Instagram/Services/IOutboundGate.cs) implementations; each
returns how long the call must wait and `InstagramThrottleGuard` does the waiting, in ascending
`Order`
([InstagramThrottleGuard.cs:50-54](../InstagramSenderApi/Instagram/Services/InstagramThrottleGuard.cs#L50-L54)).
`PerSecondDispatchGate` is deliberately last — the slot it hands out is for dispatching *now*, so
anything that sleeps must already have slept. The gate keeps a sliding-window log keyed
`{tenantId}|{class}`: the dispatch times of the last N calls (N = the cap). A caller is given the
later of "now" and "the oldest of those N plus one second", so any N+1 consecutive calls span at
least a second — never more than the cap in **any** one-second span, from the very first call — while
a burst up to the cap still leaves at once. Concurrent callers serialise into increasing delays
instead of spinning
([PerSecondDispatchGate.cs](../InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs)).
The cap in force comes from `RateLimiting:Outbound:PerSecondDispatchLimit`.

Mock enforces 5 calls/s; the sender's gate allows at most 4 in any second, so the cap is never hit.
The one-call margin is deliberate: the gate's guarantee holds when we dispatch, and network latency
can bunch calls slightly on arrival.
An empty payload on `/messages` classifies as the **text** class, so the override above
(`PerSecondDispatchLimit`) is the cap in force here — see demo 2.8 for the other classes.

```powershell
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"PerSecondRateLimit","tenantId":"ps-tenant","perSecondLimit":5}'
# fire 15 sends as fast as the shell allows
1..15 | ForEach-Object { curl -s -X POST http://localhost:5001/send -H "Content-Type: application/json" -d "{`"tenantId`":`"ps-tenant`",`"payload`":{},`"targetEndpoint`":`"/v25.0/ps-tenant/messages`"}" }
Start-Sleep 8
curl http://localhost:5020/simulator/state/ps-tenant
```

**Expected:** sender log shows 15 × `Sent job for tenant ps-tenant`, zero rate-limit errors; mock state shows `isBlocked: false`, `callCountPct: 30` (= exactly 15 calls). Re-run with the gate disabled (`Enabled: false` or a high `PerSecondDispatchLimit`) to watch the mock return error code 17 instead.

### 2.2 Retry-After header on HTTP 429 (reactive)

**What it is:** Instagram refuses with HTTP 429 carrying an error code we do not recognise. The only
usable instruction in the whole response is the `Retry-After` header.

**How it's implemented:** the 429 path is deliberately separate from the error-code path.
`RetryAfterMinutesFromHeader` accepts both the delta-seconds and the HTTP-date form, floors at one
minute, and stashes the result on `HttpRequestMessage.Options`
([InstagramRateLimitHandler.cs:180-195](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L180-L195)).
Polly reads that option back and adds a one-minute safety buffer
([InstagramResiliencePipeline.cs:103-108](../InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L103-L108)),
retrying inline only while the delay is within `MaxInlineRetryDelay` = 2 min
([InstagramResiliencePipeline.cs:30](../InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L30)).
Three minutes exceeds that, so the pipeline stops and `SendQueueWorker` pauses the tenant and
re-queues the job
([SendQueueWorker.cs:130-132](../InstagramSenderApi/Instagram/Workers/SendQueueWorker.cs#L130-L132)).

Mock answers 429 + `Retry-After: 90` with an *unrecognised* error code (Meta's `1` — "API Unknown") — only the header can save the sender. The body code here was 613 until the August-2026 pass made 613 a recognised rate-limit code; it had to change, or the scenario would pass via the code list and stop testing the header at all.

```powershell
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"RetryAfter429","tenantId":"ra-tenant","retryAfterSeconds":90}'
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"ra-tenant","payload":{},"targetEndpoint":"/v25.0/ra-tenant/messages"}'
```

**Expected sender log:**

```text
HTTP 429 for tenant ra-tenant: Retry-After → 2 min
Tenant ra-tenant is blocked for 2 min (+ 1 min buffer)
Tenant ra-tenant rate-limited (TooManyRequests) — pausing 0:03:00, then re-queuing (attempt 1)
```

Single HTTP attempt only (3 min exceeds the 2-min inline-retry cap — the queue handles it, per Meta's "stop calling" guidance). After ~3 min the job sends automatically.

### 2.3 Shared app-level budget (X-App-Usage is one budget for ALL accounts)

**What it is:** `X-App-Usage` is one budget shared by every account on the FB app, so one busy
account slows all of them — while each account's own BUC figure must stay its own.

**How it's implemented:** the handler writes two rows, never one: BUC figures to the tenant row,
`X-App-Usage` to the global `app:{AppId}` row
([InstagramRateLimitHandler.cs:220-236](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L220-L236)).
The account row is written only when there is account-level evidence
(`sawBucSignal || (isRateLimitError && !isAppLevelBlock) || success`) — without that guard an
app-usage-only response persists the app's 92 % over the account's real 5 % (finding A12.8).
`GetThrottleDelayAsync` then throttles on `max(tenantPct, appPct)` at or above 80 %, spreading the
remaining quota across the rest of the hour and clamping the delay to 200 ms - 60 s
([TenantRateLimitService.cs:108-118](../InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L108-L118)).

One tenant observes 92% app usage; a *different, fresh* tenant must slow down immediately.

```powershell
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"SharedAppBudget","appUsagePct":92}'
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-heavy","payload":{},"targetEndpoint":"/v25.0/tenant-heavy/messages"}'
Start-Sleep 3
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-light","payload":{},"targetEndpoint":"/v25.0/tenant-light/messages"}'
```

**Expected sender log:** `Proactive throttle for tenant tenant-light: delaying 9375 ms` — tenant-light's **first ever** call is throttled purely by the shared `app:local-dev-app` state row that tenant-heavy's response populated. Verify the rows:

```powershell
sqlcmd -S "(localdb)\mssqllocaldb" -d SenderDB -Q "SELECT TenantId, MaxCallCountPct, BlockedUntilUtc FROM TenantRateLimitState" -W
# → app:local-dev-app | 92 | NULL
# → tenant-light      |  5 | NULL     ← its OWN BUC figure, not the app's 92
```

That second row is the level-separation check: app-level and account-level percentages are stored on
separate rows and never merged. Before the August-2026 fix this row also read `92`, which made every
account look as hot as the shared budget. Throttling is unchanged — the guard still decides on
`max(tenantPct, appPct)`, which is why tenant-light is delayed despite its own 5 %.

### 2.4 Classic scenarios (previously verified)

**What it is:** the five original mock scenarios — quota creeping up, a sudden ban, a flapping ban, a
multi-tenant mix, and recovery.

**How it's implemented:** no sender code of its own. The mock drives the responses from
[TenantStateStore.cs](../InstagramGraphMock/State/TenantStateStore.cs) and the sender reacts through
the same two layers every other case uses. The runner treats this one as watch-only.

`GradualApproach` (proactive delays grow 3.75 s → 15 s past 80%, then 5-min block persisted, queue pauses and auto-resumes), `SuddenBlock`, `FlappingBlock`, `MultiTenantMix` (blocked tenant never delays healthy ones), `RecoveryTest`. Same pattern: apply scenario at :5020, send via :5001, watch sender log + `/simulator/state/{tenantId}`.

### 2.5 Instagram BUC block — error 80002 (the code Meta actually returns for Instagram)

**What it is:** one account has exhausted its own Business Use Case quota. Meta signals it with error
**80002 on HTTP 400 and no `Retry-After`** — nothing but the recognised-code list can classify it.

**How it's implemented:** 80002 is in `RateLimitErrorCodes` and deliberately *not* in
`AppLevelErrorCodes`
([InstagramRateLimitHandler.cs:29](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L29),
[:46](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L46)), so the block
routes to the tenant row and the shared app row is left alone
([:211](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L211)). The wait
is `max(body ETA, BUC-header ETA, 1-minute floor)`
([:146-152](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L146-L152)).

Delivered on **HTTP 400 with no `Retry-After`**, exactly like the real Graph API, so nothing but the
recognised-code list can classify it. Before this code was recognised the response took the
generic-failure path: no block, no retry.

```powershell
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"InstagramBucBlock","tenantId":"tenant-buc","blockForMinutes":8}'
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-buc","payload":{},"targetEndpoint":"/v25.0/tenant-buc/messages"}'
```

**Expected sender log:**

```text
Rate-limit error for tenant tenant-buc: code=80002, subcode=0, level=account, retryAfter=8min
Tenant tenant-buc is blocked for 8 min (+ 1 min buffer)
Tenant tenant-buc rate-limited (BadRequest) — pausing 0:09:00, then re-queuing (attempt 1)
```

Note `level=account` — the block lands on the tenant row, which is correct for a BUC code. Confirm
with the raw mock response if you want to see the wire format:
`curl -i -X POST http://localhost:5020/v25.0/tenant-buc/messages -H "Content-Type: application/json" -d "{}"`.

### 2.6 Custom rate limit — error 613 / subcode 1996

**What it is:** Meta has flagged our request *shape* rather than our volume — error 613 subcode 1996,
carrying no `estimated_time_to_regain_access` at all.

**How it's implemented:** the subcode alone selects a 15-minute floor in place of the 1-minute
default
([InstagramRateLimitHandler.cs:37](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L37),
[:146-150](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L146-L150));
retrying on the default floor would walk straight back into the flag. 613 is also in
`AppLevelErrorCodes`, so the block lands on `app:{AppId}` rather than on the tenant that happened to
receive the error.

Meta sends this when it considers the app's *request volume shape* inconsistent, with **no**
`estimated_time_to_regain_access`. The 1-minute default floor would retry straight back into the
flag, so this subcode takes a 15-minute floor. It is also an **app-level** code, so the block lands
on the shared row.

```powershell
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"CustomRateLimit613","tenantId":"tenant-613"}'
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-613","payload":{},"targetEndpoint":"/v25.0/tenant-613/messages"}'
```

**Expected sender log:**

```text
Rate-limit error for tenant tenant-613: code=613, subcode=1996, level=app, retryAfter=15min
Tenant app:local-dev-app is blocked for 15 min (+ 1 min buffer)
Tenant tenant-613 rate-limited (BadRequest) — pausing 0:16:00, then re-queuing (attempt 1)
```

The 15 minutes come from the subcode alone — nothing in the response says how long to wait. Note the
block is on `app:local-dev-app`, not on `tenant-613`: 613 is an app-level code. If you ran demo 2.5
first without resetting, a `Proactive throttle … delaying 60000 ms` line precedes all of this.

### 2.7 App-level block — error 4 holds *every* tenant

**What it is:** error 4 bans the whole app. Every account must stop — including one that never made a
call — and the account that received the error is not itself at fault.

**How it's implemented:** `isAppLevelBlock` flips the routing (`tenantBlockMinutes = null`,
`appBlockMinutes = blockMinutes`) and the handler synthesises 100 % app usage so the shared row
escalates
([InstagramRateLimitHandler.cs:157-162](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L157-L162),
[:208-218](../InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L208-L218)).
`GetThrottleDelayAsync` checks the app row's block right after the tenant's and throws
`TenantBlockedException` before any HTTP call is made
([TenantRateLimitService.cs:97-106](../InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L97-L106))
— which is why the unrelated tenant produces no state row at all. Clearing is deliberately
asymmetric: an account block is cleared by that account's next success, an app block is not, because
one account succeeding does not prove a shared budget recovered.

The demo that proves level routing. One tenant receives an app-level code; a **different, healthy**
tenant must be stopped before it makes a call. Reset first (§1) — a stale app row invalidates this.

```powershell
curl -X POST http://localhost:5020/simulator/scenario -H "Content-Type: application/json" -d '{"name":"AppLevelBlock","tenantId":"tenant-guilty","blockForMinutes":6}'
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-guilty","payload":{},"targetEndpoint":"/v25.0/tenant-guilty/messages"}'
Start-Sleep 5
curl -X POST http://localhost:5001/send -H "Content-Type: application/json" -d '{"tenantId":"tenant-innocent","payload":{},"targetEndpoint":"/v25.0/tenant-innocent/messages"}'
```

**Expected sender log:**

```text
Rate-limit error for tenant tenant-guilty: code=4, subcode=0, level=app, retryAfter=6min
Tenant app:local-dev-app is blocked for 6 min (+ 1 min buffer)
Tenant tenant-guilty rate-limited (BadRequest) — pausing 0:07:00, then re-queuing (attempt 1)
Tenant tenant-innocent held by an APP-level block for 00:06:55
```

```powershell
sqlcmd -S "(localdb)\mssqllocaldb" -d SenderDB -Q "SELECT TenantId, MaxCallCountPct, BlockedUntilUtc FROM TenantRateLimitState" -W
# → app:local-dev-app | 100 | <now + 7 min>   ← the block is here
# → tenant-guilty     |  20 | NULL            ← the account is NOT blocked; its own budget is fine
# → (no tenant-innocent row at all — it never reached the network)
```

Three things to point out while demoing: the block is on the **app** row, the receiving account is
**not** punished for an app-wide limit, and `tenant-innocent` produced no row because the guard threw
before any HTTP call. An account block is cleared by that account's next success; an app block is
**not** cleared by any tenant's success — one account succeeding does not prove a shared budget
recovered, so it expires on its own `BlockedUntilUtc`.

### 2.8 Per-second caps differ by call class (text 100/s vs audio-video 10/s)

**What it is:** two calls to the *same* `/messages` endpoint get caps an order of magnitude apart,
decided by the payload rather than the URL.

**How it's implemented:** `DispatchClassifier.Classify` reads endpoint and payload together —
`/conversations` to 2/s, `/media` and `/media_publish` to ContentPublish, `/messages` to MediaSend
when `message.attachment.type` is audio or video and TextSend otherwise, anything unrecognised to the
2/s floor
([DispatchClass.cs:39-59](../InstagramSenderApi/Instagram/Services/DispatchClass.cs#L39-L59)). A
malformed payload falls back to the text class rather than throwing: misclassifying one call must
never fail a send. The class travels to the gate on an `OutboundDispatch`, and each class keeps its
own bucket
([PerSecondDispatchGate.cs:49](../InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs#L49)),
so 100 text/s and 2 Conversations/s run concurrently instead of sharing one cap.

Both calls below hit the **same** `/messages` endpoint — only the payload differs, and that is what
selects the cap. Restart the sender without the `PerSecondDispatchLimit` override from §1 so the
defaults (100 / 10 / 2 / 2) are in force.

A `ForEach-Object { curl … }` loop is **too slow** to fill a per-second bucket (each curl process
costs tens of ms). Use one curl process with `--next` so 20 requests are enqueued in ~250 ms:

```powershell
'{"tenantId":"t-media","payload":{"message":{"attachment":{"type":"video"}}},"targetEndpoint":"/v25.0/t-media/messages"}' | Out-File media.json -Encoding ascii -NoNewline
'{"tenantId":"t-text","payload":{"message":{"text":"hi"}},"targetEndpoint":"/v25.0/t-text/messages"}'                     | Out-File text.json  -Encoding ascii -NoNewline

function Burst($file) {
  $a = @()
  1..20 | ForEach-Object { $a += @('--next','-s','-o','NUL','-X','POST','http://localhost:5001/send','-H','Content-Type: application/json','--data',"@$file") }
  curl.exe @($a[1..($a.Count-1)])
}
Burst media.json
Burst text.json
```

**Expected sender log** — ten waits for the media burst, none for the text burst:

```text
Per-second gate for tenant t-media: class=MediaSend cap=10/s → waiting 21.9999 ms
Per-second gate for tenant t-media: class=MediaSend cap=10/s → waiting 89.9999 ms
…10 lines total, then nothing for t-text (cap 100/s was never reached)
```

The Conversations class is the tightest at 2/s and needs only a handful of calls to show it (the mock
has no `/conversations` route, but the gate runs *before* the HTTP call, so the classification is
still demonstrated):

```powershell
1..6 | ForEach-Object { curl -s -X POST http://localhost:5001/send -H "Content-Type: application/json" -d "{`"tenantId`":`"t-conv`",`"payload`":{},`"targetEndpoint`":`"/v25.0/t-conv/conversations`"}" }
```

**Expected:** four `class=Conversations cap=2/s → waiting ~500 ms` lines (the first two calls pass
free, then each further call waits half a second). An endpoint the classifier does not recognise
falls to the same 2/s floor by design — guessing high is what gets an account blocked.

---

## 3. Inbound demos (Simulator → Ingest API)

### 3.1 Attack scenarios (enforce mode — the default)

**What it is:** floods from one IP, multi-IP DDoS, oversized bodies, forged signatures and slow-drip
connections aimed at the public webhook endpoint.

**How it's implemented:** one middleware and a pipeline of
[IInboundRule](../WebhookIngestApi/RateLimit/Rules/IInboundRule.cs) implementations, run fail-fast in
ascending `Order` by
[InboundRateLimitPipeline](../WebhookIngestApi/RateLimit/InboundRateLimitPipeline.cs#L35-L46) — block
list (403) → allow-list bypass → payload size (413) → HMAC of `X-Hub-Signature-256` (401) → global
window → per-IP window → per-client window (429 + `Retry-After` + `X-RateLimit-*`) → per-client
concurrency cap. The order is a security property, not wiring: the cheap checks must run before the
store is touched, and HMAC before any counter moves so forged traffic cannot spend a real client's
quota. Counting itself belongs to `IRateLimitStore`, whose in-memory implementation keys buckets in a
`ConcurrentDictionary` and prunes idle ones (Redis is a one-line DI swap). Checked-in defaults:
1000/min global, 60/min per IP, 100/min per client, burst 20, 10 concurrent per client, 1 MB maximum
body, all three windows counted with `SlidingWindow`.

```powershell
curl -m 120 -X POST http://localhost:5010/simulator/run -H "Content-Type: application/json" -d '{"scenarioName":"BurstSingleIp","durationSeconds":15}'
```

**Expected:** roughly `{"202":60,"429":140}` with `Retry-After: 59` and `X-RateLimit-*` headers — the per-IP limit (60/min) honoured, the flood rejected.

The accepted count is not a fixed number: it depends on where the burst lands inside the sliding
minute and how many of the 200 concurrent requests hit the per-client concurrency cap. `{"202":60}`
(2026-07-07) and `{"202":47,"429":153}` (2026-08-05) are both correct. The invariant to demo is the
*shape* — a bounded number accepted, everything else 429 with `Retry-After` — not the exact split.

Other scenarios (same call, change `scenarioName`): `SteadyTraffic` (all 202), `DDoSMultiIp`, `OversizedPayload` (413), `InvalidSignature` (401 × all), `SlowLoris` (10×202 + 5×429 via the concurrency cap — needs Development env for `/webhook/slow`), `GlobalFlood` (run ≥ 32 s to exceed 1000/min: observed `{"202":780,"429":180}`), `MixedAttack`. Or run everything: `curl -m 600 -X POST http://localhost:5010/simulator/run-all`.

### 3.2 Excluded paths + GET bypass (Meta-safety)

**What it is:** two things must survive even a saturated window — our own health probe, and Meta's
`hub.challenge` verification handshake.

**How it's implemented:** `IsBypassed` runs *before* evaluation, so a bypassed request never touches
a counter: any GET returns early (the handshake is a GET), and any `ExcludedPaths` prefix match skips
the entire pipeline, HMAC included
([InboundRateLimitMiddleware.cs:61-77](../WebhookIngestApi/RateLimit/InboundRateLimitMiddleware.cs#L61-L77)).
`/health` is the only excluded path in checked-in config.

With the global window saturated (right after the long GlobalFlood):

```powershell
curl -X POST http://localhost:5002/health -H "Content-Type: application/json" -d '{"x":1}'   # no HMAC signature!
curl http://localhost:5002/anything
```

**Expected:** `POST /health` → **200** (excluded path bypasses even HMAC — would be 401/429 otherwise); `GET /anything` → **404** (reached routing — GETs are never rate-limited, protecting Meta's `hub.challenge` verification handshake).

### 3.3 Observe-only mode (production rollout step P1)

**What it is:** the production rollout switch — compute every decision, log what *would* have been
rejected, reject nothing.

**How it's implemented:** the `ObserveOnly` check sits *after* `EvaluateAsync`, not before it
([InboundRateLimitMiddleware.cs:42-50](../WebhookIngestApi/RateLimit/InboundRateLimitMiddleware.cs#L42-L50)).
The full decision is made, the would-be status is resolved through the same `MapRejection` that
enforcement uses, one warning is logged, and `next(ctx)` runs anyway. That is why the logged limit
type and `Retry-After` are the real ones enforcement would have applied — trustworthy numbers are the
entire point of the mode.

Restart the ingest API with the flag, replay the same burst:

```powershell
$env:RateLimiting__Inbound__ObserveOnly = 'true'
dotnet run --project WebhookIngestApi --no-launch-profile
# then:
curl -m 120 -X POST http://localhost:5010/simulator/run -H "Content-Type: application/json" -d '{"scenarioName":"BurstSingleIp","durationSeconds":15}'
```

**Expected:** `{"202":200}` — nothing denied — while the ingest log records every would-be rejection:

```text
OBSERVE ONLY: would deny Ip → 429 for POST /webhook (retryAfter=59s)   (×142)
```

This is how production thresholds get chosen from a week of real traffic before enforcement is switched on.

---

## 4. Verified results summary

Last full re-verification: **2026-10-08 on Graph v25.0**, after replacing the per-second gate's
token bucket with a sliding-window log — **7/7 outbound and 2/2 asserting inbound demos PASS**
(runner `-Run all`, report `reports\run-20261008-170100.html`; an earlier `-Run outbound` the same
day was also 7/7), solution builds with 0 warnings, `RateLimit.Tests` 83/83 green. Previous:
2026-09-14 (6/7 + 2/2, 2.1 failing), 2026-08-05 (7/7 + 2/2); earlier manual verification: 2.1–2.3
and 3.x on 2026-07-07, 2.5–2.8 on 2026-08-04.

> **2.1 failed under the runner until 2026-10-08 — it was a real gate defect, not a demo race.**
> The token bucket started full and refilled at the cap, so a cold start admitted up to
> capacity + rate − 1 calls inside one second (7 for a 4/s gate) against a mock that allows 5; the
> 6th call drew code 17. The same arithmetic applies in production at 100/s. Fixed in
> [PerSecondDispatchGate.cs](../InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs): a
> sliding-window log admits at most N calls in **any** one-second span, cold start included, and
> still lets a burst up to N leave at once. Regression test:
> `A_cold_start_never_lets_more_than_the_cap_out_in_one_second`.
>
> One residual effect, visible in the report: the gate's guarantee holds when it hands out the
> slot. The HTTP request is logged a few ms later, and the first call after a start is slower to get
> there (34 ms on 2026-10-08), so on the wire two calls can sit slightly under a second apart. That is
> why the demo gate stays one below the mock's cap; production needs the same headroom against Meta.

| Demo | Feature | Observed |
|---|---|---|
| 2.1 | Per-second dispatch gate (O9) | 15/15 sent, 0 rate-limit errors at mock 5/s cap; busiest one-second span 4 at the gate (2026-10-08, sliding-window log). Failed 2026-09-14 under the old token bucket, see the note above |
| 2.2 | Retry-After on 429 (O11) | 90 s header → 2 min +1 buffer block persisted, job re-queued, no inline retry |
| 2.3 | Shared app budget (O10) | Fresh tenant's first send delayed 9,375 ms off the `app:{AppId}` row at 92% |
| 2.3 | Level separation (A12.8) | app row 92 %, `tenant-light` row **5 %** — the same row read 92 % before the fix |
| 2.5 | Instagram BUC code 80002 (A12.1) | `level=account`, 8-min ETA honoured → 9-min pause, job re-queued |
| 2.6 | 613 / subcode 1996 (A12.2) | 15-min floor applied from the subcode alone (no ETA in the response) |
| 2.7 | App-level block, code 4 (A12.7) | Block on `app:{AppId}`, receiving account unblocked at its own 20 %, unrelated tenant `held by an APP-level block for 00:06:55` with no HTTP call |
| 2.8 | Per-class per-second caps (A12.3) | 20 video sends → 2 waits at `cap=10/s` (longest 858 ms: the 11th call waits until the 1st is a second old, after which the queue's own pace keeps the rest inside the window); 20 text sends on the same endpoint → 0 waits; 6 `/conversations` → 4 waits at `cap=2/s` (2026-10-08). Under the token bucket media showed ~10 short waits |
| 3.1 | Per-IP window regression | 60×202 + 140×429 (2026-07-07), 47×202 + 153×429 (2026-08-05), Retry-After 59 |
| 3.1 | Global window | 780×202 + 180×429 at 1000/min |
| 3.2 | ExcludedPaths / GET bypass | POST /health 200 during saturated window; GET routed normally |
| 3.3 | Observe-only | 200×202 passed, 0 denied + 100 would-deny log lines |
| all | Mock-only guard | `Assert-MockOnly` + probe call read back off the mock at every startup |
