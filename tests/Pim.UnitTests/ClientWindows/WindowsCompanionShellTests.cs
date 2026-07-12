using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsCompanionShellTests
{
    [Fact]
    public void CompanionShellCodeRemainsAvailableButIsNotPrimaryPath()
    {
        var projectFile = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "Pim.Client.App.csproj"));
        var appStartup = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "App.xaml.cs"));
        var trayCode = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "TrayIcon.cs"));
        var hostCode = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "EmbeddedWebViewHost.cs"));

        Assert.Contains("WebView2", projectFile);
        Assert.Contains("EmbeddedWebViewHost", hostCode);
        Assert.Contains("MainShellWindow", File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "MainShellWindow.xaml.cs")));

        // startup must not auto-open shell (ignore spaces)
        Assert.DoesNotContain("ShowMainShellWindow();", appStartup.Replace(" ", string.Empty));
        Assert.DoesNotContain("OpenShell(\"/today\")", trayCode);
        Assert.Contains("ShowStatusWindow", trayCode);
        Assert.Contains("在浏览器打开 Web 工作台", trayCode);
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
