using System.Text.Json;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiConfigurationTests
{
    [Fact]
    public void Appsettings_DoNotRetainLiteLlmRuntimeDefaults()
    {
        foreach (var fileName in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            using var document = ReadApiAppsettings(fileName);
            var root = document.RootElement;
            Assert.False(root.TryGetProperty("Ai", out _), $"{fileName} must not configure a LiteLLM gateway.");
            Assert.DoesNotContain("litellm", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DevelopmentCompose_DoesNotStartOrDependOnLiteLlm()
    {
        var compose = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "docker-compose.yml"))
            .ReplaceLineEndings("\n");
        var pimApi = ExtractService(compose, "pim-api");

        Assert.DoesNotContain("litellm", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ai__", pimApi, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_ENABLED", pimApi, StringComparison.Ordinal);
        Assert.Contains("postgres:\n        condition: service_healthy", pimApi);
    }

    [Fact]
    public void ProductionCompose_DoesNotConfigureLiteLlmEnvironment()
    {
        var compose = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "docker-compose.prod.yml"));

        Assert.DoesNotContain("Ai__", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_ENABLED", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_API_KEY", compose, StringComparison.Ordinal);
        Assert.DoesNotContain("litellm", compose, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnvExamples_LeaveProviderCredentialsToTheAdminModelSettings()
    {
        foreach (var fileName in new[] { ".env.example", ".env.prod.example" })
        {
            var text = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", fileName));
            Assert.DoesNotContain("LITELLM", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AI_API_KEY", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("AI_BASE_URL", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PIM_AI_DEFAULT_MODEL", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void LiteLlmConfigFile_WasRemoved()
    {
        Assert.False(File.Exists(Path.Combine("..", "..", "..", "..", "..", "litellm-config.yaml")));
    }

    private static JsonDocument ReadApiAppsettings(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine("..", "..", "..", "..", "..",
            "src", "Pim.Api", fileName)));

    private static string ExtractService(string compose, string serviceName)
    {
        var serviceStart = compose.IndexOf($"\n  {serviceName}:\n", StringComparison.Ordinal);
        Assert.True(serviceStart >= 0, $"Expected docker-compose.yml to define service '{serviceName}'.");
        serviceStart++;

        var nextServiceStart = compose.IndexOf("\n  ", serviceStart + 1, StringComparison.Ordinal);
        while (nextServiceStart >= 0 && compose[nextServiceStart + 3] == ' ')
            nextServiceStart = compose.IndexOf("\n  ", nextServiceStart + 1, StringComparison.Ordinal);

        return nextServiceStart < 0 ? compose[serviceStart..] : compose[serviceStart..nextServiceStart];
    }
}
