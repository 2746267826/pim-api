using System.IO;
using System.Linq;
using Pim.Api.Infrastructure;
using Serilog;
using Serilog.Formatting.Compact;
using Xunit;

namespace Pim.UnitTests.Api;

public class LoggingConfigTests
{
    [Theory]
    [InlineData(null, 30)]                 // 未设置 -> 默认 30
    [InlineData("", 30)]                   // 空字符串 -> 默认 30
    [InlineData("   ", 30)]                // 空白 -> 默认 30
    [InlineData("2", 2)]                   // 正常值
    [InlineData("30", 30)]
    [InlineData("0", 30)]                  // 下限保护：0 -> 默认
    [InlineData("-1", 30)]                 // 负数 -> 默认
    [InlineData("abc", 30)]                // 非数字 -> 默认
    [InlineData("1.5", 30)]                // 非整数 -> 默认
    public void ResolveRetainedFileCount_ReturnsExpected(string? raw, int expected)
    {
        Assert.Equal(expected, LoggingConfig.ResolveRetainedFileCount(raw));
    }

    [Theory]
    [InlineData(null, 1073741824L)]
    [InlineData("", 1073741824L)]
    [InlineData("   ", 1073741824L)]
    [InlineData("1073741824", 1073741824L)]
    [InlineData("2147483648", 2147483648L)]
    [InlineData("524288000", 524288000L)]
    [InlineData("0", null)]
    [InlineData("null", null)]
    [InlineData("NULL", null)]
    [InlineData("none", null)]
    [InlineData("NONE", null)]
    [InlineData("unlimited", null)]
    [InlineData("UNLIMITED", null)]
    [InlineData("-1", 1073741824L)]
    [InlineData("abc", 1073741824L)]
    public void ResolveFileSizeLimitBytes_ReturnsExpected(string? raw, long? expected)
    {
        Assert.Equal(expected, LoggingConfig.ResolveFileSizeLimitBytes(raw));
    }

    [Theory]
    [InlineData(null, 1073741824L, true)]
    [InlineData("", 1073741824L, true)]
    [InlineData("   ", 1073741824L, true)]
    [InlineData("true", 1073741824L, true)]
    [InlineData("TRUE", 1073741824L, true)]
    [InlineData("1", 1073741824L, true)]
    [InlineData("yes", 1073741824L, true)]
    [InlineData("false", 1073741824L, false)]
    [InlineData("FALSE", 1073741824L, false)]
    [InlineData("0", 1073741824L, false)]
    [InlineData("off", 1073741824L, false)]
    [InlineData("no", 1073741824L, false)]
    [InlineData(null, null, false)]         // Unlimited file size -> rollOnFileSizeLimit must be false
    [InlineData("true", null, false)]       // Cannot roll on size if unlimited
    public void ResolveRollOnFileSizeLimit_ReturnsExpected(string? raw, long? sizeLimit, bool expected)
    {
        Assert.Equal(expected, LoggingConfig.ResolveRollOnFileSizeLimit(raw, sizeLimit));
    }

    [Fact]
    public void FileSink_WithoutRollOnFileSizeLimit_SilentlyDropsEventsAfterLimit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-log-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logPath = Path.Combine(tempDir, "pim-api-.jsonl");
            const long smallLimit = 500;
            using (var logger = new LoggerConfiguration()
                .WriteTo.File(new CompactJsonFormatter(), logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: smallLimit) // rollOnFileSizeLimit omitted -> defaults to false
                .CreateLogger())
            {
                for (int i = 0; i < 50; i++)
                {
                    logger.Information("Message number {Index} with some padding content to exceed size limit", i);
                }
            }

            var writtenFiles = Directory.GetFiles(tempDir, "*.jsonl");
            Assert.Single(writtenFiles);
            var initialFileSize = new FileInfo(writtenFiles[0]).Length;
            Assert.True(initialFileSize >= smallLimit, $"Initial file size {initialFileSize} should reach limit {smallLimit}");

            // Count lines in initial file
            var initialLines = File.ReadAllLines(writtenFiles[0]).Length;
            Assert.True(initialLines < 50, $"Expected fewer than 50 lines due to limit, got {initialLines}");

