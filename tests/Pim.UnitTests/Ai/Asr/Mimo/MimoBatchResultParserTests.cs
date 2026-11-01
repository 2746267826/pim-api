using System.Text;
using System.Text.Json;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Xunit;

namespace Pim.UnitTests.Ai.Asr.Mimo;

public sealed class MimoBatchResultParserTests
{
    [Fact]
    public void MatchesByCustomId_OutOfOrder()
    {
        var output = string.Join('\n', Success("b", "second"), Success("a", "first"));
        var result = MimoBatchResultParser.Reconcile(["a", "b"], output);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("a", result.Items[0].CustomId);
        Assert.Equal("first", result.Items[0].Content);
        Assert.All(result.Items, item => Assert.Equal(MimoBatchItemDisposition.AsrSucceeded, item.Disposition));
        Assert.False(result.HasFailures);
    }

    [Fact]
    public void Http200WithoutCanonicalAudioTokens_IsInvalidButRetainsRawBody()
    {
        var body = new
        {
            choices = new[] { new { message = new { content = "looks fine" }, finish_reason = "stop" } },
            usage = new { audio_tokens = 100 }
        };
        var result = MimoBatchResultParser.Reconcile(["a"], Success("a", body));
        var item = Assert.Single(result.Items);
        Assert.Equal(MimoBatchItemDisposition.AsrInvalid, item.Disposition);
        Assert.False(item.AsrValidated);
        Assert.Equal("audio_tokens_missing", item.AsrErrorCode);
        Assert.NotNull(item.RawBody);
        Assert.Contains("choices", item.RawBody);
        Assert.True(result.HasFailures);
    }

