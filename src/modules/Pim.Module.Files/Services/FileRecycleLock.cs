namespace Pim.Module.Files.Services;

/// <summary>
/// Advisory-lock key shared by the direct file restore path and the unified
/// recycle-bin purge path. Serializing by provider keeps a folder purge from
/// racing a child restore while avoiding a global lock for every account.
/// </summary>
public static class FileRecycleLock
{
    public static long KeyFor(Guid userId, Guid providerId)
    {
        var userBytes = userId.ToByteArray();
        var providerBytes = providerId.ToByteArray();
        return BitConverter.ToInt64(userBytes, 0)
            ^ BitConverter.ToInt64(userBytes, 8)
            ^ BitConverter.ToInt64(providerBytes, 0)
            ^ BitConverter.ToInt64(providerBytes, 8);
    }
}
