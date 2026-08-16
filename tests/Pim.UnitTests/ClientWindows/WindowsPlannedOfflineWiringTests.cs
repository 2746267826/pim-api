using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsPlannedOfflineWiringTests
{
    [Fact]
    public void AppWiresPlannedOfflineListeners()
    {
        var source = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "App.xaml.cs"));
        Assert.Contains("SystemEvents.SessionEnding", source);
        Assert.Contains("PowerModeChanged", source);
        Assert.Contains("TryReportPlannedOffline", source);
        Assert.Contains("Interlocked", source);
        Assert.Contains("_plannedOfflineTask", source);
        Assert.Contains("PowerModes.Resume", source);
        Assert.Contains("wait: true", source);
        Assert.Contains("TimeSpan.FromSeconds(2)", source);
        Assert.Contains("PlannedOfflineReporter", source);
        Assert.Contains("\"shutdown\"", source);
        Assert.Contains("\"suspend\"", source);
        Assert.Contains("\"exit\"", source);
    }

    [Fact]
    public void StartupRegistersPlannedOfflineReporter()
    {
        var startup = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "Startup.cs"));
        Assert.Contains("PlannedOfflineReporter", startup);
    }

    [Fact]
    public void CoreReporterTargetsPlannedOfflineEndpoint()
    {
        var reporter = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.Core", "Services", "PlannedOfflineReporter.cs"));
        Assert.Contains("daemon/planned-offline", reporter);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}