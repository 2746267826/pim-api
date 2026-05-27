using System.Text.Json;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiConfigurationTests
{
    [Fact]
    public void Appsettings_DefinesLiteLlmDefaults()
    {
        using var document = ReadApiAppsettings("appsettings.json");

        AssertLiteLlmDefaults(document.RootElement.GetProperty("Ai"), "http://litellm:4000");
    }

    [Fact]
    public void DevelopmentAppsettings_DefinesLocalLiteLlmDefaults()
    {
        using var document = ReadApiAppsettings("appsettings.Development.json");

        AssertLiteLlmDefaults(document.RootElement.GetProperty("Ai"), "http://127.0.0.1:4000");
    }

    [Fact]
    public void DockerCompose_AddsLiteLlmServiceAndApiEnvironment()
    {
        var compose = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "docker-compose.yml")).ReplaceLineEndings("\n");
        var pimApi = ExtractService(compose, "pim-api");
        var litellm = ExtractService(compose, "litellm");

        Assert.Contains("litellm:", compose);
        Assert.Contains("Ai__Enabled=${AI_ENABLED:-false}", pimApi);
        Assert.Contains("Ai__Provider=litellm", pimApi);
        Assert.Contains("Ai__BaseUrl=http://litellm:4000", pimApi);
        Assert.Contains("Ai__ApiKey=${PIM_LITELLM_VIRTUAL_KEY}", pimApi);
        Assert.Contains("Ai__DefaultModel=${PIM_AI_DEFAULT_MODEL:-pim-default}", pimApi);
        Assert.Contains("Ai__TimeoutSeconds=30", pimApi);
        Assert.Contains("Ai__MaxOutputTokensPerRequest=1000", pimApi);
        Assert.Contains("Ai__MaxAttemptsPerRequest=2", pimApi);
        Assert.Contains("Ai__SaveFullPrompts=true", pimApi);
        Assert.Contains("Ai__SaveFullResponses=true", pimApi);
        Assert.Contains("litellm:\n        condition: service_started", pimApi);

        Assert.Contains("docker.litellm.ai/berriai/litellm:main-latest", litellm);
        Assert.Contains("command: [\"--config\", \"/app/config.yaml\", \"--port\", \"4000\"]", litellm);
        Assert.Contains("./litellm-config.yaml:/app/config.yaml:ro", litellm);
        Assert.Contains("DATABASE_URL=postgresql://pim:${PG_PASSWORD}@postgres:5432/pim", litellm);
        Assert.Contains("LITELLM_MASTER_KEY=${LITELLM_MASTER_KEY}", litellm);
        Assert.Contains("LITELLM_SALT_KEY=${LITELLM_SALT_KEY}", litellm);
        Assert.Contains("LITELLM_UPSTREAM_MODEL=${LITELLM_UPSTREAM_MODEL}", litellm);
        Assert.Contains("LITELLM_UPSTREAM_API_KEY=${LITELLM_UPSTREAM_API_KEY}", litellm);
        Assert.Contains("LITELLM_UPSTREAM_API_BASE=${LITELLM_UPSTREAM_API_BASE}", litellm);
        Assert.Contains("postgres:\n        condition: service_healthy", litellm);
        Assert.Contains("- pim-net", litellm);
    }

    [Fact]
    public void EnvExample_UsesSameVirtualAndMasterLiteLlmKey()
    {
        var variables = ReadEnvExample();

        Assert.Equal(variables["LITELLM_MASTER_KEY"], variables["PIM_LITELLM_VIRTUAL_KEY"]);
    }

    private static JsonDocument ReadApiAppsettings(string fileName)
    {
        return JsonDocument.Parse(File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "src", "Pim.Api", fileName)));
    }

    private static void AssertLiteLlmDefaults(JsonElement ai, string expectedBaseUrl)
    {
        Assert.False(ai.GetProperty("Enabled").GetBoolean());
        Assert.Equal("litellm", ai.GetProperty("Provider").GetString());
        Assert.Equal(expectedBaseUrl, ai.GetProperty("BaseUrl").GetString());
        Assert.Equal("", ai.GetProperty("ApiKey").GetString());
        Assert.Equal("pim-default", ai.GetProperty("DefaultModel").GetString());
        Assert.Equal(30, ai.GetProperty("TimeoutSeconds").GetInt32());
        Assert.Equal(1000, ai.GetProperty("MaxOutputTokensPerRequest").GetInt32());
        Assert.Equal(2, ai.GetProperty("MaxAttemptsPerRequest").GetInt32());
        Assert.True(ai.GetProperty("SaveFullPrompts").GetBoolean());
        Assert.True(ai.GetProperty("SaveFullResponses").GetBoolean());
    }

    private static string ExtractService(string compose, string serviceName)
    {
        var serviceStart = compose.IndexOf($"\n  {serviceName}:\n", StringComparison.Ordinal);
        Assert.True(serviceStart >= 0, $"Expected docker-compose.yml to define service '{serviceName}'.");
        serviceStart++;

        var nextServiceStart = compose.IndexOf("\n  ", serviceStart + 1, StringComparison.Ordinal);
        while (nextServiceStart >= 0 && compose[nextServiceStart + 3] == ' ')
        {
            nextServiceStart = compose.IndexOf("\n  ", nextServiceStart + 1, StringComparison.Ordinal);
        }

        return nextServiceStart < 0
            ? compose[serviceStart..]
            : compose[serviceStart..nextServiceStart];
    }

    private static Dictionary<string, string> ReadEnvExample()
    {
        return File.ReadAllLines(Path.Combine("..", "..", "..", "..", "..", ".env.example"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);
    }
}
