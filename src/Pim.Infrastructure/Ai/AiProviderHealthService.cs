using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

public interface IAiProviderHealthService
{
    Task CheckAsync(CancellationToken ct = default);
}

public sealed class AiProviderHealthService(
    PimDbContext db,
    IOptions<AiOptions> options,
    IHttpClientFactory httpClientFactory) : IAiProviderHealthService
{
    public async Task CheckAsync(CancellationToken ct = default)
    {
        var ai = options.Value;
        var settings = await db.AiProviderSettings.SingleOrDefaultAsync(s => s.Provider == "litellm", ct)
            ?? new AiProviderSettingEntity { Provider = "litellm" };

        settings.BaseUrl = ai.BaseUrl;
        settings.DefaultModel = ai.DefaultModel;
        settings.Status = ai.Enabled ? "enabled" : "disabled";
        settings.LastHealthCheckAt = DateTimeOffset.UtcNow;
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            using var client = httpClientFactory.CreateClient("litellm-health");
            using var request = new HttpRequestMessage(HttpMethod.Get, ai.BaseUrl.TrimEnd('/') + "/v1/models");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ai.ApiKey);
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            settings.LastError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            settings.Status = "error";
            settings.LastError = ex.Message;
        }

        if (settings.Id == Guid.Empty || db.Entry(settings).State == EntityState.Detached)
        {
            db.AiProviderSettings.Add(settings);
        }

        await db.SaveChangesAsync(ct);
    }
}
