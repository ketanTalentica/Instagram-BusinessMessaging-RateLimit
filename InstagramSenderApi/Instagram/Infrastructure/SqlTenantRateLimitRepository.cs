using Dapper;
using Microsoft.Data.SqlClient;
using InstagramSenderApi.Instagram.Models;

namespace InstagramSenderApi.Instagram.Infrastructure;

/// <summary>
/// Dapper-backed SQL Server repository for per-tenant rate-limit state.
/// Each method opens and closes its own connection — safe to register as Singleton.
/// </summary>
public sealed class SqlTenantRateLimitRepository : ITenantRateLimitRepository
{
    private readonly string _connectionString;

    public SqlTenantRateLimitRepository(IConfiguration config)
    {
        _connectionString = config.GetConnectionString("SenderDb")
            ?? throw new InvalidOperationException(
                "Connection string 'SenderDb' is missing from configuration.");
    }

    public async Task<TenantRateLimitState?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<TenantRateLimitState>(new CommandDefinition(
            "SELECT * FROM TenantRateLimitState WHERE TenantId = @TenantId",
            new { TenantId = tenantId },
            cancellationToken: ct));
    }

    public async Task UpsertAsync(TenantRateLimitState state, CancellationToken ct = default)
    {
        const string sql = """
            MERGE INTO TenantRateLimitState AS target
            USING (SELECT @TenantId AS TenantId) AS source
               ON target.TenantId = source.TenantId
            WHEN MATCHED THEN
                UPDATE SET
                    MaxCallCountPct    = @MaxCallCountPct,
                    MaxTotalTimePct    = @MaxTotalTimePct,
                    MaxTotalCpuTimePct = @MaxTotalCpuTimePct,
                    BlockedUntilUtc    = @BlockedUntilUtc,
                    LastUpdatedUtc     = @LastUpdatedUtc
            WHEN NOT MATCHED THEN
                INSERT (TenantId, MaxCallCountPct, MaxTotalTimePct,
                        MaxTotalCpuTimePct, BlockedUntilUtc, LastUpdatedUtc)
                VALUES (@TenantId, @MaxCallCountPct, @MaxTotalTimePct,
                        @MaxTotalCpuTimePct, @BlockedUntilUtc, @LastUpdatedUtc);
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(sql, state, cancellationToken: ct));
    }

    public async Task EnsureTableExistsAsync(CancellationToken ct = default)
    {
        // First run on a fresh machine: the database itself may not exist yet
        var csb = new SqlConnectionStringBuilder(_connectionString);
        var dbName = csb.InitialCatalog;
        csb.InitialCatalog = "master";

        await using (var master = new SqlConnection(csb.ConnectionString))
        {
            await master.ExecuteAsync(new CommandDefinition(
                $"IF DB_ID(@DbName) IS NULL CREATE DATABASE [{dbName.Replace("]", "]]")}]",
                new { DbName = dbName },
                cancellationToken: ct));
        }

        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TenantRateLimitState')
            BEGIN
                CREATE TABLE TenantRateLimitState (
                    TenantId           NVARCHAR(128) NOT NULL PRIMARY KEY,
                    MaxCallCountPct    INT           NOT NULL DEFAULT 0,
                    MaxTotalTimePct    INT           NOT NULL DEFAULT 0,
                    MaxTotalCpuTimePct INT           NOT NULL DEFAULT 0,
                    BlockedUntilUtc    DATETIME2     NULL,
                    LastUpdatedUtc     DATETIME2     NOT NULL
                );
            END
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }
}
