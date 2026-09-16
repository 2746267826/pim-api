using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 迁移 DDL 的「重复执行安全」静态门禁（issue #271 根因固化）。
///
/// <para>
/// #271 的事故形态：<c>20260906132048_AddDaemonHeartbeatsUniqueIndex</c> 对
/// <c>pc_tracker_events</c> 执行 <c>AddColumn</c>/<c>CreateIndex</c>。但该表及其 browser/instance_id
/// 列、两个索引都由运行时 <c>PcTrackerSchemaInitializer</c> 的幂等 SQL 维护、<b>不在 EF 模型里</b>，
/// 于是一条从未写入历史的迁移在存量库上必然撞 42701（列已存在）→ 启动迁移失败 → 进程退出 →
/// supervisord 反复拉起，API 永远不健康。全新库上则是 42P01（表还不存在）。
/// </para>
///
/// <para>
/// 门禁直接读取 EF 的 <see cref="Migration.UpOperations"/>，<b>不去解析 C# 源码</b>：
/// 结构化 DDL 用操作对象上的 <c>Table</c> 属性（位置参数、命名参数、字符串重载都拿到同一个值），
/// 裸 SQL 用 <see cref="SqlOperation.Sql"/> 上已经过 C# 字符串处理的最终文本。
/// 早期版本用正则扫 .cs 源码，既漏掉位置参数写法（<c>AddColumn&lt;T&gt;("col", "table")</c>），
/// 又读不到逐字字符串（<c>Sql(@"...")</c>）——门禁在真正需要它的地方静默失效。
/// </para>
///
/// <list type="number">
///   <item><description>
///     <b>迁移只能操作自己建的表</b>：对「没有任何迁移 <c>CreateTable</c> 过」的表做结构化 DDL，
///     说明那张表归运行时 initializer 所有。
///   </description></item>
///   <item><description>
///     <b>裸 SQL DDL 必须自带幂等守卫</b>：<c>CREATE TABLE</c>/<c>CREATE INDEX</c>/<c>ADD COLUMN</c>/
///     <c>DROP …</c>/<c>ADD CONSTRAINT</c> 必须写成 <c>IF [NOT] EXISTS</c> 形式。
///   </description></item>
/// </list>
/// </summary>
public class MigrationGuardedDdlTests
{
    /// <summary>issue #271 的肇因迁移：修复后必须是空实现（全部对象另有归属）。</summary>
    private const string Issue271Migration = "AddDaemonHeartbeatsUniqueIndex";

