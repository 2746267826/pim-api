using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Ai;

/// <summary>
/// AI 调用审计的用户归属：AI 关闭路径（Blocked）也会写审计日志，
/// 当前用户应写入 user_id；系统上下文（无当前用户）保持 NULL。
/// </summary>
public class AiGatewayUserIdTests
{
    private sealed class FakeCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class NoopChatClientFactory : IAiChatClientFactory
    {
        public IChatClient Create(string model) => new FakeChatClient("ok");
    }

    private static PimDbContext NewDb()
        => new(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"ai-uid-{Guid.NewGuid()}")
            .Options);

    private static AiGateway NewGateway(PimDbContext db, ICurrentUserService currentUser)
        => new(
            Options.Create(new AiOptions
            {
                Enabled = false,
                Provider = "litellm",
                BaseUrl = "http://litellm:4000",
                DefaultModel = "pim-default"
            }),
            new NoopChatClientFactory(),
            new AiSchemaRegistry(),
            new AiRequestLogWriter(db),
            currentUser);

    private static AiGatewayRequest SampleRequest()
        => new(
            Module: "files",
            Purpose: "files.summary",
            SourceObjectType: "file",
            SourceObjectId: "f1",
            Messages: [new AiMessage(AiMessageRole.User, "hi")],
            Model: null,
            SchemaName: null,
            SchemaVersion: null,
            MaxOutputTokens: 32,
            MaxAttempts: 1,
            Metadata: null);

    [Fact]
    public async Task CompleteAsync_WithCurrentUser_WritesUserIdToAuditLog()
    {
        await using var db = NewDb();
        var userId = Guid.NewGuid();
        var gateway = NewGateway(db, new FakeCurrentUser(userId));

        var result = await gateway.CompleteAsync(SampleRequest());

        Assert.Equal(AiRequestStatus.Blocked, result.Status);
        var log = await db.AiRequestLogs.SingleAsync();
        Assert.Equal(userId, log.UserId);
    }

    [Fact]
    public async Task CompleteAsync_SystemContext_UserIdStaysNull()
    {
        await using var db = NewDb();
        var gateway = NewGateway(db, new FakeCurrentUser(null));

        await gateway.CompleteAsync(SampleRequest());

        var log = await db.AiRequestLogs.SingleAsync();
        Assert.Null(log.UserId);
    }
}
