using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Pim.Infrastructure.Ai.Asr;

namespace Pim.Infrastructure.Ai;

public sealed class AiModelSettingsService(PimDbContext db, ISecretProtector secrets, AiAsrPresetRegistry? asrPresets = null)
{
    public async Task<IReadOnlyList<AiModelDto>> ListAsync(CancellationToken ct) =>
        (await db.AiModelProfiles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Select(Map).ToArray();

    public async Task<AiModelDto> SaveAsync(Guid? id, SaveAiModelRequest request, CancellationToken ct)
    {
        var name = Clean(request.Name, 120, "名称");
        var model = Clean(request.Model, 128, "模型标识");
        var endpoint = Endpoint(request.BaseUrl);
        var capabilities = ToFlags(request.Capabilities);
        var presetId = string.IsNullOrWhiteSpace(request.AsrPresetId) ? null : request.AsrPresetId.Trim();
        if (request.TimeoutSeconds is < 1 or > 600 || request.MaxOutputTokens is < 1 or > 131072)
            throw Invalid("超时须为1–600秒，输出上限须为1–131072 token");
        var key = request.ApiKey?.Trim();
        if (key is not null && (key.Length is < 1 or > 4096 || key.Any(c => c < 33 || c > 126)))
            throw Invalid("API Key格式无效，请填写非空可打印字符");
        AiModelProfileEntity row;
        if (id is { } existingId)
        {
            row = await db.AiModelProfiles.SingleOrDefaultAsync(x => x.Id == existingId, ct)
                ?? throw new DomainException(40401, "模型配置不存在");
            if (request.ExpectedRevision != row.Revision) throw Conflict();
            var config = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
            if (config is not null && (config.TextModelId == row.Id && (capabilities & 1) == 0 ||
                config.VisionModelId == row.Id && (capabilities & 2) == 0 ||
                config.TranscriptionModelId == row.Id && (capabilities & 4) == 0))
                throw Invalid("请先清空或切换对应默认模型，再移除此能力");
        }
        else
        {
            if (await db.AiModelProfiles.CountAsync(ct) >= 64) throw Invalid("最多保存64个模型配置");
            if (string.IsNullOrEmpty(key)) throw Invalid("新模型需要API Key；无鉴权本地服务可填写非空占位值");
            row = new(); db.AiModelProfiles.Add(row);
        }
        if (row.AsrPresetId is not null && capabilities != 4)
            throw Invalid("ASR独立配置不可改为文本/视觉配置，请另建模型以隔离凭据");
        if (capabilities == 4 && presetId is null) presetId = row.AsrPresetId;
        if (capabilities == 4 && id is null && presetId is null) throw Invalid("新建ASR配置需要选择接入预设");
        if (presetId is not null)
        {
            if (capabilities != 4) throw Invalid("ASR预设需使用独立语音转写配置，不可混用文本/视觉凭据");
            if (row.AsrPresetId is not null && row.AsrPresetId != presetId && key is null)
                throw Invalid("切换ASR厂商预设需要重新填写凭据，不能自动沿用旧密钥");
            var preset = asrPresets?.Resolve(presetId) ?? throw Invalid("ASR预设不可用");
            preset.ValidateModel(model); endpoint = preset.NormalizeEndpoint(endpoint);
        }
        if (id is not null && !string.Equals(row.BaseUrl, endpoint, StringComparison.Ordinal) && key is null)
            throw Invalid("API地址已变化，必须重新填写API Key；不会沿用旧地址的凭据");
        var normalized = name.ToUpperInvariant();
        string effectiveKey;
        try { effectiveKey = key ?? secrets.Unprotect(row.ApiKeySecret); }
        catch (System.Security.Cryptography.CryptographicException) { throw Invalid("原凭据无法读取，请填写新API Key再保存"); }
        if (Uri.UnescapeDataString(endpoint).Contains(effectiveKey, StringComparison.Ordinal))
            throw Invalid("API地址不能夹带密钥，请将凭据仅填写在API Key字段");
        if (await db.AiModelProfiles.AnyAsync(x => x.NormalizedName == normalized && x.Id != row.Id, ct))
            throw new DomainException(40901, "已有同名模型配置");
        row.Name = name; row.NormalizedName = normalized; row.BaseUrl = endpoint; row.Model = model;
        row.Enabled = request.Enabled; row.Capabilities = capabilities; row.TimeoutSeconds = request.TimeoutSeconds;
        row.AsrPresetId = presetId;
        row.MaxOutputTokens = request.MaxOutputTokens; row.Revision = Guid.NewGuid(); row.UpdatedAt = DateTimeOffset.UtcNow;
        if (key is not null) row.ApiKeySecret = secrets.Protect(key);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        catch (DbUpdateException error) when (IsConflict(error)) { throw Conflict(); }
        return Map(row);
    }

    public async Task DeleteAsync(Guid id, Guid revision, CancellationToken ct)
    {
        var row = await db.AiModelProfiles.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException(40401, "模型配置不存在");
        if (row.Revision != revision) throw Conflict();
        if (await db.AiModelRuntime.AnyAsync(x => x.TextModelId == id || x.VisionModelId == id || x.TranscriptionModelId == id, ct))
            throw new DomainException(40901, "请先清空或切换默认模型；已有调用记录不会随配置删除");
        db.AiModelProfiles.Remove(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        catch (DbUpdateException error) when (IsConflict(error)) { throw Conflict(); }
    }

    public async Task<AiModelSettingsDto> GetSettingsAsync(CancellationToken ct)
    {
        var row = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        return row is null ? new(Guid.Empty, false, null, null, null) : Map(row);
    }

    public async Task<AiModelSettingsDto> SaveSettingsAsync(SaveAiModelSettingsRequest request, CancellationToken ct)
    {
        var row = await db.AiModelRuntime.SingleOrDefaultAsync(ct);
        if ((row?.Revision ?? Guid.Empty) != request.ExpectedRevision) throw Conflict();
        foreach (var (id, flag) in new[] { (request.TextModelId, 1), (request.VisionModelId, 2), (request.TranscriptionModelId, 4) })
        {
            if (id is null) continue;
            var profile = await db.AiModelProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (profile is null || request.Enabled && !profile.Enabled || (profile.Capabilities & flag) == 0)
                throw Invalid("默认模型不存在、未启用或未声明对应能力");
        }
        if (row is null) { row = new(); db.AiModelRuntime.Add(row); }
        row.Enabled = request.Enabled; row.TextModelId = request.TextModelId;
        row.VisionModelId = request.VisionModelId; row.TranscriptionModelId = request.TranscriptionModelId;
        row.Revision = Guid.NewGuid();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
        catch (DbUpdateException error) when (IsConflict(error)) { throw Conflict(); }
        return Map(row);
    }

    public static string Endpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw Invalid("API地址须为HTTP(S)绝对地址，包含实际API前缀（如/v1），不得夹带凭据、查询或片段");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    private static string Clean(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max || value.Any(char.IsControl))
            throw Invalid(field + "为空、过长或含控制字符");
        return value.Trim();
    }
    private static int ToFlags(IReadOnlyList<AiModelCapability>? values)
    {
        if (values is null || values.Count is < 1 or > 3 || values.Distinct().Count() != values.Count || values.Any(v => !Enum.IsDefined(v)))
            throw Invalid("请选择至少一种有效模型能力");
        return values.Aggregate(0, (result, item) => result | (1 << (int)item));
    }
    public static AiModelDto Map(AiModelProfileEntity row) => new(row.Id, row.Revision, row.Name, row.BaseUrl,
        row.Model, row.Enabled, Enum.GetValues<AiModelCapability>().Where(v => (row.Capabilities & (1 << (int)v)) != 0).ToArray(),
        row.ApiKeySecret.Length != 0, row.TimeoutSeconds, row.MaxOutputTokens, row.CreatedAt, row.UpdatedAt, row.AsrPresetId);
    private static AiModelSettingsDto Map(AiModelRuntimeEntity row) => new(row.Revision, row.Enabled,
        row.TextModelId, row.VisionModelId, row.TranscriptionModelId);
    private static DomainException Invalid(string message) => new(40001, message);
    private static bool IsConflict(DbUpdateException error) => error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation };
    private static DomainException Conflict() => new(40901, "配置已被修改、名称重复或仍被引用，请刷新核对后再保存");
}
