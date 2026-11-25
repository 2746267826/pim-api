using System.Buffers.Binary;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Streaming Ogg Opus validation. It verifies page CRC-32, page sequence and stream identity,
/// packet continuity across pages, OpusHead/OpusTags, mono mapping, EOS and exact 16 kHz sample
/// coverage without decoding audio. Decoding remains a separate provider-free gate before ASR or
/// multimodal sending.
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
        private bool _previousPageEndedWithContinuation;
        private bool _tagsOpen;
        private OpusTagsReader? _tags;
        private long _audioPacketBytes;
        private bool _complete;

        internal void Write(ReadOnlySpan<byte> bytes)
        {
            if (_complete) throw Invalid("Ogg Opus 录音已结束，不能再追加数据");
            while (bytes.Length > 0)
            {
                ProcessPages();
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
            if (!_sawHead || !_sawTags || !_sawAudio || !_sawEos || _tagsOpen ||
                _previousPageEndedWithContinuation)
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
            var continuation = (headerType & 0x01) != 0;
            var beginning = (headerType & 0x02) != 0;
            var eos = (headerType & 0x04) != 0;
            var granule = BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(6, 8));
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(14, 4));
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(18, 4));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(22, 4));
            if (Crc32(page) != checksum) throw Invalid("Ogg Opus 录音页校验和无效");
            if (_pageIndex == 0)
            {
                if (!beginning || continuation || sequence != 0)
                    throw Invalid("Ogg Opus 录音头无效或缺少起始页");
                if (granule != 0)
                    throw Invalid("Ogg Opus 录音的 OpusHead 页 granule 必须为零");
                _serial = serial;
            }
            else
            {
                if (_sawEos) throw Invalid("Ogg Opus 录音结束页后仍有数据");
                if (beginning) throw Invalid("Ogg Opus 录音包含多个起始页");
                if (serial != _serial) throw Invalid("Ogg Opus 录音包含多个流或多个 serial");
                if (sequence != unchecked(_sequence + 1))
                    throw Invalid("Ogg Opus 录音页序号不连续");
                if (continuation != _previousPageEndedWithContinuation)
                    throw Invalid(continuation
                        ? "Ogg Opus 录音页标记了不存在的跨页数据包"
                        : "Ogg Opus 录音跨页数据包缺少连续标记");
            }
            _sequence = sequence;
            var body = page[headerLength..];
            var completedAudioPackets = 0;
            var tagsCompletedOnPage = false;
            if (_pageIndex == 0)
            {
                if (segmentCount != 1 || page[HeaderBytes] >= 255 ||
                    page[HeaderBytes] != bodyLength)
                    throw Invalid("Ogg Opus 录音的 OpusHead 必须独占起始页");
                ValidateOpusHead(body);
                _sawHead = true;
                _previousPageEndedWithContinuation = false;
            }
            else if (segmentCount == 0)
            {
                // Zero-segment pages may separate complete packets, but the mandatory header
                // pages themselves must contain data. A page cannot introduce OpusTags later.
                if (!_sawTags)
                    throw Invalid("Ogg Opus 录音的 OpusTags 必须从第二页开始");
            }
            else
            {
                var lacing = page.Slice(HeaderBytes, segmentCount).ToArray();
                var packetIsTags = !_sawTags;
                if (continuation)
                {
                    if (!_sawTags && !_tagsOpen)
                        throw Invalid("Ogg Opus 录音页延续了不存在的 OpusTags 数据包");
                }
                else if (!_sawTags)
                {
                    if (_pageIndex != 1)
                        throw Invalid("Ogg Opus 录音的 OpusTags 必须从第二页开始");
                    _tagsOpen = true;
                    _tags ??= new OpusTagsReader();
                }
                else
                {
                    if (!continuation) StartAudioPacket();
                }

                var bodyOffset = 0;
                for (var index = 0; index < segmentCount; index++)
                {
                    var length = lacing[index];
                    if (index > 0 && lacing[index - 1] < 255)
                    {
                        if (!_sawTags)
                            throw Invalid("Ogg Opus 录音的 OpusTags 数据包必须结束其所在页");
                        StartAudioPacket();
                    }
                    var segment = body.Slice(bodyOffset, length);
                    if (packetIsTags)
                    {
                        var tags = _tags!;
                        tags.Write(segment);
                        if (length < 255)
                        {
                            tags.Complete();
                            _tagsOpen = false;
                            _sawTags = true;
                            tagsCompletedOnPage = true;
                            if (index != segmentCount - 1)
                                throw Invalid("Ogg Opus 录音的 OpusTags 数据包必须结束其所在页");
                        }
                    }
                    else
                    {
                        _audioPacketBytes = checked(_audioPacketBytes + length);
                        if (length < 255)
                        {
                            if (_audioPacketBytes <= 0)
                                throw Invalid("Ogg Opus 录音包含空音频数据包");
                            _sawAudio = true;
                            _audioPacketBytes = 0;
                            completedAudioPackets++;
                        }
                    }
                    bodyOffset += length;
                }

                if (!_sawTags && granule != ulong.MaxValue)
                    throw Invalid("Ogg Opus 录音未完成的 OpusTags 页 granule 必须为 -1");
                if (tagsCompletedOnPage && granule != 0)
                    throw Invalid("Ogg Opus 录音的 OpusTags 完成页 granule 必须为零");
                if (!tagsCompletedOnPage)
                {
                    if (completedAudioPackets == 0 && granule != ulong.MaxValue)
                        throw Invalid("Ogg Opus 录音未完成的音频页 granule 必须为 -1");
                    if (completedAudioPackets > 0 && granule == ulong.MaxValue)
                        throw Invalid("Ogg Opus 录音音频页缺少可用的 granule");
                }
                if (completedAudioPackets > 0) _lastGranule = granule;

                void StartAudioPacket()
                {
                    _audioPacketBytes = 0;
                    packetIsTags = false;
                }
            }

            var pageEndsWithContinuation = segmentCount > 0
                ? page[HeaderBytes + segmentCount - 1] == 255
                : _previousPageEndedWithContinuation;
            if (eos && pageEndsWithContinuation)
                throw Invalid("Ogg Opus 录音结束页包含未完成的数据包");
            if (eos && _tagsOpen)
                throw Invalid("Ogg Opus 录音结束页前 OpusTags 数据包不完整");
            _previousPageEndedWithContinuation = pageEndsWithContinuation;
            if (eos) _sawEos = true;
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
                // Mapping Family 1 for mono has three mapping bytes: stream count, coupled
                // stream count and one channel mapping value. The mapping value 0 is mono;
                // the packet is 22 bytes, not 23.
                if (head.Length != 22 || head[19] != 1 || head[20] != 0 || head[21] != 0)
                    throw Invalid("Ogg Opus 录音的单声道 Mapping Family 1 头部无效");
                return;
            }
            throw Invalid("Ogg Opus 录音的 Mapping Family 不受支持");
        }

        private sealed class OpusTagsReader
        {
            private enum Stage
            {
                Magic, VendorLength, Vendor, CommentCount, CommentLength, Comment, Done
            }

            private readonly byte[] _word = new byte[8];
            private int _wordLength;
            private Stage _stage = Stage.Magic;
            private uint _remaining;
            private ulong _commentsRemaining;

            internal void Write(ReadOnlySpan<byte> bytes)
            {
                while (true)
                {
                    switch (_stage)
                    {
                        case Stage.Magic:
                            if (!ReadMagic(ref bytes)) return;
                            _stage = Stage.VendorLength;
                            break;
                        case Stage.VendorLength:
                            if (!ReadUInt32(ref bytes, out var vendorLength)) return;
                            _remaining = vendorLength;
                            _stage = vendorLength == 0 ? Stage.CommentCount : Stage.Vendor;
                            break;
                        case Stage.Vendor:
                            if (!Consume(ref bytes, ref _remaining)) return;
                            _stage = Stage.CommentCount;
                            break;
                        case Stage.CommentCount:
                            if (!ReadUInt32(ref bytes, out var commentCount)) return;
                            _commentsRemaining = commentCount;
                            _stage = commentCount == 0 ? Stage.Done : Stage.CommentLength;
                            break;
                        case Stage.CommentLength:
                            if (!ReadUInt32(ref bytes, out var commentLength)) return;
                            _remaining = commentLength;
                            if (commentLength == 0)
                            {
                                CompleteComment();
                            }
                            else
                            {
                                _stage = Stage.Comment;
                            }
                            break;
                        case Stage.Comment:
                            if (!Consume(ref bytes, ref _remaining)) return;
                            CompleteComment();
                            break;
                        case Stage.Done:
                            if (bytes.Length > 0)
                                throw Invalid("Ogg Opus 录音的 OpusTags 注释表后有多余数据");
                            return;
                        default:
                            throw Invalid("Ogg Opus 录音的 OpusTags 状态无效");
                    }
                }
            }

            internal void Complete()
            {
                if (_stage != Stage.Done)
                    throw Invalid("Ogg Opus 录音的 OpusTags 厂商或注释长度超出数据包边界");
            }

            private void CompleteComment()
            {
                if (_commentsRemaining == 0)
                    throw Invalid("Ogg Opus 录音的 OpusTags 注释表不完整");
                _commentsRemaining--;
                _stage = _commentsRemaining == 0 ? Stage.Done : Stage.CommentLength;
            }

            private bool ReadMagic(ref ReadOnlySpan<byte> bytes)
            {
                while (bytes.Length > 0)
                {
                    var take = Math.Min(8 - _wordLength, bytes.Length);
                    bytes[..take].CopyTo(_word.AsSpan(_wordLength));
                    _wordLength += take;
                    bytes = bytes[take..];
                    if (_wordLength == 8)
                    {
                        if (!_word.AsSpan().SequenceEqual("OpusTags"u8))
                            throw Invalid("Ogg Opus 录音缺少 OpusTags");
                        _wordLength = 0;
                        return true;
                    }
                }
                return false;
            }

            private bool ReadUInt32(ref ReadOnlySpan<byte> bytes, out uint value)
            {
                var take = Math.Min(4 - _wordLength, bytes.Length);
                bytes[..take].CopyTo(_word.AsSpan(_wordLength));
                _wordLength += take;
                bytes = bytes[take..];
                if (_wordLength < 4)
                {
                    value = 0;
                    return false;
                }
                value = BinaryPrimitives.ReadUInt32LittleEndian(_word.AsSpan(0, 4));
                _wordLength = 0;
                return true;
            }

            private static bool Consume(ref ReadOnlySpan<byte> bytes, ref uint remaining)
            {
                var take = (int)Math.Min((uint)bytes.Length, remaining);
                bytes = bytes[take..];
                remaining -= (uint)take;
                return remaining == 0;
            }
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
