using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteAsrRouteGlobalDefaultsTests
{
    [Fact]
    public async Task ExplicitCloudOverrideKeepsEffectiveAndGlobalDestinationsSeparate()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("asr-route-global-" + Guid.NewGuid().ToString("N")).Options;
        await using var db = new PimDbContext(options);
        var owner = Guid.NewGuid();
        var overrideId = Guid.NewGuid();
        var globalId = Guid.NewGuid();
        await db.Users.AddAsync(new UserEntity
        {
            Id = owner, Username = "owner-" + owner.ToString("N")[..8],
            Email = owner.ToString("N")[..8] + "@example.invalid",
            PasswordHash = "synthetic", IsActive = true
        });
        db.AiModelProfiles.AddRange(
            new AiModelProfileEntity
            {
                Id = overrideId, Name = "账号覆盖 ASR", NormalizedName = "override-" + owner.ToString("N"),
                BaseUrl = "https://override.example.invalid/v1/", Model = "mimo-v2.5-asr",
                Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
                AsrPresetId = "mimo-asr", ApiKeySecret = "synthetic"
            },
            new AiModelProfileEntity
            {
                Id = globalId, Name = "全局多模态 ASR", NormalizedName = "global-" + owner.ToString("N"),
                BaseUrl = "https://global.example.invalid/v1/", Model = "mimo-v2.6-flash",
                Enabled = true, Capabilities = AiModelCapabilityFlags.Transcription,
                AsrPresetId = "mimo-multimodal", ApiKeySecret = "synthetic"
            });
        db.AiModelRuntime.Add(new AiModelRuntimeEntity
        {
            Enabled = true, TranscriptionModelId = globalId
        });
        db.NoteAsrRouteConfigs.Add(new NoteAsrRouteConfigEntity
        {
            OwnerId = owner, DefaultRoute = NoteAsrRoute.CloudAsr,
            CloudModelId = overrideId, ConsentVersion = "consent-override"
        });
        await db.SaveChangesAsync();

        var config = await Service(db, owner).GetConfigAsync(CancellationToken.None);

        Assert.Equal(overrideId, config.CloudModelId);
        Assert.Equal(overrideId, config.EffectiveCloudModelId);
        Assert.Equal("账号覆盖 ASR", config.EffectiveCloudModelName);
        Assert.Equal(NoteAsrRoute.CloudAsr, config.EffectiveCloudRoute);
        Assert.Equal(globalId, config.GlobalCloudModelId);
        Assert.Equal("全局多模态 ASR", config.GlobalCloudModelName);
        Assert.Equal(NoteAsrRoute.CloudMultimodal, config.GlobalCloudRoute);
    }

    [Fact]
    public async Task MissingGlobalTranscriptionDefaultIsReportedAsAbsent()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("asr-route-no-global-" + Guid.NewGuid().ToString("N")).Options;
        await using var db = new PimDbContext(options);
        var owner = Guid.NewGuid();
        await db.Users.AddAsync(new UserEntity
        {
            Id = owner, Username = "owner-" + owner.ToString("N")[..8],
            Email = owner.ToString("N")[..8] + "@example.invalid",
            PasswordHash = "synthetic", IsActive = true
        });
        db.AiModelRuntime.Add(new AiModelRuntimeEntity { Enabled = true, TranscriptionModelId = null });
        await db.SaveChangesAsync();

        var config = await Service(db, owner).GetConfigAsync(CancellationToken.None);

        Assert.Null(config.GlobalCloudModelId);
        Assert.Null(config.GlobalCloudModelName);
        Assert.Null(config.GlobalCloudRoute);
        Assert.Null(config.EffectiveCloudModelId);
    }

    private static NoteAsrRoutingService Service(PimDbContext db, Guid owner)
    {
        var materialOptions = Options.Create(new NoteMaterialUploadOptions
        {
            StorageRoot = Path.Combine(Path.GetTempPath(), "pim-asr-route-global-" + Guid.NewGuid().ToString("N"))
        });
        var materials = new NoteMaterialUploadStore(materialOptions);
        var tasks = new NoteAsrTaskStore(materialOptions);
        var user = new FixedUser(owner);
        return new NoteAsrRoutingService(db, user, materials,
            new AiAsrPresetRegistry(Array.Empty<IAiAsrPreset>()), tasks,
            new NoteAsrEvidenceService(db, tasks, user),
            new PassthroughSecrets(), Options.Create(new NoteAsrRoutingOptions()));
    }

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Role => "user";
    }

    private sealed class PassthroughSecrets : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }
}
