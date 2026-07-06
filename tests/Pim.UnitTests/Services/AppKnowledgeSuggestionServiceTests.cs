using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.PcTracker;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.Services;

public class AppKnowledgeSuggestionServiceTests
{
    [Fact]
    public async Task BuildRecommendedContextAsync_PrefersDomainFromPluralSanitizedContextAndReturnsPreviewAlternatives()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var app = NewApp("msedge.exe", "Microsoft Edge");
        db.Set<AppSignatureEntity>().Add(app);
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "web:github.com",
            SampleCount = 4,
            TotalDurationSeconds = 1200,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """
            {
              "clusterKey": "web:github.com",
              "apps": ["msedge.exe"],
              "domains": ["github.com"],
              "titles": ["PIM issue triage", "PIM issue triage"],
              "urls": ["https://github.com/acme/pim/issues"]
            }
            """,
            CurrentCategory = "Other",
            SuggestedCategory = "Development",
            SuggestedProjectTag = "PIM",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var preview = NewPreview(affectedRecordCount: 3, affectedDurationSeconds: 900);

        var result = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                "Engineering",
                "Task 8",
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            preview,
            CancellationToken.None);

        Assert.Equal(suggestionId, result.SuggestionId);
        Assert.Same(preview, result.Preview);
        Assert.Equal(app.Id, result.RecommendedContext.AppId);
        Assert.Equal("msedge.exe", result.RecommendedContext.ProcessName);
        Assert.Equal("domain", result.RecommendedContext.PatternType);
        Assert.Equal("github.com", result.RecommendedContext.PatternValue);
        Assert.Equal("Microsoft Edge · 域名：github.com", result.RecommendedContext.ScopeSummary);
        Assert.Equal("Engineering", result.RecommendedContext.TargetCategoryName);
        Assert.Equal("Task 8", result.RecommendedContext.ProjectTag);
        Assert.Equal("app-knowledge-suggestion", result.RecommendedContext.Source);
        Assert.Equal(3, result.RecommendedContext.AffectedRecordCount);
        Assert.Equal(900, result.RecommendedContext.AffectedDurationSeconds);
        Assert.Contains(result.Alternatives, item =>
            item.PatternType == "title" && item.PatternValue == "PIM issue triage");
        Assert.Contains(result.Alternatives, item =>
            item.PatternType == "app-default" && item.PatternValue == "msedge.exe");
        Assert.Equal(
            result.Alternatives.Count,
            result.Alternatives
                .Select(item => $"{item.PatternType}:{item.PatternValue}".ToLowerInvariant())
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task BuildRecommendedContextAsync_UsesTitleWhenSingularSanitizedContextHasNoDomain()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:code",
            SampleCount = 2,
            TotalDurationSeconds = 600,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """{"appName":"Code.exe","title":"Program.cs"}""",
            SuggestedCategory = "Development",
            SuggestedProjectTag = "PIM",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                null,
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            preview: null,
            CancellationToken.None);

        Assert.Equal("Code.exe", result.RecommendedContext.ProcessName);
        Assert.Equal("title", result.RecommendedContext.PatternType);
        Assert.Equal("Program.cs", result.RecommendedContext.PatternValue);
        Assert.Equal("Development", result.RecommendedContext.TargetCategoryName);
        Assert.Equal("PIM", result.RecommendedContext.ProjectTag);
        Assert.Equal(2, result.Preview.AffectedRecordCount);
        Assert.Equal(600, result.Preview.AffectedDurationSeconds);
        Assert.Contains("App 知识库建议影响估算", result.Preview.Summary);
        Assert.DoesNotContain("App Knowledge suggestion impact estimate", result.Preview.Summary);
        Assert.Contains(result.Alternatives, item =>
            item.PatternType == "app-default" && item.PatternValue == "Code.exe");
    }

    [Fact]
    public async Task BuildRecommendedContextAsync_ResolvesWildcardAppSignatureWithoutUpdatingLastSeen()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var originalLastSeen = DateTimeOffset.Parse("2026-01-02T03:04:05Z");
        var app = NewApp("MobaXterm*.exe", "MobaXterm");
        app.LastSeenAt = originalLastSeen;
        db.Set<AppSignatureEntity>().Add(app);
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:MobaXterm_Personal_23.6",
            SampleCount = 3,
            TotalDurationSeconds = 1500,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """
            {
              "apps": ["MobaXterm_Personal_23.6"],
              "titles": ["SSH session"]
            }
            """,
            SuggestedCategory = "Development",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);

        var result = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                null,
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            NewPreview(affectedRecordCount: 3, affectedDurationSeconds: 1500),
            CancellationToken.None);

        Assert.Equal(app.Id, result.RecommendedContext.AppId);
        Assert.Equal("MobaXterm*.exe", result.RecommendedContext.ProcessName);
        Assert.Equal("title", result.RecommendedContext.PatternType);
        Assert.Equal("SSH session", result.RecommendedContext.PatternValue);
        Assert.Equal(originalLastSeen, await db.Set<AppSignatureEntity>()
            .Where(item => item.Id == app.Id)
            .Select(item => item.LastSeenAt)
            .SingleAsync());
    }

    [Fact]
    public async Task SaveRecommendedContextAsync_PersistsSourceSuggestionAndPreviewImpact()
    {
        await using var db = CreateDb();
        var suggestionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var app = NewApp("chrome.exe", "Google Chrome");
        db.Set<AppSignatureEntity>().Add(app);
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "web:docs.example.com",
            SampleCount = 5,
            TotalDurationSeconds = 1800,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """{"apps":["chrome.exe"],"domains":["docs.example.com"]}""",
            SuggestedCategory = "Research",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var preview = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                "Documentation",
                "PIM",
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            NewPreview(affectedRecordCount: 7, affectedDurationSeconds: 2400),
            CancellationToken.None);

        var saved = await service.SaveRecommendedContextAsync(preview, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(app.Id, saved.AppId);
        Assert.Equal("chrome.exe", saved.ProcessName);
        Assert.Equal("domain", saved.PatternType);
        Assert.Equal("docs.example.com", saved.PatternValue);
        Assert.Equal("app-knowledge-suggestion", saved.Source);
        Assert.Equal(7, saved.AffectedRecordCount);
        Assert.Equal(2400, saved.AffectedDurationSeconds);
        var entity = await db.Set<AppKnowledgeContextEntity>().SingleAsync();
        Assert.Equal(suggestionId, entity.SourceSuggestionId);
        Assert.Equal("app-knowledge-suggestion", entity.Source);
        Assert.Equal(7, entity.AffectedRecordCount);
        Assert.Equal(2400, entity.AffectedDurationSeconds);
    }

    [Fact]
    public async Task SaveRecommendedContextAsync_CreatesSignatureForUnmatchedProcessSoContextIsVisible()
    {
        var databaseName = $"app-knowledge-visible-{Guid.NewGuid()}";
        await using var db = CreateDb(databaseName);
        var suggestionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:ObscureTool.exe",
            SampleCount = 2,
            TotalDurationSeconds = 900,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """{"apps":["ObscureTool.exe"],"domains":["private.example.com"]}""",
            SuggestedCategory = "Research",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var preview = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                "Research",
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            NewPreview(affectedRecordCount: 2, affectedDurationSeconds: 900),
            CancellationToken.None);
        Assert.Null(preview.RecommendedContext.AppId);

        var saved = await service.SaveRecommendedContextAsync(preview, CancellationToken.None);

        Assert.NotNull(saved.AppId);
        var savedAppId = saved.AppId!.Value;
        var app = await db.Set<AppSignatureEntity>().SingleAsync();
        Assert.Equal(savedAppId, app.Id);
        Assert.Equal("ObscureTool.exe", app.ProcessName);
        Assert.Equal("ObscureTool.exe", app.DisplayName);
        Assert.Equal("learned", app.Source);

        await using var refreshedDb = CreateDb(databaseName);
        var apps = await new AppSignatureService(refreshedDb).GetKnowledgeAppsAsync("ObscureTool", CancellationToken.None);
        var visibleApp = Assert.Single(apps);
        Assert.Equal(savedAppId, visibleApp.Id);
        Assert.Equal(1, visibleApp.ContextCount);

        var contexts = await new AppKnowledgeContextService(refreshedDb).GetByAppAsync(savedAppId, CancellationToken.None);
        var context = Assert.Single(contexts);
        Assert.Equal(saved.Id, context.Id);
        Assert.Equal("private.example.com", context.PatternValue);
    }

    [Fact]
    public async Task SaveRecommendedContextAsync_WhenLearnedAppInsertRaces_ReusesExistingSignature()
    {
        await using var db = CreateDbWithAppSignatureInsertRace();
        var suggestionId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
        {
            Id = suggestionId,
            ClusterKey = "app:StagedTool.exe",
            SampleCount = 3,
            TotalDurationSeconds = 1200,
            SampleRecordsJson = "[]",
            SanitizedContextJson = """{"apps":["StagedTool.exe"],"domains":["staged.example.com"]}""",
            SuggestedCategory = "Research",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db);
        var preview = await service.BuildRecommendedContextAsync(
            suggestionId,
            new SuggestionClassificationPreviewRequest(
                "Research",
                null,
                new ActivityClassificationApplyRangeRequest("range", "2026-07-06", "2026-07-06")),
            NewPreview(affectedRecordCount: 3, affectedDurationSeconds: 1200),
            CancellationToken.None);

        var saved = await service.SaveRecommendedContextAsync(preview, CancellationToken.None);

        var app = Assert.Single(db.Set<AppSignatureEntity>());
        Assert.Equal(app.Id, saved.AppId);
        Assert.Equal("StagedTool.exe", app.ProcessName);
        Assert.Equal("staged.example.com", Assert.Single(db.Set<AppKnowledgeContextEntity>()).PatternValue);
    }

    [Fact]
    public async Task ApplySuggestionWithSideEffectAsync_WhenAppKnowledgeSaveFails_DoesNotPersistApply()
    {
        var databaseName = $"app-knowledge-side-effect-{Guid.NewGuid()}";
        var suggestionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        await using (var db = CreateDbWithContextSaveFailure(databaseName))
        {
            db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
            {
                Id = Guid.NewGuid(),
                Name = "Programming",
                Color = "#2563eb"
            });
            db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
            {
                Id = suggestionId,
                ClusterKey = "app:code",
                SampleCount = 1,
                TotalDurationSeconds = 600,
                SampleRecordsJson = "[]",
                SanitizedContextJson = """{"apps":["Code.exe"],"titles":["Program.cs"]}""",
                SuggestedCategory = "Programming",
                Status = "pending",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
            await db.SaveChangesAsync();

            var service = CreateRecomputeService(db);
            var appKnowledge = CreateService(db);
            var drafts = new ClassificationRuleDraftService(db);
            var request = new SuggestionClassificationApplyRequest(
                "Programming",
                "PIM",
                new ActivityClassificationApplyRangeRequest("range", "2026-05-25", "2026-05-25"));
            var previewRequest = new SuggestionClassificationPreviewRequest(
                request.CategoryName,
                request.ProjectTag,
                request.Range);
            var knowledgePreview = await appKnowledge.BuildRecommendedContextAsync(
                suggestionId,
                previewRequest,
                NewPreview(affectedRecordCount: 1, affectedDurationSeconds: 600),
                CancellationToken.None);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.ApplySuggestionWithSideEffectAsync<AppKnowledgeContextDto>(
                    suggestionId,
                    request,
                    drafts,
                    (applied, token) =>
                    {
                        var appliedKnowledgePreview = knowledgePreview with
                        {
                            Preview = applied.Preview,
                            RecommendedContext = knowledgePreview.RecommendedContext with
                            {
                                AffectedRecordCount = applied.Preview.AffectedRecordCount,
                                AffectedDurationSeconds = applied.Preview.AffectedDurationSeconds
                            }
                        };
                        return appKnowledge.SaveRecommendedContextAsync(appliedKnowledgePreview, token);
                    },
                    CancellationToken.None));
            Assert.Contains("Simulated App Knowledge context save failure", ex.Message);
        }

        await using var verifyDb = CreateDb(databaseName);
        Assert.Equal("pending", await verifyDb.Set<ActivityClassificationSuggestionEntity>()
            .Where(item => item.Id == suggestionId)
            .Select(item => item.Status)
            .SingleAsync());
        Assert.Equal(0, await verifyDb.Set<ActivityCategoryRuleEntity>().CountAsync());
        Assert.Equal(0, await verifyDb.Set<ActivityClassificationEntity>().CountAsync());
        Assert.Equal(0, await verifyDb.Set<ActivityClassificationAuditEntity>().CountAsync());
        Assert.Equal(0, await verifyDb.AuditLogs.CountAsync());
        Assert.Equal(0, await verifyDb.Set<AppKnowledgeContextEntity>().CountAsync());
    }

    [Fact]
    public async Task ApplyEndpoint_DuplicateRuleFailureDoesNotPersistAppKnowledgeContext()
    {
        var databaseName = $"app-knowledge-apply-{Guid.NewGuid()}";
        await using var app = BuildEndpointApp(databaseName);
        var suggestionId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Set<PcCategoryEntity>().Add(new PcCategoryEntity
            {
                Id = Guid.NewGuid(),
                Name = "Programming",
                Color = "#2563eb"
            });
            db.Set<ActivityClassificationSuggestionEntity>().Add(new ActivityClassificationSuggestionEntity
            {
                Id = suggestionId,
                ClusterKey = "app:code",
                SampleCount = 1,
                TotalDurationSeconds = 600,
                SampleRecordsJson = "[]",
                SanitizedContextJson = """{"apps":["Code.exe"],"titles":["Program.cs"]}""",
                SuggestedCategory = "Programming",
                Status = "pending",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Set<ActivityCategoryRuleEntity>().Add(new ActivityCategoryRuleEntity
            {
                Id = Guid.NewGuid(),
                RuleName = $"Suggestion: app:code {suggestionId:N}",
                Scope = "activity",
                CategoryName = "Programming",
                Color = "#2563eb",
                Priority = 900,
                Source = "user",
                Status = "inactive",
                ConditionsJson = """
                {"all":[{"field":"appNameNormalized","op":"equals","value":"not-code"}]}
                """,
                Confidence = 0.95,
                Explanation = "Existing duplicate rule name.",
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Set<AwEventEntity>().Add(WindowEvent("2026-05-25T08:00:00Z", 600, "Code.exe", "Program.cs"));
            await db.SaveChangesAsync();
        }

        var response = await InvokeEndpointAsync(
            app,
            "POST",
            "/api/v1/pc/app-knowledge/suggestions/{id:guid}/apply",
            context =>
            {
                context.Request.RouteValues["id"] = suggestionId.ToString();
                SetJsonBody(context, """
                {
                  "categoryName": "Programming",
                  "projectTag": "PIM",
                  "range": {
                    "mode": "range",
                    "dateFrom": "2026-05-25",
                    "dateTo": "2026-05-25"
                  }
                }
                """);
            });

        Assert.Equal(StatusCodes.Status409Conflict, response.StatusCode);
        Assert.Contains("已存在", response.Body);

        await using var verifyScope = app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.Equal(0, await verifyDb.Set<AppKnowledgeContextEntity>().CountAsync());
        Assert.Equal("pending", await verifyDb.Set<ActivityClassificationSuggestionEntity>()
            .Where(item => item.Id == suggestionId)
            .Select(item => item.Status)
            .SingleAsync());
    }

    private static AppKnowledgeSuggestionService CreateService(PimDbContext db) =>
        new(db, new AppKnowledgeContextService(db), new AppSignatureService(db));

    private static ActivityClassificationRecomputeService CreateRecomputeService(PimDbContext db) =>
        new(
            db,
            new ActivityClassificationSnapshotService(db, NullLogger<ActivityClassificationSnapshotService>.Instance),
            new ActivityClassificationRuleService(db),
            new FixedCurrentUserService(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            NullLogger<ActivityClassificationRecomputeService>.Instance);

    private static ActivityClassificationPreviewDto NewPreview(int affectedRecordCount, double affectedDurationSeconds) =>
        new(
            affectedRecordCount,
            affectedDurationSeconds,
            new Dictionary<string, int> { ["Other"] = affectedRecordCount },
            new Dictionary<string, int> { ["Engineering"] = affectedRecordCount },
            Array.Empty<PcDetailRecord>(),
            affectedRecordCount > 0,
            $"将影响 {affectedRecordCount} 条记录。");

    private static AppSignatureEntity NewApp(string processName, string displayName) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProcessName = processName,
            DisplayName = displayName,
            Source = "builtin",
            Confidence = 0.99,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static PimDbContext CreateDb()
        => CreateDb(Guid.NewGuid().ToString());

    private static PimDbContext CreateDb(string databaseName)
    {
        PimDbContext.RegisterModuleAssembly(typeof(AppKnowledgeContextEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new PimDbContext(options);
    }

    private static PimDbContext CreateDbWithAppSignatureInsertRace()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AppKnowledgeContextEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppSignatureInsertRacePimDbContext(options);
    }

    private static PimDbContext CreateDbWithContextSaveFailure(string databaseName)
    {
        PimDbContext.RegisterModuleAssembly(typeof(AppKnowledgeContextEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new ContextSaveFailurePimDbContext(options);
    }

    private static WebApplication BuildEndpointApp(string databaseName)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddDbContext<PimDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<ICurrentUserService>(_ =>
            new FixedCurrentUserService(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")));
        builder.Services.AddScoped<IAuditLogService, AuditLogService>();

        var module = new PcTrackerModule();
        module.RegisterServices(builder.Services, builder.Configuration);

        var app = builder.Build();
        module.MapEndpoints(app);
        return app;
    }

    private static async Task<(int StatusCode, string Body)> InvokeEndpointAsync(
        WebApplication app,
        string method,
        string route,
        Action<DefaultHttpContext> configure)
    {
        var routeEndpoints = ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
        var endpoint = routeEndpoints.Single(endpoint =>
            NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty) == route
            && endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()?
                .HttpMethods
                .Contains(method) is true);
        Assert.NotNull(endpoint.RequestDelegate);

        using var requestScope = app.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = requestScope.ServiceProvider
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());

        configure(context);
        await endpoint.RequestDelegate(context);

        context.Response.Body.Position = 0;
        var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, responseBody);
    }

    private static string NormalizeRoute(string route)
        => route.Length > 1 ? route.TrimEnd('/') : route;

    private static void SetJsonBody(DefaultHttpContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
    }

    private static AwEventEntity WindowEvent(string timestamp, double duration, string appName, string title) =>
        new()
        {
            Id = Random.Shared.NextInt64(1, long.MaxValue),
            DeviceId = "device-1",
            Timestamp = DateTimeOffset.Parse(timestamp),
            Duration = duration,
            EventType = "window",
            AppName = appName,
            AppNameNormalized = AppNameNormalizer.Normalize(appName),
            WindowTitle = title,
            DataJson = "{}"
        };

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "User";
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class AppSignatureInsertRacePimDbContext : PimDbContext
    {
        private readonly DbContextOptions<PimDbContext> _options;
        private bool _hasThrown;

        public AppSignatureInsertRacePimDbContext(DbContextOptions<PimDbContext> options)
            : base(options)
        {
            _options = options;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pendingApp = ChangeTracker.Entries<AppSignatureEntity>()
                .SingleOrDefault(entry => entry.State == EntityState.Added);

            if (!_hasThrown && pendingApp is not null)
            {
                _hasThrown = true;
                var attempted = pendingApp.Entity;
                var now = DateTimeOffset.UtcNow;

                await using var competingDb = new PimDbContext(_options);
                competingDb.Set<AppSignatureEntity>().Add(new AppSignatureEntity
                {
                    Id = Guid.NewGuid(),
                    ProcessName = attempted.ProcessName,
                    DisplayName = attempted.DisplayName,
                    Source = "learned",
                    Confidence = 0.6,
                    SearchKeywords = attempted.ProcessName,
                    LastSeenAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                await competingDb.SaveChangesAsync(cancellationToken);

                throw new DbUpdateException("Simulated app signature insert race.");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class ContextSaveFailurePimDbContext : PimDbContext
    {
        private bool _hasThrown;

        public ContextSaveFailurePimDbContext(DbContextOptions<PimDbContext> options)
            : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var pendingContext = ChangeTracker.Entries<AppKnowledgeContextEntity>()
                .SingleOrDefault(entry => entry.State == EntityState.Added);

            if (!_hasThrown && pendingContext is not null)
            {
                _hasThrown = true;
                throw new DbUpdateException("Simulated App Knowledge context save failure.");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
