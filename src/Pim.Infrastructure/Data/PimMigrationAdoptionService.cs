using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Pim.Infrastructure.Data;

public sealed class PimMigrationAdoptionService
{
    public const string BaselineMigrationId = "20260524000000_BaselineExistingSchema";

    private readonly PimDbContext _db;
    private readonly ILogger<PimMigrationAdoptionService> _logger;

    public PimMigrationAdoptionService(PimDbContext db, ILogger<PimMigrationAdoptionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public static bool NeedsBaselineAdoption(bool usersTableExists, bool historyTableExists)
        => usersTableExists && !historyTableExists;

    public async Task AdoptExistingSchemaAsync(CancellationToken ct = default)
    {
        var usersTableExists = await TableExistsAsync("public", "users", ct);
        var historyTableExists = await TableExistsAsync("public", "__EFMigrationsHistory", ct);

        if (!NeedsBaselineAdoption(usersTableExists, historyTableExists))
        {
            return;
        }

        _logger.LogWarning("Adopting existing database schema as EF migration baseline {MigrationId}", BaselineMigrationId);

        await _db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260524000000_BaselineExistingSchema', '8.0.11')
ON CONFLICT ("MigrationId") DO NOTHING;
""", ct);
    }

    private async Task<bool> TableExistsAsync(string schema, string table, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT EXISTS (
    SELECT 1
    FROM information_schema.tables
    WHERE table_schema = @schema AND table_name = @table
);
""";

        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "schema";
        schemaParameter.Value = schema;
        command.Parameters.Add(schemaParameter);

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);

        var result = await command.ExecuteScalarAsync(ct);
        return result is bool exists && exists;
    }
}
