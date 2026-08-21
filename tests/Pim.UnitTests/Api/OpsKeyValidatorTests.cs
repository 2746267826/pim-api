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
        var v = new OpsKeyValidator(configured, null);
        Assert.Equal(expected, v.IsValid(provided));
    }

    [Fact]
    public void Cidr_Denied_WhenNotInRange()
    {
        var v = new OpsKeyValidator("k1", "10.0.0.0/8");
        Assert.False(v.IsIpAllowed("192.168.1.1"));
        Assert.True(v.IsIpAllowed("10.1.2.3"));
    }

    [Fact]
    public void Cidr_Empty_AllowsAll()
    {
        var v = new OpsKeyValidator("k1", null);
        Assert.True(v.IsIpAllowed("192.168.1.1"));
        Assert.True(v.IsIpAllowed("10.1.2.3"));
    }

    [Fact]
    public void HasKeys_False_WhenEmpty()
    {
        var v = new OpsKeyValidator(null, null);
        Assert.False(v.HasKeys);
        Assert.False(v.IsValid("k1"));
    }
}
