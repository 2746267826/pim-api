using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Pim.Infrastructure.Auth;

public class JwtService : IDisposable
{
    private readonly RSA _rsa;
    private readonly ILogger<JwtService> _logger;
    private readonly object _rsaLock = new();
    private bool _disposed;

    public JwtService(IConfiguration configuration, IHostEnvironment environment, ILogger<JwtService> logger)
    {
        _rsa = RSA.Create();
        _logger = logger;

        var keyPath = configuration["Jwt:PrivateKeyPath"];
        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            _rsa.ImportFromPem(File.ReadAllText(keyPath));
        }
        else if (environment.IsDevelopment())
        {
            _logger.LogWarning(
                "JWT private key file not found at '{KeyPath}'. Using ephemeral in-memory RSA key. "
                + "All tokens will be invalidated on application restart. "
                + "Set Jwt:PrivateKeyPath in configuration for production environments.",
                keyPath);
        }
        else
        {
            throw new InvalidOperationException(
                $"JWT private key file not found at '{keyPath}'. "
                + "Set Jwt:PrivateKeyPath in configuration to a valid PEM file path.");
        }
    }

    public string GenerateAccessToken(Guid userId, string username, string role)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be null or whitespace.", nameof(username));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Role cannot be null or whitespace.", nameof(role));

        SigningCredentials credentials;
        lock (_rsaLock)
        {
            credentials = new SigningCredentials(
                new RsaSecurityKey(_rsa),
                SecurityAlgorithms.RsaSha256
            );
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: "pim",
            audience: "pim-client",
            claims: claims,
            expires: DateTimeOffset.UtcNow.AddMinutes(15).UtcDateTime,
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    public TokenValidationParameters GetValidationParameters()
    {
        RsaSecurityKey signingKey;
        lock (_rsaLock)
        {
            signingKey = new RsaSecurityKey(_rsa);
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "pim",
            ValidateAudience = true,
            ValidAudience = "pim-client",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _rsa?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
