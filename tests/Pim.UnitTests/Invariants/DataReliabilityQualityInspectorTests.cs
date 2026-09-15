using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

public class DataReliabilityQualityInspectorTests
{
    [Fact]
    public void Invariants_WhenCollectionsEmpty_ReturnUnknownStatus()
    {
        // 验证空集合不得视为通过（绝不亮假绿灯），全部判为 ⚪ UNKNOWN
        var r1 = DataReliabilityInvariants.CheckS1_NoOverlap(new List<EventTimeSpan>());
        Assert.Equal(InvariantStatus.Unknown, r1.Status);
        Assert.False(r1.Pass);
        Assert.StartsWith("INV-P16 UNKNOWN", r1.Detail);

        var r2 = DataReliabilityInvariants.CheckS2_OverlongEventEvidence(new List<LongEventCandidate>());
        Assert.Equal(InvariantStatus.Unknown, r2.Status);
        Assert.False(r2.Pass);

        var r3 = DataReliabilityInvariants.CheckS3_DailyDurationBounded(new List<DailyActiveDuration>());
        Assert.Equal(InvariantStatus.Unknown, r3.Status);
        Assert.False(r3.Pass);

        var r4 = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(new List<BusinessRecordKey>());
        Assert.Equal(InvariantStatus.Unknown, r4.Status);
        Assert.False(r4.Pass);

        var r5 = DataReliabilityInvariants.CheckS5_ClockTrustworthy(new List<ClockEventItem>());
        Assert.Equal(InvariantStatus.Unknown, r5.Status);
        Assert.False(r5.Pass);

        var r6 = DataReliabilityInvariants.CheckS6_OfflineDeclared(new DeviceActivityTrace());
        Assert.Equal(InvariantStatus.Unknown, r6.Status);
        Assert.False(r6.Pass);

        var r7 = DataReliabilityInvariants.CheckS7_TimelineGapMarked(new List<TimelineInterval>());
        Assert.Equal(InvariantStatus.Unknown, r7.Status);
        Assert.False(r7.Pass);

        var r8 = DataReliabilityInvariants.CheckS8_DayBoundaryConsistent(new List<DayBoundarySample>());
        Assert.Equal(InvariantStatus.Unknown, r8.Status);
        Assert.False(r8.Pass);

        var r9 = DataReliabilityInvariants.CheckS9_GapHasSignal(new CoverageSignalReport { OnlineDurationSeconds = 0 });
        Assert.Equal(InvariantStatus.Unknown, r9.Status);
        Assert.False(r9.Pass);

        var r10 = DataReliabilityInvariants.CheckS10_TaskHasOutput(new List<BackgroundTaskRun>());
        Assert.Equal(InvariantStatus.Unknown, r10.Status);
        Assert.False(r10.Pass);

        var r11 = DataReliabilityInvariants.CheckS11_StatusSemantics(new List<BatchSyncStatusRecord>());
        Assert.Equal(InvariantStatus.Unknown, r11.Status);
        Assert.False(r11.Pass);

        var r12 = DataReliabilityInvariants.CheckS12_DerivedTableActive(new List<DerivedTableStatus>());
        Assert.Equal(InvariantStatus.Unknown, r12.Status);
        Assert.False(r12.Pass);

        var r13 = DataReliabilityInvariants.CheckS13_SingleInstance(new List<CollectionHeartbeat>());
        Assert.Equal(InvariantStatus.Unknown, r13.Status);
        Assert.False(r13.Pass);
    }

