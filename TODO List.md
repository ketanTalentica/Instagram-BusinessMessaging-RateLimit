# TODO List — Rate Limiting Integration

Two buckets, deliberately separated:

- **Part A** — changes to make **in this workspace** (`RateLimit` solution). These make the components integration-ready and are fully testable here against `InstagramGraphMock` / `WebhookTrafficSimulator` before any code is copied.
- **Part B** — changes to be made later **in `IGAutopilot\Codebase`** (`D:\Users\ketank\TM\repos\TMExtensions\IGAutopilot\Codebase\src`). **Plan-only until explicitly approved — no code changes there yet.** Item IDs (O1–O21, I1–I12, D1–D4, P0–P5) match [docs/INTEGRATION_PLAN_IGAutopilot.md](docs/INTEGRATION_PLAN_IGAutopilot.md).

Status legend: `[ ]` pending · `[x]` done · `[~]` in progress

> **Re-checked against IGAutopilot 2026-07-31** (TMExtensions `e6010d164` + staged TK-8475 phase 2).
> Their side moved to **net10.0**, **Flyway-owned schema (EF migrations deleted)**, `[meta.ig]` schema,
> per-env `Config\*.appsettings.json`, `Workers\Worker.cs`, and now uses the **built-in .NET rate
> limiter** in both APIs. Part B items below carry the corrected references; the re-check produced two
> new **Part A** items (A10, A11) and two new plan deltas (**O12**, **I12**) — full drift table in
> the integration plan §7.
>
> **Re-checked again 2026-08-04** (TMExtensions `e8fff0a30`, TK-8706) against Meta's live rate-limit
> docs. Produced **A12** (all eight items now done) — Instagram BUC code `80002` missing, per-second
> cap not split by call class, app-level blocks landing on the account row, and their new
> `GraphErrorDetails` type as the Part B seam.
>
> **Re-checked again 2026-08-06** — Meta's rate-limiting doc re-read in full **plus** the IGAutopilot
> send path. Structural finding: they send with a **Page Access Token**, so **BUC limits apply and
> Platform limits do not**. Produced **A13** (Part A, 7 items) and two Part B items (**O18**, **O20**),
> corrected three claims, and reopened **D4**. Derivation, limit map, header semantics and code
> evidence: integration plan **§8**. Item IDs now run O1–O21 / I1–I12.
>
> **Re-checked again 2026-09-11** — independent read of the whole outbound flow against IGAutopilot
> `c4b79454b`. The A1–A13 design holds up: header parsing, the error-code set, L1/L2 separation and the
> asymmetric block-clear are all correct and should port unchanged (confirmations listed in **A14.0**).
> Produced **A14** — twelve findings that are *not* restatements of A10/A11/A13, weighted towards
> concurrency, unbounded state and the absence of any test project — plus one new Part B delta
> (**O22**) and **two corrections to stale claims** (A12.5's "no usage-header parsing on their side",
> and the orphaned-tests note in B-Side findings). Both stale claims were true when written; their side
> has moved since.

---

## Part A — Changes in THIS workspace (RateLimit solution)

**STATUS: COMPLETE (2026-07-07).** All items implemented, live-verified against the simulators (evidence: `docs/DEMO.md` §4 and `CODE_REVIEW_AND_TEST_PLAN.md` §3.1), solution builds clean (0 warnings).

