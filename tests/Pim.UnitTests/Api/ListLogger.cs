using Microsoft.Extensions.Logging;

namespace Pim.UnitTests.Api;

/// <summary>
/// 极简的日志捕获器：断言"该记什么级别"时用，避免为了断言一条日志而去解析 Serilog 输出。
/// 只服务于单元测试，不参与生产代码。
/// </summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private readonly List<LogEntry> _entries = [];
    private readonly object _gate = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
                return _entries.ToArray();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_gate)
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