    [Fact]
    public void ReasoningContentFallback_IsAcceptedOnlyWithCanonicalAudioTokens()
    {
        var body = new
        {
            choices = new[] { new { message = new { content = (string?)null, reasoning_content = "fallback" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        };
        var item = Assert.Single(MimoBatchResultParser.Reconcile(["a"], Success("a", body)).Items);
        Assert.Equal(MimoBatchItemDisposition.AsrSucceeded, item.Disposition);
        Assert.Equal("fallback", item.Content);
        Assert.True(item.AsrValidated);
    }

    [Fact]
    public void RepetitionAndTruncation_AreInvalid()
    {
        var repeated = new
        {
            choices = new[] { new { message = new { content = "hello hello hello hello hello hello" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        };
        var truncated = new
        {
            choices = new[] { new { message = new { content = "partial" }, finish_reason = "length" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        };
        var repeatedItem = Assert.Single(MimoBatchResultParser.Reconcile(["r"], Success("r", repeated)).Items);
        var truncatedItem = Assert.Single(MimoBatchResultParser.Reconcile(["t"], Success("t", truncated)).Items);
        Assert.Equal("repetition", repeatedItem.AsrErrorCode);
        Assert.Equal("truncation", truncatedItem.AsrErrorCode);
    }

    [Fact]
    public void MissingDuplicateUnknownAndNon200_AreClassified()
    {
        var output = string.Join('\n', Success("a", "ok"), Success("a", "again"), Success("b", "other", status: 500), Success("x", "unknown"));
        var result = MimoBatchResultParser.Reconcile(["a", "b", "c"], output);
        Assert.Contains("a", result.DuplicateCustomIds);
        Assert.Contains("c", result.MissingCustomIds);
        Assert.Equal(MimoBatchItemDisposition.NonSuccessStatus, result.Items.Single(x => x.CustomId == "b").Disposition);
        Assert.Contains("x", result.UnknownCustomIds);
        Assert.Equal(MimoBatchItemDisposition.Duplicate, result.Items.Single(x => x.CustomId == "a").Disposition);
    }

    [Fact]
    public void ErrorLine_IsFailed()
    {
        var line = JsonSerializer.Serialize(new
        {
            id = "batch_req", custom_id = "a", response = (object?)null,
            error = new { code = "inference_failed", message = "400 Bad Request" }
        });
        var item = Assert.Single(MimoBatchResultParser.Reconcile(["a"], line).Items);
        Assert.Equal(MimoBatchItemDisposition.Failed, item.Disposition);
        Assert.Equal("inference_failed", item.ErrorCode);
    }

    [Fact]
    public void MalformedShapes_DoNotAbortTheWholeBatch()
    {
        var lines = new[]
        {
            JsonSerializer.Serialize(new { custom_id = "a", response = "not-an-object" }),
            JsonSerializer.Serialize(new { custom_id = "b", response = new { status_code = "200", body = new { } } }),
            JsonSerializer.Serialize(new { custom_id = "c", response = new { status_code = 200, body = new[] { 1, 2 } } }),
            JsonSerializer.Serialize(new { custom_id = "d", response = new { status_code = 200, body = new { choices = new { }, usage = new { } } } })
        };
        var result = MimoBatchResultParser.Reconcile(["a", "b", "c", "d"], string.Join('\n', lines));
        Assert.Equal(4, result.Items.Count);
        Assert.Equal(MimoBatchItemDisposition.Failed, result.Items[0].Disposition);
        Assert.Equal(MimoBatchItemDisposition.NonSuccessStatus, result.Items[1].Disposition);
        Assert.Equal(MimoBatchItemDisposition.AsrInvalid, result.Items[2].Disposition);
        Assert.Equal(MimoBatchItemDisposition.AsrInvalid, result.Items[3].Disposition);
    }

    [Fact]
    public void CustomIdIsComparedExactly_WithoutTrimming()
    {
        var result = MimoBatchResultParser.Reconcile([" id "], Success(" id ", "kept"));
        var item = Assert.Single(result.Items);
        Assert.Equal(" id ", item.CustomId);
        Assert.Equal(MimoBatchItemDisposition.AsrSucceeded, item.Disposition);
    }

    [Fact]
    public void OverlongLine_IsBoundedAndCountedMalformed()
    {
        var line = new string('x', 200);
        var result = MimoBatchResultParser.Reconcile(["a"], line,
            limits: new MimoBatchParseLimits(MaxLineChars: 32, MaxLines: 100, MaxTotalChars: 10_000));
        Assert.Equal(1, result.MalformedLines);
        Assert.Equal(MimoBatchItemDisposition.Missing, Assert.Single(result.Items).Disposition);
    }

    [Fact]
    public void TotalCharBudget_IsEnforced()
    {
        var output = Success("a", "1234567890") + "\n" + Success("b", "1234567890");
        var error = Assert.Throws<MimoBatchException>(() => MimoBatchResultParser.Reconcile(["a", "b"], output,
            limits: new MimoBatchParseLimits(MaxLineChars: 4096, MaxLines: 100, MaxTotalChars: 20)));
        Assert.Equal(42202, error.ErrorCode);
    }

    [Fact]
    public void OutputAndErrorFilesShareOneTotalCharBudget()
    {
        var output = Success("a", "ok");
        var error = Success("b", "failed");
        var limit = output.Length + error.Length - 5;
        var exception = Assert.Throws<MimoBatchException>(() => MimoBatchResultParser.Reconcile(["a"], output, error,
            new MimoBatchParseLimits(MaxLineChars: 4096, MaxLines: 100, MaxTotalChars: limit)));
        Assert.Equal(42202, exception.ErrorCode);
    }

    [Fact]
    public void StreamOverload_ReadsWithoutMaterializingWholeFile()
    {
        var bytes = Encoding.UTF8.GetBytes(Success("a", "streamed"));
        using var stream = new MemoryStream(bytes);
        var item = Assert.Single(MimoBatchResultParser.Reconcile(["a"], stream).Items);
        Assert.Equal(MimoBatchItemDisposition.AsrSucceeded, item.Disposition);
    }

    [Fact]
    public void EmptyOutput_MarksEveryExpectedItemMissing()
    {
        var result = MimoBatchResultParser.Reconcile(["a", "b"], "");
        Assert.Equal(new[] { "a", "b" }, result.MissingCustomIds);
        Assert.All(result.Items, item => Assert.Equal(MimoBatchItemDisposition.Missing, item.Disposition));
    }

    private static string Success(string customId, string content, int status = 200) =>
        Success(customId, new
        {
            choices = new[] { new { message = new { content }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 5 } }
        }, status);

    private static string Success(string customId, object body, int status = 200) =>
        JsonSerializer.Serialize(new
        {
            id = "batch_req", custom_id = customId,
            response = new { status_code = status, body }, error = (object?)null
        });
}
