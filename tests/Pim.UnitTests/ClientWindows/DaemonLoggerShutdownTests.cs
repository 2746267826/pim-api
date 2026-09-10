using System.IO;
using Pim.Client.App.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// Logger.Shutdown 安全性测试。
/// （Logger 经 Compile Link 编入测试程序集，与 Pim.Client.App 中的实例互不影响。）
/// </summary>
public class DaemonLoggerShutdownTests
{
    [Fact]
    public void Shutdown_WithoutInitialize_IsSafeNoOp()
    {
        var ex = Record.Exception(() => Logger.Shutdown());

        Assert.Null(ex);
    }

    [Fact]
    public void Shutdown_IsIdempotent_CanCallRepeatedly()
    {
        var ex = Record.Exception(() =>
        {
            Logger.Shutdown();
            Logger.Shutdown();
            Logger.Shutdown();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Write_AfterShutdown_FallsBackWithoutThrowing()
    {
        Logger.Shutdown();

        var ex = Record.Exception(() =>
        {
            Logger.Info("info after shutdown");
            Logger.Warn("warn after shutdown");
            Logger.Error("error after shutdown", new InvalidOperationException("boom"));
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Initialize_And_Shutdown_FlushesBufferedLogsToDisk()
    {
        var originalDir = Logger.LogDir;
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logger-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Logger.LogDir = tempDir;
            Logger.Initialize();
            Logger.Info("Test message before shutdown");
            Logger.Shutdown();

            var logFiles = Directory.GetFiles(tempDir, "pim-daemon-*.jsonl");
            Assert.NotEmpty(logFiles);
            var content = File.ReadAllText(logFiles[0]);
            Assert.Contains("Test message before shutdown", content);
        }
        finally
        {
            Logger.Shutdown();
            Logger.LogDir = originalDir;
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
