using System.Text.Json;
using Pim.Infrastructure.Ai;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiRedactorTests
{
    [Fact]
    public void RedactJson_RemovesKnownCredentialFields()
    {
        var json = """
        {
          "Authorization": "Bearer secret-token",
          "api_key": "sk-live-secret",
          "refresh_token": "refresh-secret",
          "nested": { "nextcloud_app_password": "app-secret" },
          "safe": "keep-me"
        }
        """;

        var redacted = AiRedactor.RedactJson(json);

        Assert.DoesNotContain("secret-token", redacted);
        Assert.DoesNotContain("sk-live-secret", redacted);
        Assert.DoesNotContain("refresh-secret", redacted);
        Assert.DoesNotContain("app-secret", redacted);
        Assert.Contains("keep-me", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactJson_RemovesExpandedCredentialFields()
    {
        var json = """
        {
          "client_secret": "plain-client-secret",
          "x-api-key": "plain-api-key",
          "safe": "keep-me"
        }
        """;

        var redacted = AiRedactor.RedactJson(json);

        Assert.DoesNotContain("plain-client-secret", redacted);
        Assert.DoesNotContain("plain-api-key", redacted);
        Assert.Contains("keep-me", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactJson_RemovesNormalizedSensitiveKeyVariants()
    {
        var json = """
        {
          "openai_api_key": "plain-openai-key",
          "privateKey": "plain-private-key",
          "accessToken": "plain-access-token",
          "proxy_authorization": "plain-proxy-auth",
          "safe": "keep-me"
        }
        """;

        var redacted = AiRedactor.RedactJson(json);

        Assert.DoesNotContain("plain-openai-key", redacted);
        Assert.DoesNotContain("plain-private-key", redacted);
        Assert.DoesNotContain("plain-access-token", redacted);
        Assert.DoesNotContain("plain-proxy-auth", redacted);
        Assert.Contains("keep-me", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactJson_InvalidJson_ReturnsParseableJsonWithRedactedRawText()
    {
        var redacted = AiRedactor.RedactJson("not json sk-task3-canary-1234567890");

        using var document = JsonDocument.Parse(redacted);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("raw", out _));
        Assert.DoesNotContain("sk-task3-canary-1234567890", redacted);
    }

    [Fact]
    public void RedactPlainText_RemovesTokenLikeValues()
    {
        var redacted = AiRedactor.RedactPlainText("error Bearer abc+/def== sk-task3-canary-1234567890");

        Assert.DoesNotContain("abc+/def==", redacted);
        Assert.DoesNotContain("sk-task3-canary-1234567890", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void RedactPlainText_RemovesSensitiveKeyValueFragments()
    {
        var text = """
        api_key=plain-api-secret
        password: hunter2
        client_secret=plain-client-secret
        "refreshToken":"plain-refresh-token"
        'accessToken':'plain-access-token'
        safe=value
        """;

        var redacted = AiRedactor.RedactPlainText(text);

        Assert.DoesNotContain("plain-api-secret", redacted);
        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("plain-client-secret", redacted);
        Assert.DoesNotContain("plain-refresh-token", redacted);
        Assert.DoesNotContain("plain-access-token", redacted);
        Assert.Contains("safe=value", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }
}
