namespace Pim.Module.Calendar.DTOs;

public sealed record OutlookAuthorizationSessionResponse(
    Guid Id,
    string Status,
    string? VerificationUri,
    string? UserCode,
    DateTimeOffset? ExpiresAt,
    string? AccountDisplayName,
    string? AccountLoginHint,
    string? ErrorCode,
    string? ErrorMessage,
    string? RecoveryAction);
