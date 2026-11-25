using System.Buffers.Binary;
using System.Text.Json;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Notes;

internal enum NoteAudioFormat
{
    Wav,
    Opus
}

internal static class NoteAudioFormats
{
    internal const long MaxOpusFileBytes = 64L * 1024 * 1024;
    internal const long MaxSamplesPerChunk = 172_800_000;

    internal static NoteAudioFormat Parse(JsonElement chunk)
    {
        var fromFormat = ParseFormat(chunk);
        var fromMime = ParseMimeType(chunk);
        if (fromFormat is not null && fromMime is not null && fromFormat != fromMime)
            throw Invalid("录音格式与 MIME 类型不一致");
        return fromFormat ?? fromMime ?? NoteAudioFormat.Wav;
    }

    internal static string Extension(NoteAudioFormat format) => format switch
    {
        NoteAudioFormat.Wav => ".wav",
        NoteAudioFormat.Opus => ".opus",
        _ => throw Invalid("录音格式不受支持")
    };

    internal static string MimeType(NoteAudioFormat format) => format switch
    {
        NoteAudioFormat.Wav => "audio/wav",
        NoteAudioFormat.Opus => "audio/ogg",
        _ => throw Invalid("录音格式不受支持")
    };

    internal static string Path(Guid recordingId, int segment, int index, NoteAudioFormat format) =>
        $"recordings/{recordingId:D}/s{segment:D4}-c{index:D6}{Extension(format)}";

    internal static bool HasValidDeclaredSize(NoteAudioFormat format, long sizeBytes, long samples) =>
        format switch
        {
            NoteAudioFormat.Wav => sizeBytes == checked(44 + samples * 2),
            NoteAudioFormat.Opus => samples > 0 && sizeBytes is > 0 and <= MaxOpusFileBytes,
            _ => false
        };

    internal static void WriteWavHeader(Span<byte> destination, int dataLength)
    {
        if (destination.Length < 44 || dataLength < 0 || dataLength % 2 != 0)
            throw new ArgumentException("WAV header requires an even non-negative data length", nameof(dataLength));
        "RIFF"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4), checked((uint)(44 + dataLength - 8)));
        "WAVEfmt "u8.CopyTo(destination.Slice(8));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24), NoteAsrPlan.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28), NoteAsrPlan.SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(34), 16);
        "data"u8.CopyTo(destination.Slice(36));
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(40), checked((uint)dataLength));
    }

    private static NoteAudioFormat? ParseFormat(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("format", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid("录音格式必须是字符串");
        return value.GetString()?.Trim().ToLowerInvariant() switch
        {
            "wav" => NoteAudioFormat.Wav,
            "opus" => NoteAudioFormat.Opus,
            _ => throw Invalid("录音格式仅支持 WAV 或 Opus")
        };
    }

    private static NoteAudioFormat? ParseMimeType(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("mimeType", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid("录音 MIME 类型必须是字符串");
        var semicolon = value.GetString()?.IndexOf(';') ?? -1;
        var text = semicolon >= 0 ? value.GetString()![..semicolon].Trim() : value.GetString()?.Trim();
        return text?.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => NoteAudioFormat.Wav,
            "audio/ogg" or "audio/opus" => NoteAudioFormat.Opus,
            _ => throw Invalid("录音 MIME 类型仅支持 audio/wav 或 audio/ogg")
        };
    }

    private static DomainException Invalid(string message) => new(40001, message);
}
