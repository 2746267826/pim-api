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
}
