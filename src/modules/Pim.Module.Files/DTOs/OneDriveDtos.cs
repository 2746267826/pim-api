using Pim.Module.Files.Services;

namespace Pim.Module.Files.DTOs;

public sealed record StartOneDriveBindingRequest(string ClientId);

public sealed record OneDriveBindingStartDto(Guid ProviderId, string UserCode, string VerificationUri, int ExpiresIn)
{
    public static OneDriveBindingStartDto From(OneDriveBindingStartResult result)
        => new(result.ProviderId, result.UserCode, result.VerificationUri, result.ExpiresIn);
}

public sealed record OneDriveBindingStatusDto(
    string Status,
    string? DriveId,
    string? AccountId,
    string? AccountName,
    string? UserCode,
    string? VerificationUri,
    DateTimeOffset? DeviceCodeExpiresAt)
{
    public static OneDriveBindingStatusDto From(OneDriveBindingStatusResult result)
        => new(result.Status, result.DriveId, result.AccountId, result.AccountName,
            result.UserCode, result.VerificationUri, result.DeviceCodeExpiresAt);
}

public sealed record OneDriveSyncResultDto(int PagesProcessed, int ItemsApplied, int ItemsDeleted, bool FullRecrawl)
{
    public static OneDriveSyncResultDto From(OneDriveSyncResult result)
        => new(result.PagesProcessed, result.ItemsApplied, result.ItemsDeleted, result.FullRecrawl);
}
