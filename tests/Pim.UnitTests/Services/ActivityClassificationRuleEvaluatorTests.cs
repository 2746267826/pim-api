using Pim.Module.PcTracker.DTOs;
using Xunit;

namespace Pim.UnitTests.Services;

public class ActivityClassificationRuleEvaluatorTests
{
    [Fact]
    public void ActivityClassificationResult_HasFallbackDefaults()
    {
        var result = ActivityClassificationResult.Fallback();

        Assert.Equal("其他", result.CategoryName);
        Assert.Equal("#64748b", result.CategoryColor);
        Assert.Null(result.ProjectTag);
        Assert.Equal("fallback", result.Source);
        Assert.True(result.Confidence < 0.5);
    }
}