    /// <summary>
    /// 允许对「非迁移所有表」做结构化 DDL 的显式豁免；<b>只减不增</b>。
    /// 空字典本身就是期望状态：#271 修好后不应再有任何一张这类表被迁移碰。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NonMigrationOwnedTableAllowlist =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Migrations_OnlyTouchTablesThatSomeMigrationCreates()
    {
        var migrations = LoadMigrations();
        var ownedTables = migrations.SelectMany(TablesCreatedBy).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(ownedTables);

        var offenders = new List<string>();
        foreach (var migration in migrations)
        {
            foreach (var (table, operation) in StructuredDdlTargets(migration))
            {
                if (ownedTables.Contains(table) || NonMigrationOwnedTableAllowlist.ContainsKey(table))
                {
                    continue;
                }

                offenders.Add($"{migration.GetType().Name}: {operation} 操作了非迁移建表的 {table}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下迁移操作了「没有任何迁移 CreateTable 过」的表。这类表由运行时 SchemaInitializer 以"
            + "幂等 SQL 维护，迁移里的结构化 DDL 会在存量库上撞 42701(列已存在)/42P07(对象已存在)、"
            + "在全新库上撞 42P01，让进程启动即失败（issue #271）："
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        // 覆盖自检：位置参数写法（AddColumn<string>("col", "table")）必须同样被认出来。
        // 早先用正则扫源码的实现只看 table: 命名参数，会整条漏掉这种写法。
        var positional = new PositionalArgumentMigration();
        var targeted = StructuredDdlTargets(positional).Select(t => t.Table).ToList();
        Assert.Contains("runtime_owned_table", targeted);
    }

    /// <summary>
    /// 只为覆盖检查而存在的探针迁移：用<b>位置参数</b>写法对一张非迁移建的表做 DDL。
    /// 它验证门禁读的是 EF 操作对象（而不是源码里的参数名），因此不受写法影响。
    /// </summary>
    private sealed class PositionalArgumentMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>("new_col", "runtime_owned_table", nullable: true);
            migrationBuilder.CreateIndex("ix_demo", "runtime_owned_table", "new_col");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }

    [Fact]
    public void RawSqlDdlInMigrations_IsGuardedWithIfExists()
    {
        var migrations = LoadMigrations();
        var sqlBlocks = migrations
            .SelectMany(m => SqlBlocks(m).Select(sql => (m.GetType().Name, Sql: sql)))
            .ToList();

        // 扫描面自检：必须真的读到了 DDL，否则下面的断言毫无意义（读法失效时会静默变绿）。
        Assert.True(
            sqlBlocks.Count > 0,
            "一条裸 SQL 都没读到，门禁已退化为恒真断言（SqlOperation.Sql 的读法可能失效了）");
        Assert.True(
            sqlBlocks.Any(block => Regex.IsMatch(
                StripSqlComments(block.Sql), @"\b(CREATE|ALTER|DROP)\b", RegexOptions.IgnoreCase)),
            "读到的裸 SQL 里没有任何 CREATE/ALTER/DROP，门禁实际没在检查 DDL");

        var unguarded = new List<string>();
        foreach (var (migrationName, sql) in sqlBlocks)
        {
            var code = StripSqlComments(sql);
            foreach (var (pattern, label) in UnguardedDdlPatterns)
            {
                foreach (Match match in pattern.Matches(code))
                {
                    unguarded.Add(
                        $"{migrationName}:{LineOf(code, match.Index)} [{label}] {Truncate(Snippet(code, match.Index))}");
                }
            }
        }

        Assert.True(
            unguarded.Count == 0,
            "以下裸 SQL 里的 DDL 没有 IF [NOT] EXISTS 守卫，重放或半应用状态会中断整条迁移链："
            + Environment.NewLine + string.Join(Environment.NewLine, unguarded));

        // 反向验证：规则认得出来未加守卫的写法，也不是恒真的空断言。
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

        // 注释剥离必须是「字符串感知」的：注释里的 DDL 不算数，字符串里的注释符也不算注释。
        AssertUnflagged("/* CREATE INDEX ux_demo ON demo (a); */");
        AssertUnflagged("CREATE TABLE IF NOT EXISTS demo (a int DEFAULT '--');");
        AssertUnflagged("CREATE TABLE IF NOT EXISTS demo (a text DEFAULT '/*');");
        // 双引号标识符里的 -- 不能被当成行注释而把后面的 DDL 吞掉。
        AssertFlagged("ALTER TABLE demo ADD COLUMN \"we--ird\" int;");
        AssertFlagged("ALTER TABLE demo ADD COLUMN \"a/*b\" int;");
        // 行注释不能吃掉下一行的 DDL（这正是不该漏报的场景）。
        AssertFlagged("-- 注释\nALTER TABLE demo ADD COLUMN b int;");
        // 转义单引号之后的 DDL 仍要被检查。
        AssertFlagged("CREATE TABLE IF NOT EXISTS demo (a text DEFAULT 'it''s'); ALTER TABLE demo ADD c int;");
    }

    /// <summary>
    /// 注释剥离的边界回归：这些样例锁住「字符串感知」的实现，避免退回简单正则后
    /// 出现「把真实 DDL 当注释剥掉」的漏报。
    /// </summary>
    [Fact]
    public void StripSqlComments_IsStringAware()
    {
        // 行注释整条剥掉；换行保留。
        Assert.DoesNotContain("CREATE", StripSqlComments("-- CREATE INDEX ux ON demo (a);"), StringComparison.Ordinal);
        // 块注释整条剥掉。
        Assert.DoesNotContain("ALTER", StripSqlComments("/* ALTER TABLE demo ADD c int; */"), StringComparison.Ordinal);
        // 未闭合块注释把后面全部当注释（不会误报为 DDL）。
        Assert.DoesNotContain("ALTER", StripSqlComments("/* ALTER TABLE demo ADD c int;"), StringComparison.Ordinal);
        // 注释后的 DDL 必须保留下来。
        Assert.Contains("ALTER", StripSqlComments("/* note */ ALTER TABLE demo ADD c int;"), StringComparison.Ordinal);
        Assert.Contains("ALTER", StripSqlComments("-- note\nALTER TABLE demo ADD c int;"), StringComparison.Ordinal);

        // 单引号字符串里的注释符是数据，不能被剥掉。
        Assert.Contains("'--'", StripSqlComments("SELECT '--';"), StringComparison.Ordinal);
        Assert.Contains("'/*'", StripSqlComments("SELECT '/*';"), StringComparison.Ordinal);
        // 转义引号 '' 不结束字符串。
        Assert.Contains("'it''s--still'", StripSqlComments("SELECT 'it''s--still';"), StringComparison.Ordinal);

        // $1 这类参数占位符不是 dollar-quote 定界符，后面的 DDL 不能被吞掉。
        Assert.Contains("ALTER", StripSqlComments("SELECT $1; ALTER TABLE demo ADD c int;"), StringComparison.Ordinal);
        // dollar-quoted 块的内容会真实执行，必须保留以便继续检查。
        Assert.Contains("INDEX", StripSqlComments("DO $$ BEGIN CREATE INDEX ux ON demo (a); END $$;"), StringComparison.Ordinal);

        // 引号转义后的注释仍要被剥掉：即 "a""b" 与 'a''b' 都必须被当作「一个字面引号」，
        // 否则解析器会以为自己还在字符串/标识符里，把后面的注释当成正文，造成漏报。
        foreach (var prefix in new[]
                 {
                     "ALTER TABLE demo ADD COLUMN \"a\"\"b\" int;",
                     "ALTER TABLE demo ADD COLUMN c text DEFAULT 'a''b';",
                 })
        {
            var line = StripSqlComments(prefix + " -- CREATE INDEX ux_demo ON demo (a);");
            Assert.DoesNotContain("CREATE INDEX", line, StringComparison.Ordinal);

            var block = StripSqlComments(prefix + " /* CREATE INDEX ux_demo ON demo (a); */");
            Assert.DoesNotContain("CREATE INDEX", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// #271 的定点回归：肇因迁移必须保持空实现。
    /// 它要建的每个对象都另有更早或更可靠的所有者（Stage0 的唯一索引、运行时 initializer 的
    /// tracker 表与列），任何「把它加回来」的改动都会重新引入启动即失败。
    /// </summary>
    [Fact]
    public void Issue271Migration_StaysEmpty()
    {
        var migration = LoadMigrations()
            .SingleOrDefault(m => m.GetType().Name == Issue271Migration);

        Assert.NotNull(migration);

        var schemaChanging = migration!.UpOperations
            .Where(op => op is not SqlOperation)
            .Select(op => op.GetType().Name)
            .ToList();
        Assert.True(
            schemaChanging.Count == 0,
            $"{Issue271Migration} 的 Up 不应再做结构化 schema 变更（{string.Join(", ", schemaChanging)}）："
            + "这些对象由 Stage0 迁移与运行时 PcTrackerSchemaInitializer 拥有，重复创建会撞 42701/42P07。");

        var sqlStatements = string.Join(
            "\n",
            migration.UpOperations.OfType<SqlOperation>().Select(op => op.Sql ?? string.Empty));
        foreach (var (pattern, label) in UnguardedDdlPatterns)
        {
            Assert.False(
                pattern.IsMatch(StripSqlComments(sqlStatements)),
                $"{Issue271Migration} 的 Up 里仍有未加守卫的裸 SQL DDL（{label}）");
        }

        // Down 同样必须为空：原实现会删 tracker 列（不可逆数据丢失）并重建缺 COALESCE 的
        // ux_tracker_events_dedup（回归 #173）。
        Assert.Empty(migration.DownOperations.Where(op => op is not SqlOperation));
        Assert.DoesNotContain(
            migration.DownOperations.OfType<SqlOperation>(),
            op => Regex.IsMatch(StripSqlComments(op.Sql ?? string.Empty), @"\bDROP\s+COLUMN\b", RegexOptions.IgnoreCase));
    }

    /// <summary>断言某条 SQL 在门禁下「干净」。</summary>
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

    private static List<Migration> LoadMigrations()
    {
        var assembly = typeof(Pim.Infrastructure.Data.PimDbContext).Assembly;
        var migrations = assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Migration)))
            .Select(t => (Migration)Activator.CreateInstance(t)!)
            .ToList();

        Assert.NotEmpty(migrations);
        return migrations;
    }

    /// <summary>
    /// 取迁移 <c>Up</c> 里建立的表名：结构化 <see cref="CreateTableOperation"/> 的 <c>Name</c>，
    /// 加上裸 SQL 里的 <c>CREATE TABLE</c>。
    /// </summary>
    private static IEnumerable<string> TablesCreatedBy(Migration migration)
    {
        foreach (var operation in migration.UpOperations)
        {
            switch (operation)
            {
                case CreateTableOperation createTable when !string.IsNullOrWhiteSpace(createTable.Name):
                    yield return createTable.Name;
                    break;

                case SqlOperation sql:
                    foreach (Match match in RawCreateTablePattern.Matches(sql.Sql ?? string.Empty))
                    {
                        yield return match.Groups["name"].Value;
                    }

                    break;
            }
        }
    }

    private static readonly Regex RawCreateTablePattern = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[""']?(?<name>[a-zA-Z_][a-zA-Z0-9_]*)[""']?",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// 结构化 DDL 的目标表与操作名。
    ///
    /// <para>
    /// 用反射读取操作对象上的所有 <c>Table</c> / <c>PrincipalTable</c> 属性，
    /// 而不是硬编码操作类型清单：EF 新增操作类型（<c>RenameColumnOperation</c>、
    /// <c>AddPrimaryKeyOperation</c>、<c>DropTableOperation</c>、外键的 <c>PrincipalTable</c>…）
    /// 时会自动纳入，不会留下「清单没跟上」的漏报。
    /// </para>
    ///
    /// <para>
    /// 同时天然覆盖位置参数/命名参数/字符串重载三种写法 —— 门禁读的是操作对象，
    /// 而不是源码里的参数名（正则扫源码会整条漏掉 <c>AddColumn&lt;T&gt;("col", "table")</c>）。
    /// </para>
    /// </summary>
    private static IEnumerable<(string Table, string Operation)> StructuredDdlTargets(Migration migration)
    {
        foreach (var operation in migration.UpOperations)
        {
            // CreateTable 自己就是建表，不参与「是否操作了别人表」的判定。
            if (operation is CreateTableOperation)
            {
                continue;
            }

            // 去重：外键操作同时有 Table（子表）与 PrincipalTable（父表），
            // 两者都要检查，但同一张表只报一次，避免错误信息里出现重复行。
            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in TableTargetsOf(operation))
            {
                if (reported.Add(table))
                {
                    yield return (table, operation.GetType().Name);
                }
            }
        }
    }

    /// <summary>
    /// 读取操作对象上所有指向表名的属性。
    ///
    /// <para>
    /// 只有两类属性可能是表名：<c>Table</c> / <c>PrincipalTable</c>（各类列/索引/外键操作），
    /// 以及表级操作（<c>DropTableOperation</c>/<c>RenameTableOperation</c>/<c>AlterTableOperation</c>…）
    /// 的 <c>Name</c>。其余操作的 <c>Name</c> 是索引名、约束名、序列名或 schema 名，
    /// 不能被当成表名（否则会产生假阳性）。
    /// </para>
    /// </summary>
    private static IEnumerable<string> TableTargetsOf(MigrationOperation operation)
    {
        var type = operation.GetType();
        var isTableLevelOperation = type.Name.EndsWith("TableOperation", StringComparison.Ordinal);

        foreach (var property in type.GetProperties())
        {
            if (property.PropertyType != typeof(string))
            {
                continue;
            }

            var isTableProperty = property.Name.EndsWith("Table", StringComparison.Ordinal);
            var isTableName = isTableLevelOperation && property.Name == "Name";
            if (!isTableProperty && !isTableName)
            {
                continue;
            }

            if (property.GetValue(operation) is string value && !string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static IEnumerable<string> SqlBlocks(Migration migration) =>
        migration.UpOperations
            .OfType<SqlOperation>()
            .Select(op => op.Sql)
            .Where(sql => !string.IsNullOrWhiteSpace(sql))
            .Select(sql => sql!);

    /// <summary>
    /// 去掉 SQL 注释再匹配：否则注释里出现的 "CREATE INDEX ..." 会被误判成真实 DDL
    /// （迁移里写「不要用 CREATE INDEX」的说明是很自然的事）。
    ///
    /// <para>
    /// 按字符扫描而不是直接上正则：单引号字符串里的 <c>--</c> 或 <c>/*</c> 是数据、不是注释
    /// （例如 <c>DEFAULT '--'</c>），双引号标识符与 <c>$$ … $$</c> 里的内容同样要按原样保留。
    /// 早期版本用纯正则会把这些情况误剥，导致漏报。
    /// </para>
    /// </summary>
    private static string StripSqlComments(string sql)
    {
        var result = new System.Text.StringBuilder(sql.Length);
        var inSingle = false;
        var inDouble = false;
        var dollarTag = (string?)null;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            // dollar-quoted 块（$$ … $$ 或 $tag$ … $tag$）整体保留。
            if (dollarTag is not null)
            {
                if (sql.AsSpan(i).StartsWith(dollarTag, StringComparison.Ordinal))
                {
                    result.Append(dollarTag);
                    i += dollarTag.Length - 1;
                    dollarTag = null;
                }
                else
                {
                    result.Append(c);
                }

                continue;
            }

            if (inSingle)
            {
                result.Append(c);
                if (c == '\'')
                {
                    // '' 是转义的引号，不算结束。
                    if (i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        result.Append('\'');
                        i++;
                    }
                    else
                    {
                        inSingle = false;
                    }
                }

                continue;
            }

            if (inDouble)
            {
                result.Append(c);
                if (c == '"')
                {
                    // 与单引号同理："" 是转义的双引号（表示标识符里的一个字面 "），不算结束。
                    // 即使不处理，成对的 "" 也只是「关了又开」、净状态相同；这里显式处理是为了
                    // 让意图清楚，并避免将来有人把它简化成单次 toggle 时踩坑。
                    if (i + 1 < sql.Length && sql[i + 1] == '"')
                    {
                        result.Append('"');
                        i++;
                    }
                    else
                    {
                        inDouble = false;
                    }
                }

                continue;
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    result.Append(c);
                    break;

                case '"':
                    inDouble = true;
                    result.Append(c);
                    break;

                case '$':
                    var tag = MatchDollarTag(sql, i);
                    if (tag is not null)
                    {
                        dollarTag = tag;
                        result.Append(tag);
                        i += tag.Length - 1;
                    }
                    else
                    {
                        result.Append(c);
                    }

                    break;

                case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                    // 行注释：跳到行尾（保留换行以维持行号）。
                    while (i < sql.Length && sql[i] != '\n')
                    {
                        i++;
                    }

                    if (i < sql.Length)
                    {
                        result.Append('\n');
                    }

                    break;

                case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                    i += 2;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    {
                        if (sql[i] == '\n')
                        {
                            result.Append('\n');
                        }

                        i++;
                    }

                    i++;
                    break;

                default:
                    result.Append(c);
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>在 <paramref name="index"/> 处匹配 <c>$$</c> / <c>$tag$</c> 定界符，不匹配则返回 null。</summary>
    private static string? MatchDollarTag(string sql, int index)
    {
        var end = sql.IndexOf('$', index + 1);
        if (end < 0)
        {
            return null;
        }

        var tag = sql[index..(end + 1)];
        // 标签只能是字母/下划线，且不能含空白或引号。
        return tag[1..^1].All(ch => char.IsLetterOrDigit(ch) || ch == '_') ? tag : null;
    }

    /// <summary>
    /// 裸 SQL 里出现即视为「缺守卫」的 DDL 形态。
    ///
    /// <para>
    /// <c>ALTER TABLE … ALTER COLUMN …</c> 有意<b>不</b>在此列：它没有 <c>IF EXISTS</c> 语法，
    /// 但 <c>SET DEFAULT</c> / <c>DROP DEFAULT</c> / <c>SET NOT NULL</c> 本身就天然幂等
    /// （重复执行同一个 SET 是 no-op），迁移 <c>20260705122322</c> 大量使用这类语句且工作正常。
    /// 会因重复执行而中断的是「创建/删除对象」与「加列」——那些都在下面的清单里。
    /// <c>ALTER COLUMN … TYPE</c> 会重写表、重复执行代价高但不会失败，属于性能问题而非
    /// 「中断迁移链」的缺陷，故一并放行。
    /// </para>
    /// </summary>
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

    private static int LineOf(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static string Snippet(string text, int index)
    {
        var start = Math.Max(0, index);
        var length = Math.Min(80, text.Length - start);
        return text.Substring(start, length);
    }

    private static string Truncate(string value)
    {
        var single = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= 80 ? single : single[..80];
    }
}
