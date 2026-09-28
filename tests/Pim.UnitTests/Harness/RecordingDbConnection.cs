using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pim.UnitTests.Harness;

/// <summary>
/// 离线可用的 ADO.NET 记录桩：不连数据库，只记下每条 SQL、绑定的参数名与参数值。
/// 用它可以断言"取数下限绑的是考核线还是覆盖率窗口""SQL 里没有写操作 / 没有漏插值的占位符"这类
/// 只有真库才会暴露的问题（见 <c>DataReliabilityQualityInspectorTests</c> 与考核窗用例）。
/// </summary>

internal sealed class RecordingDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    public List<string> ExecutedCommands { get; } = new();

    /// <summary>每条语句执行时已绑定的参数名（用于验证 SQL 里的 @xxx 都真的被绑定了）。</summary>
    public List<IReadOnlyList<string>> ExecutedParameterNames { get; } = new();

    /// <summary>
    /// 每条语句执行时已绑定的**参数值**（参数名 → 值），与 <see cref="ExecutedCommands"/> 一一对应。
    /// 考核窗与 S9 覆盖率窗口是两套独立配置，"取数下限到底绑了哪个时间"只能靠值钉住（AC-5.5 / AC-5.6）。
    /// </summary>
    public List<IReadOnlyDictionary<string, object?>> ExecutedParameterValues { get; } = new();

    /// <summary>
    /// 当前还没被释放的 DataReader 数量。真实 Npgsql 在同一连接上"上一条命令还没读完"时
    /// 会抛 <c>A command is already in progress</c>；这个桩复刻该行为，
    /// 让"忘了先关 reader 就发下一条命令"这类只在真库上才炸的缺陷在单测里就能被抓住。
    /// </summary>
    private int _openReaders;

    internal int OpenReaderCount => _openReaders;

    internal void OnReaderOpened() => _openReaders++;

    internal void OnReaderClosed() => _openReaders--;

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

internal sealed class RecordingDbCommand : DbCommand
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
    protected override DbParameterCollection DbParameterCollection { get; } = new RecordingDbParameterCollection();
    protected override DbTransaction? DbTransaction { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    public override void Cancel() { }
    protected override DbParameter CreateDbParameter() => new RecordingDbParameter();

    private void Record()
    {
        if (_connection.OpenReaderCount > 0)
        {
            throw new InvalidOperationException($"A command is already in progress: {CommandText}");
        }

        _connection.ExecutedCommands.Add(CommandText);
        var bound = Parameters.Cast<DbParameter>().ToList();
        _connection.ExecutedParameterNames.Add(bound.Select(parameter => parameter.ParameterName).ToList());
        _connection.ExecutedParameterValues.Add(bound.ToDictionary(
            parameter => parameter.ParameterName,
            parameter => parameter.Value,
            StringComparer.OrdinalIgnoreCase));
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        Record();
        return new EmptyDbDataReader(_connection);
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        Record();
        return Task.FromResult<DbDataReader>(new EmptyDbDataReader(_connection));
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

internal sealed class RecordingDbParameter : DbParameter
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

internal sealed class RecordingDbParameterCollection : DbParameterCollection
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
    protected override DbParameter GetParameter(string parameterName) => _parameters.Find(p => p.ParameterName == parameterName) ?? new RecordingDbParameter();
    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => _parameters.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
    public override void Remove(object value) => _parameters.Remove((DbParameter)value);
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);
    public override void RemoveAt(string parameterName) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters.RemoveAt(idx); }
    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) { int idx = IndexOf(parameterName); if (idx >= 0) _parameters[idx] = value; }
}

internal sealed class EmptyDbDataReader : DbDataReader
{
    public EmptyDbDataReader(RecordingDbConnection? owner = null)
    {
        _owner = owner;
        _owner?.OnReaderOpened();
    }

    private readonly RecordingDbConnection? _owner;
    private bool _disposed;

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _owner?.OnReaderClosed();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        return ValueTask.CompletedTask;
    }

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
