using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Mobile;

namespace Pim.UnitTests.Mobile;

internal static class MobileTestHelpers
{
    public static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public static void RegisterMobileModule()
        => new MobileModule().RegisterServices(new ServiceCollection(), new ConfigurationBuilder().Build());

    public static PimDbContext CreateDb()
    {
        RegisterMobileModule();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"mobile-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    public static ICurrentUserService CurrentUser(Guid? userId = null)
        => new StubCurrentUserService(userId ?? UserId);

    public static TimeProvider Time(DateTimeOffset utcNow)
        => new FixedTimeProvider(utcNow);

    private sealed class StubCurrentUserService : ICurrentUserService
    {
        public StubCurrentUserService(Guid userId)
        {
            UserId = userId;
        }

        public Guid? UserId { get; }
        public string? Role => "user";
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
