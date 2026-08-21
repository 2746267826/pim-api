using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pim.Api.Infrastructure.Ops;

public sealed class SqlAstValidator
{
    private static readonly HashSet<string> RestrictedColumns = new(StringComparer.OrdinalIgnoreCase) { "password_hash", "token_hash" };

    // Enhanced regex with comment tolerance and system table coverage
    private static readonly Regex SelectStarRegex = new(@"\bSELECT\s*(/\*.*?\*/\s*)*\*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommaStarRegex = new(@",\s*(/\*.*?\*/\s*)*\*", RegexOptions.Compiled);
    private static readonly Regex DotStarRegex = new(@"\.\s*(/\*.*?\*/\s*)*\*", RegexOptions.Compiled);
    private static readonly Regex PgCatalogRegex = new(@"\bpg_catalog\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InformationSchemaRegex = new(@"\binformation_schema\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PgTableRegex = new(@"\bpg_\w+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RestrictedColRegex = new(@"\b(password_hash|token_hash)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AllowedStartRegex = new(@"^\s*(WITH\b|SELECT\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ForbiddenKeywordRegex = new(@"\b(DELETE|UPDATE|INSERT|DROP|ALTER|TRUNCATE|CREATE|GRANT|REVOKE|CALL|EXECUTE|VACUUM|REINDEX|CLUSTER|COMMENT|SECURITY|SHOW)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public (bool IsValid, string? Error) Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (false, "SqlEmpty");

        var trimmed = sql.Trim();

        // Check allowed start: must be SELECT or WITH
        if (!AllowedStartRegex.IsMatch(trimmed))
            return (false, "SqlNotAllowed");

        // Reject multiple statements (semicolon handling - allow trailing semicolon only)
        var withoutTrailing = trimmed.TrimEnd().TrimEnd(';').TrimEnd();
        var semicolonCount = trimmed.Count(c => c == ';');
        if (semicolonCount > 1) return (false, "SqlNotAllowed");
        if (semicolonCount == 1 && !trimmed.TrimEnd().EndsWith(";"))
            return (false, "SqlNotAllowed");
        if (withoutTrailing.Contains(';'))
            return (false, "SqlNotAllowed");

        // Forbidden keywords early (cheap)
        var forbiddenMatch = ForbiddenKeywordRegex.Match(trimmed);
        if (forbiddenMatch.Success)
        {
            return (false, "SqlNotAllowed");
        }

        // Try libpg_query AST validation via Npgquery (authoritative) - handles SELECT *, column blacklist, system tables precisely
        var pgResult = TryValidateWithPgQuery(trimmed);
        if (pgResult is not null)
        {
            return pgResult.Value;
        }

        // Fallback regex (when libpg_query unavailable): enhanced checks
        if (PgCatalogRegex.IsMatch(trimmed) || InformationSchemaRegex.IsMatch(trimmed))
            return (false, "SystemTableNotAllowed");
        if (PgTableRegex.IsMatch(trimmed))
            return (false, "SystemTableNotAllowed");

        var restrictedMatch = RestrictedColRegex.Match(trimmed);
        if (restrictedMatch.Success)
            return (false, $"ColumnRestricted:{restrictedMatch.Value.ToLowerInvariant()}");

        if (SelectStarRegex.IsMatch(trimmed) || CommaStarRegex.IsMatch(trimmed) || DotStarRegex.IsMatch(trimmed))
            return (false, "SelectStarNotAllowed");

        return (true, null);
    }

    private (bool IsValid, string? Error)? TryValidateWithPgQuery(string sql)
    {
        try
        {
            var parser = new Npgquery.Parser();
            var result = parser.Parse(sql);
            if (result.IsError)
            {
                // Parse error -> not allowed
                return (false, "SqlNotAllowed");
            }

            var pt = result.ParseTree;
            if (pt is null) return (false, "SqlNotAllowed");
            var root = pt.RootElement;

            // Check stmts count and type
            if (!root.TryGetProperty("stmts", out var stmts) || stmts.ValueKind != JsonValueKind.Array)
                return (false, "SqlNotAllowed");
            if (stmts.GetArrayLength() == 0)
                return (false, "SqlNotAllowed");
            if (stmts.GetArrayLength() > 1)
                return (false, "SqlNotAllowed");

            foreach (var raw in stmts.EnumerateArray())
            {
                if (!raw.TryGetProperty("stmt", out var stmtObj))
                    return (false, "SqlNotAllowed");
                if (stmtObj.ValueKind != JsonValueKind.Object)
                    return (false, "SqlNotAllowed");
                // Must contain SelectStmt and no other statement types
                if (!stmtObj.TryGetProperty("SelectStmt", out _))
                    return (false, "SqlNotAllowed");
                // Ensure no other top-level statement keys exist (e.g., DeleteStmt)
                foreach (var prop in stmtObj.EnumerateObject())
                {
                    if (prop.Name != "SelectStmt")
                        return (false, "SqlNotAllowed");
                }
            }

            // Walk tree for violations
            if (ContainsAStar(root))
                return (false, "SelectStarNotAllowed");

            var (hasRestricted, col) = FindRestrictedColumn(root);
            if (hasRestricted)
                return (false, $"ColumnRestricted:{col!.ToLowerInvariant()}");

            if (ContainsSystemTable(root))
                return (false, "SystemTableNotAllowed");

            // All good
            return (true, null);
        }
        catch (Npgquery.ParseException)
        {
            return (false, "SqlNotAllowed");
        }
        catch (Npgquery.NativeLibraryException)
        {
            // Native lib not available -> fallback to regex
            return null;
        }
        catch (Exception)
        {
            // Any other unexpected -> fallback to regex result (treat as no opinion)
            return null;
        }
    }

    private static bool ContainsAStar(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Name == "A_Star")
                        return true;
                    if (ContainsAStar(prop.Value))
                        return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsAStar(item))
                        return true;
                }
                break;
        }
        return false;
    }

    private static (bool found, string? col) FindRestrictedColumn(JsonElement element)
    {
        // Walk and look for ColumnRef objects
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Check if this object has "ColumnRef"
            if (element.TryGetProperty("ColumnRef", out var colRef))
            {
                if (colRef.ValueKind == JsonValueKind.Object && colRef.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
                {
                    foreach (var field in fields.EnumerateArray())
                    {
                        if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("String", out var strObj))
                        {
                            if (strObj.ValueKind == JsonValueKind.Object && strObj.TryGetProperty("sval", out var sval) && sval.ValueKind == JsonValueKind.String)
                            {
                                var val = sval.GetString();
                                if (val != null && RestrictedColumns.Contains(val))
                                    return (true, val);
                            }
                        }
                    }
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                var (found, col) = FindRestrictedColumn(prop.Value);
                if (found) return (true, col);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var (found, col) = FindRestrictedColumn(item);
                if (found) return (true, col);
            }
        }
        return (false, null);
    }

    private static bool ContainsSystemTable(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("RangeVar", out var rangeVar) && rangeVar.ValueKind == JsonValueKind.Object)
            {
                string? schemaname = null;
                string? relname = null;
                if (rangeVar.TryGetProperty("schemaname", out var sn) && sn.ValueKind == JsonValueKind.String)
                    schemaname = sn.GetString();
                if (rangeVar.TryGetProperty("relname", out var rn) && rn.ValueKind == JsonValueKind.String)
                    relname = rn.GetString();

                if (!string.IsNullOrEmpty(schemaname))
                {
                    if (string.Equals(schemaname, "pg_catalog", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(schemaname, "information_schema", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                if (!string.IsNullOrEmpty(relname))
                {
                    if (relname.StartsWith("pg_", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (ContainsSystemTable(prop.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsSystemTable(item))
                    return true;
            }
        }
        return false;
    }
}
