using Pim.Api.Infrastructure.Ops;
using Xunit;

public class OpsKeyValidatorTests
{
    [Theory]
    [InlineData(null, "k1", false)]
    [InlineData("", "k1", false)]
    [InlineData("k1", "k1", true)]
    [InlineData(" k1 ", "k1", true)]
    [InlineData("k2", "k1,k2", true)]
    [InlineData("K1", "k1", false)]
    public void Validate_ReturnsExpected(string? provided, string configured, bool expected)
    {
        var v = new OpsKeyValidator(configured);
        Assert.Equal(expected, v.IsValid(provided));
    }

    [Fact]
    public void NoCidrConfig_AllowsAllIps_Conceptually()
    {
        // CIDR 已移除，无 IP 白名单；仅密钥校验生效
        var v = new OpsKeyValidator("k1");
        Assert.True(v.IsValid("k1"));
        Assert.False(v.IsValid("wrong"));
    }

    [Fact]
    public void HasKeys_False_WhenEmpty()
    {
        var v = new OpsKeyValidator(null);
        Assert.False(v.HasKeys);
        Assert.False(v.IsValid("k1"));
    }
}
