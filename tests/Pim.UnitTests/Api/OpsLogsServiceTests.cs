using Pim.Api.Services;
using Pim.Core.Exceptions;
using Xunit;

namespace Pim.UnitTests.Api;

public class OpsLogsServiceTests
{
    [Fact]
    public async Task Tail_RespectsLimit500_AndMaxBytes5MB()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var svc = new OpsLogsService(tmp);
            var ex = await Assert.ThrowsAsync<DomainException>(() => svc.QueryAsync(new OpsLogsQuery { File = "pim-api-20260821.jsonl", Limit = 501 }));
            Assert.Equal(40003, ex.ErrorCode);
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public async Task Tail_FileTraversal_Rejected()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var svc = new OpsLogsService(tmp);
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("../etc/passwd", 10, null, null));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task Tail_InvalidLimit_Rejected()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var svc = new OpsLogsService(tmp);
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("pim-api-20260821.jsonl", 0, null, null));
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("pim-api-20260821.jsonl", 501, null, null));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task Tail_InvalidFileName_Rejected()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var svc = new OpsLogsService(tmp);
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("bad/name.jsonl", 10, null, null));
        await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("bad*.jsonl", 10, null, null));
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task Tail_FileNotFound_Rejected()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var svc = new OpsLogsService(tmp);
        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.TailAsync("pim-api-20260821.jsonl", 10, null, null));
        Assert.Equal(40401, ex.ErrorCode);
        Directory.Delete(tmp, true);
    }

    [Fact]
    public async Task Tail_ReturnsLines_WithLevelAndKeywordFilter()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.Combine(tmp, "pim-api-20260821.jsonl");
            var lines = new[]
            {
                """{"@t":"2026-08-21T00:00:00Z","@l":"Information","@m":"hello world"}""",
                """{"@t":"2026-08-21T00:01:00Z","@l":"Error","@m":"something failed"}""",
                """{"@t":"2026-08-21T00:02:00Z","@l":"Information","@m":"keyword match here"}""",
            };
            await File.WriteAllLinesAsync(file, lines);
            var svc = new OpsLogsService(tmp);
            var r = await svc.TailAsync("pim-api-20260821.jsonl", 10, "Error", null);
            Assert.Single(r.Lines);
            Assert.Contains("something failed", r.Lines[0]);
            var r2 = await svc.TailAsync("pim-api-20260821.jsonl", 10, null, "keyword");
            Assert.Single(r2.Lines);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Tail_Truncates_WhenExceeds5MB()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.Combine(tmp, "pim-api-20260821.jsonl");
            // each line ~ 1KB, 6000 lines => ~6MB >5MB
            var bigLine = new string('a', 11 * 1024);
            var lines = Enumerable.Range(0, 6000).Select(i => $"{{\"@t\":\"2026-08-21T00:00:00Z\",\"@m\":\"{bigLine}{i}\"}}");
            await File.WriteAllLinesAsync(file, lines);
            var svc = new OpsLogsService(tmp);
            var r = await svc.TailAsync("pim-api-20260821.jsonl", 500, null, null);
            Assert.True(r.Truncated);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task ListFiles_ReturnsSorted()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var f1 = Path.Combine(tmp, "pim-api-20260820.jsonl");
            var f2 = Path.Combine(tmp, "pim-api-20260821.jsonl");
            await File.WriteAllTextAsync(f1, "{}");
            await Task.Delay(10);
            await File.WriteAllTextAsync(f2, "{}");
            var svc = new OpsLogsService(tmp);
            var files = await svc.ListFilesAsync(CancellationToken.None);
            Assert.Equal(2, files.Count);
            Assert.Equal("pim-api-20260821.jsonl", files[0].Name);
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Query_FiltersByFromTo_AndCursor()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pim-logs-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var file = Path.Combine(tmp, "pim-api-20260821.jsonl");
            var lines = new[]
            {
                """{"@t":"2026-08-21T00:00:00Z","@m":"first"}""",
                """{"@t":"2026-08-21T01:00:00Z","@m":"second"}""",
                """{"@t":"2026-08-21T02:00:00Z","@m":"third"}""",
            };
            await File.WriteAllLinesAsync(file, lines);
            var svc = new OpsLogsService(tmp);
            var r = await svc.QueryAsync(new OpsLogsQuery { File = "pim-api-20260821.jsonl", Limit = 10, From = "2026-08-21T00:30:00Z", To = "2026-08-21T01:30:00Z" });
            Assert.Single(r.Lines);
            Assert.Contains("second", r.Lines[0]);
        }
        finally { Directory.Delete(tmp, true); }
    }
}
