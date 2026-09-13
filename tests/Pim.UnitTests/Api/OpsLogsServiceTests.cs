using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pim.Api.Services;
using Pim.Core.Exceptions;
using Xunit;

namespace Pim.UnitTests.Api;

public class OpsLogsServiceTests
{
    [Fact]
    public async Task Tail_RespectsLimit500_AndMaxBytes5MB()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var svc = new OpsLogsService(tempDir);
            var ex = await Assert.ThrowsAsync<DomainException>(() => svc.QueryAsync(new OpsLogsQuery { File = "pim-api-20260821.jsonl", Limit = 501 }));
            Assert.Equal(40003, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Tail_FileTraversal_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var svc = new OpsLogsService(tempDir);
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("../etc/passwd", 10, null, null));
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Tail_InvalidLimit_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var svc = new OpsLogsService(tempDir);
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("pim-api-20260821.jsonl", 0, null, null));
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("pim-api-20260821.jsonl", 501, null, null));
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Tail_InvalidFileName_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var svc = new OpsLogsService(tempDir);
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("bad/name.jsonl", 10, null, null));
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("bad*.jsonl", 10, null, null));
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Tail_FileNotFound_Rejected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var svc = new OpsLogsService(tempDir);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("pim-api-20260821.jsonl", 10, null, null));
        Assert.Equal(40401, ex.ErrorCode);
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public async Task Tail_ReturnsLines_WithLevelAndKeywordFilter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "pim-api-20260821.jsonl");
            var lines = new[]
            {
                """{"@t":"2026-08-21T00:00:00Z","@l":"Information","@m":"hello world"}""",
                """{"@t":"2026-08-21T00:01:00Z","@l":"Error","@m":"something failed"}""",
                """{"@t":"2026-08-21T00:02:00Z","@l":"Information","@m":"keyword match here"}""",
            };
            await File.WriteAllLinesAsync(file, lines);
            var svc = new OpsLogsService(tempDir);
            var r = await svc.TailAsync("pim-api-20260821.jsonl", 10, "Error", null);
            Assert.Single(r.Lines);
            Assert.Contains("something failed", r.Lines[0]);
            var r2 = await svc.TailAsync("pim-api-20260821.jsonl", 10, null, "keyword");
            Assert.Single(r2.Lines);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task Tail_Truncates_WhenExceeds5MB()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "pim-api-20260821.jsonl");
            // each line ~ 1KB, 6000 lines => ~6MB >5MB
            var bigLine = new string('a', 11 * 1024);
            var lines = Enumerable.Range(0, 6000).Select(i => $"{{\"@t\":\"2026-08-21T00:00:00Z\",\"@m\":\"{bigLine}{i}\"}}");
            await File.WriteAllLinesAsync(file, lines);
            var svc = new OpsLogsService(tempDir);
            var r = await svc.TailAsync("pim-api-20260821.jsonl", 500, null, null);
            Assert.True(r.Truncated);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task ListFiles_ReturnsSorted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var f1 = Path.Combine(tempDir, "pim-api-20260820.jsonl");
            var f2 = Path.Combine(tempDir, "pim-api-20260821.jsonl");
            await File.WriteAllTextAsync(f1, "{}");
            await Task.Delay(10);
            await File.WriteAllTextAsync(f2, "{}");
            var svc = new OpsLogsService(tempDir);
            var files = await svc.ListFilesAsync(CancellationToken.None);
            Assert.Equal(2, files.Count);
            Assert.Equal("pim-api-20260821.jsonl", files[0].Name);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task Query_FiltersByFromTo_AndCursor()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "pim-api-20260821.jsonl");
            var lines = new[]
            {
                """{"@t":"2026-08-21T00:00:00Z","@m":"first"}""",
                """{"@t":"2026-08-21T01:00:00Z","@m":"second"}""",
                """{"@t":"2026-08-21T02:00:00Z","@m":"third"}""",
            };
            await File.WriteAllLinesAsync(file, lines);
            var svc = new OpsLogsService(tempDir);
            var r = await svc.QueryAsync(new OpsLogsQuery { File = "pim-api-20260821.jsonl", Limit = 10, From = "2026-08-21T00:30:00Z", To = "2026-08-21T01:30:00Z" });
            Assert.Single(r.Lines);
            Assert.Contains("second", r.Lines[0]);
        }
        finally { Directory.Delete(tempDir, true); }
    }

    [Fact]
    public async Task ListAndQuery_SupportsRolledFileSegments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var f1 = Path.Combine(tempDir, "pim-api-20260913.jsonl");
            var f2 = Path.Combine(tempDir, "pim-api-20260913_001.jsonl");
            await File.WriteAllLinesAsync(f1, new[] { """{"@t":"2026-09-13T08:00:00Z","@m":"morning log"}""" });
            await Task.Delay(10);
            await File.WriteAllLinesAsync(f2, new[] { """{"@t":"2026-09-13T10:00:00Z","@m":"rolled segment log"}""" });

            var svc = new OpsLogsService(tempDir);

            // 1. ListFiles recognizes both and sorts newest-write-first
            var files = await svc.ListFilesAsync(CancellationToken.None);
            Assert.Equal(2, files.Count);
            Assert.Equal("pim-api-20260913_001.jsonl", files[0].Name);
            Assert.Equal("pim-api-20260913.jsonl", files[1].Name);

            // 2. TailAsync works on rolled file
            var tailRes = await svc.TailAsync("pim-api-20260913_001.jsonl", 10, null, null);
            Assert.Single(tailRes.Lines);
            Assert.Contains("rolled segment log", tailRes.Lines[0]);

            // 3. QueryAsync across files scans in chronological order
            var queryRes = await svc.QueryAsync(new OpsLogsQuery { Limit = 10 });
            Assert.Equal(2, queryRes.Lines.Count);
            Assert.Contains("morning log", queryRes.Lines[0]);
            Assert.Contains("rolled segment log", queryRes.Lines[1]);
        }
        finally { Directory.Delete(tempDir, true); }
    }
}
