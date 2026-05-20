using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class AwBucketSelectionTests
{
    [Fact]
    public void IsSupportedUploadBucket_IncludesWindowAfkAndBrowserPages()
    {
        Assert.True(AwBucketSelection.IsSupportedUploadBucket("aw-watcher-window_DESKTOP", "currentwindow", "aw-watcher-window"));
        Assert.True(AwBucketSelection.IsSupportedUploadBucket("aw-watcher-afk_DESKTOP", "afkstatus", "aw-watcher-afk"));
        Assert.True(AwBucketSelection.IsSupportedUploadBucket("aw-watcher-web-edge_DESKTOP", "web.tab.current", "aw-client-web"));
    }

    [Fact]
    public void IsSupportedUploadBucket_ExcludesInputBuckets()
    {
        Assert.False(AwBucketSelection.IsSupportedUploadBucket("aw-watcher-input_DESKTOP", "os.hid.input", "aw-watcher-input"));
        Assert.False(AwBucketSelection.IsSupportedUploadBucket("aw-watcher-input_DESKTOP", "currentwindow", "aw-watcher-input"));
    }

    [Fact]
    public void DescribeBucketKind_ReturnsStableLogLabels()
    {
        Assert.Equal("window", AwBucketSelection.DescribeBucketKind("currentwindow"));
        Assert.Equal("afk", AwBucketSelection.DescribeBucketKind("afkstatus"));
        Assert.Equal("web", AwBucketSelection.DescribeBucketKind("web.tab.current"));
        Assert.Equal("unknown", AwBucketSelection.DescribeBucketKind("other"));
    }
}
