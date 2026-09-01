using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

public sealed class McpClientServiceTests : IDisposable
{
    private readonly PimDbContext _db;
    private readonly McpClientService _service;
    private readonly JwtService _jwt;
    private readonly Guid _owner;

    public McpClientServiceTests()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("mcp-tests-" + Guid.NewGuid())
            .Options;
        _db = new PimDbContext(options);
        var module = new Pim.Module.Mcp.McpModule();
        module.RegisterServices(new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            new ConfigurationBuilder().Build());

        var env = new StubHostEnvironment();
        _jwt = new JwtService(new ConfigurationBuilder().Build(), env, NullLogger<JwtService>.Instance);
        var audit = new AuditLogService(_db);
        _service = new McpClientService(_db, audit, _jwt);
        _owner = Guid.NewGuid();
        _db.Users.Add(new UserEntity
        {
            Id = _owner,
            Username = "alice",
            Email = "alice@example.com",
            PasswordHash = "x",
            Role = "admin",
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _jwt.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task CreateAsync_ReturnsOneTimeToken_AndStoresHash()
    {
        var result = await _service.CreateAsync("Hermes", _owner);
        Assert.StartsWith("pim_mcp_", result.Token);
        var stored = _db.Set<Pim.Module.Mcp.Entities.McpClientEntity>().Single();
        Assert.Equal(McpTokenService.HashToken(result.Token), stored.TokenHash);
        Assert.Equal(McpTokenService.TokenPrefix(result.Token), stored.TokenPrefix);
        Assert.NotEqual(result.Token, stored.TokenHash);
        Assert.True(stored.Permissions["read"].Values.All(v => v));
        Assert.True(stored.Permissions["write"].Values.All(v => !v));
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        await _service.CreateAsync("Hermes", _owner);
        await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(() => _service.CreateAsync("Hermes", _owner));
    }

    [Fact]
    public async Task VerifyAsync_UnknownToken_Returns401()
    {
        var outcome = await _service.VerifyAsync("pim_mcp_doesnotexist", "get_tasks", null);
        Assert.Equal(401, outcome.HttpStatus);
    }

    [Fact]
    public async Task VerifyAsync_MissingTool_Returns400()
    {
        var created = await _service.CreateAsync("NoTool", _owner);
        var outcome = await _service.VerifyAsync(created.Token, null, null);
        Assert.Equal(400, outcome.HttpStatus);
        Assert.Equal("tool is required", outcome.Error);
    }

    [Fact]
    public async Task VerifyAsync_RevokedToken_Returns401()
    {
        var created = await _service.CreateAsync("Revoked", _owner);
        await _service.RevokeAsync(created.Client.Id, _owner);
        var outcome = await _service.VerifyAsync(created.Token, "get_tasks", null);
        Assert.Equal(401, outcome.HttpStatus);
    }

    [Fact]
    public async Task VerifyAsync_WriteToolDeniedByDefault_Returns403()
    {
        var created = await _service.CreateAsync("ReadOnly", _owner);
        var outcome = await _service.VerifyAsync(created.Token, "create_task", "{}");
        Assert.Equal(403, outcome.HttpStatus);
        Assert.Equal("permission denied: create_task", outcome.Error);
    }

    [Fact]
    public async Task VerifyAsync_ValidReadTool_ReturnsJwtAndTracksActivity()
    {
        var created = await _service.CreateAsync("Reader", _owner);
        var outcome = await _service.VerifyAsync(created.Token, "get_tasks", null);
        Assert.Equal(0, outcome.HttpStatus);
        Assert.NotNull(outcome.Result);
        Assert.Equal(created.Client.Id, outcome.Result.ClientId);
        Assert.False(outcome.Result.IsWrite);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Result.AccessToken));
        Assert.Equal(_owner, outcome.Result.UserId);

        var stored = _db.Set<Pim.Module.Mcp.Entities.McpClientEntity>().Single(e => e.Id == created.Client.Id);
        Assert.Equal(1, stored.CallCount);
        Assert.Equal(0, stored.WriteCallCount);
        Assert.Equal("get_tasks", stored.LastTool);
        Assert.NotNull(stored.LastSeenAt);
    }

