namespace Pim.Infrastructure.Auth;

/// <summary>一条账号凭据校验失败：错误码 + 提示语（两者都是契约的一部分）。</summary>
public readonly record struct CredentialValidationError(int Code, string Message);

/// <summary>
/// 账号凭据校验（用户名 / 邮箱 / 密码 / 显示名）。
///
/// 公开注册（<c>POST /api/v1/auth/register</c>）与管理员建号（<c>POST /api/v1/admin/users</c>）
/// 共用同一套口径与错误码：<c>40001</c>–<c>40008</c>（现状核实现 §1.1，已冻结）。
/// 抽出来的目的是**不允许两处各写一份**——一旦分叉，两条入口的校验就会漂移。
///
/// 判定顺序与原有注册实现逐条一致（先空值、再格式、再长度）：
///   40001 用户名空 / 40002 邮箱空 / 40003 邮箱格式非法 / 40004 密码 &lt; 8 /
///   40005 用户名 &gt; 50 / 40006 邮箱 &gt; 255 / 40007 密码 &gt; 100 / 40008 显示名 &gt; 100。
/// 返回首个命中的错误；<c>null</c> 表示通过。
/// </summary>
public static class UserCredentialsValidator
{
    /// <summary>密码过短（&lt; 8）的错误码与提示语（沿用注册既有口径，已冻结）。</summary>
    public const int PasswordTooShortCode = 40004;
    public const string PasswordTooShortMessage = "Password must be at least 8 characters";

    /// <summary>密码过长（&gt; 100）的错误码与提示语（沿用注册既有口径，已冻结）。</summary>
    public const int PasswordTooLongCode = 40007;
    public const string PasswordTooLongMessage = "Password must not exceed 100 characters";

    public static CredentialValidationError? Validate(
        string username, string email, string? password, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(username))
            return new CredentialValidationError(40001, "Username is required");
        if (string.IsNullOrWhiteSpace(email))
            return new CredentialValidationError(40002, "Email is required");
        if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
            return new CredentialValidationError(40003, "Invalid email format");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return new CredentialValidationError(PasswordTooShortCode, PasswordTooShortMessage);
        if (username.Length > 50)
            return new CredentialValidationError(40005, "Username must not exceed 50 characters");
        if (email.Length > 255)
            return new CredentialValidationError(40006, "Email must not exceed 255 characters");
        if (password.Length > 100)
            return new CredentialValidationError(PasswordTooLongCode, PasswordTooLongMessage);
        if (!string.IsNullOrEmpty(displayName) && displayName.Length > 100)
            return new CredentialValidationError(40008, "DisplayName must not exceed 100 characters");

        return null;
    }

    /// <summary>密码长度校验（改密码 / 重置密码用）：&lt; 8 → 40004，&gt; 100 → 40007。</summary>
    public static CredentialValidationError? ValidatePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return new CredentialValidationError(PasswordTooShortCode, PasswordTooShortMessage);
        if (password.Length > 100)
            return new CredentialValidationError(PasswordTooLongCode, PasswordTooLongMessage);

        return null;
    }
}
