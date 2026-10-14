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
    // 可空是事实，不是将就：users.display_name 在库里 is_nullable = YES，
    // 注册时也会走到「displayName 为空则回落用户名」以外的历史数据。
    // 契约若声明非空，严格反序列化的客户端会在登录/续期时直接失败（issue #417）。
    string? DisplayName,
    string Role
);
