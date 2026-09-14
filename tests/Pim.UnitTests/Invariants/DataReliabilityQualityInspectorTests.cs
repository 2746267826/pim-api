using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
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
        Assert.Contains("S1_INV-P16", result.Details.Keys);
        Assert.Contains("S13_INV-P22", result.Details.Keys);
        foreach (var kvp in result.Details)
        {
            Assert.StartsWith("⚪ UNKNOWN", kvp.Value);
        }
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
        for (int i = 1; i <= 13; i++)
        {
            var keyPrefix = $"S{i}_";
            Assert.Contains(result.Details.Keys, k => k.StartsWith(keyPrefix));
        }
    }

    #region Mock ADO.NET Infrastructure for Offline Verification

    private sealed class RecordingDbConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Open;

        public List<string> ExecutedCommands { get; } = new();

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

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            _connection.ExecutedCommands.Add(CommandText);
            return new EmptyDbDataReader();
        }

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            _connection.ExecutedCommands.Add(CommandText);
            return Task.FromResult<DbDataReader>(new EmptyDbDataReader());
        }

        public override int ExecuteNonQuery()
        {
            _connection.ExecutedCommands.Add(CommandText);
            return 1;
        }

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            _connection.ExecutedCommands.Add(CommandText);
            return Task.FromResult(1);
        }

        public override object? ExecuteScalar()
        {
            _connection.ExecutedCommands.Add(CommandText);
            return 0L;
        }

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            _connection.ExecutedCommands.Add(CommandText);
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
