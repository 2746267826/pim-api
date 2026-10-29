using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Storage;
using Pim.Infrastructure.Auth;

namespace Pim.Infrastructure.Ai;

public sealed record AiVisionInput(string Name, string ContentType, byte[] Bytes, string Sha256)
{
    public override string ToString() => $"AiVisionInput {{ Name = {Name}, Bytes = {Bytes.Length}, Sha256 = {Sha256} }}";
}

public sealed class AiVisionMaterialService(PimDbContext db, IFileAttachmentStore files, ICurrentUserService currentUser)
{
    public const int MaxImages = 4;
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MaxTotalBytes = 8 * 1024 * 1024;

    public static IReadOnlyList<AiVisionInput> Decode(AiVisionCallRequest request)
    {
        if (request.ImageCount is < 1 or > MaxImages || request.Images is null || request.Images.Count != request.ImageCount)
            throw Invalid("请选择1至4张图片，图片清单须与选择数量一致");
        var result = new List<AiVisionInput>(); var total = 0;
        foreach (var image in request.Images)
        {
            if (image is null || string.IsNullOrWhiteSpace(image.Name) || image.Name.Length > 200 || image.Name.Any(char.IsControl) ||
                image.ContentType is not ("image/png" or "image/jpeg") || image.Base64 is null ||
                image.Base64.Length > (MaxImageBytes + 2) / 3 * 4)
                throw Invalid("图片名称、类型或大小无效；当前支持PNG/JPEG，单张最多4MiB");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(image.Base64); }
            catch (FormatException) { throw Invalid("图片内容不是有效Base64"); }
            if (bytes.Length is < 1 or > MaxImageBytes || ImageFileSignature.Detect(bytes) != image.ContentType)
                throw Invalid("图片文件签名与类型不符，或单张超过4MiB");
            total = checked(total + bytes.Length);
            if (total > MaxTotalBytes) throw Invalid("本次图片合计最多8MiB，请减少图片或调整文件大小");
            var name = image.Name.Replace('\\', '/').Split('/').Last();
            if (string.IsNullOrWhiteSpace(name)) throw Invalid("图片名称无效");
            result.Add(new(name, image.ContentType, bytes, Hash(bytes)));
        }
        return result;
    }

    public static List<AiCallImageEntity> Describe(Guid logId, IReadOnlyList<AiVisionInput> input) =>
        input.Select((image, index) => new AiCallImageEntity { LogId = logId, Index = index, Name = image.Name,
            ContentType = image.ContentType, SizeBytes = image.Bytes.Length, Sha256 = image.Sha256 }).ToList();

    public async Task StoreAsync(Guid owner, IReadOnlyList<AiCallImageEntity> rows, IReadOnlyList<AiVisionInput> input, CancellationToken ct)
    {
        if (owner != currentUser.UserId) throw new DomainException(40301, "图片只能存入当前调用账号的文件来源");
        await StoreForOwnerAsync(owner, rows, input, ct);
    }

    internal async Task StoreForOwnerAsync(Guid owner, IReadOnlyList<AiCallImageEntity> rows,
        IReadOnlyList<AiVisionInput> input, CancellationToken ct)
    {
        if (rows.Count != input.Count) throw Invalid("图片材料清单数量不一致");
        foreach (var logId in rows.Select(x => x.LogId).Distinct()) await RequireLogOwnerAsync(owner, logId, ct);
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index]; var image = input[index];
            using var content = new MemoryStream(image.Bytes, writable: false);
            var extension = image.ContentType == "image/png" ? "png" : "jpg";
            var reference = await files.StoreAsync(owner, $"ai-calls/{row.LogId:D}/{row.Id:D}.{extension}",
                content, image.ContentType, image.Bytes.Length, ct);
            row.ProviderId = reference.ProviderId; row.ProviderKey = reference.ProviderKey; row.ObjectKey = reference.ObjectKey;
        }
        // All input references must be durable before any model request is sent.
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AiCallImageDto>> ListAsync(Guid logId, CancellationToken ct)
    {
        await RequireOwnerAsync(logId, ct);
        return await db.AiCallImages.AsNoTracking().Where(x => x.LogId == logId).OrderBy(x => x.Index)
            .Select(x => new AiCallImageDto(x.Id, x.Index, x.Name, x.ContentType, x.SizeBytes, x.Sha256, x.ObjectKey != null)).ToListAsync(ct);
    }

    public async Task<(byte[] Bytes, string ContentType)> ReadAsync(Guid logId, Guid imageId, CancellationToken ct)
    {
        var owner = await RequireOwnerAsync(logId, ct);
        var row = await db.AiCallImages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == imageId && x.LogId == logId, ct);
        if (row?.ObjectKey is null) throw Missing();
        await using var content = await files.OpenReadAsync(owner, new(row.ObjectKey, row.ProviderKey!, row.ProviderId), ct);
        using var buffer = new MemoryStream(); var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, ct); if (read == 0) break;
            if (buffer.Length + read > MaxImageBytes) throw Changed();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length != row.SizeBytes || Hash(bytes) != row.Sha256) throw Changed();
        return (bytes, row.ContentType);
    }

    private async Task<Guid> RequireOwnerAsync(Guid logId, CancellationToken ct)
    {
        var user = currentUser.UserId ?? throw new DomainException(40301, "请先登录调用发起账号");
        await RequireLogOwnerAsync(user, logId, ct);
        return user;
    }
    private async Task RequireLogOwnerAsync(Guid owner, Guid logId, CancellationToken ct)
    {
        if (!await db.AiRequestLogs.AsNoTracking().AnyAsync(x => x.Id == logId && x.UserId == owner, ct)) throw Missing();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Missing() => new(40401, "图片未保存或记录不存在");
    private static DomainException Changed() => new(40901, "文件中的图片已改变，无法作为本次实际输入显示；原调用文字记录保留");
}
