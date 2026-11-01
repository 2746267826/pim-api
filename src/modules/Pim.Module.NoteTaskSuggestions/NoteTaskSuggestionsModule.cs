using System.Reflection;
using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pim.Core.Common;
using Pim.Core.Modules;
using Pim.Infrastructure.Data;
using Pim.Module.NoteTaskSuggestions.DTOs;
using Pim.Module.NoteTaskSuggestions.Jobs;
using Pim.Module.NoteTaskSuggestions.Services;

namespace Pim.Module.NoteTaskSuggestions;

public sealed class NoteTaskSuggestionsModule : IModule
{
    public string Name => "note-task-suggestions";
    public string Version => "1.0.0";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        PimDbContext.RegisterModuleAssembly(Assembly.GetExecutingAssembly());
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<INoteTaskSuggestionSourceReader, DerivedNoteTaskSuggestionSourceReader>();
        services.AddScoped<INoteTaskSuggestionModelClient, AiTextNoteTaskSuggestionModelClient>();
        services.AddScoped<NoteTaskSuggestionService>();
        services.AddScoped<NoteTaskSuggestionExtractionJob>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(NoteTaskSuggestionEndpointPaths.Root)
            .RequireAuthorization();

        group.MapPost("/extractions", async (
            [FromBody] StartNoteTaskSuggestionExtractionRequest request,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
        {
            var result = await service.StartExtractionAsync(request, ct);
            return Results.Accepted(
                NoteTaskSuggestionEndpointPaths.Extraction(result.Id),
                ApiResponse<NoteTaskSuggestionExtractionDto>.Ok(result));
        })
        .Produces<ApiResponse<NoteTaskSuggestionExtractionDto>>(202)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(403)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(413)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/extractions/{id:guid}", async (
            Guid id,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteTaskSuggestionExtractionDto>.Ok(
                await service.GetExtractionAsync(id, ct))))
        .Produces<ApiResponse<NoteTaskSuggestionExtractionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("", async (
            [FromQuery] Guid? noteId,
            [FromQuery] NoteTaskSuggestionStatusDto? status,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteTaskSuggestionListDto>.Ok(
                await service.ListAsync(noteId, status, ct))))
        .Produces<ApiResponse<NoteTaskSuggestionListDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/{id:guid}", async (
            Guid id,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteTaskSuggestionDto>.Ok(await service.GetAsync(id, ct))))
        .Produces<ApiResponse<NoteTaskSuggestionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateNoteTaskSuggestionRequest request,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteTaskSuggestionDto>.Ok(
                await service.UpdateAsync(id, request, ct))))
        .Produces<ApiResponse<NoteTaskSuggestionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        group.MapPost("/{id:guid}/confirm", async (
            Guid id,
            [FromBody] ConfirmNoteTaskSuggestionRequest request,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteTaskSuggestionDto>.Ok(
                await service.ConfirmAsync(id, request, ct))))
        .Produces<ApiResponse<NoteTaskSuggestionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        group.MapPost("/{id:guid}/dismiss", async (
            Guid id,
            [FromBody] DismissNoteTaskSuggestionRequest request,
            [FromServices] NoteTaskSuggestionService service,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NoteTaskSuggestionDto>.Ok(
                await service.DismissAsync(id, request, ct))))
        .Produces<ApiResponse<NoteTaskSuggestionDto>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);
    }

    public Task InitializeAsync(IServiceProvider serviceProvider)
    {
        try
        {
            var recurring = serviceProvider.GetService<IRecurringJobManager>();
            recurring?.AddOrUpdate<NoteTaskSuggestionExtractionJob>(
                "native-note-task-suggestion-extraction",
                job => job.RunQueuedAsync(CancellationToken.None),
                Cron.Minutely);
        }
        catch (Exception error)
        {
            serviceProvider.GetService<ILogger<NoteTaskSuggestionsModule>>()?.LogWarning(
                error, "Task suggestion extraction dispatcher registration was skipped");
        }

        return Task.CompletedTask;
    }
}

public static class NoteTaskSuggestionEndpointPaths
{
    public const string Root = "/api/v1/native-notes/task-suggestions";

    public static string Extraction(Guid id) => $"{Root}/extractions/{id:D}";
    public static string Suggestion(Guid id) => $"{Root}/{id:D}";
    public static string Confirm(Guid id) => $"{Suggestion(id)}/confirm";
    public static string Dismiss(Guid id) => $"{Suggestion(id)}/dismiss";
}
