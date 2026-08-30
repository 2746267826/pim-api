using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class ScheduledTaskServiceTests
{
    private sealed class FakeRunner : ISchtasksRunner
    {
        public List<string[]> Calls { get; } = new();
        public Queue<(int code, string output)> Responses { get; } = new();
        public (int code, string output) DefaultResponse { get; set; } = (0, "");

        public (int ExitCode, string Output) Exec(params string[] args)
        {
            Calls.Add(args);
            if (Responses.Count > 0) return Responses.Dequeue();
            return DefaultResponse;
        }
    }

    [Fact]
    public void TaskExists_ReturnsTrue_WhenExitZero()
    {
        var fake = new FakeRunner { DefaultResponse = (0, "TaskName: \\PIM\\PIM Daemon") };
        var svc = new ScheduledTaskService(fake);
        Assert.True(svc.TaskExists(ScheduledTaskService.DaemonTaskPath));
        Assert.Single(fake.Calls);
        Assert.Contains("/query", fake.Calls[0]);
    }

    [Fact]
    public void TaskExists_ReturnsFalse_WhenExitNonZero()
    {
        var fake = new FakeRunner { DefaultResponse = (1, "ERROR: The system cannot find the file specified.") };
        var svc = new ScheduledTaskService(fake);
        Assert.False(svc.TaskExists(ScheduledTaskService.DaemonTaskPath));
    }

    [Fact]
    public void TryCreateDaemonTask_RetriesWithoutDelay_WhenFirstFails()
    {
        var fake = new FakeRunner();
        fake.Responses.Enqueue((1, "delay not supported"));
        fake.Responses.Enqueue((0, "SUCCESS"));
        var svc = new ScheduledTaskService(fake);
        var ok = svc.TryCreateDaemonTask(@"C:\Program Files\PIM\Pim.Client.App.exe");
        Assert.True(ok);
        Assert.Equal(2, fake.Calls.Count);
        Assert.Contains("/delay", fake.Calls[0]);
        Assert.DoesNotContain("/delay", fake.Calls[1]);
        Assert.Contains("limited", fake.Calls[0]);
    }

    [Fact]
    public void TryCreateKeyStatsTask_UsesHighest()
    {
        var fake = new FakeRunner { DefaultResponse = (0, "") };
        var svc = new ScheduledTaskService(fake);
        var ok = svc.TryCreateKeyStatsTask(@"C:\Program Files\PIM\KeyStats.exe");
        Assert.True(ok);
        Assert.Contains("highest", fake.Calls[0]);
        Assert.Contains(ScheduledTaskService.KeyStatsTaskPath, fake.Calls[0]);
    }

    [Fact]
    public void TryRunTask_Succeeds_WhenExitZero()
    {
        var fake = new FakeRunner { DefaultResponse = (0, "SUCCESS") };
        var svc = new ScheduledTaskService(fake);
        Assert.True(svc.TryRunTask(ScheduledTaskService.KeyStatsTaskPath));
        Assert.Contains("/run", fake.Calls[0]);
    }

    [Fact]
    public void TryRunTask_Fails_WhenNonZero()
    {
        var fake = new FakeRunner { DefaultResponse = (1, "ERROR") };
        var svc = new ScheduledTaskService(fake);
        Assert.False(svc.TryRunTask(ScheduledTaskService.KeyStatsTaskPath));
    }

    [Fact]
    public void TryDeleteTask_ReturnsTrueOnZero()
    {
        var fake = new FakeRunner { DefaultResponse = (0, "") };
        var svc = new ScheduledTaskService(fake);
        Assert.True(svc.TryDeleteTask(ScheduledTaskService.DaemonTaskPath));
        Assert.Contains("/delete", fake.Calls[0]);
        Assert.Contains("/f", fake.Calls[0]);
    }

    [Fact]
    public void CleanupLegacyTasks_AttemptsBothNames()
    {
        var fake = new FakeRunner { DefaultResponse = (1, "not found") };
        var svc = new ScheduledTaskService(fake);
        svc.CleanupLegacyTasks();
        Assert.Equal(2, fake.Calls.Count);
        var allArgs = string.Join(" ", fake.Calls.SelectMany(a => a));
        Assert.Contains("PimKeyStats", allArgs);
    }

    [Fact]
    public void TryCreateDaemonTask_QuotesExePath()
    {
        var fake = new FakeRunner { DefaultResponse = (0, "") };
        var svc = new ScheduledTaskService(fake);
        var path = @"C:\Program Files\PIM\Pim.Client.App.exe";
        svc.TryCreateDaemonTask(path);
        var trIndex = Array.IndexOf(fake.Calls[0], "/tr");
        Assert.True(trIndex >= 0);
        var trValue = fake.Calls[0][trIndex + 1];
        Assert.Contains(path, trValue);
        Assert.StartsWith("\"", trValue);
    }
}
