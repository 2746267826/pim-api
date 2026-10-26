namespace Pim.Infrastructure.Notes;

public sealed class NoteMaterialUploadOptions
{
    public const int PartSizeBytes = 1_048_576;
    public const int MaxMetadataCharacters = 8_388_608;
    public string StorageRoot { get; set; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PIM", "server", "native-notes")
        : "/data/pim/native-notes";
    public long MaxVersionBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public long MaxUserReservedBytes { get; set; } = 20L * 1024 * 1024 * 1024;
    public int MaxFiles { get; set; } = 20_000;
    public int MaxPendingVersions { get; set; } = 10;
    public int MaxVersionsPerUser { get; set; } = 2_000;
    public int UploadExpiryHours { get; set; } = 168;

    public bool IsValid() => !string.IsNullOrWhiteSpace(StorageRoot) &&
        Path.IsPathFullyQualified(StorageRoot) && MaxVersionBytes is > 0 and <= 64L * 1024 * 1024 * 1024 &&
        MaxUserReservedBytes >= MaxVersionBytes && MaxFiles is >= 2 and <= 100_000 &&
        MaxPendingVersions is > 0 and <= 100 && MaxVersionsPerUser is > 0 and <= 100_000 &&
        UploadExpiryHours is > 0 and <= 720;
}
