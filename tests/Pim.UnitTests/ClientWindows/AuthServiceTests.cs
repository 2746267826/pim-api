using System.Net;
using Pim.Client.Core;
using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class AuthServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tokenPath;

    public AuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pim-auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _tokenPath = Path.Combine(_tempDir, "token.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TryRestoreToken_WhenNoFile_ReturnsNoSavedToken()
    {
        var apiClient = new ApiClient();
        var authService = new AuthService(apiClient, _tokenPath);

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.NoSavedToken, result);
        Assert.False(authService.IsAuthenticated);
        Assert.False(authService.HasSavedToken);
    }

    [Fact]
    public async Task TryRestoreToken_WhenTokenValid_ReturnsSuccess()
    {
        var apiClient = new ApiClient();
        var authService = new AuthService(apiClient, _tokenPath);

        // Pre-create valid persisted token
        var validJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "valid-access-token",
            RefreshToken = "valid-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            UserId = "user-123",
            Username = "testuser",
            DisplayName = "Test User"
        });
        await File.WriteAllTextAsync(_tokenPath, validJson);

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.Success, result);
        Assert.True(authService.IsAuthenticated);
        Assert.True(authService.HasSavedToken);
        Assert.Equal("testuser", authService.CurrentUsername);
        Assert.Equal("user-123", authService.CurrentUserId);
    }

    [Fact]
    public async Task TryRestoreToken_WhenTokenExpiredAndNetworkFails_ReturnsPendingNetworkAndKeepsCredentials()
    {
        var apiClient = new ApiClient();
        apiClient.SetBaseUrl("http://127.0.0.1:59999");

        var authService = new AuthService(apiClient, _tokenPath);

        var expiredJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            AccessToken = "expired-access-token",
            RefreshToken = "my-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-2),
            UserId = "user-456",
            Username = "offline-user",
            DisplayName = "Offline User"
        });
        await File.WriteAllTextAsync(_tokenPath, expiredJson);

        var result = await authService.TryRestoreTokenDetailedAsync();

        Assert.Equal(TokenRestoreResult.PendingNetwork, result);
        Assert.True(authService.HasSavedToken);
        Assert.False(authService.IsAuthenticated);
        Assert.Equal("offline-user", authService.CurrentUsername);
        Assert.Equal("user-456", authService.CurrentUserId);
        Assert.True(File.Exists(_tokenPath));
    }
}
