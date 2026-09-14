using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public sealed class TrackerStateManagerTests : IDisposable
{
    private readonly string _tempFile;

    public TrackerStateManagerTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"tracker_state_test_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempFile))
                File.Delete(_tempFile);
        }
        catch { }
    }

    [Fact]
    public void LoadState_WhenFileDoesNotExist_ReturnsNull()
    {
        var manager = new TrackerStateManager(_tempFile);
        var state = manager.LoadState();
        Assert.Null(state);
    }

    [Fact]
    public void SaveAndLoadState_PersistsTimestampsAndDeviceId()
    {
        var manager = new TrackerStateManager(_tempFile);
        var now = new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero);

        manager.SaveState(now, now.AddSeconds(-10), "TestPC");

        var loaded = manager.LoadState();
        Assert.NotNull(loaded);
        Assert.Equal(now, loaded.LastPollTime);
        Assert.Equal(now.AddSeconds(-10), loaded.LastActiveTime);
        Assert.Equal("TestPC", loaded.DeviceId);
    }

    [Fact]
    public void LoadState_WhenFileCorrupted_ReturnsNullSafely()
    {
        File.WriteAllText(_tempFile, "{ corrupted json syntax");
        var manager = new TrackerStateManager(_tempFile);

        var loaded = manager.LoadState();
        Assert.Null(loaded);
    }
}
