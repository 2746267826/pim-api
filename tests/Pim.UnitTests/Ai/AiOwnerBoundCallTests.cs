using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Ai;

public sealed class AiOwnerBoundCallTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a8X8AAAAASUVORK5CYII=");
    private readonly PimDbContext _db = new(new DbContextOptionsBuilder<PimDbContext>()
        .UseInMemoryDatabase("ai-owner-bound-" + Guid.NewGuid()).Options);
    private readonly Owner _currentUser = new();
    private readonly OwnerUser _owner = new();
    private readonly Files _files = new();
    private readonly Wire _wire = new();
    private readonly ISecretProtector _secrets = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly AiTextCallGate _gate = new();
    private AiModelSettingsService Settings => new(_db, _secrets);
    private AiVisionMaterialService Materials => new(_db, _files, _currentUser);
    private AiTextCallService Caller => new(_db, _secrets, new AiProfileChatClientFactory(_wire), _currentUser, _gate, Materials);

    public AiOwnerBoundCallTests()
    {
        _db.Users.Add(new UserEntity { Id = _owner.UserId, Username = "durable-owner", Role = "user", IsActive = true });
        _db.SaveChanges();
    }

    [Fact]
    public async Task TextCallUsesDurableOwnerInsteadOfHttpCurrentUserAndLogsTaskStep()
    {
        var model = await Model(AiModelCapability.Text); await Enable(model.Id, null);
        var taskId = Guid.NewGuid(); var request = new AiTextCallRequest(Guid.NewGuid(), _owner.UserId, "summarize");

        var result = await Caller.CallForOwnerAsync(_owner.UserId, request, Destination(model), "native-note-derived-task",
            taskId.ToString("D"), "note-derived.text.summary", default);

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, _wire.Calls);
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal(_owner.UserId, log.UserId);
        Assert.NotEqual(_currentUser.UserId, log.UserId);
        Assert.Equal("notes", log.Module);
        Assert.Equal("note-derived.text.summary", log.Purpose);
        Assert.Equal("native-note-derived-task", log.SourceObjectType);
        Assert.Equal(taskId.ToString("D"), log.SourceObjectId);
    }

    [Fact]
    public async Task VisionCallUsesDurableOwnerForTransportAndMaterialStorage()
    {
        var model = await Model(AiModelCapability.Vision); await Enable(null, model.Id);
        var taskId = Guid.NewGuid();
        var request = new AiVisionCallRequest(Guid.NewGuid(), _owner.UserId, "read this photo", 1,
            [new("photo.png", "image/png", Convert.ToBase64String(Png))],
            new(model.Id, model.Revision, model.BaseUrl, true));

        var result = await Caller.CallVisionForOwnerAsync(_owner.UserId, request, "native-note-derived-task",
            taskId.ToString("D"), "note-derived.vision.ocr", default);

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, _wire.Calls);
        Assert.Equal(_owner.UserId, _files.LastOwner);
        var log = await _db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal(_owner.UserId, log.UserId);
        Assert.Equal("note-derived.vision.ocr", log.Purpose);
        Assert.Equal(taskId.ToString("D"), log.SourceObjectId);
    }

    [Fact]
    public async Task InactiveOrMismatchedOwnerIsRejectedBeforeProviderAndReceiptCreation()
    {
        var model = await Model(AiModelCapability.Text); await Enable(model.Id, null);
        var entity = await _db.Users.SingleAsync();
        entity.IsActive = false; await _db.SaveChangesAsync();
        var inactive = new AiTextCallRequest(Guid.NewGuid(), _owner.UserId, "must not send");

        var error = await Assert.ThrowsAsync<DomainException>(() => Caller.CallForOwnerAsync(_owner.UserId,
            inactive, Destination(model), "native-note-derived-task", Guid.NewGuid().ToString("D"),
            "note-derived.text.summary", default));

        Assert.Equal(40301, error.ErrorCode);
        Assert.Equal(0, _wire.Calls);
        Assert.Empty(_db.AiRequestLogs);
        entity.IsActive = true; await _db.SaveChangesAsync();
        var mismatched = inactive with { ExpectedUserId = Guid.NewGuid() };
        error = await Assert.ThrowsAsync<DomainException>(() => Caller.CallForOwnerAsync(_owner.UserId,
            mismatched, Destination(model), "native-note-derived-task", Guid.NewGuid().ToString("D"),
            "note-derived.text.summary", default));
        Assert.Equal(40301, error.ErrorCode);
        Assert.Equal(0, _wire.Calls);
        Assert.Empty(_db.AiRequestLogs);
    }

    [Fact]
    public async Task SameRequestIdCannotBeReusedAcrossDerivedTasksOrSteps()
    {
        var model = await Model(AiModelCapability.Text); await Enable(model.Id, null);
        var request = new AiTextCallRequest(Guid.NewGuid(), _owner.UserId, "same input");
        var first = await Caller.CallForOwnerAsync(_owner.UserId, request, Destination(model), "native-note-derived-task",
            Guid.NewGuid().ToString("D"), "note-derived.text.summary", default);
        Assert.Equal(AiRequestStatus.Succeeded, first.Status);

        var error = await Assert.ThrowsAsync<DomainException>(() => Caller.CallForOwnerAsync(_owner.UserId,
            request, Destination(model), "native-note-derived-task", Guid.NewGuid().ToString("D"),
            "note-derived.text.summary", default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(1, _wire.Calls);
    }

    [Fact]
    public async Task DestinationChangedAfterPrecheckIsRejectedByTheOwnerBoundCall()
    {
        var model = await Model(AiModelCapability.Text); await Enable(model.Id, null);
        var destination = Destination(model);
        var updated = await Settings.SaveAsync(model.Id, new(model.Name, "https://changed.invalid/v1", model.Model,
            true, model.Capabilities, model.TimeoutSeconds, model.MaxOutputTokens,
            ApiKey: "synthetic-changed-owner-bound-key", ExpectedRevision: model.Revision), default);
        Assert.NotEqual(model.Revision, updated.Revision);
        var request = new AiTextCallRequest(Guid.NewGuid(), _owner.UserId, "must not leave after change");

        var error = await Assert.ThrowsAsync<DomainException>(() => Caller.CallForOwnerAsync(_owner.UserId,
            request, destination, "native-note-derived-task", Guid.NewGuid().ToString("D"),
            "note-derived.text.summary", default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Contains("材料未发送", error.Message);
        Assert.Equal(0, _wire.Calls);
        Assert.Empty(_db.AiRequestLogs);
    }

    [Fact]
    public async Task SameRequestIdCannotReuseADifferentDestinationConsent()
    {
        var model = await Model(AiModelCapability.Text); await Enable(model.Id, null);
        var destination = Destination(model);
        var request = new AiTextCallRequest(Guid.NewGuid(), _owner.UserId, "same consent-bound input");
        var first = await Caller.CallForOwnerAsync(_owner.UserId, request, destination, "native-note-derived-task",
            Guid.NewGuid().ToString("D"), "note-derived.text.summary", default);
        Assert.Equal(AiRequestStatus.Succeeded, first.Status);

        var error = await Assert.ThrowsAsync<DomainException>(() => Caller.CallForOwnerAsync(_owner.UserId,
            request, destination with { ModelRevision = Guid.NewGuid() }, "native-note-derived-task",
            Guid.NewGuid().ToString("D"), "note-derived.text.summary", default));

        Assert.Equal(40901, error.ErrorCode);
        Assert.Equal(1, _wire.Calls);
    }

    private async Task<AiModelDto> Model(AiModelCapability capability)
    {
        var name = capability == AiModelCapability.Vision ? "vision" : "text";
        return await Settings.SaveAsync(null, new(name, "https://synthetic.invalid/v1", name + "-model", true,
            [capability], 30, 128, "synthetic-key"), default);
    }

    private async Task Enable(Guid? textModelId, Guid? visionModelId)
    {
        var runtime = await Settings.GetSettingsAsync(default);
        await Settings.SaveSettingsAsync(new(runtime.Revision, true, textModelId, visionModelId, null), default);
    }

    private static AiVisionDestinationConsent Destination(AiModelDto model) =>
        new(model.Id, model.Revision, model.BaseUrl, true);

    public void Dispose() => _db.Dispose();

    private sealed record OwnerUser(Guid UserId)
    {
        public OwnerUser() : this(Guid.NewGuid()) { }
    }

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Role => "user";
    }

    private sealed class Files : IFileAttachmentStore
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public Guid? LastOwner { get; private set; }

        public async Task<FileAttachmentRef> StoreAsync(Guid userId, string objectKey, Stream content,
            string contentType, long sizeBytes, CancellationToken ct = default)
        {
            LastOwner = userId; using var memory = new MemoryStream(); await content.CopyToAsync(memory, ct);
            Objects[objectKey] = memory.ToArray();
            return new(objectKey, "synthetic", userId);
        }

        public Task<Stream> OpenReadAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(Objects[reference.ObjectKey]));

        public Task<string?> GetDirectLinkAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task DeleteAsync(Guid userId, FileAttachmentRef reference, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Wire : IHttpClientFactory
    {
        public int Calls { get; private set; }

        public HttpClient CreateClient(string name) => new(new AiTextResponseLimitHandler { InnerHandler = new Handler(this) });

        private sealed class Handler(Wire owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Calls++;
                await request.Content!.ReadAsStringAsync(ct);
                var body = JsonSerializer.Serialize(new
                {
                    id = "synthetic-owner-bound", created = 1780000000, model = "synthetic-model", @object = "chat.completion",
                    choices = new[] { new { index = 0, finish_reason = "stop",
                        message = new { role = "assistant", content = "complete" } } },
                    usage = new { prompt_tokens = 3, completion_tokens = 2, total_tokens = 5 }
                });
                return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }
        }
    }
}
