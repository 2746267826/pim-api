using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Data;
using Pim.Module.QuickNotes.DTOs;
using Pim.Module.QuickNotes.Services;

namespace Pim.Module.QuickNotes;

public class QuickNotesModule : IModule
{
    public string Name => "quick-notes";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        var hasMinio = !string.IsNullOrWhiteSpace(configuration["Minio:Endpoint"])
            && !string.IsNullOrWhiteSpace(configuration["Minio:AccessKey"])
            && !string.IsNullOrWhiteSpace(configuration["Minio:SecretKey"]);
        if (hasMinio)
            services.AddScoped<IQuickNoteObjectStorage, MinioQuickNoteObjectStorage>();
        else
            services.AddScoped<IQuickNoteObjectStorage, NullQuickNoteObjectStorage>();
        services.AddScoped<QuickNoteAttachmentService>();
        services.AddScoped<QuickNoteService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(QuickNoteEndpointPaths.Root)
            .RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? status,
            [FromQuery] string? search,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
        {
            var result = await service.ListAsync(new QuickNoteListQuery(status, search, page ?? 1, pageSize ?? 30), ct);
            return Results.Ok(ApiResponse<PagedResult<QuickNoteListItemDto>>.Ok(result));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<QuickNoteDetailDto>.Ok(await service.GetAsync(id, ct))));

        group.MapPost("", async (
            [FromBody] CreateQuickNoteRequest request,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            return Results.Created(
                QuickNoteEndpointPaths.Note(result.Id.ToString()),
                ApiResponse<QuickNoteDetailDto>.Ok(result));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateQuickNoteRequest request,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<QuickNoteDetailDto>.Ok(await service.UpdateAsync(id, request, ct))));

        group.MapPost("/{id:guid}/process", async (
            Guid id,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<QuickNoteDetailDto>.Ok(await service.ProcessAsync(id, ct))));

        group.MapPost("/{id:guid}/archive", async (
            Guid id,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<QuickNoteDetailDto>.Ok(await service.ArchiveAsync(id, ct))));

        group.MapPost("/{id:guid}/restore", async (
            Guid id,
            [FromBody] RestoreQuickNoteRequest request,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<QuickNoteDetailDto>.Ok(await service.RestoreAsync(id, request, ct))));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            [FromServices] QuickNoteService service,
            CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.Ok(ApiResponse<string>.Ok("已删除"));
        });

        group.MapPost("/attachments", async (
            HttpRequest request,
            [FromServices] QuickNoteAttachmentService service,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(ApiResponse<string>.Error(400, "需要 multipart/form-data 请求"));

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException or IOException)
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, "multipart/form-data 请求无效"));
            }

            var file = form.Files.GetFile("file");
            if (file is null)
                return Results.BadRequest(ApiResponse<string>.Error(400, "缺少 file 文件字段"));

            await using var stream = file.OpenReadStream();
            var result = await service.UploadAsync(stream, file.FileName, file.ContentType, file.Length, ct);
            return Results.Ok(ApiResponse<QuickNoteAttachmentUploadDto>.Ok(result));
        });

        group.MapGet("/attachments/{id:guid}/download", async (
            Guid id,
            [FromServices] QuickNoteAttachmentService service,
            CancellationToken ct) =>
        {
            var download = await service.DownloadAsync(id, ct);
            return Results.File(download.Content, download.ContentType, download.FileName);
        });

        group.MapDelete("/attachments/{id:guid}", async (
            Guid id,
            [FromServices] QuickNoteAttachmentService service,
            CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.Ok(ApiResponse<string>.Ok("已删除"));
        });
    }

    public async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        await Task.CompletedTask;
    }
}

public static class QuickNoteEndpointPaths
{
    public const string Root = "/api/v1/quick-notes";
    public const string Attachments = "/api/v1/quick-notes/attachments";

    public static string Note(string id) => $"{Root}/{id}";

    public static string AttachmentDownload(string id) => $"{Attachments}/{id}/download";
}
