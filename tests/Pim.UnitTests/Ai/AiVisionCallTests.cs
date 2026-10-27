using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai;

// Synthetic PNGs, an in-memory file provider and the real SDK over a fake HTTP transport only.
public sealed class AiVisionCallTests : IDisposable
{
    private readonly PimDbContext _db = new(new DbContextOptionsBuilder<PimDbContext>()
        .UseInMemoryDatabase("ai-vision-" + Guid.NewGuid()).Options);
    private readonly User _user = new();
    private readonly Store _files = new();
    private readonly Wire _wire = new();
    private readonly ISecretProtector _secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly AiTextCallGate _gate = new();
    private AiModelSettingsService Settings => new(_db, _secrets);
    private AiVisionMaterialService Materials => new(_db, _files, _user);
    private AiTextCallService Caller(bool dropImages = false) => new(_db, _secrets,
        dropImages ? new DropFactory(new AiProfileChatClientFactory(_wire)) : new AiProfileChatClientFactory(_wire), _user, _gate, Materials);

    internal static readonly byte[] FirstPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGP8oKHBwMDAxMDAwMDAAAAP8gFEwVJxKAAAAABJRU5ErkJggg==");
    // A different valid opaque fixture; this is not a user attachment.
    internal static readonly byte[] SecondPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAFklEQVR4nGPUCPjAwMDAxMDAwMDAAAAQQgFsd1MS4gAAAABJRU5ErkJggg==");
    private async Task<AiVisionCallRequest> Request(int timeout = 60)
    {
        var model = await Settings.SaveAsync(null, new("视觉" + Guid.NewGuid(), "https://synthetic-provider.invalid/v1", "fixture-vision", true,
            [AiModelCapability.Vision], timeout, 123, "synthetic-model-key"), default);
        var runtime = await Settings.GetSettingsAsync(default);
        await Settings.SaveSettingsAsync(new(runtime.Revision, true, null, model.Id, null), default);
        return new(Guid.NewGuid(), _user.UserId!.Value, "  完整正文\npassword=普通内容  ", 2,
            [new("照片.png", "image/png", Convert.ToBase64String(FirstPng)), new("手写.png", "image/png", Convert.ToBase64String(SecondPng))],
            new(model.Id, model.Revision, model.BaseUrl, true), "结合全部图片回答");
    }