            // Reopen / restart API - simulating container restart
            using (var logger2 = new LoggerConfiguration()
                .WriteTo.File(new CompactJsonFormatter(), logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: smallLimit)
                .CreateLogger())
            {
                logger2.Information("Message after restart that should be logged");
            }

            var sizeAfterRestart = new FileInfo(writtenFiles[0]).Length;
            var linesAfterRestart = File.ReadAllLines(writtenFiles[0]).Length;

            // Silently dropped!
            Assert.Equal(initialFileSize, sizeAfterRestart);
            Assert.Equal(initialLines, linesAfterRestart);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FileSink_WithRollOnFileSizeLimit_RollsToNewFileAndDoesNotDropEvents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-log-roll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logPath = Path.Combine(tempDir, "pim-api-.jsonl");
            const long smallLimit = 500;
            using (var logger = new LoggerConfiguration()
                .WriteTo.File(new CompactJsonFormatter(), logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: smallLimit,
                    rollOnFileSizeLimit: true)
                .CreateLogger())
            {
                for (int i = 0; i < 50; i++)
                {
                    logger.Information("Message number {Index} with some padding content to exceed size limit", i);
                }
            }

            var writtenFiles = Directory.GetFiles(tempDir, "*.jsonl");
            Assert.True(writtenFiles.Length > 1, $"Expected multiple files after rolling, but found {writtenFiles.Length}");

            var totalLines = writtenFiles.Sum(f => File.ReadAllLines(f).Length);
            Assert.Equal(50, totalLines);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FileSink_WithDefaultsAndRestart_ContinuesWritingAcrossRestarts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-log-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logPath = Path.Combine(tempDir, "pim-api-.jsonl");
            const long smallLimit = 500;

            // Step 1: fill the file past the limit with rollOnFileSizeLimit enabled
            using (var logger1 = new LoggerConfiguration()
                .WriteTo.File(new CompactJsonFormatter(), logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: smallLimit,
                    rollOnFileSizeLimit: LoggingConfig.DefaultRollOnFileSizeLimit)
                .CreateLogger())
            {
                for (int i = 0; i < 50; i++)
                {
                    logger1.Information("Session 1 Message {Index} with padding content", i);
                }
            }

            var filesAfterSession1 = Directory.GetFiles(tempDir, "*.jsonl");
            var linesAfterSession1 = filesAfterSession1.Sum(f => File.ReadAllLines(f).Length);
            Assert.Equal(50, linesAfterSession1);

            // Step 2: restart the service (new logger instance pointing to the same folder)
            using (var logger2 = new LoggerConfiguration()
                .WriteTo.File(new CompactJsonFormatter(), logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: smallLimit,
                    rollOnFileSizeLimit: LoggingConfig.DefaultRollOnFileSizeLimit)
                .CreateLogger())
            {
                for (int i = 0; i < 20; i++)
                {
                    logger2.Information("Session 2 Message {Index} after container restart", i);
                }
            }

            var filesAfterSession2 = Directory.GetFiles(tempDir, "*.jsonl");
            var linesAfterSession2 = filesAfterSession2.Sum(f => File.ReadAllLines(f).Length);
            Assert.Equal(70, linesAfterSession2);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FileSink_WithRollOnFileSizeLimit_AndRetainedFileCountLimit_PurgesOldestSegments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-log-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var logPath = Path.Combine(tempDir, "pim-api-.jsonl");
            const long smallLimit = 500;
            const int retainedCount = 2;

            using (var logger = new LoggerConfiguration()
                .WriteTo.File(new CompactJsonFormatter(), logPath,
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: smallLimit,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: retainedCount)
                .CreateLogger())
            {
                // Write enough logs to trigger multiple rollings (more than retainedCount files)
                for (int i = 0; i < 60; i++)
                {
                    logger.Information("Message number {Index} with padding content to trigger file size rolling", i);
                }
            }

            var writtenFiles = Directory.GetFiles(tempDir, "*.jsonl");
            Assert.True(writtenFiles.Length <= retainedCount,
                $"Expected at most {retainedCount} files retained, but found {writtenFiles.Length}");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
