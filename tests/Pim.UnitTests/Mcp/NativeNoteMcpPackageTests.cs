using System.IO.Compression;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Bounds for the base64 ZIP package parser. These run without a database so the limit checks
/// can be exercised in focused unit-test runs.
/// </summary>
public sealed class NativeNoteMcpPackageTests
{
    [Fact]
    public void ParsePackage_RejectsEntryCountAboveHardLimit()
    {
        var zip = BuildZipWithEntries(NativeNoteMcpService.MaxPackageEntries + 1);

        var error = Assert.Throws<DomainException>(() =>
            NativeNoteMcpService.ParsePackage(Guid.NewGuid(), Convert.ToBase64String(zip),
                Guid.NewGuid()));

        Assert.Equal(41301, error.ErrorCode);
        Assert.Contains("条目数", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePackage_ChecksCountBeforeMetadataAndAcceptsTheLimit()
    {
        var zip = BuildZipWithEntries(NativeNoteMcpService.MaxPackageEntries);

        var error = Assert.Throws<DomainException>(() =>
            NativeNoteMcpService.ParsePackage(Guid.NewGuid(), Convert.ToBase64String(zip),
                Guid.NewGuid()));

        Assert.Equal(40001, error.ErrorCode);
        Assert.Contains("material-version.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePackage_RejectsDeclaredUncompressedSizeAboveLimit()
    {
        var zip = BuildZipWithDeclaredBytes(NativeNoteMcpService.MaxPackageBytes + 1);

        var error = Assert.Throws<DomainException>(() =>
            NativeNoteMcpService.ParsePackage(Guid.NewGuid(), Convert.ToBase64String(zip),
                Guid.NewGuid()));

        Assert.Equal(41301, error.ErrorCode);
        Assert.Contains("64 MB", error.Message, StringComparison.Ordinal);
    }

    private static byte[] BuildZipWithEntries(int count)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < count; index++)
                archive.CreateEntry($"material-{index}.txt");
        }
        return output.ToArray();
    }

    private static byte[] BuildZipWithDeclaredBytes(long length)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("huge.bin", CompressionLevel.SmallestSize);
            using var stream = entry.Open();
            var chunk = new byte[1024 * 1024];
            while (length > 0)
            {
                var count = (int)Math.Min(chunk.Length, length);
                stream.Write(chunk, 0, count);
                length -= count;
            }
        }
        return output.ToArray();
    }
}
