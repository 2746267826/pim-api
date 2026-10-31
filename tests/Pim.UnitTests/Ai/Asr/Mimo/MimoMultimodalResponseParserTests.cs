using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Xunit;

namespace Pim.UnitTests.Ai.Asr.Mimo;

public sealed class MimoMultimodalResponseParserTests
{
    private static AiAsrInput Wave(int dataBytes = 3200) => new("fixture.wav", "wav", BuildWave(dataBytes), "sha256-fixture");

    [Fact]
    public void ParsesContentAndUsage_WhenAudioTokensPresent()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "你好，世界。" }, finish_reason = "stop" } },
            usage = new
            {
                prompt_tokens = 11, completion_tokens = 5, total_tokens = 16, seconds = 2.5,
                prompt_tokens_details = new { audio_tokens = 7 }
            }
        }), Wave(), "mimo-v2.6-pro");

        Assert.Equal("你好，世界。", result.Text);
        Assert.True(result.HasTranscript);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(11, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);
        Assert.Equal(16, result.TotalTokens);
        Assert.Equal(2.5, result.ReportedSeconds);
        using var raw = JsonDocument.Parse(result.RawJson);
        Assert.Equal("content", raw.RootElement.GetProperty("contentSource").GetString());
        Assert.False(raw.RootElement.GetProperty("flags").GetProperty("empty").GetBoolean());
    }

    [Fact]
    public void FallsBackToReasoningContent_WhenContentIsEmpty()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "", reasoning_content = "回退转写" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 3 } }
        }), Wave());

        Assert.Equal("回退转写", result.Text);
        Assert.True(result.HasTranscript);
        using var raw = JsonDocument.Parse(result.RawJson);
        Assert.Equal("reasoning_content", raw.RootElement.GetProperty("contentSource").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void MissingOrZeroAudioTokens_FailsClosed(int? audioTokens)
    {
        var body = audioTokens is null
            ? Json(new { choices = new[] { new { message = new { content = "text" }, finish_reason = "stop" } }, usage = new { prompt_tokens = 1 } })
            : Json(new { choices = new[] { new { message = new { content = "text" }, finish_reason = "stop" } }, usage = new { prompt_tokens_details = new { audio_tokens = audioTokens } } });
        var error = Assert.Throws<MimoBatchException>(() => MimoMultimodalResponseParser.Parse(body, Wave()));
        Assert.Equal(50202, error.ErrorCode);
    }

    [Fact]
    public void DirectAudioTokensPositive_ButCanonicalZero_FailsClosed()
    {
        var body = Json(new
        {
            choices = new[] { new { message = new { content = "text" }, finish_reason = "stop" } },
            usage = new { audio_tokens = 100, prompt_tokens_details = new { audio_tokens = 0 } }
        });
        var error = Assert.Throws<MimoBatchException>(() => MimoMultimodalResponseParser.Parse(body, Wave()));
        Assert.Equal(50202, error.ErrorCode);
    }

    [Fact]
    public void DirectAudioTokensPositive_ButCanonicalMissing_FailsClosed()
    {
        var body = Json(new
        {
            choices = new[] { new { message = new { content = "text" }, finish_reason = "stop" } },
            usage = new { audio_tokens = 100 }
        });
        var error = Assert.Throws<MimoBatchException>(() => MimoMultimodalResponseParser.Parse(body, Wave()));
        Assert.Equal(50202, error.ErrorCode);
    }

    [Fact]
    public void ReasoningDetailsAudioTokens_AreAccepted()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "ok" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 4 } }
        }), Wave());
        Assert.Equal("ok", result.Text);
    }

    [Fact]
    public void TruncationFinishReason_MarksIncomplete()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "partial" }, finish_reason = "length" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        }), Wave());
        Assert.Equal(MimoMultimodalResponseParser.TruncationFinishReason, result.FinishReason);
        using var raw = JsonDocument.Parse(result.RawJson);
        Assert.True(raw.RootElement.GetProperty("flags").GetProperty("truncation").GetBoolean());
    }

    [Fact]
    public void EmptyTranscript_IsPreservedAsIncomplete()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        }), Wave());
        Assert.False(result.HasTranscript);
        Assert.Equal("", result.Text);
        Assert.Equal(MimoMultimodalResponseParser.EmptyFinishReason, result.FinishReason);
    }

    [Fact]
    public void Repetition_IsDetected()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "hello hello hello hello hello hello" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        }), Wave());
        Assert.Equal(MimoMultimodalResponseParser.RepetitionFinishReason, result.FinishReason);
        using var raw = JsonDocument.Parse(result.RawJson);
        Assert.True(raw.RootElement.GetProperty("flags").GetProperty("repetition").GetBoolean());
    }

    [Fact]
    public void MalformedTimestampPrefix_PreservesOriginalPlainText()
    {
        const string text = "[ab:cd] 这句话的时间戳格式无效";
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        }), Wave());
        Assert.Equal(text, result.Text);
        using var raw = JsonDocument.Parse(result.RawJson);
        Assert.True(raw.RootElement.GetProperty("flags").GetProperty("timestampFallback").GetBoolean());
    }

    [Fact]
    public void NonJsonBody_FailsClosed()
    {
        var error = Assert.Throws<MimoBatchException>(() => MimoMultimodalResponseParser.Parse("plain text transcript", Wave()));
        Assert.Equal(50203, error.ErrorCode);
    }

    [Fact]
    public void SegmentFormat_IsMappedIntoRawJson()
    {
        const string text = "0-1.5 | 说话人A: 你好\n1.5-3 | 说话人B: 世界";
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 9 } }
        }), Wave());
        Assert.Equal(text, result.Text);
        using var raw = JsonDocument.Parse(result.RawJson);
        var transcript = raw.RootElement.GetProperty("transcript");
        Assert.Equal(3.0, transcript.GetProperty("coverageSeconds").GetDouble());
        Assert.Equal(2, transcript.GetProperty("segments").GetArrayLength());
        Assert.Equal("说话人A", transcript.GetProperty("segments")[0].GetProperty("speaker").GetString());
        Assert.True(raw.RootElement.GetProperty("flags").GetProperty("formatDetected").GetBoolean());
        Assert.False(raw.RootElement.GetProperty("flags").GetProperty("hasGaps").GetBoolean());
        Assert.False(raw.RootElement.GetProperty("flags").GetProperty("hasOverlaps").GetBoolean());
    }

    [Fact]
    public void InvalidSegmentRange_FallsBackButPreservesText()
    {
        const string text = "0-2 | A: ok\n3-1 | B: bad range";
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = text }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 9 } }
        }), Wave());
        Assert.Equal(text, result.Text);
        using var raw = JsonDocument.Parse(result.RawJson);
        Assert.True(raw.RootElement.GetProperty("flags").GetProperty("timestampFallback").GetBoolean());
        Assert.Equal(1, raw.RootElement.GetProperty("transcript").GetProperty("segments").GetArrayLength());
    }

    [Fact]
    public void WaveHeader_EstimatesDuration_WhenUsageSecondsMissing()
    {
        var result = MimoMultimodalResponseParser.Parse(Json(new
        {
            choices = new[] { new { message = new { content = "ok" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        }), Wave(3200));
        Assert.Equal(0.1, result.ReportedSeconds);
    }

    [Fact]
    public void Mp3Header_EstimatesDuration()
    {
        var frame = new byte[] { 0xff, 0xfb, 0x90, 0x64 };
        var bytes = new byte[frame.Length + 16000];
        frame.CopyTo(bytes, 0);
        var audio = new AiAsrInput("fixture.mp3", "mp3", bytes, "sha256-fixture");
        var seconds = MimoMultimodalResponseParser.EstimateAudioSeconds(audio);
        Assert.NotNull(seconds);
        Assert.True(seconds > 0);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    internal static byte[] BuildWave(int dataBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        return stream.ToArray();
    }
}
