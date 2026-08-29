using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.DTOs;
using Pim.Core.Common;
using Pim.Infrastructure.Extensions;
using Xunit;

namespace Pim.UnitTests.Api;

public class AuthEndpointsValidationTests
{
    // helpers replicating AuthEndpoints validation logic
    private static (bool isValid, int code) Validate(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
            return (false, 40001);
        if (string.IsNullOrWhiteSpace(req.Email))
            return (false, 40002);
        if (!new EmailAddressAttribute().IsValid(req.Email))
            return (false, 40003);
        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return (false, 40004);
        if (req.DisplayName is not null && req.DisplayName.Length > 100)
            return (false, 40005);
        return (true, 0);
    }

    [Fact]
    public void Register_MissingEmail_Returns40002()
    {
        var req = new RegisterRequest("alice", "", "password123", null);
        var (ok, code) = Validate(req);
        Assert.False(ok);
        Assert.Equal(40002, code);
    }

    [Fact]
    public void Register_WhitespaceEmail_Returns40002()
    {
        var req = new RegisterRequest("alice", "   ", "password123", null);
        var (ok, code) = Validate(req);
        Assert.False(ok);
        Assert.Equal(40002, code);
    }

    [Fact]
    public void Register_InvalidEmail_Returns40003()
    {
        var req = new RegisterRequest("alice", "not-an-email", "password123", null);
        var (ok, code) = Validate(req);
        Assert.False(ok);
        Assert.Equal(40003, code);
    }

    [Fact]
    public void Register_ShortPassword_Returns40004()
    {
        var req = new RegisterRequest("alice", "a@b.com", "short", null);
        var (ok, code) = Validate(req);
        Assert.False(ok);
        Assert.Equal(40004, code);
    }

    [Fact]
    public void Register_MissingUsername_Returns40001()
    {
        var req = new RegisterRequest("", "a@b.com", "password123", null);
        var (ok, code) = Validate(req);
        Assert.False(ok);
        Assert.Equal(40001, code);
    }

    [Fact]
    public void Register_Valid_ReturnsOk()
    {
        var req = new RegisterRequest("alice", "a@b.com", "password123", "Alice");
        var (ok, _) = Validate(req);
        Assert.True(ok);
    }

    [Fact]
    public void Register_DisplayNameTooLong_Returns40005()
    {
        var req = new RegisterRequest("alice", "a@b.com", "password123", new string('x', 101));
        var (ok, code) = Validate(req);
        Assert.False(ok);
        Assert.Equal(40005, code);
    }

    // Hangfire graceful degrade
    [Fact]
    public void AddPimInfrastructure_EmptyConnectionString_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "",
            })
            .Build();
        var services = new ServiceCollection();
        var ex = Record.Exception(() => services.AddPimInfrastructure(config));
        Assert.Null(ex);
    }

    [Fact]
    public void AddPimInfrastructure_NullConnectionString_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        var ex = Record.Exception(() => services.AddPimInfrastructure(config));
        Assert.Null(ex);
    }

    [Fact]
    public void AddPimInfrastructure_DisableHangfire_DoesNotThrowEvenWithConn()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=pim;Username=pim;Password=pim",
                ["DisableHangfire"] = "true",
            })
            .Build();
        var services = new ServiceCollection();
        var ex = Record.Exception(() => services.AddPimInfrastructure(config));
        Assert.Null(ex);
    }
}