    [Fact]
    public async Task Inspector_WhenDbNull_ReturnsUnhealthyAndThirteenUnknowns()
    {
        var options = Options.Create(new InvariantOptions());
        var inspector = new DataReliabilityQualityInspector(null, options, NullLogger<DataReliabilityQualityInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        Assert.False(result.IsHealthy);
        Assert.Equal(13, result.IssueCount);

        // Details 是可空属性：先钉住再取值，后续断言就不会踩空。
        Assert.NotNull(result.Details);
        var details = result.Details!;
        Assert.Contains("S1_INV-P16", details.Keys);
        Assert.Contains("S13_INV-P22", details.Keys);

        // 13 条尺子的结论必须全部是"未知"，绝不亮假绿灯（summary 是聚合行，不参与该断言）。
        foreach (var kvp in details.Where(entry => entry.Key != "summary"))
        {
            Assert.StartsWith("⚪ UNKNOWN", kvp.Value);
        }

        Assert.Equal("0 Red, 0 Yellow, 0 Green, 13 Unknown", details["summary"]);
    }

    [Fact]
    public void InvariantResult_FourStatesProperties_AreMutuallyConsistent()
    {
        var pass = InvariantResult.Success("OK");
        Assert.True(pass.IsPass);
        Assert.False(pass.IsWarning);
        Assert.False(pass.IsFail);
        Assert.False(pass.IsUnknown);

        var warn = InvariantResult.Warning("Warn");
        Assert.False(warn.IsPass);
        Assert.True(warn.IsWarning);
        Assert.False(warn.IsFail);
        Assert.False(warn.IsUnknown);

        var fail = InvariantResult.Failure("Fail");
        Assert.False(fail.IsPass);
        Assert.False(fail.IsWarning);
        Assert.True(fail.IsFail);
        Assert.False(fail.IsUnknown);

        var unknown = InvariantResult.Unknown("Unknown");
        Assert.False(unknown.IsPass);
        Assert.False(unknown.IsWarning);
        Assert.False(unknown.IsFail);
        Assert.True(unknown.IsUnknown);
    }

    /// <summary>
    /// 验证取数层（无外置 PG 依赖）：断言 Inspector 生成的 SQL 全面使用 pc_tracker_events 与明确的 Asia/Shanghai 04:00 业务日表达式，
    /// 彻底剔除 pc_aw_events，并接入 pc_tracker_health。
    /// </summary>
    [Fact]
    public async Task Inspector_QueriesUseNativeTrackerAndExplicitBizDay_WithoutExternalDb()
    {
        var recordingConn = new RecordingDbConnection();
        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(recordingConn);

        await using var db = new PimDbContext(optionsBuilder.Options);
        var options = Options.Create(new InvariantOptions());
        var inspector = new DataReliabilityQualityInspector(db, options, NullLogger<DataReliabilityQualityInspector>.Instance);

        var result = await inspector.InspectAsync(DateTimeOffset.UtcNow);

        // 验证执行的所有 SQL 语句
        var executedSqlList = recordingConn.ExecutedCommands;
        Assert.NotEmpty(executedSqlList);

        // 1. 绝不包含任何 pc_aw_events
        foreach (var sql in executedSqlList)
        {
            Assert.DoesNotContain("pc_aw_events", sql);
        }

        // 2. 必须包含 pc_tracker_events 取数
        Assert.Contains(executedSqlList, sql => sql.Contains("FROM pc_tracker_events"));

        // 3. 必须包含 pc_tracker_health 取数
        Assert.Contains(executedSqlList, sql => sql.Contains("pc_tracker_health"));

        // 4. 业务日表达式必须显式写为 ((timestamp AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date
        Assert.Contains(executedSqlList, sql => sql.Contains("((timestamp AT TIME ZONE 'Asia/Shanghai') - interval '4 hours')::date"));

        // 5. 必须覆盖 S1 到 S13 的所有判据键
        Assert.NotNull(result.Details);
        for (int i = 1; i <= 13; i++)
        {
            var keyPrefix = $"S{i}_";
            Assert.Contains(result.Details!.Keys, k => k.StartsWith(keyPrefix));
        }
    }

    private static readonly DateTimeOffset ReportNow = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static DataReliabilityQualityInspector CreateRecordingInspector(RecordingDbConnection conn)
    {
        var optionsBuilder = new DbContextOptionsBuilder<PimDbContext>();
        optionsBuilder.UseNpgsql(conn);
        var db = new PimDbContext(optionsBuilder.Options);
        return new DataReliabilityQualityInspector(
            db,
            Options.Create(new InvariantOptions()),
            NullLogger<DataReliabilityQualityInspector>.Instance);
    }

    /// <summary>
    /// 结构化报告（#260）：13 条尺子齐全，且每条都带有前端要展示的判据/阈值/理由/关联 issue 与分档计数。
    /// 样例不得超过 T7 的 10 条上限。
    /// </summary>
    [Fact]
    public async Task InspectReportAsync_ReturnsThirteenStructuredRules()
    {
        var inspector = CreateRecordingInspector(new RecordingDbConnection());

        var report = await inspector.InspectReportAsync(ReportNow);

        Assert.Equal(13, report.Rules.Count);
        Assert.Equal(13, report.RedCount + report.YellowCount + report.GreenCount + report.UnknownCount);
        Assert.Equal(report.Rules.Select(rule => rule.Code).OrderBy(code => code.Length).ThenBy(code => code),
            report.Rules.Select(rule => rule.Code).OrderBy(code => code.Length).ThenBy(code => code));

        foreach (var rule in report.Rules)
        {
            Assert.False(string.IsNullOrWhiteSpace(rule.Threshold), $"{rule.Code} 缺阈值");
            Assert.False(string.IsNullOrWhiteSpace(rule.Criterion), $"{rule.Code} 缺判据原文");
            Assert.False(string.IsNullOrWhiteSpace(rule.Rationale), $"{rule.Code} 缺设定理由");
            Assert.True(rule.Samples.Count <= new InvariantOptions().MaxSampleCount, $"{rule.Code} 样例超过上限");
            Assert.InRange(rule.NewViolations + rule.HistoricalViolations, 0, rule.TotalViolations);
            Assert.Contains(rule.Status, new[] { "red", "yellow", "green", "unknown" });
            Assert.False(string.IsNullOrWhiteSpace(rule.StatusLabel));
        }

        // 能判定出状态的尺子必须给出当前值；未知的尺子不得把"违规数 0"伪装成测量值。
        Assert.All(report.Rules.Where(rule => rule.Status != "unknown"), rule => Assert.NotNull(rule.CurrentValue));
        Assert.All(
            report.Rules.Where(rule => rule.Status == "unknown" && rule.CurrentValueUnit == "条"),
            rule => Assert.Null(rule.CurrentValue));

        // 总览状态必须与逐条结论自洽（红 > 黄 > 未知 > 绿）。
        var expectedOverall = report.RedCount > 0
            ? "red"
            : report.YellowCount > 0
                ? "yellow"
                : report.UnknownCount > 0
                    ? "unknown"
                    : "green";
        Assert.Equal(expectedOverall, report.Status);
    }

    [Fact]
    public async Task InspectReportAsync_AllDatabaseAccessIsReadOnly()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);
        await inspector.GetViolationsAsync("S1", 50);

