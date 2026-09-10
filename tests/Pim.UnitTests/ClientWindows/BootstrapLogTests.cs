using Pim.Client.App.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// BootstrapLog 单元测试：通过 internal 字段把日志路径重定向到临时目录，避免污染真实用户目录。
/// 同测试类内 xUnit 串行执行，静态路径在 finally 中还原。
/// </summary>
public class BootstrapLogTests : IDisposable
{
    private readonly string _originalLogDir = BootstrapLog.LogDir;
    private readonly string _originalFilePath = BootstrapLog.FilePath;
    private readonly string _originalOldFilePath = BootstrapLog.OldFilePath;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "pim-bootstraplog-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        BootstrapLog.LogDir = _originalLogDir;
        BootstrapLog.FilePath = _originalFilePath;
        BootstrapLog.OldFilePath = _originalOldFilePath;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void RedirectToTempDir()
    {
        BootstrapLog.LogDir = _tempDir;
        BootstrapLog.FilePath = Path.Combine(_tempDir, "bootstrap.log");
        BootstrapLog.OldFilePath = Path.Combine(_tempDir, "bootstrap.log.old");
    }

    [Fact]
    public void Write_AppendsLine_WithTimestampPidAndMessage()
    {
        RedirectToTempDir();

        BootstrapLog.Write("hello bootstrap");

        Assert.True(File.Exists(BootstrapLog.FilePath));
        var content = File.ReadAllText(BootstrapLog.FilePath);
        Assert.Contains("hello bootstrap", content);
        Assert.Contains($"[{Environment.ProcessId}]", content);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]", content);
    }

    [Fact]
    public void Write_CreatesMissingDirectory()
    {
        RedirectToTempDir();
        Assert.False(Directory.Exists(_tempDir));

        BootstrapLog.Write("creates dir");

        Assert.True(File.Exists(BootstrapLog.FilePath));
    }

    [Fact]
    public void Write_RotatesToOldFile_WhenOverMaxSize()
    {
        RedirectToTempDir();
        Directory.CreateDirectory(_tempDir);
        // 预置超过 512KB 的现有日志，触发轮转
        File.WriteAllText(BootstrapLog.FilePath, new string('x', 600 * 1024));

        BootstrapLog.Write("after-rotate");

        Assert.True(File.Exists(BootstrapLog.OldFilePath));
        Assert.Equal(600 * 1024, new FileInfo(BootstrapLog.OldFilePath).Length);
        var fresh = File.ReadAllText(BootstrapLog.FilePath);
        Assert.Contains("after-rotate", fresh);
        Assert.DoesNotContain(new string('x', 1000), fresh);
    }

    [Fact]
    public void Write_KeepsOldContent_WhenUnderMaxSize()
    {
        RedirectToTempDir();
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(BootstrapLog.FilePath, "previous line\n");

        BootstrapLog.Write("next line");

        var content = File.ReadAllText(BootstrapLog.FilePath);
        Assert.Contains("previous line", content);
        Assert.Contains("next line", content);
        Assert.False(File.Exists(BootstrapLog.OldFilePath));
    }

    [Fact]
    public void Write_SwallowsExceptions_WhenDirectoryNotCreatable()
    {
        // 用一个已存在的文件作为路径中间级，CreateDirectory 必然失败
        Directory.CreateDirectory(_tempDir);
        var blockerFile = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blockerFile, "not a directory");
        BootstrapLog.LogDir = Path.Combine(blockerFile, "sub");
        BootstrapLog.FilePath = Path.Combine(BootstrapLog.LogDir, "bootstrap.log");
        BootstrapLog.OldFilePath = Path.Combine(BootstrapLog.LogDir, "bootstrap.log.old");

        var ex = Record.Exception(() => BootstrapLog.Write("must not throw"));

        Assert.Null(ex);
    }
}
