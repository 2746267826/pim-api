using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai.Asr;

public sealed class AiAsrMaterialService(PimDbContext db, IFileAttachmentStore files, ICurrentUserService currentUser)
{
    // Bounded single-call input, not the long-recording upload/segmentation pipeline.
    public const int MaxAudioBytes = 4 * 1024 * 1024;
    public static AiAsrInput Decode(AiAsrAudioRequest? audio)
    {
        if (audio is null || string.IsNullOrWhiteSpace(audio.Name) || audio.Name.Length > 200 || audio.Name.Any(char.IsControl) ||
            audio.Format is not ("wav" or "mp3") || audio.Base64 is null || audio.Base64.Length > (MaxAudioBytes + 2) / 3 * 4)
            throw Invalid("请选择单条WAV/MP3音频；本次调用入口单文件最多4MiB");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(audio.Base64); }
        catch (FormatException) { throw Invalid("音频内容不是有效Base64"); }
        var wav = bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8);
        var mp3 = bytes.Length >= 3 && (bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) || bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0);
        if (bytes.Length is < 1 or > MaxAudioBytes || (audio.Format == "wav" ? !wav : !mp3)) throw Invalid("音频文件签名、格式或大小无效");
        var name = audio.Name.Replace('\\', '/').Split('/').Last();
        if (string.IsNullOrWhiteSpace(name)) throw Invalid("音频名称无效");
        return new(name, audio.Format, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
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
        var reference = await files.StoreAsync(owner, $"ai-calls/{row.LogId:D}/{row.Id:D}.{audio.Format}", content, audio.ContentType, audio.Bytes.Length, ct);
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
        var row = await db.AiCallAudio.AsNoTracking().SingleOrDefaultAsync(x => x.LogId == logId, ct);
        if (row?.ObjectKey is null) throw Missing();
        await using var stream = await files.OpenReadAsync(owner, new(row.ObjectKey, row.ProviderKey!, row.ProviderId), ct);
        using var content = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct); if (count == 0) break;
            if (content.Length + count > MaxAudioBytes) throw Changed();
            await content.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        var bytes = content.ToArray();
        if (bytes.Length != row.SizeBytes || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != row.Sha256) throw Changed();
        return (bytes, Mime(row.Format));
    }
    private async Task<Guid> RequireOwnerAsync(Guid id, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(40301, "请先登录调用发起账号");
        if (!await db.AiRequestLogs.AsNoTracking().AnyAsync(x => x.Id == id && x.UserId == owner, ct)) throw Missing();
        return owner;
    }
    private static string Mime(string format) => format == "wav" ? "audio/wav" : "audio/mpeg";
    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Missing() => new(40401, "音频未保存或记录不存在");
    private static DomainException Changed() => new(40901, "文件中的音频已改变，无法作为本次原始输入播放；转写记录保留");
}
