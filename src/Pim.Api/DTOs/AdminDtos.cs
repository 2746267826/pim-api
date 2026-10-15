using System.ComponentModel.DataAnnotations;

namespace Pim.Api.DTOs;

public record UpdateUserRoleRequest(
    [property:Required][property:MaxLength(20)] string Role
);

public record UpdateUserStatusRequest(
    [property:Required] bool IsActive
);

/// <summary>
/// 管理员建号（REQ-3）。校验口径与公开注册完全一致（40001–40008）。
/// <c>Role</c> 缺省或为空时按 <c>user</c> 处理；只接受 <c>admin</c> / <c>user</c>，
/// 其他值返回 400 / 40041。
/// </summary>
public record CreateUserRequest(
    [property:Required][property:MaxLength(50)] string Username,
    [property:Required][property:MaxLength(255)][property:EmailAddress] string Email,
    [property:MaxLength(100)] string? DisplayName,
    [property:Required][property:MinLength(8)][property:MaxLength(100)] string Password,
    // 与 UpdateUserRoleRequest.Role 对齐；取值本身由服务端枚举校验（40041）兜底
    [property:MaxLength(20)] string? Role
);

/// <summary>管理员重置密码（REQ-4）：新密码沿用注册的长度口径（8–100）。</summary>
public record ResetPasswordRequest(
    [property:Required][property:MinLength(8)][property:MaxLength(100)] string NewPassword
);
