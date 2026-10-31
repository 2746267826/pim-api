using System.Net;
using System.Text;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Xunit;

namespace Pim.UnitTests.Ai.Asr.Mimo;

public sealed class MimoMultimodalPresetTests
{
    private static readonly AiModelConnection Connection =
        new("https://api.xiaomimimo.com/v1/", "mimo-v2.6-pro", "synthetic-key", 5);

    [Fact]
    public async Task Transcribe_SendsAudioAndLanguage_AndParsesResult()
    {
        var (preset, handler) = Create((_, _) => Task.FromResult(Json(new
        {
            choices = new[] { new { message = new { content = "你好" }, finish_reason = "stop" } },
            usage = new
            {
                prompt_tokens = 3, completion_tokens = 2, total_tokens = 5, seconds = 1.2,
                prompt_tokens_details = new { audio_tokens = 7 }
            }
        })));
        var result = await preset.TranscribeAsync(Connection, Wave(), "zh", default);
        Assert.Equal("你好", result.Text);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(1.2, result.ReportedSeconds);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/chat/completions", request.Uri!.AbsoluteUri);
        Assert.Equal("Bearer synthetic-key", request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("mimo-v2.6-pro", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        var parts = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("input_audio", parts[0].GetProperty("type").GetString());
        Assert.StartsWith("data:audio/wav;base64,", parts[0].GetProperty("input_audio").GetProperty("data").GetString());
        Assert.Contains("language=zh", parts[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Transcribe_FallsBackToReasoningContent()
    {
        var (preset, _) = Create((_, _) => Task.FromResult(Json(new
        {
            choices = new[] { new { message = new { content = "", reasoning_content = "fallback" }, finish_reason = "stop" } },
            usage = new { prompt_tokens_details = new { audio_tokens = 2 } }
        })));
        var result = await preset.TranscribeAsync(Connection, Wave(), "auto", default);
        Assert.Equal("fallback", result.Text);
        Assert.True(result.HasTranscript);
    }

    [Fact]
    public async Task Transcribe_MissingCanonicalAudioTokens_FailsClosed()
    {
        var (preset, _) = Create((_, _) => Task.FromResult(Json(new
        {
            choices = new[] { new { message = new { content = "looks fine" }, finish_reason = "stop" } },
            usage = new { audio_tokens = 100 }
        })));
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            preset.TranscribeAsync(Connection, Wave(), "zh", default));
        Assert.Equal(50202, error.ErrorCode);
    }

    [Fact]
    public async Task Transcribe_NonSuccessStatus_IsNotRetried()
    {
        var (preset, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            preset.TranscribeAsync(Connection, Wave(), "zh", default));
        Assert.Equal(50201, error.ErrorCode);
    }

    [Fact]
    public async Task Transcribe_Redirect_IsRejected()
    {
        var (preset, _) = Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)));
        var error = await Assert.ThrowsAsync<MimoBatchException>(() =>
            preset.TranscribeAsync(Connection, Wave(), "zh", default));
        Assert.Equal(50204, error.ErrorCode);
    }

    [Fact]
    public async Task Transcribe_Timeout_IsNotRetried()
    {
        var (preset, _) = Create(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return Json(new { });
        });
        var connection = Connection with { TimeoutSeconds = 1 };
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            preset.TranscribeAsync(connection, Wave(), "zh", default));
        Assert.Equal(50401, error.ErrorCode);
    }

    [Fact]
    public void AuditRequest_RedactsAudioReference_AndRejectsLanguageMismatch()
    {
        var (preset, _) = Create((_, _) => throw new InvalidOperationException("no request"));
        var audio = Wave();
        var audioId = Guid.NewGuid();
        var body = JsonSerializer.Serialize(new
        {
            model = "mimo-v2.6-pro",
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_audio", input_audio = new { data = $"data:audio/wav;base64,{Convert.ToBase64String(audio.Bytes)}", format = "wav" } },
                        new { type = "text", text = "输出格式：起始秒-结束秒 | 说话人: 文本。language=zh。" }
                    }
                }
            }
        });
        var audited = preset.AuditRequest(Encoding.UTF8.GetBytes(body), Connection, audio, audioId, "zh");
        Assert.DoesNotContain(Convert.ToBase64String(audio.Bytes), audited);
        Assert.Contains($"pim-ai-audio:{audioId:D}", audited);
        Assert.Throws<InvalidDataException>(() =>
            preset.AuditRequest(Encoding.UTF8.GetBytes(body), Connection, audio, audioId, "en"));
    }

    [Fact]
    public async Task Transcribe_RejectsUppercaseModelWithoutSending()
    {
        var (preset, handler) = Create((_, _) => throw new InvalidOperationException("should not send"));
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            preset.TranscribeAsync(Connection with { Model = "MIMO-V2.6-PRO" }, Wave(), "zh", default));
        Assert.Equal(40001, error.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void NormalizeEndpoint_StripsFullOperation()
    {
        var (preset, _) = Create((_, _) => throw new InvalidOperationException("no request"));
        Assert.Equal("https://api.xiaomimimo.com/v1/",
            preset.NormalizeEndpoint("https://api.xiaomimimo.com/v1/chat/completions/"));
    }

    private static (MimoMultimodalPreset Preset, StubHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handler = new StubHandler(responder);
        var preset = new MimoMultimodalPreset(_ => new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });
        return (preset, handler);
    }

    private static AiAsrInput Wave() =>
        new("fixture.wav", "wav", MimoMultimodalResponseParserTests.BuildWave(3200), "sha256-fixture");

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public List<(Uri? Uri, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri, body, request.Headers.Authorization?.ToString()));
            return await responder(request, cancellationToken);
        }
    }
}
