using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 迁移 DDL 的「重复执行安全」静态门禁（issue #271 根因固化）。
///
/// <para>
/// #271 的事故形态：<c>20260906132048_AddDaemonHeartbeatsUniqueIndex</c> 对
/// <c>pc_tracker_events</c> 执行 <c>AddColumn</c>/<c>CreateIndex</c>。但该表及其 browser/instance_id
/// 列、两个索引都由运行时 <c>PcTrackerSchemaInitializer</c> 的幂等 SQL 维护、<b>不在 EF 模型里</b>，
/// 于是一条从未成功写入历史的迁移在存量库上必然撞 42701（列已存在）→ 启动迁移失败 → 进程退出 →
/// supervisord 反复拉起，API 永远不健康。
/// </para>
///
/// <para>本门禁用两条不依赖真库的静态规则，把这类缺陷挡在提交前（真库侧由
/// <see cref="Migration271RealDbTests"/> 重放真实迁移链兜底）：</para>
/// <list type="number">
///   <item><description>
///     <b>迁移只能操作自己建的表</b>：对「没有任何迁移 <c>CreateTable</c> 过」的表做结构化 DDL，
///     说明那张表归运行时 initializer 所有 —— 迁移里的 <c>AddColumn</c>/<c>CreateIndex</c>
///     在「对象已存在」的存量库上必然失败。
///   </description></item>
///   <item><description>
///     <b>裸 SQL DDL 必须自带幂等守卫</b>：<c>migrationBuilder.Sql</c> 里的
///     <c>CREATE TABLE</c>/<c>CREATE INDEX</c>/<c>ADD COLUMN</c>/<c>DROP …</c>/<c>ADD CONSTRAINT</c>
///     必须写成 <c>IF [NOT] EXISTS</c> 形式，否则重放或半应用状态会中断整条迁移链。
///   </description></item>
/// </list>
/// </summary>
public class MigrationGuardedDdlTests
{
    private const string MigrationsRelativePath = "src/Pim.Infrastructure/Data/Migrations";

