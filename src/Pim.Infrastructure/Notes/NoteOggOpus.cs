using System.Buffers.Binary;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Structural Ogg Opus validation. It verifies the container identity, mono stream,
/// pre-skip, EOS page and exact 16 kHz sample coverage without decoding audio bytes.
/// Decoding remains a separate provider-free gate before ASR or multimodal sending.
/// </summary>
internal static class NoteOggOpus
{
    internal const int ProbeBytes = 64 * 1024;

    internal static void Validate(ReadOnlySpan<byte> head, ReadOnlySpan<byte> tail, long fileSize,
        long expectedSamples)
    {
        if (fileSize < 27 || !TryParsePage(head, out var first) || (first.HeaderType & 0x02) == 0)
            throw Invalid("Ogg Opus 录音头无效或缺少起始页");

        var packetLength = 0;
        var completePacket = false;
        for (var index = 0; index < first.SegmentCount; index++)
        {
            var value = head[27 + index];
            packetLength += value;
            if (value < 255)
            {
                completePacket = true;
                break;
            }
        }
        if (!completePacket || packetLength < 19 ||
            first.BodyOffset + packetLength > head.Length)
            throw Invalid("Ogg Opus 录音缺少有效的 OpusHead");

        var opusHead = head.Slice(first.BodyOffset, packetLength);
        if (!opusHead[..8].SequenceEqual("OpusHead"u8) || opusHead[8] != 1 || opusHead[9] != 1)
            throw Invalid("Ogg 容器必须包含单声道 Opus 录音");
        var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(opusHead.Slice(10, 2));

        if (!TryFindLastPage(tail, fileSize, out var last) || (last.HeaderType & 0x04) == 0)
            throw Invalid("Ogg Opus 录音不完整或缺少结束页");
        if (last.Granule == ulong.MaxValue || last.Granule < preSkip)
            throw Invalid("Ogg Opus 录音的样本数无效");

        var sourceSamples = last.Granule - preSkip;
        if (sourceSamples % 3 != 0)
            throw Invalid("Ogg Opus 录音的样本数不能精确换算为 16 kHz 单声道");
        if (expectedSamples <= 0)
            throw Invalid("Ogg Opus 录音的样本数无效");
        var actualSamples = sourceSamples / 3;
        if (actualSamples != (ulong)expectedSamples)
            throw Invalid($"Ogg Opus 录音包含 {actualSamples} 个样本，与材料清单声明的 {expectedSamples} 个不一致");
    }

    private static bool TryFindLastPage(ReadOnlySpan<byte> tail, long fileSize, out PageInfo page)
    {
        page = default;
        if (fileSize < tail.Length) return false;
        var tailStart = fileSize - tail.Length;
        for (var offset = tail.Length - 4; offset >= 0; offset--)
        {
            if (!tail.Slice(offset, 4).SequenceEqual("OggS"u8) ||
                !TryParsePage(tail.Slice(offset), out var candidate))
                continue;
            if (tailStart + offset + candidate.TotalLength != fileSize) continue;
            page = candidate;
            return true;
        }
        return false;
    }

    private static bool TryParsePage(ReadOnlySpan<byte> bytes, out PageInfo page)
    {
        page = default;
        if (bytes.Length < 27 || !bytes[..4].SequenceEqual("OggS"u8) || bytes[4] != 0) return false;
        var segmentCount = bytes[26];
        var bodyOffset = 27 + segmentCount;
        if (bytes.Length < bodyOffset) return false;
        var bodyLength = 0;
        for (var index = 0; index < segmentCount; index++) bodyLength += bytes[27 + index];
        var totalLength = checked(bodyOffset + bodyLength);
        if (bytes.Length < totalLength) return false;
        page = new PageInfo(bytes[5], BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(6, 8)),
            segmentCount, bodyOffset, totalLength);
        return true;
    }

    private static DomainException Invalid(string message) => new(40001, message);

    private readonly record struct PageInfo(byte HeaderType, ulong Granule, int SegmentCount,
        int BodyOffset, int TotalLength);
}
