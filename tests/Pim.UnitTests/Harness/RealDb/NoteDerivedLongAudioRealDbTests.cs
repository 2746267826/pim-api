using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Ai;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

[Trait("DataSource", "RealDb")]
public sealed class NoteDerivedLongAudioRealDbTests
{
    private const string Key = "h03-real-db-provider-key";

    [SkippableFact]
    public async Task AudioBatchRequestIdReplaysOnceAndConflictsOnDifferentInputAgainstRealPostgres()
    {
        await using var database = await TempMigrationDatabase.CreateAsync("h03_long_audio");
        await database.MigrateAsync();
        var db = database.Db;
        var owner = new Owner();
        db.Users.Add(new UserEntity
        {
            Id = owner.UserId!.Value,
            Username = "h03-long-audio-owner",
            Email = "h03-long-audio@example.invalid",
            Role = "admin"
        });
        await db.SaveChangesAsync();

        var secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var settings = new AiModelSettingsService(db, secrets);
        var model = await settings.SaveAsync(null, new SaveAiModelRequest(
            "H03 long audio", "https://fixture.invalid/v1", "fixture-model", true,
            [AiModelCapability.Text], 60, 123, Key), default);
        var runtime = await settings.GetSettingsAsync(default);
        await settings.SaveSettingsAsync(new(runtime.Revision, true, model.Id, null, null), default);

        var wire = new Wire();
        var caller = new AiTextCallService(db, secrets, new AiProfileChatClientFactory(wire),
            owner, new AiTextCallGate());
        var noteId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var chunkSha = Digest(Encoding.UTF8.GetBytes("fixed-long-audio-chunk"));
        var first = Wave(160, 1);
        var second = Wave(160, 2);
        var audio = new[]
        {
            Slice(owner.UserId!.Value, noteId, versionId, chunkSha, first, 0,
                "recording-o000000000000-n160.wav"),
            Slice(owner.UserId!.Value, noteId, versionId, chunkSha, second, 160,
                "recording-o000000000160-n160.wav")
        };
        var requestId = Guid.NewGuid();
        var request = new AiTextCallRequest(requestId, owner.UserId.Value,
            "长录音批次整理", ModelId: model.Id);
        var consent = new AiVisionDestinationConsent(model.Id, model.Revision, model.BaseUrl, true);

        var firstResult = await caller.CallWithAudioForOwnerAsync(owner.UserId.Value, request, audio,
            consent, "native-note-derived-task", taskId.ToString("D"),
            "note.derived.audio-batch", default);

        Assert.Equal(AiRequestStatus.Succeeded, firstResult.Status);
        Assert.Equal(1, wire.Calls);
        var log = await db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal(requestId, log.ClientRequestId);
        Assert.Equal("note.derived.audio-batch", log.Purpose);
        Assert.Equal("native-note-derived-task", log.SourceObjectType);
        Assert.Equal(taskId.ToString("D"), log.SourceObjectId);
        Assert.Contains("pim-ai-audio:", log.RequestMessagesJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(first), log.RequestMessagesJson,
            StringComparison.Ordinal);
        using var metadata = JsonDocument.Parse(log.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("requestBodyCaptured").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("responseBodyComplete").GetBoolean());
        Assert.Equal("sha256-reference",
            metadata.RootElement.GetProperty("audioEncoding").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("audioInputsReferenced").GetInt32());
        Assert.Equal(chunkSha,
            metadata.RootElement.GetProperty("audioInputsSha256")[0].GetString());
        Assert.Equal(owner.UserId.Value,
            metadata.RootElement.GetProperty("audioContextOwnerId").GetGuid());
        Assert.Equal(noteId, metadata.RootElement.GetProperty("audioContextNoteId").GetGuid());
        Assert.Equal(versionId,
            metadata.RootElement.GetProperty("audioContextSourceVersionId").GetGuid());

        var replay = await caller.CallWithAudioForOwnerAsync(owner.UserId.Value, request, audio,
            consent, "native-note-derived-task", taskId.ToString("D"),
            "note.derived.audio-batch", default);
        Assert.True(replay.Replayed);
        Assert.Equal(firstResult.LogId, replay.LogId);
        Assert.Equal(1, wire.Calls);
        Assert.Equal(1, await db.AiRequestLogs.CountAsync());

        var conflict = await Assert.ThrowsAsync<DomainException>(() =>
            caller.CallWithAudioForOwnerAsync(owner.UserId.Value, request with
            {
                Prompt = "同一请求标识的另一份音频输入"
            }, audio, consent, "native-note-derived-task", taskId.ToString("D"),
                "note.derived.audio-batch", default));
        Assert.Equal(40901, conflict.ErrorCode);
        Assert.Equal(1, wire.Calls);
    }

    private static AiAudioContextInput Slice(Guid owner, Guid noteId, Guid versionId,
        string chunkSha, byte[] bytes, long offset, string name) =>
        new(name, "audio/wav", chunkSha, Digest(bytes), bytes.Length, owner, noteId, versionId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), 1, 1, 5, offset,
            (bytes.Length - 44) / 2, "recordings/fixture.wav", bytes);

    private static byte[] Wave(int samples, byte value)
    {
        var bytes = new byte[44 + samples * 2];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 16_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 32_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (short)16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.TryWriteBytes(bytes.AsSpan(40), samples * 2);
        bytes.AsSpan(44).Fill(value);
        return bytes;
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Role => "admin";
    }

    private sealed class Wire : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(
            new AiTextResponseLimitHandler { InnerHandler = this }, false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken ct)
        {
            Calls++;
            _ = await request.Content!.ReadAsStringAsync(ct);
            var body = JsonSerializer.Serialize(new
            {
                id = "chatcmpl-h03", created = 1780000000, model = "fixture-model",
                @object = "chat.completion",
                choices = new[]
                {
                    new { index = 0, finish_reason = "stop",
                        message = new { role = "assistant", content = "本批录音整理完成" } }
                },
                usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
            });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
