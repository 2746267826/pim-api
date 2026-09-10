using System.Threading;
using Pim.Client.App.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class SingleInstanceGuardTests : IDisposable
{
    public SingleInstanceGuardTests()
    {
        SingleInstanceGuard.Release();
    }

    public void Dispose()
    {
        SingleInstanceGuard.Release();
    }

    [Fact]
    public void TryAcquire_AcquiresSuccessfully_WhenFree()
    {
        var id = Guid.NewGuid().ToString("N");
        var globalName = $"test_global_guard_{id}";
        var localName = $"test_local_guard_{id}";

        var acquired = SingleInstanceGuard.TryAcquire(globalName, localName);

        Assert.True(acquired);
    }

    [Fact]
    public void TryAcquire_ReturnsFalse_WhenAnotherInstanceHoldsMutex()
    {
        var id = Guid.NewGuid().ToString("N");
        var globalName = $"test_global_guard_{id}";
        var localName = $"test_local_guard_{id}";

        using var existingMutex = new Mutex(true, localName, out var created);
        Assert.True(created);

        var acquired = SingleInstanceGuard.TryAcquire(globalName, localName);

        Assert.False(acquired);
    }

    [Fact]
    public void Release_AllowsReacquisition()
    {
        var id = Guid.NewGuid().ToString("N");
        var globalName = $"test_global_guard_{id}";
        var localName = $"test_local_guard_{id}";

        var first = SingleInstanceGuard.TryAcquire(globalName, localName);
        Assert.True(first);

        SingleInstanceGuard.Release();

        var second = SingleInstanceGuard.TryAcquire(globalName, localName);
        Assert.True(second);
    }
}
