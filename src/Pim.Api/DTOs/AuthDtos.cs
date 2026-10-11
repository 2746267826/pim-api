using System.ComponentModel.DataAnnotations;

namespace Pim.Api.DTOs;

public record RegisterRequest(
    [property:Required][property:MaxLength(50)] string Username,
    [property:Required][property:MaxLength(255)][property:EmailAddress] string Email,
    [property:Required][property:MinLength(8)][property:MaxLength(100)] string Password,
    [property:MaxLength(100)] string? DisplayName
);

public record LoginRequest(
    [property:Required] string Username,
    [property:Required] string Password
);

public record RefreshRequest(
    [property:Required] string RefreshToken
);

public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    UserInfo User
);

public record UserInfo(
    Guid Id,
    string Username,
    string DisplayName,
    string Role
);
