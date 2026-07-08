using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsCompanionShellTests
{
    [Fact]
    public void ProjectAndShellDeclareEmbeddedWebWorkbench()
    {
        var projectFile = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "Pim.Client.App.csproj"));
        var appStartup = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "App.xaml.cs"));
        var mainShellXaml = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "MainShellWindow.xaml"));
        var mainShellCode = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "MainShellWindow.xaml.cs"));
        var hostCode = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "EmbeddedWebViewHost.cs"));
        var trayCode = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "TrayIcon.cs"));

        Assert.Contains("WebView2", projectFile);
        Assert.Contains("MainShellWindow", appStartup);
        Assert.Contains("EmbeddedWebViewHost", mainShellCode);
        Assert.Contains("CoreWebView2", hostCode);

        foreach (var route in new[]
        {
            "/today",
            "/tasks",
            "/calendar",
            "/reports",
            "/sync",
            "/data-center",
            "/confirmations"
        })
        {
            Assert.Contains(route, mainShellCode + mainShellXaml + trayCode);
        }

        Assert.Contains("通知中心", mainShellXaml);
        Assert.Contains("审计中心", trayCode);
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
