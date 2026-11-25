using System.Buffers.Binary;
using System.Text;

namespace Pim.UnitTests.Notes;

internal static class OggOpusTestData
{
    // 0.1 s, mono, 16 kHz sine encoded as Ogg Opus by FFmpeg. The final granule minus
    // pre-skip is exactly 1,600 16 kHz samples.
    internal const string TinyOpusBase64 =
        "T2dnUwACAAAAAAAAAAD3WTvRAAAAALaS03IBE09wdXNIZWFkAQE4AYA+AAAAAABPZ2dTAAAAAAAAAAAAAPdZO9EBAAAAymKEcwE9T3B1c1RhZ3MMAAAATGF2ZjYyLjMuMTAwAQAAAB0AAABlbmNvZGVyPUxhdmM2Mi4xMS4xMDAgbGlib3B1c09nZ1MABPgTAAAAAAAA91k70QIAAAC8/jk5BlgsNDAyHUiBpvLzsXG9AAACAawTWRg+ZP9rYrq27r4PgIGySHntw3HAMzw+4Lq3gqoGy5yUXZK+KTRPCk79dJc5puC04dhrz1VXUtfo+gRq5gEzgyuwxrFRMQV5R6tInNZB5/yVTU6qGAOfCY8CGpOpsivqsqn3IPjcjREgsKc2cUKEV3tyaOknaEiZwl9zLdIzOkXZqNtXjU5TFYlQeWjabWDvCunj8JEhdpsOa4eEvMQ7u7IjyTp0wWsILyBImZ1ysJO6ioODsnzV0ZDnEngGehIa1xNwwmd25QUrHqhBoRTnNbik07INHVVVAkZImUofcy3SMQ7YBziRnHv/ofyN3Rgg3m1Z+H/fHXc2+9+Uhuw6nImqJMAREvhGiD3YP0gFjIXwEebjMnv5p2Wc22oURcy0X+x1X9RwbPlg";

    internal static byte[] TinyOpus() => Convert.FromBase64String(TinyOpusBase64);

    internal static byte[] Valid(int samples = 1600, int family = 0, uint serial = 0x12345678,
        bool includeTags = true, bool includeAudio = true, bool eos = true)
    {
        var pages = new List<byte[]>
        {
            Page(0x02, serial, 0, 0, OpusHead(family))
        };
        var sequence = 1u;
        if (includeTags)
            pages.Add(Page(0, serial, sequence++, 0, OpusTags()));
        if (includeAudio)
            pages.Add(Page(eos ? (byte)0x04 : (byte)0, serial, sequence,
                312UL + (ulong)samples * 3, AudioPacket()));
        return Concat(pages);
    }

    internal static byte[] Page(byte flags, uint serial, uint sequence, ulong granule,
        byte[] body)
    {
        var lacing = new List<byte>();
        var remaining = body.Length;
        while (remaining >= 255)
        {
            lacing.Add(255);
            remaining -= 255;
        }
        lacing.Add((byte)remaining);
        var page = new byte[27 + lacing.Count + body.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = flags;
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page.AsSpan(27));
        body.CopyTo(page.AsSpan(27 + lacing.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc32(page));
        return page;
    }

    internal static byte[] OpusHead(int family = 0)
    {
        var size = family == 1 ? 23 : 19;
        var head = new byte[size];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48_000);
        head[18] = (byte)family;
        if (family == 1)
        {
            head[19] = 1;
            head[20] = 0;
            head[21] = 1;
            head[22] = 0;
        }
        return head;
    }

    internal static byte[] OpusTags(string vendor = "Lavf62.3.100")
    {
        var vendorBytes = Encoding.UTF8.GetBytes(vendor);
        var tags = new byte[16 + vendorBytes.Length];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendorBytes.Length);
        vendorBytes.CopyTo(tags.AsSpan(12));
        return tags;
    }

    internal static byte[] AudioPacket(int length = 3) => Enumerable.Repeat((byte)0x42, length).ToArray();

    internal static byte[] Concat(IEnumerable<byte[]> pages) =>
        pages.SelectMany(page => page).ToArray();

    internal static uint Crc32(byte[] page)
    {
        var crc = 0u;
        for (var index = 0; index < page.Length; index++)
        {
            var value = index is >= 22 and < 26 ? (byte)0 : page[index];
            crc = (crc << 8) ^ Table[((crc >> 24) ^ value) & 0xFF];
        }
        return crc;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
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