    [Fact]
    public async Task RealSdkSendsEveryImageAndExactText_AuditReferencesReproduceBytes_AndReplayDoesNotCallAgain()
    {
        var request = await Request();
        var result = await Caller().CallVisionAsync(request, default);
        Assert.Equal(AiRequestStatus.Succeeded, result.Status); Assert.Equal("完整视觉回执", result.ResponseText);
        Assert.Equal(1, _wire.Calls); Assert.Equal(2, _files.Writes);
        using var sent = JsonDocument.Parse(_wire.Bodies.Single());
        Assert.Equal(request.SystemPrompt, sent.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var content = sent.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(request.Prompt, content[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64," + request.Images[0].Base64, content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/png;base64," + request.Images[1].Base64, content[2].GetProperty("image_url").GetProperty("url").GetString());
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("ai.vision-call", log.Purpose); Assert.DoesNotContain("data:image", log.RequestPayloadJson);
        Assert.DoesNotContain("synthetic-model-key", log.RequestPayloadJson);
        using var recorded = JsonDocument.Parse(log.RequestMessagesJson);
        Assert.Equal(request.Prompt, recorded.RootElement[1].GetProperty("content")[0].GetProperty("text").GetString());
        var images = await Materials.ListAsync(log.Id, default);
        Assert.Equal(2, images.Count); Assert.All(images, image => Assert.True(image.Stored));
        Assert.Equal(FirstPng, (await Materials.ReadAsync(log.Id, images[0].Id, default)).Bytes);
        Assert.Equal(SecondPng, (await Materials.ReadAsync(log.Id, images[1].Id, default)).Bytes);
        Assert.Contains("pim-ai-image:" + images[0].Id, log.RequestMessagesJson);
        using var meta = JsonDocument.Parse(log.MetadataJson);
        Assert.True(meta.RootElement.GetProperty("providerAttempted").GetBoolean());
        Assert.Equal("sha256-reference", meta.RootElement.GetProperty("imageEncoding").GetString());
        Assert.True((await Caller().CallVisionAsync(request, default)).Replayed);
        Assert.Equal(1, _wire.Calls); Assert.Equal(2, _files.Writes);
        await Assert.ThrowsAsync<DomainException>(() => Caller().CallVisionAsync(request with { Images = request.Images.Reverse().ToArray() }, default));
        Assert.Equal(1, _wire.Calls);
    }

    [Theory]
    [InlineData("confirmation")]
    [InlineData("endpoint")]
    [InlineData("revision")]
    [InlineData("model")]
    public async Task MissingOrChangedDestinationConsentNeverUploadsOrContactsProvider(string change)
    {
        var request = await Request(); var consent = request.Consent;
        consent = change switch { "confirmation" => consent with { Confirmed = false }, "endpoint" => consent with { Endpoint = "https://unapproved.invalid/" },
            "revision" => consent with { ModelRevision = Guid.NewGuid() }, _ => consent with { ModelId = Guid.NewGuid() } };
        await Assert.ThrowsAsync<DomainException>(() => Caller().CallVisionAsync(request with { Consent = consent }, default));
        Assert.Equal(0, _wire.Calls); Assert.Equal(0, _files.Writes); Assert.Empty(_db.AiRequestLogs);
    }

    [Fact]
    public async Task ChangedDefaultModelRequiresNewConsent_AndChangedActorIsRejected()
    {
        var request = await Request();
        await Request(); // The default now points at a different server-side profile.
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() => Caller().CallVisionAsync(request, default))).ErrorCode);
        Assert.Equal(40301, (await Assert.ThrowsAsync<DomainException>(() => Caller().CallVisionAsync(
            request with { ExpectedUserId = Guid.NewGuid() }, default))).ErrorCode);
        Assert.Equal(0, _wire.Calls); Assert.Equal(0, _files.Writes);
    }

    [Fact]
    public async Task FailedFilePreparationRetainsPartialReferences_AndNeverSendsAnIncompleteImageSet()
    {
        var request = await Request(); _files.FailOnWrite = 2;
        var result = await Caller().CallVisionAsync(request, default);
        Assert.Equal(AiRequestStatus.Failed, result.Status); Assert.Equal(0, _wire.Calls);
        var row = await _db.AiRequestLogs.AsNoTracking().SingleAsync(); Assert.Equal("image_prepare_failed", row.ErrorCode);
        var images = await Materials.ListAsync(row.Id, default); Assert.True(images[0].Stored); Assert.False(images[1].Stored);
        Assert.True((await Caller().CallVisionAsync(request, default)).Replayed); Assert.Equal(2, _files.Writes);
        _files.FailOnWrite = 0;
        Assert.Equal(AiRequestStatus.Succeeded, (await Caller().CallVisionAsync(request with { ClientRequestId = Guid.NewGuid() }, default)).Status);
    }

    [Fact]
    public async Task SdkOmittingImagesIsRejectedBeforeTransport()
    {
        var result = await Caller(true).CallVisionAsync(await Request(), default);
        Assert.Equal(AiRequestStatus.Failed, result.Status); Assert.Equal(0, _wire.Calls);
        Assert.Equal("image_manifest_mismatch", (await _db.AiRequestLogs.SingleAsync()).ErrorCode);
    }

    [Fact]
    public async Task OriginalImageAccessIsOwnerOnly_AndModifiedBytesAreNeverShownAsOriginal()
    {
        var result = await Caller().CallVisionAsync(await Request(), default);
        var image = (await Materials.ListAsync(result.LogId, default))[0]; var owner = _user.UserId;
        _user.UserId = Guid.NewGuid();
        Assert.Equal(40401, (await Assert.ThrowsAsync<DomainException>(() => Materials.ListAsync(result.LogId, default))).ErrorCode);
        await Assert.ThrowsAsync<DomainException>(() => Materials.ReadAsync(result.LogId, image.Id, default)); Assert.Equal(0, _files.Reads);
        _user.UserId = owner;
        var stored = await _db.AiCallImages.SingleAsync(x => x.Id == image.Id);
        _files.Objects[stored.ObjectKey!] = SecondPng;
        Assert.Equal(40901, (await Assert.ThrowsAsync<DomainException>(() => Materials.ReadAsync(result.LogId, image.Id, default))).ErrorCode);
        Assert.Equal("完整视觉回执", (await _db.AiRequestLogs.SingleAsync()).ResponseText);
    }

    [Fact]
    public async Task CancelledAfterTransportKeepsAllInputsAndUnknownBilling()
    {
        var request = await Request(); _wire.Block = true;
        using var cancel = new CancellationTokenSource(); var running = Caller().CallVisionAsync(request, cancel.Token);
        await _wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // If the process disappears before final persistence, a surviving intent must not claim "not sent".
        using (var pending = JsonDocument.Parse((await _db.AiRequestLogs.AsNoTracking().SingleAsync()).MetadataJson))
            Assert.Equal(JsonValueKind.Null, pending.RootElement.GetProperty("providerAttempted").ValueKind);
        cancel.Cancel();
        var result = await running;
        Assert.Equal(AiRequestStatus.Cancelled, result.Status);
        var row = await _db.AiRequestLogs.SingleAsync(); Assert.Null(row.EstimatedCost); Assert.Null(row.TotalTokens);
        Assert.Equal(2, (await Materials.ListAsync(result.LogId, default)).Count(x => x.Stored));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("base64")]
    [InlineData("signature")]
    [InlineData("total")]
    public async Task InvalidOrMissingImagesAreRejectedWithoutProviderOrFileWrites(string problem)
    {
        var request = await Request();
        request = problem switch
        {
            "count" => request with { ImageCount = 3 },
            "base64" => request with { ImageCount = 1, Images = [new("x.png", "image/png", "invalid")] },
            "signature" => request with { ImageCount = 1, Images = [new("x.png", "image/png", Convert.ToBase64String("not an image"u8))] },
            _ => request with { ImageCount = 3, Images = Enumerable.Range(0, 3).Select(_ => {
                var bytes = new byte[3 * 1024 * 1024]; FirstPng.CopyTo(bytes, 0); return new AiVisionImageRequest("x.png", "image/png", Convert.ToBase64String(bytes)); }).ToArray() }
        };
        Assert.Equal(40001, (await Assert.ThrowsAsync<DomainException>(() => Caller().CallVisionAsync(request, default))).ErrorCode);
        Assert.Equal(0, _wire.Calls); Assert.Equal(0, _files.Writes);
    }

    private sealed class User : ICurrentUserService
    { public Guid? UserId { get; set; } = Guid.NewGuid(); public string? Username => "synthetic-owner"; public string? Role => "admin"; }
    private sealed class Store : IFileAttachmentStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new(); public int Writes; public int Reads; public int FailOnWrite;
        public async Task<FileAttachmentRef> StoreAsync(Guid userId, string objectKey, Stream content, string contentType, long sizeBytes, CancellationToken ct = default)
        { if (++Writes == FailOnWrite) throw new DomainException(5320, "synthetic unavailable source"); using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct); Objects[objectKey] = memory.ToArray(); return new(objectKey, "local", userId); }
        public Task<Stream> OpenReadAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default)
        { Reads++; return Task.FromResult<Stream>(new MemoryStream(Objects[reference.ObjectKey])); }
        public Task<string?> GetDirectLinkAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Wire : IHttpClientFactory
    {
        public List<string> Bodies { get; } = []; public int Calls; public bool Block;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(new AiTextResponseLimitHandler { InnerHandler = new Handler(this) });
        private sealed class Handler(Wire owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Calls++; owner.Bodies.Add(await request.Content!.ReadAsStringAsync(ct)); owner.Started.TrySetResult();
                if (owner.Block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"synthetic-vision\",\"object\":\"chat.completion\",\"created\":1780000000,\"model\":\"fixture-vision\",\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"完整视觉回执\"}}]}", Encoding.UTF8, "application/json") };
            }
        }
    }
    private sealed class DropFactory(IAiProfileChatClientFactory inner) : IAiProfileChatClientFactory
    {
        public AiProfileClientLease Create(AiModelConnection connection)
        { var original = inner.Create(connection); return new(new DropClient(original.Client), original); }
        private sealed class DropClient(IChatClient inner) : DelegatingChatClient(inner)
        {
            public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
                base.GetResponseAsync(messages.Select(x => new ChatMessage(x.Role, x.Text)), options, ct);
        }
    }
    public void Dispose() => _db.Dispose();
}