    /// <summary>
    /// 允许对「非迁移所有表」做结构化 DDL 的显式豁免；<b>只减不增</b>。
    /// 空字典本身就是期望状态：#271 修好后不应再有任何一张这类表被迁移碰。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NonMigrationOwnedTableAllowlist =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Migrations_OnlyTouchTablesThatSomeMigrationCreates()
    {
        var ownedTables = LoadMigrations()
            .SelectMany(TablesCreatedBy)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(ownedTables);

        var offenders = new List<string>();
        foreach (var (file, source) in EnumerateMigrationSources())
        {
            var up = ExtractMethod(source, "Up");
            if (up is null)
            {
                continue;
            }

            foreach (var (table, operation) in StructuredDdlTargets(up))
            {
                if (ownedTables.Contains(table) || NonMigrationOwnedTableAllowlist.ContainsKey(table))
                {
                    continue;
                }

                offenders.Add($"{file}: {operation} 操作了非迁移建表的 {table}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下迁移操作了「没有任何迁移 CreateTable 过」的表。这类表由运行时 SchemaInitializer 以"
            + "幂等 SQL 维护，迁移里的结构化 DDL 会在存量库上撞 42701(列已存在)/42P07(对象已存在)，"
            + "让进程启动即失败（issue #271）："
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void RawSqlDdlInMigrations_IsGuardedWithIfExists()
    {
        var unguarded = new List<string>();
        foreach (var (file, source) in EnumerateMigrationSources())
        {
            foreach (var (line, statement) in RawSqlStatements(source))
            {
                var code = StripSqlComments(statement);
                foreach (var (pattern, label) in UnguardedDdlPatterns)
                {
                    if (pattern.IsMatch(code))
                    {
                        unguarded.Add($"{file}:{line} [{label}] {Truncate(statement)}");
                    }
                }
            }
        }

        Assert.True(
            unguarded.Count == 0,
            "以下裸 SQL 里的 DDL 没有 IF [NOT] EXISTS 守卫，重放或半应用状态会中断整条迁移链："
            + Environment.NewLine + string.Join(Environment.NewLine, unguarded));

        // 反向验证：规则确实认得出来未加守卫的写法，不是恒真的空断言。
        AssertUnflagged("CREATE UNIQUE INDEX IF NOT EXISTS ux_demo ON demo (a);");
        AssertUnflagged("CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_demo ON demo (a);");
        AssertUnflagged("CREATE TABLE IF NOT EXISTS demo (a int);");
        AssertUnflagged("ALTER TABLE demo ADD COLUMN IF NOT EXISTS b int;");
        AssertUnflagged("ALTER TABLE demo ADD IF NOT EXISTS b int;");
        AssertUnflagged("DROP INDEX IF EXISTS ux_demo;");
        AssertUnflagged("ALTER TABLE demo DROP COLUMN IF EXISTS b;");
        AssertUnflagged("-- CREATE INDEX ux_demo ON demo (a);");

        AssertFlagged("CREATE UNIQUE INDEX ux_demo ON demo (a);");
        AssertFlagged("CREATE TABLE demo (a int);");
        AssertFlagged("ALTER TABLE demo ADD COLUMN b int;");
        // PostgreSQL 里 COLUMN 关键字可省略：省略写法同样必须被拦住。
        AssertFlagged("ALTER TABLE demo ADD b int;");
        AssertFlagged("DROP INDEX ux_demo;");
        AssertFlagged("ALTER TABLE demo DROP COLUMN b;");
        AssertFlagged("ALTER TABLE demo ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES x (id);");
    }

    /// <summary>断言某条 SQL 在门禁下「干净」（没有命中任何未加守卫的 DDL 形态）。</summary>
    private static void AssertUnflagged(string statement)
    {
        var hit = UnguardedDdlPatterns
            .Where(entry => entry.Pattern.IsMatch(StripSqlComments(statement)))
            .Select(entry => entry.Label)
            .ToList();

        Assert.True(
            hit.Count == 0,
            $"该语句本应视为已加守卫，却被判为违规（{string.Join(", ", hit)}）：{statement}");
    }

    /// <summary>断言某条 SQL 会命中门禁（证明规则不是恒假的空断言）。</summary>
    private static void AssertFlagged(string statement)
    {
        Assert.True(
            UnguardedDdlPatterns.Any(entry => entry.Pattern.IsMatch(StripSqlComments(statement))),
            $"该语句本应被判为「缺守卫」，却漏过了门禁：{statement}");
    }

    /// <summary>
    /// 去掉 SQL 注释再匹配：否则注释里出现的 "CREATE INDEX ..." 会被误判成真实 DDL
    /// （迁移里写「不要用 CREATE INDEX」的说明是很自然的事）。
    /// </summary>
    private static string StripSqlComments(string sql) =>
        BlockCommentPattern.Replace(LineCommentPattern.Replace(sql, " "), " ");

    private static readonly Regex LineCommentPattern = new(@"--[^\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockCommentPattern = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>裸 SQL 里出现即视为「缺守卫」的 DDL 形态。</summary>
    private static readonly (Regex Pattern, string Label)[] UnguardedDdlPatterns =
    [
        (new Regex(@"\bCREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS\b)", RegexOptions.IgnoreCase), "CREATE TABLE"),
        (new Regex(
            // CONCURRENTLY 写在 IF NOT EXISTS 之前，必须放进同一个前瞻里判断：
            // 若把它写成前面一个可选分组，正则回溯会绕过前瞻、把合法写法误判成违规。
            @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+(?!(?:CONCURRENTLY\s+)?IF\s+NOT\s+EXISTS\b)",
            RegexOptions.IgnoreCase), "CREATE INDEX"),
        (new Regex(
            @"\bALTER\s+TABLE\s+\S+\s+ADD\s+(?!COLUMN\s+IF\s+NOT\s+EXISTS\b)(?!IF\s+NOT\s+EXISTS\b)(?!CONSTRAINT\b)",
            RegexOptions.IgnoreCase), "ADD COLUMN"),
        (new Regex(@"\bDROP\s+(?:TABLE|INDEX)\s+(?!IF\s+EXISTS\b)", RegexOptions.IgnoreCase), "DROP TABLE/INDEX"),
        (new Regex(@"\bDROP\s+COLUMN\s+(?!IF\s+EXISTS\b)", RegexOptions.IgnoreCase), "DROP COLUMN"),
        (new Regex(@"\bADD\s+CONSTRAINT\s+(?!IF\s+NOT\s+EXISTS\b)", RegexOptions.IgnoreCase), "ADD CONSTRAINT"),
    ];

    private static List<Migration> LoadMigrations()
    {
        var assembly = typeof(Pim.Infrastructure.Data.PimDbContext).Assembly;
        return assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Migration)))
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .ToList();
    }

    /// <summary>取迁移 <c>Up</c> 里建立的表名（结构化 <c>CreateTable</c> + 裸 SQL 写法）。</summary>
    private static IEnumerable<string> TablesCreatedBy(Migration migration)
    {
        foreach (var operation in migration.UpOperations)
        {
            var type = operation.GetType();

            if (type.Name == "CreateTableOperation")
            {
                if (type.GetProperty("Name")?.GetValue(operation) is string name && name.Length > 0)
                {
                    yield return name;
                }

                continue;
            }

            if (type.Name != "SqlOperation")
            {
                continue;
            }

            if (type.GetProperty("Sql")?.GetValue(operation) is not string sql)
            {
                continue;
            }

            foreach (Match match in RawCreateTablePattern.Matches(sql))
            {
                yield return match.Groups["name"].Value;
            }
        }
    }

    private static readonly Regex RawCreateTablePattern = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[""']?(?<name>[a-zA-Z_][a-zA-Z0-9_]*)[""']?",
        RegexOptions.IgnoreCase);

    /// <summary>结构化 DDL 的目标表与操作名（只认带 <c>table:</c> 具名参数的重载）。</summary>
    private static IEnumerable<(string Table, string Operation)> StructuredDdlTargets(string upBody)
    {
        foreach (Match call in StructuredDdlCallPattern.Matches(upBody))
        {
            var args = ReadBalancedArguments(upBody, call.Index + call.Length - 1);
            var table = Regex.Match(args, @"\btable:\s*""(?<t>[^""]+)""", RegexOptions.IgnoreCase);

            if (!table.Success)
            {
                continue;
            }

            yield return (table.Groups["t"].Value, call.Groups["op"].Value);
        }
    }

    private static readonly Regex StructuredDdlCallPattern = new(
        @"(?<op>AddColumn|DropColumn|AlterColumn|CreateIndex|DropIndex|RenameIndex|AddForeignKey|DropForeignKey)"
        + @"(?:<[^<>()]*>)?\s*\(",
        RegexOptions.IgnoreCase);

    /// <summary>从 <paramref name="openParen"/> 处的 <c>(</c> 起，读回括号平衡的实参文本。</summary>
    private static string ReadBalancedArguments(string text, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[openParen..(i + 1)];
                }
            }
        }

        return text[openParen..];
    }

    /// <summary>
    /// 取出 <c>migrationBuilder.Sql(...)</c> 的字符串实参，按分号切成语句并给出源码行号。
    /// 行号按「语句结束的分号所在行」计，便于直接跳到文件里对应位置。
    /// </summary>
    private static IEnumerable<(int Line, string Statement)> RawSqlStatements(string source)
    {
        foreach (Match match in RawSqlCallPattern.Matches(source))
        {
            var delimiter = match.Groups["d"].Value;
            var start = match.Index + match.Length;
            var end = source.IndexOf(delimiter, start, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var body = source[start..end];
            var statementStart = 0;
            var line = source[..start].Count(c => c == '\n') + 1;

            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '\n')
                {
                    line++;
                }

                if (body[i] != ';')
                {
                    continue;
                }

                var statement = body[statementStart..(i + 1)].Trim();
                if (statement.Length > 0)
                {
                    yield return (line, statement);
                }

                statementStart = i + 1;
            }

            var tail = body[statementStart..].Trim();
            if (tail.Length > 0)
            {
                yield return (line, tail);
            }
        }
    }

    private static readonly Regex RawSqlCallPattern = new(
        @"migrationBuilder\.Sql\(\s*(?<d>@?""""|@?"")",
        RegexOptions.IgnoreCase);

    /// <summary>按花括号平衡取出方法体。</summary>
    private static string? ExtractMethod(string source, string methodName)
    {
        var signature = Regex.Match(
            source,
            @"(?:protected|public|private|internal)[^\n]*\b" + methodName + @"\s*\(");
        if (!signature.Success)
        {
            return null;
        }

        var open = source.IndexOf('{', signature.Index + signature.Length);
        if (open < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        return source[open..];
    }

    private static string Truncate(string value)
    {
        var single = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 70 ? single : single[..70];
    }

    private static IEnumerable<(string File, string Source)> EnumerateMigrationSources()
    {
        var directory = Path.Combine(
            ResolveRepositoryRoot(),
            MigrationsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(directory), $"迁移目录不存在：{directory}");

        var files = Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(p => !p.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith("PimDbContextModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(files);

        foreach (var path in files)
        {
            yield return (Path.GetFileName(path), File.ReadAllText(path));
        }
    }

    /// <summary>从测试输出的 bin 目录向上找到含 Pim.sln 的仓库根。</summary>
    private static string ResolveRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pim.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根目录（缺少 Pim.sln）");
    }
}
