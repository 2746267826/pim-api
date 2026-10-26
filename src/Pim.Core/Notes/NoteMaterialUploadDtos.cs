using System.ComponentModel.DataAnnotations;

namespace Pim.Core.Notes;

public sealed record BeginNoteMaterialUploadRequest(
    Guid VersionId,
    Guid NoteId,
    Guid? BaseVersionId,
    [property: Required, MaxLength(8_388_608)] string InputJson,
    [property: Required, MaxLength(8_388_608)] string ManifestJson,
    bool StorageConsent);

public sealed record NoteMaterialUploadLimitsDto(
    int PartSizeBytes,
    int MaxMetadataCharacters,
    long MaxVersionBytes,
    long MaxUserReservedBytes,
    int MaxFiles,
    int MaxPendingVersions,
    int MaxVersionsPerUser,
    int UploadExpiryHours);

public sealed record NoteMaterialUploadFileDto(
    int Index,
    string Path,
    string Kind,
    string Sha256,
    long SizeBytes,
    int PartCount,
    IReadOnlyList<int> UploadedParts);

public sealed record NoteMaterialUploadDto(
    Guid VersionId,
    Guid NoteId,
    Guid? BaseVersionId,
    Guid? CurrentVersionId,
    string Phase,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent,
    bool HasConflict,
    NoteMaterialUploadLimitsDto Limits,
    IReadOnlyList<NoteMaterialUploadFileDto> Files);

public sealed record NoteMaterialVersionDto(
    NoteMaterialUploadDto Upload,
    string InputJson,
    string ManifestJson);

public sealed record PromoteNoteMaterialVersionRequest(Guid? ExpectedCurrentVersionId);
