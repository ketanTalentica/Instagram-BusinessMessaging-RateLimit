/* =============================================================================
   001_Create_TenantRateLimitState.sql

   Rate-limiting integration (IGAutopilot) — release script, P0 phase.
   Creates the TenantRateLimitState table used by the outbound Instagram
   rate-limiting layer (InstagramRateLimitHandler / TenantRateLimitService).

   IMPORTANT — process notes:
   * IGAutopilot DB changes ship as release scripts, NOT EF migrations.
     The corresponding EF entity must be mapped with ExcludeFromMigrations()
     so EF never generates DDL for this table. Keep entity and script in sync
     by review.
   * Idempotent: safe to re-run.
   * Key is NVARCHAR(128), not a Guid: besides one row per Instagram account id,
     the table holds a global row keyed 'app:{FbAppId}' carrying the shared
     X-App-Usage budget observed across ALL accounts on the FB app.

   Row semantics:
   * MaxCallCountPct / MaxTotalTimePct / MaxTotalCpuTimePct — worst-case usage %
     parsed from X-App-Usage / X-Business-Use-Case-Usage on the latest response.
   * BlockedUntilUtc — non-null while Meta has blocked the account
     (estimated_time_to_regain_access + 1-minute buffer). Never set on the
     app-level row.
   ============================================================================= */

IF OBJECT_ID(N'dbo.TenantRateLimitState', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TenantRateLimitState
    (
        TenantId           NVARCHAR(128) NOT NULL,
        MaxCallCountPct    INT           NOT NULL CONSTRAINT DF_TenantRateLimitState_CallCount DEFAULT (0),
        MaxTotalTimePct    INT           NOT NULL CONSTRAINT DF_TenantRateLimitState_TotalTime DEFAULT (0),
        MaxTotalCpuTimePct INT           NOT NULL CONSTRAINT DF_TenantRateLimitState_TotalCpu  DEFAULT (0),
        BlockedUntilUtc    DATETIME2     NULL,
        LastUpdatedUtc     DATETIME2     NOT NULL,

        CONSTRAINT PK_TenantRateLimitState PRIMARY KEY CLUSTERED (TenantId)
    );

    PRINT 'Created table dbo.TenantRateLimitState';
END
ELSE
BEGIN
    PRINT 'dbo.TenantRateLimitState already exists — no action taken';
END
GO
