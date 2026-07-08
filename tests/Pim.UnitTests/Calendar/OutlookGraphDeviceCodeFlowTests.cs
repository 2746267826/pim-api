using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookGraphDeviceCodeFlowTests
{
    private static readonly Guid UserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task DeviceCodeFlowStoresEncryptedTokensAndUpdatesConnectionHealth()
    {
        await using var db = CreateDb();
        var graph = new FakeMicrosoftGraphClient
        {
            DeviceCode = new DeviceCodeResult(
                "device-code",
                "USER-CODE",
                "https://www.microsoft.com/link",
                "Open link.",
                900),
            Token = new TokenResult(
                "access-token",
                "refresh-token",
                3600,
                "Calendars.ReadWrite offline_access")
        };
        var protector = new FakeSecretProtector();
        var service = CreateService(db, graph, protector);
        await service.UpdateSettingsAsync(
            UserId,
            new UpdateOutlookSettingsRequest("common", "client-id", "Calendars.ReadWrite offline_access"));

        var code = await service.CreateDeviceCodeRequestAsync(UserId);
        var result = await service.PollDeviceCodeAsync(UserId, code.DeviceCode!, CancellationToken.None);

        Assert.Equal("connected", result.Status);
        Assert.Equal("healthy", result.TokenHealth);
        Assert.Contains("Calendars.ReadWrite", result.Scopes);

        var connection = await db.Set<OutlookConnectionEntity>().SingleAsync(c => c.UserId == UserId);
        var storedAccessToken = Encoding.UTF8.GetString(connection.AccessTokenEncrypted);
        Assert.DoesNotContain("access-token", storedAccessToken);
        Assert.Equal("access-token", protector.Unprotect(storedAccessToken));
        Assert.Equal("refresh-token", protector.Unprotect(Encoding.UTF8.GetString(connection.RefreshTokenEncrypted!)));
        Assert.True(connection.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(55));
    }

    private static OutlookSyncService CreateService(
        PimDbContext db,
        IMicrosoftGraphClient graph,
        ISecretProtector protector)
        => new(
            db,
            new StubHttpClientFactory(),
            new OperationConfirmationService(db),
            new OutlookTokenService(db, protector),
            graph);

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-device-code-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
