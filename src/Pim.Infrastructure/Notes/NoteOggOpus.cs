using System.Buffers.Binary;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Streaming Ogg Opus validation. It verifies page CRC-32, page sequence and stream identity,
/// OpusHead/OpusTags, mono mapping, EOS and exact 16 kHz sample coverage without decoding audio.
/// Decoding remains a separate provider-free gate before ASR or multimodal sending.
/// </summary>
internal static class NoteOggOpus
{
    // Ogg's maximum page is 27 header bytes + 255 lacing values + 255 * 255 body bytes.
    internal const int MaxPageBytes = 27 + 255 + 255 * 255;

    internal static void Validate(ReadOnlySpan<byte> bytes, long expectedSamples)
    {
        var validator = new Validator();
        validator.Write(bytes);
        validator.Complete(expectedSamples);
    }

    internal sealed class Validator
    {
        private const int HeaderBytes = 27;
        private readonly byte[] _pending = new byte[MaxPageBytes];
        private int _pendingLength;
        private int _pageIndex;
        private uint _serial;
        private uint _sequence;
        private ushort _preSkip;
        private ulong _lastGranule;
        private bool _sawHead;
        private bool _sawTags;
        private bool _sawAudio;
        private bool _sawEos;
        private bool _tagsContinuation;
        private bool _complete;

        internal void Write(ReadOnlySpan<byte> bytes)
        {
            if (_complete) throw Invalid("Ogg Opus 录音已结束，不能再追加数据");
            while (bytes.Length > 0)
            {
                if (_pendingLength == _pending.Length)
                    throw Invalid("Ogg Opus 录音包含超长或不完整的页");
                var take = Math.Min(_pending.Length - _pendingLength, bytes.Length);
                bytes[..take].CopyTo(_pending.AsSpan(_pendingLength));
                _pendingLength += take;
                bytes = bytes[take..];
                ProcessPages();
            }
        }

        internal void Complete(long expectedSamples)
        {
            if (_complete) throw Invalid("Ogg Opus 录音已结束");
            ProcessPages();
            if (_pendingLength != 0) throw Invalid("Ogg Opus 录音末尾不完整");
            _complete = true;
            if (!_sawHead || !_sawTags || !_sawAudio || !_sawEos || _tagsContinuation)
                throw Invalid("Ogg Opus 录音缺少起始页、OpusTags、音频页或结束页");
            if (expectedSamples <= 0) throw Invalid("Ogg Opus 录音的样本数无效");
            if (_lastGranule == ulong.MaxValue || _lastGranule < _preSkip)
                throw Invalid("Ogg Opus 录音的样本数无效");
            var sourceSamples = _lastGranule - _preSkip;
            if (sourceSamples % 3 != 0 || sourceSamples / 3 != (ulong)expectedSamples)
                throw Invalid($"Ogg Opus 录音包含 {sourceSamples / 3} 个样本，与材料清单声明的 {expectedSamples} 个不一致");
        }

        private void ProcessPages()
        {
            var offset = 0;
            while (_pendingLength - offset >= HeaderBytes)
            {
                var bytes = _pending.AsSpan(offset, _pendingLength - offset);
                if (!bytes[..4].SequenceEqual("OggS"u8) || bytes[4] != 0)
                    throw Invalid("Ogg Opus 录音页头无效");
                var segmentCount = bytes[26];
                var headerLength = HeaderBytes + segmentCount;
                if (bytes.Length < headerLength) break;
                var bodyLength = 0;
                for (var index = 0; index < segmentCount; index++)
                    bodyLength += bytes[HeaderBytes + index];
                var totalLength = headerLength + bodyLength;
                if (bytes.Length < totalLength) break;
                ValidatePage(bytes[..totalLength], segmentCount, headerLength, bodyLength);
                offset += totalLength;
            }
            if (offset > 0)
            {
                _pendingLength -= offset;
                if (_pendingLength > 0)
                    _pending.AsSpan(offset, _pendingLength).CopyTo(_pending);
            }
        }

