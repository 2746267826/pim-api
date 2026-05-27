using System.Text.Json;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiConfigurationTests
{
    [Fact]
    public void Appsettings_DefinesLiteLlmDefaults()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "src", "Pim.Api", "appsettings.json")));
        var ai = document.RootElement.GetProperty("Ai");

        Assert.Equal("litellm", ai.GetProperty("Provider").GetString());
        Assert.Equal("http://litellm:4000", ai.GetProperty("BaseUrl").GetString());
        Assert.Equal(2, ai.GetProperty("MaxAttemptsPerRequest").GetInt32());
        Assert.True(ai.GetProperty("SaveFullPrompts").GetBoolean());
        Assert.True(ai.GetProperty("SaveFullResponses").GetBoolean());
    }

    [Fact]
    public void DockerCompose_AddsLiteLlmServiceAndApiEnvironment()
    {
        var compose = File.ReadAllText(Path.Combine("..", "..", "..", "..", "..", "docker-compose.yml"));

        Assert.Contains("litellm:", compose);
        Assert.Contains("docker.litellm.ai/berriai/litellm:main-latest", compose);
        Assert.Contains("Ai__BaseUrl=http://litellm:4000", compose);
        Assert.Contains("Ai__Provider=litellm", compose);
    }
}
