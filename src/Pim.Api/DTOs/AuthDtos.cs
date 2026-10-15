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

/// <summary>
/// 用户修改自己密码（REQ-5）。
/// <c>RefreshToken</c> 是**当前设备**持有的明文 refresh token：
/// 服务端据此识别「哪条会话保留」，其余设备一律撤销（决策记录 §3.3）。
/// 前端必须在提交时从令牌存储现取，不得缓存于组件状态（界面文案与交互要点 §4.1）。
/// </summary>
public record ChangePasswordRequest(
    [property:Required] string CurrentPassword,
    [property:Required][property:MinLength(8)][property:MaxLength(100)] string NewPassword,
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