        private void ValidatePage(ReadOnlySpan<byte> page, int segmentCount, int headerLength,
            int bodyLength)
        {
            var headerType = page[5];
            if ((headerType & ~0x07) != 0) throw Invalid("Ogg Opus 录音页类型无效");
            var granule = BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(6, 8));
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(14, 4));
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(18, 4));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(22, 4));
            if (Crc32(page) != checksum) throw Invalid("Ogg Opus 录音页校验和无效");
            if (_pageIndex == 0)
            {
                if ((headerType & 0x02) == 0 || (headerType & 0x01) != 0 || sequence != 0)
                    throw Invalid("Ogg Opus 录音头无效或缺少起始页");
                _serial = serial;
            }
            else
            {
                if (_sawEos) throw Invalid("Ogg Opus 录音结束页后仍有数据");
                if ((headerType & 0x02) != 0) throw Invalid("Ogg Opus 录音包含多个起始页");
                if (serial != _serial) throw Invalid("Ogg Opus 录音包含多个流或多个 serial");
                if (sequence != unchecked(_sequence + 1))
                    throw Invalid("Ogg Opus 录音页序号不连续");
                if (_pageIndex == 1 && (headerType & 0x01) != 0)
                    throw Invalid("Ogg Opus 录音 OpusTags 页不完整");
                if (_tagsContinuation && (headerType & 0x01) == 0)
                    throw Invalid("Ogg Opus 录音 OpusTags 页缺少连续标记");
            }
            _sequence = sequence;
            _lastGranule = granule;
            var body = page[headerLength..];
            if (_pageIndex == 0)
            {
                var headLength = CompletePacketLength(page.Slice(HeaderBytes, segmentCount));
                if (headLength <= 0 || headLength != bodyLength)
                    throw Invalid("Ogg Opus 录音的 OpusHead 必须独占起始页");
                ValidateOpusHead(body);
                _sawHead = true;
            }
            else if (_pageIndex == 1)
            {
                if (body.Length < 8 || !body[..8].SequenceEqual("OpusTags"u8))
                    throw Invalid("Ogg Opus 录音缺少 OpusTags");
                var tagsLength = CompletePacketLength(page.Slice(HeaderBytes, segmentCount));
                if (tagsLength > 0)
                {
                    ValidateOpusTags(body[..tagsLength]);
                    if (tagsLength < bodyLength) _sawAudio = true;
                }
                else
                {
                    _tagsContinuation = true;
                }
                _sawTags = true;
            }
            else if (_tagsContinuation)
            {
                var tagsLength = CompletePacketLength(page.Slice(HeaderBytes, segmentCount));
                if (tagsLength > 0)
                {
                    _tagsContinuation = false;
                    if (tagsLength < bodyLength) _sawAudio = true;
                }
            }
            else if (bodyLength > 0)
            {
                _sawAudio = true;
            }
            if ((headerType & 0x04) != 0) _sawEos = true;
            _pageIndex++;
        }

        private void ValidateOpusHead(ReadOnlySpan<byte> head)
        {
            if (head.Length < 19 || !head[..8].SequenceEqual("OpusHead"u8) || head[8] != 1 ||
                head[9] != 1)
                throw Invalid("Ogg 容器必须包含单声道 Opus 录音");
            _preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.Slice(10, 2));
            var family = head[18];
            if (family == 0)
            {
                if (head.Length != 19)
                    throw Invalid("Ogg Opus 录音的 Mapping Family 0 头部长度无效");
                return;
            }
            if (family == 1)
            {
                if (head.Length != 23 || head[19] != 1 || head[20] != 0 || head[21] != 1 ||
                    head[22] != 0)
                    throw Invalid("Ogg Opus 录音的单声道 Mapping Family 1 头部无效");
                return;
            }
            throw Invalid("Ogg Opus 录音的 Mapping Family 不受支持");
        }

        private static void ValidateOpusTags(ReadOnlySpan<byte> tags)
        {
            if (tags.Length < 16) throw Invalid("Ogg Opus 录音的 OpusTags 不完整");
            var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(tags.Slice(8, 4));
            if (vendorLength > (ulong)tags.Length - 16)
                throw Invalid("Ogg Opus 录音的 OpusTags 厂商字段无效");
            var commentCount = BinaryPrimitives.ReadUInt32LittleEndian(
                tags.Slice(12 + (int)vendorLength, 4));
            var position = 16 + (int)vendorLength;
            for (var index = 0UL; index < commentCount; index++)
            {
                if ((ulong)position + 4 > (ulong)tags.Length)
                    throw Invalid("Ogg Opus 录音的 OpusTags 注释表不完整");
                var length = BinaryPrimitives.ReadUInt32LittleEndian(tags.Slice(position, 4));
                position += 4;
                if (length > (ulong)tags.Length - (ulong)position)
                    throw Invalid("Ogg Opus 录音的 OpusTags 注释表不完整");
                position += (int)length;
            }
        }

        private static int CompletePacketLength(ReadOnlySpan<byte> lacing)
        {
            var length = 0;
            foreach (var value in lacing)
            {
                length += value;
                if (value < 255) return length;
            }
            return -1;
        }

        private static uint Crc32(ReadOnlySpan<byte> page)
        {
            var crc = 0u;
            for (var index = 0; index < page.Length; index++)
            {
                var value = index is >= 22 and < 26 ? (byte)0 : page[index];
                crc = (crc << 8) ^ CrcTable[((crc >> 24) ^ value) & 0xFF];
            }
            return crc;
        }

        private static readonly uint[] CrcTable = CreateCrcTable();

        private static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (var index = 0; index < table.Length; index++)
            {
                var value = (uint)index << 24;
                for (var bit = 0; bit < 8; bit++)
                    value = (value & 0x80000000) != 0
                        ? (value << 1) ^ 0x04C11DB7
                        : value << 1;
                table[index] = value;
            }
            return table;
        }
    }

    private static DomainException Invalid(string message) => new(40001, message);
}