### A1. Per-account per-second dispatch cap (plan delta **O9**) ✅
- [x] `PerSecondDispatchGate` (reservation-style token bucket per tenant) added in `InstagramSenderApi\Instagram\Services\`; limit via `RateLimiting:Outbound:PerSecondDispatchLimit` (default 100/s; per-message-class limits are a config change at integration time).
- [x] Enforced by `InstagramThrottleGuard.EnforceAsync` as the last pre-flight step, before the HTTP call.
- [x] Verified: 15 sends through a 4/s gate vs mock 5/s cap → 15/15 sent, zero code-17 errors, mock never blocked.

### A2. App-level shared budget (plan delta **O10**) ✅
- [x] `InstagramRateLimitHandler` mirrors `X-App-Usage` to global row `app:{AppId}` (`RateLimiting:Outbound:AppId`). *(Superseded by A12.7: the app row now DOES carry a block, for app-level codes 4 / 613.)*
- [x] `TenantRateLimitService.GetThrottleDelayAsync` throttles on `max(accountPct, appPct)`.
- [x] New mock scenario `SharedAppBudget` + `/simulator/app-usage` endpoint (store-level `GlobalAppUsagePct`). Verified: fresh tenant's first send delayed 9,375 ms from the app row at 92 %.

### A3. Honor standard `Retry-After` header (plan delta **O11**) ✅
- [x] Handler treats any HTTP 429 as rate-limited; parses `Retry-After` (delta-seconds + HTTP-date), merges with error-body ETA (ceil to minutes, min 1).
- [x] New mock scenario `RetryAfter429` (429 + header, unrecognised code; the mock now sends `1`, since 613 became a recognised rate-limit code in A12.1). Verified: 90 s → 2-min block (+1 buffer), job re-queued, no inline retry.

### A4. Middleware `ExcludedPaths` option (needed by **I3**) ✅
- [x] `InboundRateLimitOptions.ExcludedPaths` (prefix match, leading-slash normalised).
- [x] Middleware bypasses excluded paths and ALL GET requests (`hub.challenge` protection); `/health` endpoint added (GET+POST) as demo target.
- [x] Verified during saturated global window: unsigned `POST /health` → 200; `GET /anything` → 404 (routed, not denied).

### A5. Inbound observe/log-only mode (needed by **D2** / phase P1) ✅
- [x] `ObserveOnly` option (in renamed `RateLimiting:Inbound` section): full evaluation runs, would-be denial logged with status code, request passes.
- [x] Verified: `BurstSingleIp` in observe mode → 200×202 + 142 `OBSERVE ONLY: would deny Ip → 429` log lines.

### A6. net8.0 compatibility pass (needed by **P0**) ✅
- [x] Scratch net8.0 classlib linking all transferable sources compiles clean (Dapper 2.1.79, Microsoft.Data.SqlClient 7.0.1, Polly.Core 8.6.5).
- [x] One .NET 9-only API found and fixed in source: `HttpContent.LoadIntoBufferAsync(CancellationToken)` → parameterless overload. No other substitutions needed.

### A7. Config-flag surface matching the integration plan (§2.3) ✅
- [x] `RateLimiting:Outbound` options (`Enabled`, `PerSecondDispatchLimit`, `AppId`); guard enforcement gated by `Enabled`, handler always observes.
- [x] Inbound section renamed `WebhookRateLimits` → `RateLimiting:Inbound`; `Enabled` + `ObserveOnly` added; appsettings updated.
- [x] DB release script drafted: `docs/release-scripts/001_Create_TenantRateLimitState.sql` (idempotent, NVARCHAR(128) key to accommodate the `app:{FbAppId}` row, ExcludeFromMigrations note in header).

### A8. Standalone demo & internal test suite ✅
- [x] Mock scenario additions: `RetryAfter429`, `SharedAppBudget` (+ `/simulator/app-usage`); `PerSecondRateLimit` re-used for the gate proof.
- [x] `docs/DEMO.md` runbook written: start order, exact commands, expected outputs for every feature — all values are actually observed ones.
- [x] Verification pass run 2026-07-07: new features (per-second gate, Retry-After, shared budget, bypasses, observe mode) + regressions `BurstSingleIp` (exactly 60×202+140×429, Retry-After 59 — unchanged) and `GlobalFlood` (780×202+180×429 at 1000/min). Remaining classic scenarios (GradualApproach, SuddenBlock, SlowLoris, InvalidSignature, …) unchanged by these edits and retain their §3/§3.1 evidence; re-runnable any time via `DEMO.md`.

### A9. Docs & spec sync (after A1–A8) ✅
- [x] Both specs updated (July-2026 delta sections, renamed config, new options), `docs/TRD_RateLimiting.md` §5 known-gaps updated, `CLAUDE.md` (scenarios/config/doc map), `docs/INTEGRATION_PLAN_IGAutopilot.md` §5 marked O9–O11 implemented.
- [x] `CODE_REVIEW_AND_TEST_PLAN.md` §3.1 added with the 2026-07-07 evidence table; improvement-plan item 2 (per-second cap) marked done.

### Part A follow-ups — from the 2026-07-31 IGAutopilot re-check (NOT yet done)

A1–A9 above remain complete and verified. These two are new work in **this** workspace, both needed
before Part B phase P2/P0 respectively. Plan-only until approved — nothing implemented yet.

#### A10. Cap inline waiting so a delay never parks a consumer slot (plan delta **O12**)
- [ ] Add `RateLimiting:Outbound:MaxInlineDelaySeconds` (default 5) to `OutboundRateLimitOptions`.
- [ ] `InstagramThrottleGuard.EnforceAsync` ([InstagramThrottleGuard.cs:50](InstagramSenderApi/Instagram/Services/InstagramThrottleGuard.cs#L50)): if the proactive delay exceeds the cap, **do not `Task.Delay`** — surface the block immediately (`TenantBlockedException` / a shed result carrying `RetryAfter`) so the job re-queues instead of sleeping. Same treatment for a long `PerSecondDispatchGate.WaitAsync` wait.
- [ ] **Why:** in our solution the caller is a `Channel` consumer, so sleeping is free; in IGAutopilot it is a RabbitMQ consumer holding a prefetched unacked message, and parking it is the exact failure their ADR-0008 exists to prevent (Polly's 14 s sleeps × 200 workers). Our measured proactive delay already reached **9,375 ms** in the A2 demo.
- [ ] Verify in-workspace with the `SharedAppBudget` scenario: delay above the cap → immediate shed + re-queue (no 9 s sleep); delay below → unchanged inline behaviour. Update `docs/DEMO.md` and the outbound spec.

#### A11. Reshape the DB script into a Flyway release/rollback pair (plan delta **O2a**)
- [ ] `docs/release-scripts/001_Create_TenantRateLimitState.sql` has the right table shape but the wrong name, schema and process. Retarget it to `[meta.ig]` in **TailoredmailDB** and rename to their convention `V{yyyy.MM.dd.HHmm}__TK-XXXX_Create_TenantRateLimitState.sql` (ticket number to be allocated).
- [ ] **Write the missing rollback script** `R{same-stamp}__…sql` — a V script with no R is not deployable there.
- [ ] Drop the `ExcludeFromMigrations()` note from the script header: EF has no migration pipeline left (TK-8475 deleted `Migrations/` and the `EntityFrameworkCore.Design` reference), so there is nothing to exclude from.
- [ ] Keep the `NVARCHAR(128)` key (must hold the `app:{FbAppId}` row) and record that this is a **Category-B** table: no int `Id`/`UniqueId`, no `CreatedBy`/`ModifiedBy`, no reserved `Active`, not `IAuditableEntity` — so their `AuditStampingInterceptor` never touches it.

#### A12. Align with Meta's documented limits (Aug 2026) and the newest IGAutopilot seams
Sources: [Graph API Rate Limits](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/) re-read 2026-08-04; IGAutopilot `e8fff0a30` (TK-8706) + staged `appsettings.json`.

**Instagram-relevant rate-limit codes only.** Meta's tables also list Ads Insights (80000), Ads
Management (80004), Custom Audience (80003), LeadGen (80005), WhatsApp (80008), Catalog (80009 /
80014) and the Ads-API subcode 2446079 — we never call those surfaces, so they are deliberately
**not** handled. Codes that do apply to us:

| Code | Sub | Meaning on our surface | Class | Status |
|---|---|---|---|---|
| 4 | — | App-level limit (shared by all tenants on the FB app) | mandatory | ✅ pre-existing |
| 17 | — | Per-account/user limit — also what a per-second overrun surfaces as | mandatory | ✅ pre-existing |
| 32 | — | Page calls with a User access token | mandatory | ✅ pre-existing |
| 80001 | — | Page calls with a Page / System-User token | mandatory | ✅ pre-existing |
| **80002** | — | **Instagram BUC limit — the code for standard IG Platform endpoints** | **mandatory** | ✅ **A12.1** |
| **613** | — | Custom rate limit; arrives on HTTP 400 as well as 429 | **mandatory** | ✅ **A12.1** |
| **613** | **1996** | Meta flagged our request *volume shape*, not a quota — no ETA supplied | **mandatory** | ✅ **A12.2** |
| 80006 | — | Messenger BUC — IG DMs share the `/messages` surface | nice-to-have | ✅ A12.1 (defensive) |

- [x] **A12.1 — Recognise the Instagram codes.** `RateLimitErrorCodes` ([InstagramRateLimitHandler.cs:29](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L29)) is now `[4, 17, 32, 613, 80001, 80002, 80006]`. 80002 was the critical miss — we handled its sibling 80001 instead, so a real Instagram BUC block set no `BlockedUntilUtc` and Polly did not treat it as retryable (`IsRateLimitedKey` comes from this one list, [InstagramResiliencePipeline.cs:99](InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L99)). New mock scenarios `InstagramBucBlock` (80002) and `CustomRateLimit613` (613/1996), both on **HTTP 400 with no `Retry-After`**, so only the code list can recognise them.
- [x] **A12.2 — `error_subcode` parsed and acted on.** Added `InstagramError.Subcode`; logged on every rate-limit warning. `613/1996` carries no `estimated_time_to_regain_access`, so the old 1-minute floor would have hammered straight back into the flag — it now takes a **15-minute** floor (`InconsistentVolumeBlockMin`) while every other code keeps the 1-minute floor.
- [x] **Verified live (2026-08-04)** against `InstagramGraphMock`, build 0 warnings:
  - `code=80002, subcode=0, retryAfter=8min` → `Tenant tenant-buc is blocked for 8 min (+ 1 min buffer)` → re-queued after 0:09:00; row `BlockedUntilUtc = 12:49:46`.
  - `code=613, subcode=1996, retryAfter=15min` → blocked 15 min from the subcode alone → re-queued after 0:16:00; row `BlockedUntilUtc = 12:56:46`.
- [x] **A12.3 — Per-second caps split by call class.** Four caps replace the single 100/s: `PerSecondDispatchLimit` 100 (text/links/reactions/stickers), `PerSecondMediaDispatchLimit` 10 (audio/video), `PerSecondConversationsDispatchLimit` 2, `PerSecondUnclassifiedDispatchLimit` 2. New `DispatchClassifier` ([DispatchClass.cs](InstagramSenderApi/Instagram/Services/DispatchClass.cs)) classifies from the endpoint **and** `message.attachment.type` — text and audio/video share `/messages`, so the URL alone cannot separate them. One bucket per tenant *and* class (Meta enforces them as separate limits). Verified: 6 `/conversations` jobs → `class=Conversations cap=2/s` with 4 waits ≈500 ms; 20 video sends → `class=MediaSend cap=10/s` with 10 waits; 20 text sends on the **same** endpoint in the same burst → no waits.
- [x] **A12.4 — Impression-derived ceiling recorded.** TRD §3.5.4 now states that Meta's allowance is `4800 × impressions` over 24 h, that only a percentage is ever reported, that **no absolute per-account call budget can be pre-computed**, and that the 80 % threshold is therefore the primary defence rather than a refinement of a known quota. Mirrored in the outbound spec's August-2026 section.
- [x] **A12.5 — Plan corrected to reuse their type (no IGAutopilot code touched).** Precise locations recorded in the integration plan: the **type** `GraphErrorDetails` is in `IGAutopilot.Core/Exceptions/GraphApiException.cs:14`, the **parser** `ParseGraphError` in `InstagramService.cs:1185` (call sites `:463`, `:1163`), surfaced by TK-8706 via `ConversationsController`. Their record already carries `error_subcode`, so B-P1 consumes it instead of porting our `InstagramErrorResponse`. Re-verified 2026-08-04: no usage-header parsing anywhere in their `src`, so the observation layer stays ours. **— SUPERSEDED 2026-09-11 (see A14.0): `IGAutopilot.Shared/Diagnostics/MetaUsage.cs` was added 2026-08-26 under TK-8749 and does parse both headers. It was not there on 08-04; the claim was true when written.**
- [x] **A12.7 — App-level blocks now land on the app row.** `AppLevelErrorCodes = [4, 613]`; those codes block `app:{AppId}` and leave the receiving account unblocked, and `GetThrottleDelayAsync` throws for **any** tenant while the app row is blocked. Verified with the new `AppLevelBlock` mock scenario: `code=4, level=app` → `app:local-dev-app` blocked, `tenant-guilty` unblocked at its own 20 %, and an unrelated healthy tenant logged `held by an APP-level block for 00:06:55` **without issuing a call**. Found during verification: a success must **not** clear an app block (an unrelated tenant's in-flight success wiped a live one) — a success now clears only account blocks; the app block expires on `BlockedUntilUtc`.
- [x] **A12.8 — L1 and L2 figures separated.** `X-App-Usage` is recorded only on the app row and BUC figures only on the tenant row; the tenant row is written only when account-level evidence exists, so an app-usage-only response can no longer overwrite real per-account percentages with zeros. Verified with `SharedAppBudget` at 92 %: `app:local-dev-app` = 92, `tenant-light` = **5** — the same row read **92** before the fix.
- [x] **A12.6 — Not applicable; the premise was wrong.** Verified: neither `InstagramSenderApi/Program.cs` nor `WebhookIngestApi/Program.cs` logs configuration at startup, and no log statement anywhere in the solution emits a secret, connection string or API key. Nothing to mask. The genuine (already-documented) exposure is the dev-only `HmacSecretKey` placeholder in checked-in config — see the CLAUDE.md config note, not a logging change.

#### A13. BUC correctness pass (Aug 2026) — enforce what we already read
Sources: [Graph API Rate Limits](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/) +
[Instagram Platform overview](https://developers.facebook.com/docs/instagram-platform/overview) +
[Messenger Platform rate limits](https://developers.facebook.com/documentation/business-messaging/messenger-platform/overview/rate-limiting),
re-read 2026-08-06; IGAutopilot send path re-scanned the same day. Full derivation, limit map, header
table and code evidence: [integration plan §8](docs/INTEGRATION_PLAN_IGAutopilot.md#8-buc-correctness-pass-2026-08-06).

**Premise now settled:** sends use a **Page Access Token** (`InstagramService.cs:391`/`:429`) ⇒ **BUC
limits apply, Platform limits do not**, and BUC quota is scoped **per Instagram professional account**.
There is no shared app-wide DM budget. Nothing below is implemented yet — plan-only until approved.

- [ ] **A13.1 (O13) — Throttle on all three usage dimensions, and stop at 100 %.** `call_count`,
  `total_time` and `total_cputime` are all persisted, but only `MaxCallCountPct` drives the decision
  ([TenantRateLimitService.cs:108](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L108)),
  and at 100 % the method still only returns a delay clamped to 60 s ([:118](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L118))
  — against Meta's explicit *"When the limit has been reached, stop making API calls"*. Extract an
  **`IThrottlePolicy`** (`Pass` / `Delay` / `Stop`) so the service stays a state facade and the policy
  owns the arithmetic; tiers `ThrottleThresholdPct` 80 → `NearStopThresholdPct` 95 →
  `HardStopThresholdPct` 100 (throws `TenantBlockedException`, no HTTP call). Verify with a mock
  scenario that reports `total_cputime: 100` while `call_count` stays low — today that passes through.
- [ ] **A13.2 (O14) — Pace against the real window.** The formula `3_600_000 / (remainingPct × 48)`
  assumes a **1-hour** window; every BUC budget on our surface is **24 h** (`instagram` 4800 × impressions,
  `messenger` 200 × engaged users), so at 80 % it permits ≈960 calls/hour and spends the remaining 20 %
  of a day's budget within the hour. Move the window and the `48` into `OutboundRateLimitOptions`
  (`BucWindowHours`, `CallsPerPercentUnit`) and select per BUC type. State the honest limitation in the
  spec: Meta reports no time-remaining, so pacing is an estimate — which is why A13.1's hard stop matters more.
- [ ] **A13.3 (O15) — Keep the BUC `type`.** [InstagramRateLimitHandler.cs:115–122](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L115-L122)
  folds every `type` and business-id into one `Math.Max`, so we cannot tell which budget is hot and a
  `messenger`-type block halts `instagram`-type calls for that account (over-blocking = lost
  deliverability). Key state on `{accountId}` + `{bucType}` — one row shape, repository interface
  unchanged, `app:{AppId}` still works as a degenerate case.
- [ ] **A13.4 (O17) — Model the Private Reply class.** They send private replies
  (`recipient = { comment_id }`, `InstagramService.cs:401–412`), capped at **750/hour** per account
  (post/reel comments) and 100/s on Live. `DispatchClassifier` labels them `TextSend` at 100/s
  ([DispatchClass.cs:53–56](InstagramSenderApi/Instagram/Services/DispatchClass.cs#L53-L56)). Add
  `DispatchClass.PrivateReply` detected from `recipient.comment_id` (same payload-reading mechanism as
  the audio/video split) and replace the `static DispatchClassifier` with **`IDispatchClassifier`** so
  IGAutopilot can substitute its own without editing ours. **Record the coupling:** their 200/h counter
  keeps 750/h unreachable today — raising `Instagram:RateLimitPerHour` above 750 without this re-opens it.
- [ ] **A13.5 (O16) — Put the per-second gate behind an interface.** `PerSecondDispatchGate` holds
  buckets in a process-local `ConcurrentDictionary` ([:21](InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs#L21)),
  and the Worker runs on **4 servers** (`m1`/`m2`/`m7`/`m9`, 200 message workers each) → up to **4×
  Meta's cap**; Conversations 2/s becomes 8/s. Introduce **`IDispatchRateLimiter`** with the current
  class as `InMemoryDispatchRateLimiter`, mirroring the inbound `IRateLimitStore` seam so the solution
  has one "counter store" pattern. Add `DispatchGateScope: Node | Cluster` so the ×N is explicit in
  config. Also hosts A13.4's hourly window.
- [ ] **A13.6 (O19) — Second-granularity blocks.** `RetryAfterMinutesFromHeader`
  ([:239–247](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L239-L247)) ceils
  seconds to minutes, so `Retry-After: 5` becomes a 1-minute block +1-minute buffer = a **2-minute stall
  for a 5-second cooldown**. Carry `TimeSpan` through `RecordUsageAsync`; keep the minute path for
  `estimated_time_to_regain_access` (Meta reports whole minutes) and make the buffer proportional.
  Interacts with A10/O12 — a sub-minute block should shed, and the requeue delay must then be second-accurate.
- [ ] **A13.7 (O21) — Bound the app-block blast radius.** `AppLevelErrorCodes = [4, 613]` makes one
  code 4 stop **every** account. Code 4 is the app-token **Platform** limit, which Page tokens should
  never reach — so a single stray occurrence halts the fleet for the full window. Keep the mechanism
  (613/1996 genuinely is app-wide) but add `AppBlockMaxMinutes` and require either 613 or repeated
  code-4 evidence before a global block; log the triggering count so a fleet-wide stop is never silent.

**Design constraint for all of A13:** the split stays intact — `InstagramRateLimitHandler` **observes
only** (parse → persist, never decide), `InstagramThrottleGuard` **enforces only** (ask the policy, wait
or throw). That is what makes observe-first rollout possible. Every new number goes into
`OutboundRateLimitOptions`; the hard-coded `48` and `3_600_000` are exactly what hid A13.2.

**B-Side finding (new, report — do not fix here):** their staged `appsettings.json` now carries **real** `Instagram:FbAppSecret` and `IgAppSecret` values in all three projects (Ingest/Worker previously held `REPLACE_WITH_…` placeholders), and `Rules:IgnoreCooldowns` is staged as `true`. Both look unintended for a commit; the secrets must be rotated and moved to Key Vault / user-secrets (their OBS Ingest **FIX-35** is the canonical tracker).

#### A14. Independent review pass (2026-09-11) — concurrency, unbounded state, tests

Scope: `InstagramSenderApi/Instagram/**` read end-to-end against IGAutopilot `c4b79454b`. Deliberately
**excludes** anything already tracked by A10 (inline-delay cap), A11 (Flyway pair) or A13.1–A13.7 —
those stand as written and are not restated here. The inbound flow is out of scope for this pass: their
`Ingest.Api` owns inbound and already has fail-closed, constant-time, replay-protected HMAC.

##### A14.0 — Confirmed correct, port unchanged (no action)

Recorded so a later reviewer does not re-litigate them:

| Area | Why it is right |
|---|---|
| `RateLimitErrorCodes` = `[4, 17, 32, 613, 80001, 80002, 80006]` | Each justified in-comment with source + read-date; Ads/WhatsApp/Catalog codes deliberately excluded rather than forgotten. |
| `613/1996` → 15-min floor | Meta flags request *shape*, supplies no ETA; a 1-min floor re-enters the flag. |
| L1/L2 separation (A12.8) | `X-App-Usage` → `app:{AppId}` row only; BUC → tenant row only. Folding them made every account read as hot as the app budget. |
| Asymmetric block clear (A12.7) | A success clears an account block, never the app block. Found by running it, not by reasoning — an unrelated tenant's in-flight success was wiping a live app block. |
| Write semantics `>0 / 0 / null` | A header-less 500 can no longer zero real state. |
| Polly order Retry → Breaker → Timeout (per attempt) | The inverted order was killing any retry honouring a multi-minute ETA. |
| `MaxInlineRetryDelay` 2 min | Long blocks go to the queue instead of sleeping in Polly — matches Meta's "stop calling". |
| Dispatch classes split by **payload**, unclassified → tightest cap | Text and audio/video share `/messages`; the URL alone cannot separate them, and guessing high is the failure mode that gets an account blocked. |
| `Enabled` gates enforcement, handler always observes | The only reason observe-first rollout (D2/P1) is possible. |

**Correction carried from A12.5:** the observation layer is **no longer exclusively ours**.
`IGAutopilot.Shared/Diagnostics/MetaUsage.cs` (added 2026-08-26, TK-8749) parses both headers and
returns `Reading(Summary, Peak, BlockedForSeconds)` with an `IsPressured` flag at 75 %. Three limits
make it an observation *aid*, not a decision layer, so **O3 stands unchanged** — but the plan must stop
claiming they have nothing: (a) it is log-only, nothing persists; (b) it is called from
`InstagramOAuthService` and `FacebookPageWebhookSubscriber`, **never from `InstagramService`** — the
send path, and the only path their 200/h counter guards; (c) `Peak = max(calls, cpu, time)` across every
business id and `type`, the same collapse **A13.3** already flags on our side.
- [ ] **A14.0a** — Update integration plan §8 and the A12.5 bullet to describe `MetaUsage` as existing
  prior art, and decide explicitly at P1 review: keep both (ours persists + decides, theirs stays a log
  line) or retire `MetaUsage` once our handler is wired. Recommend **keep both initially**, retire theirs
  at P2 — two parsers reading the same header is acceptable while one is purely diagnostic, and deleting
  their instrumentation during an observe-only phase removes the very signal P1 is there to gather.

##### A14.1 (High) — There is no test project in this solution
- [ ] `RateLimit.sln` contains four projects — `InstagramGraphMock`, `InstagramSenderApi`,
  `WebhookIngestApi`, `WebhookTrafficSimulator` — and **no test project**. All verification to date is
  manual scenario runs via `docs/DEMO.md`.
- [ ] **Why:** the scenario evidence is genuinely good for integration behaviour, but it cannot cover the
  branch matrix that A14.2–A14.5 live in (interleaved responses, out-of-order writes, eviction). It also
  will not survive the port: IGAutopilot gates on `dotnet test`, and this lands as a large untested block
  in the middle of the send path — the highest-consequence code in the product.
- [ ] **Action:** add `InstagramSenderApi.Tests` (xUnit, matching their conventions) covering, at minimum:
  `InstagramRateLimitHandler` header/error-body parsing including malformed headers and each error code;
  the `>0 / 0 / null` block semantics; `TenantRateLimitService` threshold and app-vs-account precedence;
  `DispatchClassifier` for every class including the attachment split; `PerSecondDispatchGate` refill
  arithmetic. Blocked on **A14.6** for anything time-dependent.

##### A14.2 (High) — `EnsureTableExistsAsync` creates a **database** at runtime
- [ ] [SqlTenantRateLimitRepository.cs:55](InstagramSenderApi/Instagram/Infrastructure/SqlTenantRateLimitRepository.cs#L55)
  connects to `master` and issues `CREATE DATABASE` ([:65](InstagramSenderApi/Instagram/Infrastructure/SqlTenantRateLimitRepository.cs#L65)) when the catalog is missing.
- [ ] **Why:** correct for a LocalDB demo, disqualifying in TailoredmailDB. `tmapp` holds no such right,
  and the schema is Flyway-owned. This is **not** the same item as A11/O2a: those reshape the *script*,
  while this is a *runtime* code path that must not exist at all. O2 mentions replacing the repository
  but never says the bootstrap must be deleted, so it can survive a careless port.
- [ ] **Action:** delete `EnsureTableExistsAsync` and its `ITenantRateLimitRepository` member as part of
  O2, and drop the startup call in `Program.cs`. Record in the plan that table creation is Flyway's job,
  full stop.

##### A14.3 (High) — Usage is written to SQL inline, on every Graph response
- [ ] [InstagramRateLimitHandler.cs:224](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L224)
  / [:233](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L233) `await`
  `RecordUsageAsync` → `UpsertAsync` before the response returns to the caller.
- [ ] **Why:** one SQL round-trip added to the latency of **every** outbound Graph call. In this workspace
  that is a demo with a handful of tenants; in IGAutopilot the Worker runs **200 message workers × 4
  nodes**, so this becomes a per-call write amplifier against a shared database on the hot path — and the
  handler is on the chain even in observe-only mode (P1), where it is supposed to be free.
- [ ] **Action (new Part B delta **O22**):** buffer writes behind a bounded channel with a single drain
  loop coalescing by `{tenantId}`, or make the write fire-and-forget with a failure counter. The **block**
  path must stay synchronous — losing a block write means calling into a live limit. Only the percentage
  refresh may be deferred. Size the buffer so a SQL stall sheds readings rather than backing up the send path.

##### A14.4 (High) — Lost update: concurrent responses overwrite each other's percentages
- [ ] The `MERGE` at [SqlTenantRateLimitRepository.cs:34](InstagramSenderApi/Instagram/Infrastructure/SqlTenantRateLimitRepository.cs#L34)
  sets all three percentage columns unconditionally in `WHEN MATCHED`
  ([:37](InstagramSenderApi/Instagram/Infrastructure/SqlTenantRateLimitRepository.cs#L37)).
- [ ] **Why:** two in-flight calls for one account complete out of order and the **later-arriving, older**
  reading wins. Usage percentages are monotonic within a window, so this silently *lowers* recorded usage
  — the exact direction that delays throttling. A12.8 fixed zeroing from the wrong *source*; this is
  zeroing from the wrong *order*, and no guard exists for it.
- [ ] **Action:** guard the update — either `WHEN MATCHED AND @LastUpdatedUtc >= target.LastUpdatedUtc`,
  or take the column-wise maximum within the window. Cover with a test that applies readings out of order
  (A14.1). Interacts with A13.3: once rows are keyed per BUC `type`, the same guard must apply per row.

##### A14.5 (Medium) — Read-modify-write race on block preservation
- [ ] [TenantRateLimitService.cs:59](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L59)
  — the `estimatedBlockMinutes is null` branch reads existing state, then writes it back.
- [ ] **Why:** two responses can both read "no block yet" and both write a row without one, dropping a
  block that a third response set in between. Low probability, high consequence: a dropped block means the
  queue resumes straight into a live limit, which is what A12.7 exists to prevent.
- [ ] **Action:** fold preservation into the SQL statement (`BlockedUntilUtc = COALESCE(@BlockedUntilUtc,
  target.BlockedUntilUtc)`) rather than doing it in C#. Removes the read entirely and pairs naturally with
  the A14.4 guard — do both in one edit.

##### A14.6 (Medium) — `PerSecondDispatchGate._buckets` is unbounded
- [ ] [PerSecondDispatchGate.cs:21](InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs#L21)
  — `ConcurrentDictionary` keyed `{tenantId}|{class}`, entries added at
  [:74](InstagramSenderApi/Instagram/Services/PerSecondDispatchGate.cs#L74) and **never evicted**.
- [ ] **Why:** this is the same defect class the review already found and fixed on the inbound side
  (`CODE_REVIEW_AND_TEST_PLAN.md` §2 defect #18 — self-pruning buckets, idle eviction, key caps). The
  outbound gate never got the same treatment. Bounded by account count rather than attacker input, so it
  is a slow leak rather than a vector — but the fix is known and already written once in this repo.
- [ ] **Action:** prune buckets idle beyond a few refill windows, mirroring the inbound store. Fold into
  **A13.5** when `IDispatchRateLimiter` is extracted — same file, same edit, and the interface is the
  natural place to specify eviction.

##### A14.7 (Medium) — Safety buffers stack to roughly double the block
- [ ] `+1 min` is added three times: [TenantRateLimitService.cs:54](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L54)
  (persisted block), [InstagramResiliencePipeline.cs:108](InstagramSenderApi/Instagram/Infrastructure/InstagramResiliencePipeline.cs#L108)
  (inline retry delay), [SendQueueWorker.cs:127](InstagramSenderApi/Instagram/Workers/SendQueueWorker.cs#L127)
  (re-queue pause).
- [ ] **Why:** each is individually defensible, and together a 5-minute ETA becomes an ~8-minute stall.
  Over-blocking is lost deliverability, the same cost A13.3 names for over-blocking by `type`. Compounds
  **A13.6**, where a 5-**second** `Retry-After` already inflates to a 2-minute stall.
- [ ] **Action:** apply the buffer **once**, where the block is persisted, and have the pipeline and worker
  read the stored `BlockedUntilUtc` instead of re-deriving a delay. Make the buffer proportional
  (`max(30s, 10 %)`) rather than a flat minute, and move it to `OutboundRateLimitOptions`.

##### A14.8 (Medium) — A missing `TenantIdKey` silently disables all rate limiting for that call
- [ ] [InstagramRateLimitHandler.cs:65](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L65)
  reads the option; [:69](InstagramSenderApi/Instagram/Infrastructure/InstagramRateLimitHandler.cs#L69)
  skips **all** parsing and recording when it is absent.
- [ ] **Why:** the failure is invisible. A future call site that forgets to set the option gets zero
  protection and zero diagnostics, and the symptom surfaces days later as an unexplained block. O5 adds
  exactly such a call site in their `InstagramService`, so the risk is live at integration time.
- [ ] **Action:** log a warning (throttled, e.g. once per minute per request URI) when a Graph response
  carries usage headers but no tenant id. Cheap, and it converts a silent gap into a visible one.

##### A14.9 (Low) — `ResiliencePipelineRegistry` entries are never evicted
- [ ] [InstagramClient.cs:49](InstagramSenderApi/Instagram/Client/InstagramClient.cs#L49) —
  `GetOrAddPipeline<HttpResponseMessage>` keyed `instagram:{tenantId}`, retained for process lifetime.
- [ ] **Why:** per-tenant isolation is correct and must stay (review defect #1). The cost is one retained
  pipeline plus breaker state per account ever seen — negligible at demo scale, unbounded across a
  long-lived Worker serving every connected account.
- [ ] **Action:** note the growth characteristic in the outbound spec; revisit only if account counts make
  it material. No change now — recorded so it is a decision rather than an oversight.

##### A14.10 (Low) — No clock abstraction
- [ ] `DateTime.UtcNow` throughout (`TenantRateLimitState.IsCurrentlyBlocked`,
  [TenantRateLimitService.cs:54](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L54)),
  `Environment.TickCount64` in the gate.
- [ ] **Why:** every interesting behaviour here is time-dependent — block expiry, the 10-second cache,
  token refill, window resets. Without an injectable clock those branches can only be tested by sleeping,
  which is why **A14.1** is hard to start. This is a prerequisite, not a nicety.
- [ ] **Action:** inject `TimeProvider` (in-box on net8+, and IGAutopilot is on net10) through
  `TenantRateLimitService`, `TenantRateLimitState` and `PerSecondDispatchGate`. Do this **before** A14.1.

##### A14.11 (Low) — `SELECT *` against a shared, Flyway-owned schema
- [ ] [SqlTenantRateLimitRepository.cs:26](InstagramSenderApi/Instagram/Infrastructure/SqlTenantRateLimitRepository.cs#L26).
- [ ] **Why:** harmless against a table we own outright; brittle in `[meta.ig]`, where a column added by an
  unrelated release changes what Dapper materialises. Moot if O2's EF rewrite lands first — listed so it
  is not carried over verbatim if the repository is ported as-is.
- [ ] **Action:** name the columns, or let O2's EF rewrite supersede it. No standalone work.

##### A14.12 (Low) — `ThrottleThresholdPct` is a `const`, not configuration
- [ ] [TenantRateLimitService.cs:14](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L14),
  used at [:111](InstagramSenderApi/Instagram/Services/TenantRateLimitService.cs#L111).
- [ ] **Why:** every other number in this layer is options-bound, and the design constraint under A13 says
  so explicitly — "the hard-coded `48` and `3_600_000` are exactly what hid A13.2". 80 % is the single
  most likely number to want tuning per environment during the P1 observation window, and it is the one
  that requires a redeploy.
- [ ] **Action:** move to `OutboundRateLimitOptions` alongside A13.1's `NearStopThresholdPct` /
  `HardStopThresholdPct` — same edit, so do it there rather than separately.

**Suggested order.** A14.10 → A14.1 (clock, then tests — everything below is easier to prove once these
exist) → A14.4 + A14.5 (one SQL edit) → A14.2 → A14.3/O22 → A14.7 → A14.8 → A14.6 (with A13.5)
→ A14.12 (with A13.1) → A14.11 (with O2) → A14.9 (note only).

**Relationship to A13.** A13 is about *deciding correctly* from what we read; A14 is about *not losing or
corrupting* what we read, and being able to prove either. They are independent — neither blocks the other
— but A14.10 and A14.1 should come first regardless, because A13's tiered policy and window arithmetic
are precisely the things nobody should ship untested.

---

## Part B — Changes in `IGAutopilot\Codebase` (PLAN ONLY — do not touch yet)

Executed later per rollout phases P0–P5 (plan §3). Decisions D1–D4 are confirmed (plan §4).

### B-P0. Foundation
- [ ] **O1**: Copy components into `IGAutopilot.Infrastructure` (new `RateLimiting` folder); rename namespaces to `IGAutopilot.*`; retarget **net10.0** (not net8.0 — they moved in TK-8629; A6's net8 pass is superseded and its one API substitution is no longer needed). Add **no** new NuGet package versions — `packages.lock.json` is pinned and restore will fail.
- [ ] **O2**: `TenantRateLimitState` entity in `ApplicationDbContext`, default schema `[meta.ig]`, **TailoredmailDB** (`ConnectionStrings:DefaultConnection`); reimplement repository on **EF via `IDbContextFactory<ApplicationDbContext>`** (replaces Dapper + runtime `EnsureTableExistsAsync`). Keep the `NVARCHAR(128)` natural key — it also holds the `app:{FbAppId}` global row from A2, so it is deliberately **not** an FK to `InstagramAccounts`. **Category-B table**: no int `Id`/GUID `UniqueId`, no audit columns, no reserved `Active`, not `IAuditableEntity`.
- [ ] **O2a — Flyway release + rollback pair (there are no EF migrations any more)**: ship `V{yyyy.MM.dd.HHmm}__TK-XXXX_Create_TenantRateLimitState.sql` in `Codebase\SQLDB\ReleaseScripts\` plus the matching `R{same-stamp}__…sql` in `Codebase\SQLDB\RollbackScripts\TailoredMailDB\`, per the house `flyway-release-script` skill. Entity/script kept in sync by review (nothing warns on drift). Draft both in **Part A** — see A11.

### B-P1. Observe-only wiring (zero behaviour change)
- [ ] **O3**: `Worker\Program.cs:149` and `Admin.Api\Program.cs:159` (re-verified 08-06 — was `:143`/`:140`) — `.AddHttpMessageHandler<InstagramRateLimitHandler>()` on the existing typed-client chain. Handler stays on unconditionally (observe-only). Admin.Api gets **only** this, per **D4** (reinforced: their FIX-79 limiter already owns generic Admin API throttling).
- [ ] **Config**: add the `RateLimiting` section to **every** per-env file — `src\<Project>\Config\{local,dev,qa,m1,m2,m7,m9,video}.appsettings.json` (swapped by `deploy-igapps.ps1`), not a single `appsettings.json`; update `Deliverables\CONFIGURATION.md`.
- [ ] **I1**: Copy inbound components into `Ingest.Api\RateLimit\`.
- [ ] **I2**: `Ingest.Api\Program.cs` — bind options, register store/service singletons (near their existing `AddRateLimiter` block at `:49–61`, which stays untouched).
- [ ] **I3**: `Ingest.Api\Program.cs` before `UseAuthentication` (`:298`) — i.e. also before `UseAuthorization` (`:299`) and their `UseRateLimiter` (`:302`) — add middleware with `Enabled:false` / observe mode (A5), `ExcludedPaths` = `/health`, `/health/threadpool`, swagger, GET verification (A4).
- [ ] **I12** (positioning, no code): confirm at review that our middleware and their built-in limiter never evaluate the same request — ours on the anonymous webhook path, theirs (`data-deletion-status`, and Admin.Api's global limiter) on their own routes. Do not merge, duplicate, or modify their registrations.
- [ ] Run ~1 week observation (per **D2**): per-account usage % in `TenantRateLimitState`; inbound counters in logs.

### B-P2. Outbound enforcement (behind `RateLimiting:Outbound:Enabled`)
- [ ] **O4**: `Worker\Program.cs:154–181` (retry predicate `:161`, breaker predicate `:175`; Admin.Api `:164–181`) — re-verified 08-06, was `:148–175`/`:155`/`:162` — keep their shared 5xx circuit breaker; **remove the 429-only retry** when the flag is on (retries move to the per-account pipeline). **Coordinate with their FIX-48**, which will add 5xx/network/timeout retry + 401/code-190 token refresh to this same block — agree the split first or you get two retry layers.
- [ ] **O5**: `InstagramService.SendMessageToInstagramAsync` (`:374`) — set account id on `request.Options`; execute inside per-account pipeline from `ResiliencePipelineRegistry<string>` with a fresh request per attempt. Also **fix `:478`** (rate-limited = 429 only) to classify 400 + codes 4/17/32/613/80001/80002 — reuse their existing `GraphErrorDetails` parser (`:1179+`), don't add a second one.
- [ ] **O6**: `InstagramService.SendMessageAsync` (`:51`) — `InstagramThrottleGuard.EnforceAsync(accountId)` before send (proactive delay ≥80 %, `TenantBlockedException` → `RateLimitExceeded` result without an HTTP call). Requires **A10/O12** first so a long delay sheds instead of parking a consumer. Existing 200/h `RateLimitTracking` counter stays (`:161` check, `:48` limit, atomic increment `:220`) — **corrected rationale:** it protects no app-level budget (Page token ⇒ no Platform limit); its value is being the only **hourly-class** guard, and at 200/h it sits below the 750/h Private Reply cap (A13.4).
- [ ] **O20**: extend their `GraphErrorDetails` (`Core/Exceptions/GraphApiException.cs:14`) with one additive `int? EstimatedTimeToRegainAccessMinutes { get; init; }` and populate it in `ParseGraphError` (`InstagramService.cs:1185`). A12.5's "reuse their record, port nothing" is right for *classification* but their record carries no `error_data.estimated_time_to_regain_access` — the value that sets the block window. Init-only record ⇒ breaks nothing, and the codebase keeps exactly one Graph error parser.
- [ ] **O18 / D4 amendment**: `GetConversationsAsync` (`InstagramService.cs:718`, called from `Admin.Api ConversationsController.cs:75`) hits `/{page-id}/conversations` — **2 calls/s per account**, the tightest messaging cap — and bypasses even their own 200/h counter (only `SendMessageAsync:83` calls it). Decide at P2 review: (a) enforce the guard in Admin.Api for the `Conversations` dispatch class only (the guard is already class-aware — one `EnforceAsync` call plus config, no new machinery), or (b) accept the risk **in writing**. Recommend (a): 2/s leaves no headroom for a polling UI.
- [ ] **O9/O10/O11** land here automatically via the copied components (built in A1–A3). **O12** lands the same way once A10 is done; **O13–O17, O19, O21** the same way once **A13** is done. Note the **O10 correction**: with Page tokens `X-App-Usage` is normally absent, so the `app:{FbAppId}` row's live purpose is carrying app-flagged blocks (613 / 613-1996), not a shared percentage budget — do not report it as app-budget coverage.
- [ ] **O7**: `IInstagramService.cs:95–99` — append `TimeSpan? RetryAfter = null` to the `InstagramSendResult` record (5th positional member, after the existing `RateLimitExceeded`; additive, existing call sites keep compiling).
- [ ] **Telemetry**: emit usage %, block state and gate waits as OTel activity tags alongside the existing `instagram.rate_limited` tag (`Workers\Worker.cs:545`/`:983`), not log-only — their observability specs (FIX-31) expect it.
- [ ] Staging validation against `InstagramGraphMock` via `Instagram:GraphApiUrl` (scenarios: GradualApproach, SuddenBlock, MultiTenantMix, RecoveryTest).

### B-P3. Re-drive of rate-limited sends (**O8** / **D1**)
- [ ] New `RateLimitRetryWorker` in `IGAutopilot.Worker`: periodically scans `Status=RateLimited` rows whose account's `BlockedUntilUtc` has passed, re-enqueues to the existing path. Bounded attempts; `ResponseMessageId` checkpoint prevents double-sends. (`Workers\Worker.cs:605–611` / `:1073–1074` stop being terminal.)
- [ ] **Close the Spec 03 reconciliation** (their side already recorded it): update `Deliverables\adr\0008-429-dlx-ttl-requeue.md` from "Proposed" to superseded-for-the-rate-limit-case, and the note in `specs\System-Design-ToDo.md`. Their DLX topology stays for **non**-rate-limit redelivery (FIX-53) — do not implement both mechanisms for rate limits.
- [ ] **Agree gate limit vs consumer sizing** with their FIX-52/FIX-62: they intend to size `MessageWorkerCount`/prefetch against our O9 per-account per-second gate rather than raw RPS. Synergy with Spec 01/ADR-0006 per-account bucket queues — no change needed on our side, but the two numbers must be set together.

### B-P4. Inbound enforcement
- [ ] **I7/D2**: Set real thresholds from the P1 observation (starting point: global 5000/min, per-IP 2000/min, concurrency 100; measured peaks ×5).
- [ ] **I4**: Keep `HmacValidationEnabled:false` — their `Ingest.Api\Controllers\WebhooksController.cs` HMAC (`:99`/`:137`/`:163`) remains the single implementation; since 07-07 it is also fail-closed (FIX-73), constant-time (FIX-74) and replay-protected (FIX-75).
- [ ] **I5**: Keep both payload guards (our 411/413 pre-checks + their in-controller checks at `:76`/`:83`).
- [ ] **I6/D3**: `TrustForwardedFor: true` (behind IIS/ARR, single node). Redis store (**I8**) deferred — documented future note only.
- [ ] **I9**: Seed `AllowedIps` with Meta's published egress CIDRs; under extreme overload prefer fast-200-and-shed over 429 to Meta (prolonged 429/5xx risks webhook-subscription disable). **Their condition (PERF-TODO):** shed mode is emergency-only, requires sampling **and** an explicit operator decision — not automatic.
- [ ] Validate with `WebhookTrafficSimulator` against staging ingest.

### B-P5. Production rollout
- [ ] Enable inbound first (monitor 429/403 ≈ 0 for legit traffic) → outbound proactive throttle for one pilot account → all accounts. Rollback at every step = flag off.
- [ ] **I10** (deployment checklist, not code): verify IIS/ARR request limits + Kestrel `MinRequestBodyDataRate` for slow-transport attacks; add to runbook.
- [ ] **I11** (deployment checklist, not code): if an edge layer is available (Cloudflare / nginx / IIS+ARR request-limit rules), configure volumetric DDoS absorption + coarse per-IP limits there as the **first line**; middleware `Global`/`PerIp` windows become a cheap backstop (keep enabled). Identity-aware layers (per-client, HMAC, `hub.challenge` bypass, Meta-safe shedding) stay in the middleware; the outbound system is unaffected — edge products don't address outgoing Graph-API-limit compliance. Rationale: TRD §4.4, inbound spec "Positioning" section.

### B-Side finding (report to team — independent of this work)

- [ ] Committed secrets (`Instagram:FbAppSecret`, `Encryption:Key`): rotate and move to Key Vault / user-secrets. **Already tracked on their side as OBS Ingest FIX-35 (canonical there)** — raise as a pointer to FIX-35, not as a new finding. Config files have since moved to `src\IGAutopilot.Worker\Config\{env}.appsettings.json`.
- [ ] **Access token in a query string** (found 2026-08-06): `GetConversationsAsync` appends `&access_token={accessToken}` to the URL (`InstagramService.cs:718`), while the send path correctly uses the `Authorization: Bearer` header (`:429`). Tokens in URLs land in web-server logs and proxy traces. Security finding, not rate limiting — report alongside the FIX-35 pointer, do not fix inside this workstream.
- [ ] **5xx responses are dropped, not retried** — their pipeline retries **429 only** (`Worker/Program.cs:161`) and our `InstagramResiliencePipeline` handles only transport exceptions + rate-limit results, so an HTTP 500/503 *response* is treated as non-retryable and the DM is lost. **Their FIX-48 owns this** (scheduled to add 5xx/network/timeout retry + 401/code-190 token refresh). Do not fix it here; O4's coordination note already flags the overlap.
- [x] ~~Note for P2/P3 validation planning: their `IGAutopilot.Admin.Api.Tests` / `Infrastructure.Tests` are **orphaned**~~ — **CORRECTED 2026-09-11.** `IGAutopilot.Infrastructure.Tests` is in `IGAutopilot.sln`, compiles, and `dotnet test` runs **113 passing tests**. `Admin.Api.Tests` no longer exists at all. Staging validation can therefore ride on their suite as well as `InstagramGraphMock` — and anything we port is expected to arrive with tests (see **A14.1**).
