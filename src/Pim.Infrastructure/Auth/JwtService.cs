using System.Diagnostics;
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
        var sw = Stopwatch.StartNew();
        _rsa = RSA.Create();
        _logger = logger;

        var keyPath = configuration["Jwt:PrivateKeyPath"];
        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            _rsa.ImportFromPem(File.ReadAllText(keyPath));
        }
        else if (environment.IsDevelopment())
        {
            var keySize = _rsa.KeySize;
            _logger.LogWarning(
                "JWT private key file not found at '{KeyPath}'. Using ephemeral in-memory RSA key ({KeySize} bits). "
                + "All tokens will be invalidated on application restart. "
                + "RSA init took {ElapsedMs}ms. "
                + "Set Jwt:PrivateKeyPath in configuration for production environments.",
                keyPath, keySize, sw.ElapsedMilliseconds);
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

        var sw = Stopwatch.StartNew();
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

        var result = new JwtSecurityTokenHandler().WriteToken(token);
        _logger.LogDebug("GenerateAccessToken took {ElapsedMs}ms", sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>
    /// Generates an access token with additional claims. Used to scope MCP-issued tokens to a
    /// specific verified tool so they cannot be reused against unrelated REST endpoints.
    /// </summary>
    public string GenerateScopedAccessToken(
        Guid userId,
        string username,
        string role,
        IReadOnlyDictionary<string, string> extraClaims,
        TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username cannot be null or whitespace.", nameof(username));
        if (string.IsNullOrWhiteSpace(role))
            throw new ArgumentException("Role cannot be null or whitespace.", nameof(role));
        if (extraClaims is null || extraClaims.Count == 0)
            throw new ArgumentException("extraClaims cannot be empty.", nameof(extraClaims));

        SigningCredentials credentials;
        lock (_rsaLock)
        {
            credentials = new SigningCredentials(
                new RsaSecurityKey(_rsa),
                SecurityAlgorithms.RsaSha256
            );
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        foreach (var (key, value) in extraClaims)
            claims.Add(new Claim(key, value));

        var token = new JwtSecurityToken(
            issuer: "pim",
            audience: "pim-client",
            claims: claims,
            expires: DateTimeOffset.UtcNow.Add(lifetime).UtcDateTime,
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
