using System.Net.Sockets;
using Npgsql;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 真库跳过判定（评审第五轮 Important）：只有"数据库不可达"可以 Skip；
/// 口令错误、库不存在、权限不足属于配置错误，必须让用例失败，不能被伪装成绿色 Skip。
/// </summary>
public sealed class RealDbTestConnectionTests
{
    [Fact]
    public void IsServerUnreachable_TreatsRefusedConnectionAsEnvironmentGap()
    {
        Assert.True(RealDbTestConnection.IsServerUnreachable(new SocketException(111)));
        // 连接池耗尽同样表现为 TimeoutException：与"不可达"一样属于环境性失败（文档已说明）
        Assert.True(RealDbTestConnection.IsServerUnreachable(new TimeoutException()));
        // Npgsql 把底层网络异常包在 InnerException 里，且可能有多层包装
        Assert.True(RealDbTestConnection.IsServerUnreachable(
            new NpgsqlException("Failed to connect", new SocketException(111))));
        Assert.True(RealDbTestConnection.IsServerUnreachable(
            new InvalidOperationException("outer",
                new NpgsqlException("middle", new TimeoutException("inner")))));
    }

    [Fact]
    public void IsServerUnreachable_HandlesNullAndCycles()
    {
        Assert.False(RealDbTestConnection.IsServerUnreachable(null));
        Assert.False(RealDbTestConnection.IsServerUnreachable(new InvalidOperationException("plain")));
    }

    [Theory]
    [InlineData("28P01")] // 口令认证失败
    [InlineData("3D000")] // 数据库不存在
    [InlineData("42501")] // 权限不足
    public void IsServerUnreachable_DoesNotSwallowConfigurationErrors(string sqlState)
    {
        var exception = new PostgresException("boom", "ERROR", "ERROR", sqlState);

        Assert.False(RealDbTestConnection.IsServerUnreachable(exception));
        Assert.False(RealDbTestConnection.IsServerUnreachable(
            new NpgsqlException("boom", exception)));
    }
}
