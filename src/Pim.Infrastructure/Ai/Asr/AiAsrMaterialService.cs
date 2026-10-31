using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai.Asr;

public sealed class AiAsrMaterialService(PimDbContext db, IFileAttachmentStore files, ICurrentUserService currentUser,
    AiAsrPresetRegistry? presets = null)
{
    // Bounded single-call input, not the long-recording upload/segmentation pipeline.
    public const int MaxAudioBytes = 4 * 1024 * 1024;
    // Decoded/persisted URL-audio byte budget, not a guarantee on transient process peak memory.
    public const int MaxRemoteAudioBytes = 100 * 1024 * 1024;
    // Covers a 50 MiB decoded MiMo audio payload after base64 plus a finite JSON envelope.
    public const long MaxMimoRequestJsonBytes = 72L * 1024 * 1024;
    public static AiAsrInput Decode(AiAsrAudioRequest? audio, IReadOnlyCollection<string>? formats = null,
        int maxBytes = MaxAudioBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var allowed = formats is null ? ["wav", "mp3"] : formats.ToArray();
        if (audio is null || string.IsNullOrWhiteSpace(audio.Name) || audio.Name.Length > 200 || audio.Name.Any(char.IsControl) ||
            audio.Format is null || !allowed.Contains(audio.Format, StringComparer.Ordinal) || audio.Base64 is null ||
            audio.Base64.Length > 4L * ((maxBytes + 2L) / 3L))
            throw Invalid($"请选择预设支持的音频；本次调用入口单文件最多{maxBytes / (1024 * 1024)}MiB");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(audio.Base64); }
        catch (FormatException) { throw Invalid("音频内容不是有效Base64"); }
        if (bytes.Length < 1 || bytes.Length > maxBytes || !MatchesSignature(audio.Format, bytes))
            throw Invalid("音频文件签名、格式或大小无效");
        var name = audio.Name.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(name)) throw Invalid("音频名称无效");
        return new(name, audio.Format, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
    public static AiAsrInput AcceptRemote(string? name, string? format, Uri source,
        RemoteAudioDownload download, IReadOnlyCollection<string> formats)
    {
        if (name is null || string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
            throw Invalid("URL音频名称无效");
        if (format is null || !formats.Contains(format, StringComparer.Ordinal))
            throw Invalid("URL音频格式不符合所选预设");
        var bytes = download.Bytes;
        if (bytes.Length < 1 || bytes.Length > MaxRemoteAudioBytes ||
            download.Provenance.ByteCount != bytes.Length ||
            !string.Equals(download.Provenance.Sha256,
                Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(), StringComparison.Ordinal))
            throw Invalid("URL音频大小或预检摘要无效");
        var safeName = name.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(safeName)) throw Invalid("URL音频名称无效");
        return new(safeName, format, bytes.ToArray(), download.Provenance.Sha256,
            source.OriginalString, download.Provenance.Origin);
    }
    public static AiCallAudioEntity Describe(Guid logId, AiAsrInput audio) => new()
    { LogId = logId, Name = audio.Name, Format = audio.Format, SizeBytes = audio.Bytes.Length, Sha256 = audio.Sha256 };
    public async Task StoreAsync(Guid owner, AiCallAudioEntity row, AiAsrInput audio, CancellationToken ct)
    {
        if (owner != currentUser.UserId) throw new DomainException(40301, "音频只能存入当前调用账号的文件来源");
        await StoreForOwnerAsync(owner, row, audio, ct);
    }
    internal async Task StoreForOwnerAsync(Guid owner, AiCallAudioEntity row, AiAsrInput audio, CancellationToken ct)
    {
        if (!await db.AiRequestLogs.AsNoTracking().AnyAsync(x => x.Id == row.LogId && x.UserId == owner, ct)) throw Missing();
        using var content = new MemoryStream(audio.Bytes, writable: false);
        var reference = files is IBoundedFileAttachmentStore bounded
            ? await bounded.StoreAsync(owner, $"ai-calls/{row.LogId:D}/{row.Id:D}.{audio.Format}",
                content, audio.ContentType, audio.Bytes.Length, audio.Bytes.LongLength, ct)
            : await files.StoreAsync(owner, $"ai-calls/{row.LogId:D}/{row.Id:D}.{audio.Format}",
                content, audio.ContentType, audio.Bytes.Length, ct);
        row.ProviderId = reference.ProviderId; row.ProviderKey = reference.ProviderKey; row.ObjectKey = reference.ObjectKey;
        await db.SaveChangesAsync(ct);
    }
    public async Task<AiCallAudioDto?> DescribeAsync(Guid logId, CancellationToken ct)
    {
        await RequireOwnerAsync(logId, ct);
        var row = await db.AiCallAudio.AsNoTracking().SingleOrDefaultAsync(x => x.LogId == logId, ct);
        return row is null ? null : new(row.Id, row.Name, Mime(row.Format), row.SizeBytes, row.Sha256, row.ObjectKey != null);
    }
    public async Task<(byte[] Bytes, string ContentType)> ReadAsync(Guid logId, CancellationToken ct)
    {
        var owner = await RequireOwnerAsync(logId, ct);
        var stored = await (from audio in db.AiCallAudio.AsNoTracking()
            join log in db.AiRequestLogs.AsNoTracking() on audio.LogId equals log.Id
            where audio.LogId == logId && log.UserId == owner
            select new { Audio = audio, PresetId = log.Provider, log.Purpose }).SingleOrDefaultAsync(ct);
        var row = stored?.Audio;
        if (row?.ObjectKey is null) throw Missing();
        var maxBytes = ReadBudget(stored!.PresetId, stored.Purpose);
        var reference = new FileAttachmentRef(row.ObjectKey, row.ProviderKey!, row.ProviderId);
        await using var stream = files is IBoundedFileAttachmentStore bounded
            ? await bounded.OpenReadAsync(owner, reference, maxBytes, ct)
            : await files.OpenReadAsync(owner, reference, ct);
        using var content = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct); if (count == 0) break;
            if (content.Length + count > maxBytes) throw Changed();
            await content.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var bytes = content.ToArray();
        if (bytes.Length != row.SizeBytes || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != row.Sha256) throw Changed();
        return (bytes, Mime(row.Format));
    }
    private int ReadBudget(string? presetId, string? purpose)
    {
        if (purpose == "ai.asr-url-call") return MaxRemoteAudioBytes;
        if (presets is null || string.IsNullOrWhiteSpace(presetId)) return MaxAudioBytes;
        try
        {
            var max = presets.Resolve(presetId).Descriptor.MaxAudioBytes;
            return max is > 0 and <= 50 * 1024 * 1024 ? max : MaxAudioBytes;
        }
        catch (Exception error) when (error is DomainException or InvalidOperationException or KeyNotFoundException)
        {
            return MaxAudioBytes;
        }
    }
    private async Task<Guid> RequireOwnerAsync(Guid id, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(40301, "请先登录调用发起账号");
        if (!await db.AiRequestLogs.AsNoTracking().AnyAsync(x => x.Id == id && x.UserId == owner, ct)) throw Missing();
        return owner;
    }
    private static string Mime(string format) => format switch
    {
        "wav" => "audio/wav",
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "m4a" => "audio/mp4",
        "ogg" => "audio/ogg",
        _ => "application/octet-stream"
    };
    private static bool MatchesSignature(string format, byte[] bytes) => format switch
    {
        "wav" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                 bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8),
        "mp3" => bytes.Length >= 3 && (bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) ||
                 bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0),
        "flac" => bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("fLaC"u8),
        "m4a" => bytes.Length >= 12 && bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8),
        "ogg" => bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("OggS"u8),
        _ => false
    };
    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Missing() => new(40401, "音频未保存或记录不存在");
    private static DomainException Changed() => new(40901, "文件中的音频已改变，无法作为本次原始输入播放；转写记录保留");
}