    [Fact]
    public async Task VerifyAsync_EnabledWriteTool_ReturnsJwtAuditAndCounters()
    {
        var created = await _service.CreateAsync("Writer", _owner);
        var permissions = created.Client.Permissions;
        permissions["write"]["create_task"] = true;
        await _service.UpdateAsync(created.Client.Id, null, permissions, _owner);

        var outcome = await _service.VerifyAsync(created.Token, "create_task", "{\"title\":\"x\"}");
        Assert.Equal(0, outcome.HttpStatus);
        Assert.True(outcome.Result!.IsWrite);

        var stored = _db.Set<Pim.Module.Mcp.Entities.McpClientEntity>().Single(e => e.Id == created.Client.Id);
        Assert.Equal(1, stored.WriteCallCount);
        Assert.Equal(1, stored.CallCount);

        var audit = _db.AuditLogs.FirstOrDefault(a => a.Action == "mcp.write.create_task");
        Assert.NotNull(audit);
        Assert.Equal(_owner, audit.UserId);
        Assert.Contains("clientId", audit.MetadataJson);
    }

    [Fact]
    public async Task UpdateAsync_RenamesAndSanitizesPermissions()
    {
        var created = await _service.CreateAsync("Old", _owner);
        var perms = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, bool>>
        {
            ["read"] = new() { ["get_tasks"] = false, ["bogus_tool"] = true },
            ["write"] = new() { ["create_task"] = true, ["nope"] = true },
        };
        var dto = await _service.UpdateAsync(created.Client.Id, "New", perms, _owner);
        Assert.Equal("New", dto.Name);
        Assert.False(dto.Permissions["read"]["get_tasks"]);
        Assert.False(dto.Permissions["read"].ContainsKey("bogus_tool"));
        Assert.True(dto.Permissions["write"]["create_task"]);
        Assert.False(dto.Permissions["write"].ContainsKey("nope"));
    }

    [Fact]
    public async Task UpdateAsync_OtherOwner_Forbidden()
    {
        var created = await _service.CreateAsync("Mine", _owner);
        await Assert.ThrowsAsync<Pim.Core.Exceptions.DomainException>(
            () => _service.UpdateAsync(created.Client.Id, "X", null, Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteAsync_RemovesClient()
    {
        var created = await _service.CreateAsync("Temp", _owner);
        await _service.DeleteAsync(created.Client.Id, _owner);
        Assert.Empty(_db.Set<Pim.Module.Mcp.Entities.McpClientEntity>());
    }

    [Fact]
    public async Task ListAsync_OnlyReturnsOwnersClients()
    {
        await _service.CreateAsync("Mine1", _owner);
        await _service.CreateAsync("Mine2", _owner);
        var other = Guid.NewGuid();
        _db.Users.Add(new UserEntity
        {
            Id = other,
            Username = "bob",
            Email = "bob@example.com",
            PasswordHash = "x",
            Role = "user",
        });
        await _db.SaveChangesAsync();
        await _service.CreateAsync("Theirs", other);

        var mine = await _service.ListAsync(_owner);
        var theirs = await _service.ListAsync(other);
        Assert.Equal(2, mine.Count);
        Assert.All(mine, c => Assert.Equal("alice", c.CreatedByUsername));
        Assert.Equal(1, theirs.Count);
        Assert.Equal("Theirs", theirs[0].Name);
    }

    [Fact]
    public async Task UpdateAsync_PartialPermissions_PreservesOtherSection()
    {
        var created = await _service.CreateAsync("Partial", _owner);
        // Send only the write section; read must be preserved as-is (all on).
        var writeOnly = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, bool>>
        {
            ["write"] = new() { ["create_task"] = true },
        };
        var dto = await _service.UpdateAsync(created.Client.Id, null, writeOnly, _owner);
        Assert.Equal(101, dto.Permissions["read"].Count);
        Assert.True(dto.Permissions["read"]["get_tasks"]);
        Assert.True(dto.Permissions["write"]["create_task"]);
        // Unlisted write tools keep their previous value (not wiped).
        Assert.Equal(50, dto.Permissions["write"].Count);
    }

    [Fact]
    public async Task UpdateAsync_PartialPermission_KeepsOtherKeysInSameSection()
    {
        var created = await _service.CreateAsync("Partial2", _owner);
        var singleKey = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, bool>>
        {
            ["read"] = new() { ["get_events"] = false },
        };
        var dto = await _service.UpdateAsync(created.Client.Id, null, singleKey, _owner);
        Assert.False(dto.Permissions["read"]["get_events"]);
        Assert.True(dto.Permissions["read"]["get_tasks"]);
        Assert.Equal(101, dto.Permissions["read"].Count);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
