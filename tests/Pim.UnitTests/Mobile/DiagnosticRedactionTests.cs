using Pim.Module.Mobile.Diagnostics;
using Xunit;

namespace Pim.UnitTests.Mobile;

public sealed class DiagnosticRedactionTests
{
    [Fact]
    public void RedactText_RemovesTokensSensitiveFieldsAndTemporaryPaths()
    {
        const string raw = """
            Authorization: Bearer abcdefghijklmnop
            GraphEventId=graphevent-secret
            ChangeKey=change-secret
            token=plain-token-secret
            provider key sk-abcdefghijklmnopqrstuvwxyz
            /tmp/pim/private/upload.bin
            C:\Users\alice\AppData\Local\Temp\dump.txt
            """;

        var result = DiagnosticRedaction.RedactText(raw, 10_000);
        const string queryRaw = "https://example.test/callback?access_token=" + "query" + "-value&ok=1";
        var queryResult = DiagnosticRedaction.RedactText(queryRaw, 10_000);

        Assert.DoesNotContain("abcdefghijklmnop", result, StringComparison.Ordinal);
        Assert.DoesNotContain("graphevent-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("change-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-token-secret", result, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", result, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\alice", result, StringComparison.Ordinal);
        Assert.DoesNotContain("query-value", queryResult, StringComparison.Ordinal);
        Assert.Contains("ok=1", queryResult, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.Marker, result, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedaction.PathMarker, result, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactAttributes_DropsSensitiveNamesAndRedactsValues()
    {
        var result = DiagnosticRedaction.RedactAttributes(new Dictionary<string, string>
        {
            ["route"] = "/diagnostics",
            ["access_token"] = "never-export-this",
            ["/tmp/pim/attribute-key"] = "path-key",
            ["detail"] = "Bearer abcdefghijklmnop /tmp/pim/private.txt"
        });

        Assert.Equal("/diagnostics", result["route"]);
        Assert.False(result.ContainsKey("access_token"));
        Assert.False(result.ContainsKey("/tmp/pim/attribute-key"));
        Assert.DoesNotContain("abcdefghijklmnop", result["detail"], StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/pim", result["detail"], StringComparison.Ordinal);
    }
}
