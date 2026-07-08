using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class ReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IOperationConfirmationService _confirmations;

    public ReportService(
        PimDbContext db,
        ICurrentUserService currentUser,
        IOperationConfirmationService confirmations)
    {
        _db = db;
        _currentUser = currentUser;
        _confirmations = confirmations;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(01002, "Login required");

    public async Task<ReportArtifactDto> GenerateAsync(
        GenerateReportRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var kind = NormalizeKind(request.Kind);
        var metrics = new
        {
            tasks = await _db.Set<TaskEntity>().CountAsync(t => t.UserId == userId, ct),
            completedTasks = await _db.Set<TaskEntity>().CountAsync(t => t.UserId == userId && t.Status == "COMPLETED", ct),
            events = await _db.Set<EventEntity>().CountAsync(e => e.Calendar.UserId == userId, ct),
            reminders = await _db.Set<ReminderEntity>().CountAsync(r => r.UserId == userId, ct),
            habits = await _db.Set<HabitRoutineEntity>().CountAsync(h => h.UserId == userId, ct)
        };
        var inputs = new
        {
            request.Date,
            request.ProjectId,
            generatedFrom = new[]
            {
                "planned-vs-actual",
                "task-completion",
                "calendar-occupancy",
                "outlook-impact",
                "collection-quality",
                "habit-completion",
                "reminder-response"
            }
        };
        var content = $"""
        # {kind} Report

        Date: {request.Date:yyyy-MM-dd}

        - Tasks: {metrics.tasks}
        - Completed tasks: {metrics.completedTasks}
        - Calendar events: {metrics.events}
        - Reminders: {metrics.reminders}
        - Habits: {metrics.habits}

        Suggestions are stored separately and require confirmation before changing facts.
        """;

        var entity = new ReportArtifactEntity
        {
            UserId = userId,
            Kind = kind,
            ProjectId = request.ProjectId,
            RiskLevel = "L0AutomaticArtifact",
            InputsJson = JsonSerializer.Serialize(inputs, JsonOptions),
            MetricsJson = JsonSerializer.Serialize(metrics, JsonOptions),
            ContentMarkdown = content,
            GeneratedAt = DateTimeOffset.UtcNow,
            Status = "Active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        _db.Set<ReportArtifactEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<IReadOnlyList<ReportArtifactDto>> ListAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var reports = await _db.Set<ReportArtifactEntity>()
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.GeneratedAt)
            .ToListAsync(ct);
        return reports.Select(Map).ToList();
    }

    public async Task<ReportArtifactDto> GetAsync(Guid id, CancellationToken ct = default)
        => Map(await LoadReportAsync(id, ct));

    public async Task<ReportArtifactDto> ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var report = await LoadReportAsync(id, ct);
        report.Status = "Archived";
        report.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(report);
    }

    public async Task<OperationConfirmationDto> RequestSuggestionActionAsync(
        Guid suggestionId,
        CancellationToken ct = default)
    {
        var suggestion = await _db.Set<ReportSuggestionEntity>()
            .Include(s => s.Report)
            .FirstOrDefaultAsync(s => s.Id == suggestionId && s.UserId == UserId, ct)
            ?? throw new DomainException(02043, "Report suggestion does not exist.");
        var changedFields = ReadChangedFields(suggestion.ChangedFieldsJson);
        var confirmation = await _confirmations.CreateAsync(
            new CreateOperationConfirmationRequest(
                UserId,
                "report.suggestion." + suggestion.Action,
                suggestion.Summary,
                OperationRiskLevel.L2PimFactChange,
                "report",
                suggestion.PayloadJson,
                JsonSerializer.Serialize(new
                {
                    suggestion.Action,
                    suggestion.Summary,
                    reportId = suggestion.ReportId
                }, JsonOptions),
                DateTimeOffset.UtcNow.AddHours(6),
                suggestion.Id.ToString("N"),
                changedFields,
                ["confirm", "reject"],
                "report-suggestion",
                suggestion.Id,
                false,
                null,
                suggestion.PayloadJson),
            ct);

        suggestion.ConfirmationId = confirmation.Id;
        suggestion.Status = "PendingConfirmation";
        suggestion.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return confirmation;
    }

    private async Task<ReportArtifactEntity> LoadReportAsync(Guid id, CancellationToken ct)
        => await _db.Set<ReportArtifactEntity>()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct)
            ?? throw new DomainException(02044, "Report does not exist.");

    private static ReportArtifactDto Map(ReportArtifactEntity entity)
        => new(
            entity.Id,
            entity.Kind,
            entity.ProjectId,
            entity.RiskLevel,
            entity.ContentMarkdown,
            entity.MetricsJson,
            entity.GeneratedAt,
            entity.Status);

    private static IReadOnlyList<string> ReadChangedFields(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeKind(string kind)
        => kind switch
        {
            "Daily" or "Weekly" or "Monthly" or "Project" => kind,
            _ => throw new DomainException(02045, "Unsupported report kind.")
        };
}
