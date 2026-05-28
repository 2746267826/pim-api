using Pim.Core.Ai;
using System.Text.Json;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiContractTests
{
    [Fact]
    public void AiGatewayRequest_ClampsAttemptsToFirstVersionHardLimit()
    {
        var request = new AiGatewayRequest(
            Module: "quick-notes",
            Purpose: "quick-notes.convert",
            SourceObjectType: "quick_note",
            SourceObjectId: "note-1",
            Messages: [new AiMessage(AiMessageRole.User, "convert this note")],
            Model: null,
            SchemaName: "quick-note-conversion",
            SchemaVersion: "1",
            MaxOutputTokens: 800,
            MaxAttempts: 9,
            Metadata: new Dictionary<string, string> { ["origin"] = "unit-test" });

        Assert.Equal(2, request.EffectiveMaxAttempts);
    }

    [Fact]
    public void AiResult_FailedValidationIncludesUserFacingErrorAndLogId()
    {
        var logId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var result = AiResult.FailedValidation(
            logId,
            ["$.title is required"]);

        Assert.Equal(AiRequestStatus.FailedValidation, result.Status);
        Assert.Equal(logId, result.LogId);
        Assert.Contains("AI 响应不符合要求的格式", result.UserFacingError);
        Assert.Equal(["$.title is required"], result.SchemaValidationErrors);
    }

    [Fact]
    public void AiResult_SerializesStatusAsString()
    {
        var result = new AiResult(
            AiRequestStatus.FailedValidation,
            ResponseText: null,
            ParsedOutputJson: null,
            SchemaValidationErrors: [],
            Usage: new AiTokenUsage(null, null, null, null, null),
            LogId: null,
            UserFacingError: null);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"FailedValidation\"", json);
        Assert.DoesNotContain("\"status\":4", json);
    }
}
