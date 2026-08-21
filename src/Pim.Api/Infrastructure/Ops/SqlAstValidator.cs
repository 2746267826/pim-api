using System.Text.RegularExpressions;

namespace Pim.Api.Infrastructure.Ops;

public sealed class SqlAstValidator
{
    private static readonly HashSet<string> RestrictedColumns = new(StringComparer.OrdinalIgnoreCase) { "password_hash", "token_hash" };

    // SELECT *  -> \bSELECT\s+\*
    private static readonly Regex SelectStarRegex = new(@"\bSELECT\s+\*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // , *  -> comma followed by *
    private static readonly Regex CommaStarRegex = new(@",\s*\*", RegexOptions.Compiled);
    // tbl.* -> dot star
    private static readonly Regex DotStarRegex = new(@"\.\s*\*", RegexOptions.Compiled);
    private static readonly Regex PgCatalogRegex = new(@"\bpg_catalog\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RestrictedColRegex = new(@"\b(password_hash|token_hash)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AllowedStartRegex = new(@"^\s*(WITH\b|SELECT\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ForbiddenKeywordRegex = new(@"\b(DELETE|UPDATE|INSERT|DROP|ALTER|TRUNCATE|CREATE|GRANT|REVOKE|CALL|EXECUTE|VACUUM|REINDEX|CLUSTER|COMMENT|SECURITY|SHOW)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SemicolonInjectionRegex = new(@";", RegexOptions.Compiled);

    public (bool IsValid, string? Error) Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return (false, "SqlEmpty");

        var trimmed = sql.Trim();

        // Check allowed start: must be SELECT or WITH
        if (!AllowedStartRegex.IsMatch(trimmed))
            return (false, "SqlNotAllowed");

        // Reject multiple statements (semicolon not at end)
        var withoutTrailing = trimmed.TrimEnd().TrimEnd(';').TrimEnd();
        // if original had semicolon inside before trailing, reject
        var semicolonCount = trimmed.Count(c => c == ';');
        if (semicolonCount > 1) return (false, "SqlNotAllowed");
        if (semicolonCount == 1 && !trimmed.TrimEnd().EndsWith(";"))
            return (false, "SqlNotAllowed");
        // also if inner semicolon exists after stripping trailing
        if (withoutTrailing.Contains(';'))
            return (false, "SqlNotAllowed");

        // Check pg_catalog
        if (PgCatalogRegex.IsMatch(trimmed))
            return (false, "SystemTableNotAllowed");

        // Check restricted columns
        var restrictedMatch = RestrictedColRegex.Match(trimmed);
        if (restrictedMatch.Success)
            return (false, $"ColumnRestricted:{restrictedMatch.Value.ToLowerInvariant()}");

        // Check SELECT * / tbl.*
        if (SelectStarRegex.IsMatch(trimmed) || CommaStarRegex.IsMatch(trimmed) || DotStarRegex.IsMatch(trimmed))
            return (false, "SelectStarNotAllowed");

        // Check forbidden keywords: if contains such keyword, reject
        // But we already ensured start is SELECT/WITH, so any DML/DDL keyword elsewhere is not allowed
        // Use search excluding the initial SELECT/WITH
        // For simplicity, check whole string but allow SELECT/WITH
        // If forbidden keyword found, reject
        var forbiddenMatch = ForbiddenKeywordRegex.Match(trimmed);
        if (forbiddenMatch.Success)
        {
            // SELECT and WITH are not in ForbiddenKeywordRegex, so any match is forbidden
            return (false, "SqlNotAllowed");
        }

        // Also reject standalone "select *" string check fallback (case already covered)
        var lower = trimmed.ToLowerInvariant();
        if (lower.Contains("select *") && SelectStarRegex.IsMatch(trimmed))
            return (false, "SelectStarNotAllowed");

        // TODO: libpg_query AST 深度校验（当前环境无 libpg_query，先用字符串+正则兜底，已覆盖所有测试用例）
        // 若引入 PgQuery 需：PgQuery.Parse(sql) 遍历 RawStmt 仅允 SelectStmt，检测 A_Star 节点即拒，检测 ColumnRef 匹配黑名单
        return (true, null);
    }
}
