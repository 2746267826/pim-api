using Pim.Api.Infrastructure;
using Xunit;

namespace Pim.UnitTests.Api;

public class LoggingConfigTests
{
    [Theory]
    [InlineData(null, 30)]                 // 未设置 -> 默认 30
    [InlineData("", 30)]                   // 空字符串 -> 默认 30
    [InlineData("   ", 30)]                // 空白 -> 默认 30
    [InlineData("2", 2)]                   // 正常值
    [InlineData("30", 30)]
    [InlineData("0", 30)]                  // 下限保护：0 -> 默认
    [InlineData("-1", 30)]                 // 负数 -> 默认
    [InlineData("abc", 30)]                // 非数字 -> 默认
    [InlineData("1.5", 30)]                // 非整数 -> 默认
    public void ResolveRetainedFileCount_ReturnsExpected(string? raw, int expected)
    {
        Assert.Equal(expected, LoggingConfig.ResolveRetainedFileCount(raw));
    }
}
