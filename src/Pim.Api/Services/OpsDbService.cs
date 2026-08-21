using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pim.Api.Infrastructure.Ops;
using Pim.Core.Exceptions;

namespace Pim.Api.Services;

public record TableInfo(string Name, string Type);
public record ColumnInfo(string ColumnName, string DataType, bool IsNullable, string? DefaultValue);
public record OpsDbQueryResult(IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated);

public sealed class OpsDbService
{
    private readonly string _roConn;
    private readonly SqlAstValidator _validator;
    private const int DefaultMaxRows = 200;
    private const int MaxRowsLimit = 500;
    private const long MaxBytes = 5 * 1024 * 1024;
    private const int StatementTimeoutMs = 10000;
    private static readonly Regex TableNameRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);

    public OpsDbService(IConfiguration cfg, SqlAstValidator validator)
    {
        _roConn = cfg["PIM_OPS_RO_CONNECTION"] ?? cfg.GetConnectionString("OpsRo") ?? "";
        _validator = validator;
    }

    // For testing without IConfiguration
    public OpsDbService(string roConn, SqlAstValidator validator)
    {
        _roConn = roConn;
        _validator = validator;
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken ct)
    {
        EnsureConnectionConfigured();
        await using var conn = new NpgsqlConnection(_roConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var set1 = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, (NpgsqlTransaction)tx))
        {
            await set1.ExecuteNonQueryAsync(ct);
        }
        await using (var set2 = new NpgsqlCommand("SET statement_timeout = 10000", conn, (NpgsqlTransaction)tx))
        {
            await set2.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name, table_type FROM information_schema.tables WHERE table_schema='public' ORDER BY table_name", conn, (NpgsqlTransaction)tx);
        cmd.CommandTimeout = 10;
        var list = new List<TableInfo>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1);
                list.Add(new TableInfo(name, type));
            }
            await tx.CommitAsync(ct);
            return list;
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw new DomainException(40302, "RestrictedColumn");
        }
    }

    public async Task<IReadOnlyList<ColumnInfo>> DescribeAsync(string table, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(table) || !TableNameRegex.IsMatch(table))
            throw new DomainException(40002, "InvalidTableName");
        EnsureConnectionConfigured();
        await using var conn = new NpgsqlConnection(_roConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var set1 = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, (NpgsqlTransaction)tx))
        {
            await set1.ExecuteNonQueryAsync(ct);
        }
        await using (var set2 = new NpgsqlCommand("SET statement_timeout = 10000", conn, (NpgsqlTransaction)tx))
        {
            await set2.ExecuteNonQueryAsync(ct);
        }
        // verify table exists to return 404 if not
        try
        {
            await using var existCmd = new NpgsqlCommand(
                "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=@t LIMIT 1", conn, (NpgsqlTransaction)tx);
            existCmd.Parameters.AddWithValue("t", table);
            existCmd.CommandTimeout = 10;
            var exists = await existCmd.ExecuteScalarAsync(ct);
            if (exists is null)
                throw new DomainException(40401, "TableNotFound");

            await using var cmd = new NpgsqlCommand(
                "SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_schema='public' AND table_name=@t ORDER BY ordinal_position", conn, (NpgsqlTransaction)tx);
            cmd.Parameters.AddWithValue("t", table);
            cmd.CommandTimeout = 10;
            var cols = new List<ColumnInfo>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var col = reader.GetString(0);
                var dt = reader.GetString(1);
                var nullable = reader.GetString(2) == "YES";
                var def = reader.IsDBNull(3) ? null : reader.GetString(3);
                cols.Add(new ColumnInfo(col, dt, nullable, def));
            }
            await tx.CommitAsync(ct);
            return cols;
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw new DomainException(40302, "RestrictedColumn");
        }
    }

    public async Task<OpsDbQueryResult> QueryAsync(string sql, Dictionary<string, object?>? @params, int? maxRows, CancellationToken ct)
    {
        var (ok, err) = _validator.Validate(sql);
        if (!ok) throw new DomainException(40002, err!);
        var limit = Math.Clamp(maxRows ?? DefaultMaxRows, 1, MaxRowsLimit);
        EnsureConnectionConfigured();
        await using var conn = new NpgsqlConnection(_roConn);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var set1 = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, (NpgsqlTransaction)tx))
        {
            await set1.ExecuteNonQueryAsync(ct);
        }
        await using (var set2 = new NpgsqlCommand("SET statement_timeout = 10000", conn, (NpgsqlTransaction)tx))
        {
            await set2.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = new NpgsqlCommand(sql, conn, (NpgsqlTransaction)tx);
        cmd.CommandTimeout = 10;
        if (@params != null)
        {
            foreach (var kv in @params)
            {
                var name = kv.Key.TrimStart('@', ':');
                cmd.Parameters.AddWithValue(name, kv.Value ?? DBNull.Value);
            }
        }

        var rows = new List<Dictionary<string, object?>>();
        long bytes = 0;
        bool truncated = false;
        var sw = Stopwatch.StartNew();

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (sw.Elapsed.TotalSeconds > 10)
                {
                    truncated = true;
                    break;
                }
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var val = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                    row[name] = val;
                }
                // estimate bytes
                var json = JsonSerializer.Serialize(row);
                var lb = Encoding.UTF8.GetByteCount(json) + 1;
                if (bytes + lb > MaxBytes)
                {
                    truncated = true;
                    break;
                }
                rows.Add(row);
                bytes += lb;
                if (bytes >= MaxBytes)
                {
                    truncated = true;
                    break;
                }
            }

            try { await tx.CommitAsync(ct); } catch { await tx.RollbackAsync(ct); }

            return new OpsDbQueryResult(rows, truncated);
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw new DomainException(40302, "RestrictedColumn");
        }
    }

    private void EnsureConnectionConfigured()
    {
        if (string.IsNullOrWhiteSpace(_roConn))
            throw new DomainException(50301, "OpsRoConnectionNotConfigured");
    }
}