        Assert.NotEmpty(conn.ExecutedCommands);

        // 关键字必须按词边界匹配：created_at / updated_at 是列名，不能误判为写操作。
        var writeKeyword = new System.Text.RegularExpressions.Regex(
            @"\b(INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|TRUNCATE|COPY|GRANT|VACUUM|REINDEX)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var sql in conn.ExecutedCommands)
        {
            var match = writeKeyword.Match(sql);
            Assert.False(match.Success, $"体检链路出现了写操作 {match.Value}: {sql}");
            Assert.Contains("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>13 个取数 SQL 都必须在数据库侧被限行（#260 验收标准 2：单条尺子查询有上限保护）。</summary>
    [Fact]
    public async Task InspectReportAsync_BoundsEveryScan()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        // 只要求"会把多行拉进内存"的查询带上 LIMIT；count(*)/sum() 这类单值聚合天然只有一行结果。
        var scans = conn.ExecutedCommands
            .Where(sql => sql.Contains("FROM pc_tracker_events", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("FROM mobile_", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("FROM pc_tracker_health", StringComparison.OrdinalIgnoreCase))
            .Where(sql => !System.Text.RegularExpressions.Regex.IsMatch(
                sql,
                @"SELECT\s+(count|COALESCE\s*\(\s*SUM|SUM)\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        Assert.NotEmpty(scans);
        Assert.All(scans, sql => Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 回归防线：所有取数 SQL 都必须真正完成字符串插值。
    /// 漏写 `$` 的原始字符串会把 `{options.MaxScanRows + 1}` 原样发给 PostgreSQL（42601 语法错误），
    /// 而在 mock 连接下不会报错，只有实机体检才会暴露 —— 这里用断言把它挡在 CI 里。
    /// </summary>
    [Fact]
    public async Task InspectReportAsync_NeverSendsUninterpolatedPlaceholders()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);
        await inspector.GetViolationsAsync("S1", 5);

        foreach (var sql in conn.ExecutedCommands)
        {
            Assert.DoesNotContain("{options.", sql);
            Assert.DoesNotContain("{thresholdSeconds", sql);
            Assert.DoesNotContain("{max", sql);
        }
    }

    /// <summary>
    /// 回归防线：SQL 里出现的每一个 @参数都必须真的被绑定过。
    /// 漏绑会让 PostgreSQL 抛 42883/42P02，而在 mock 连接下静默通过 —— 只有实机体检才暴露。
    /// </summary>
    [Fact]
    public async Task InspectReportAsync_BindsEveryReferencedParameter()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);
        await inspector.GetViolationsAsync("S1", 5);

        Assert.NotEmpty(conn.ExecutedCommands);
        Assert.Equal(conn.ExecutedCommands.Count, conn.ExecutedParameterNames.Count);

        for (int i = 0; i < conn.ExecutedCommands.Count; i++)
        {
            var sql = conn.ExecutedCommands[i];
            var bound = conn.ExecutedParameterNames[i].Select(name => name.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(sql, @"@([A-Za-z_][A-Za-z0-9_]*)"))
            {
                Assert.True(bound.Contains(match.Groups[1].Value),
                    $"SQL 引用了未绑定的参数 {match.Value}: {sql}");
            }
        }
    }

    /// <summary>体检窗口必须来自调用方传入的时钟，不得依赖数据库 NOW()（否则结论随库时钟漂移）。</summary>
    [Fact]
    public async Task InspectReportAsync_DoesNotRelyOnTheDatabaseClock()
    {
        var conn = new RecordingDbConnection();
        var inspector = CreateRecordingInspector(conn);

        await inspector.InspectReportAsync(ReportNow);

        Assert.All(conn.ExecutedCommands, sql =>
            Assert.DoesNotContain("NOW()", sql, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetViolationsAsync_UnknownRule_Throws()
    {
        var inspector = CreateRecordingInspector(new RecordingDbConnection());

        await Assert.ThrowsAsync<ArgumentException>(() => inspector.GetViolationsAsync("S99", 10));
    }

    #region Mock ADO.NET Infrastructure for Offline Verification

    private sealed class RecordingDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public List<string> ExecutedCommands { get; } = new();

        /// <summary>每条语句执行时已绑定的参数名（用于验证 SQL 里的 @xxx 都真的被绑定了）。</summary>
        public List<IReadOnlyList<string>> ExecutedParameterNames { get; } = new();

        [AllowNull]
        public override string ConnectionString { get; set; } = "Host=mock;Database=mock";
        public override string Database => "mock";
        public override string DataSource => "mock";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            _state = ConnectionState.Open;
            return Task.CompletedTask;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new RecordingDbCommand(this);
    }

    private sealed class RecordingDbCommand : DbCommand
    {
        private readonly RecordingDbConnection _connection;

        public RecordingDbCommand(RecordingDbConnection connection)
        {
            _connection = connection;
        }

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        protected override DbConnection? DbConnection
        {
            get => _connection;
            set { }
        }
        protected override DbParameterCollection DbParameterCollection { get; } = new DummyParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }
        protected override DbParameter CreateDbParameter() => new DummyParameter();

        private void Record()
        {
            _connection.ExecutedCommands.Add(CommandText);
            _connection.ExecutedParameterNames.Add(
                Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName).ToList());
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Record();
            return new EmptyDbDataReader();
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult<DbDataReader>(new EmptyDbDataReader());
        }

        public override int ExecuteNonQuery()
        {
            Record();
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult(1);
        }

        public override object? ExecuteScalar()
        {
            Record();
            return 0L;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            Record();
            return Task.FromResult<object?>(0L);
        }

        public override void Prepare() { }
    }

    private sealed class DummyParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class DummyParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = new();
        public override int Count => _parameters.Count;
        public override object SyncRoot => this;
        public override int Add(object value) { _parameters.Add((DbParameter)value); return _parameters.Count - 1; }
        public override void AddRange(Array values)
        {
            foreach (var val in values)
            {
                if (val is DbParameter p) _parameters.Add(p);
            }
        }
        public override void Clear() => _parameters.Clear();
        public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
        public override bool Contains(string value) => _parameters.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)_parameters).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();
        protected override DbParameter GetParameter(int index) => _parameters[index];
        protected override DbParameter GetParameter(string parameterName) => _parameters.Find(p => p.ParameterName == parameterName) ?? new DummyParameter();
        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _parameters.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters.RemoveAt(idx); }
        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters[idx] = value; }
    }

    private sealed class EmptyDbDataReader : DbDataReader
    {
        public override int FieldCount => 0;
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override bool HasRows => false;

        public override object this[int ordinal] => DBNull.Value;
        public override object this[string name] => DBNull.Value;

        public override bool Read() => false;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public override bool NextResult() => false;
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => ' ';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override string GetDataTypeName(int ordinal) => string.Empty;
        public override DateTime GetDateTime(int ordinal) => DateTime.UtcNow;
        public override decimal GetDecimal(int ordinal) => 0m;
        public override double GetDouble(int ordinal) => 0.0;
        public override Type GetFieldType(int ordinal) => typeof(object);
        public override float GetFloat(int ordinal) => 0f;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override string GetName(int ordinal) => string.Empty;
        public override int GetOrdinal(string name) => -1;
        public override string GetString(int ordinal) => string.Empty;
        public override object GetValue(int ordinal) => DBNull.Value;
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => true;
        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }

    #endregion
}
