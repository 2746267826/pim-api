using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class CategoryLegacyMapperTests
{
    [Theory]
    [InlineData("编程", "编程/折腾")]
    [InlineData("前端", "编程/折腾")]
    [InlineData("后端", "编程/折腾")]
    [InlineData("终端", "编程/折腾")]
    [InlineData("运维", "编程/折腾")]
    [InlineData("设计", "编程/折腾")]
    [InlineData("技术学习", "学习")]
    [InlineData("外语学习", "学习")]
    [InlineData("阅读", "学习")]
    [InlineData("沟通", "聊天")]
    [InlineData("即时消息", "聊天")]
    [InlineData("邮件", "聊天")]
    [InlineData("社交", "聊天")]
    [InlineData("会议", "聊天")]
    [InlineData("办公", "文档")]
    [InlineData("文件", "文档")]
    [InlineData("浏览", "文档")]
    [InlineData("单机游戏", "游戏")]
    [InlineData("网络游戏", "游戏")]
    [InlineData("娱乐", "其他")]
    [InlineData("音乐", "其他")]
    [InlineData("工作", "其他")]
    [InlineData(null, "其他")]
    [InlineData("", "其他")]
    [InlineData("编程/折腾", "编程/折腾")]
    [InlineData("学习", "学习")]
    public void MapToUnified_ReturnsExpected(string? legacy, string expected)
        => Assert.Equal(expected, CategoryLegacyMapper.MapToUnified(legacy));

    [Fact]
    public void UnifiedCategories_ContainsExactlySeven()
    {
        var names = CategoryLegacyMapper.UnifiedCategoryNames;
        var expected = new[] { "编程/折腾", "学习", "视频", "聊天", "文档", "游戏", "其他" };
        Assert.Equal(7, names.Length);
        Assert.True(names.SequenceEqual(expected), $"Expected [{string.Join(", ", expected)}] but got [{string.Join(", ", names)}]");
    }
}
