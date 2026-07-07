using System.Net;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public class OutlookGraphSyncFoundationTests
{
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task GetSettingsAsync_ReturnsDefaultOutlookGraphSettings()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var settings = await service.GetSettingsAsync(UserId);

        Assert.Equal("outlook", settings.Provider);
        Assert.Equal("common", settings.TenantId);
        Assert.Contains("Calendars.ReadWrite", settings.Scopes);
        Assert.Contains("offline_access", settings.Scopes);
        Assert.Equal("not-connected", settings.Status);
        Assert.Equal("missing", settings.TokenHealth);
    }

    [Fact]
    public async Task CreateDeviceCodeRequestAsync_UsesCommonTenantEndpointAndPlaceholderWithoutNetwork()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var response = await service.CreateDeviceCodeRequestAsync(UserId);

        Assert.Equal(
            "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode",
            response.Endpoint);
        Assert.Equal("https://www.microsoft.com/link", response.VerificationUri);
        Assert.Equal("PIM-DEVICE-CODE", response.UserCode);
        Assert.Contains("microsoft.com/link", response.Message);
        Assert.True(response.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(14));
        Assert.True(response.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [Fact]
    public async Task SyncAsync_WhenConnectionIsMissing_ReturnsVisibleFailedOutlookBatch()
    {
        await using var db = CreateDb();
        var service = CreateService(db);

        var response = await service.SyncAsync(UserId);

        Assert.Equal("outlook", response.Provider);
        Assert.Equal("failed", response.Status);
        Assert.Contains(response.Steps, step => step.Name == "Load provider configuration");
        Assert.NotNull(response.ErrorSummary);
        Assert.Contains("Outlook", response.ErrorSummary, StringComparison.OrdinalIgnoreCase);

        var batches = await service.ListBatchesAsync(UserId);
        var persisted = Assert.Single(batches);
        Assert.Equal(response.Id, persisted.Id);
        Assert.Equal("outlook", persisted.Provider);
        Assert.Equal("failed", persisted.Status);
    }

    [Fact]
    public async Task CreateOutlookWritebackConfirmationAsync_UsesSharedL3ConfirmationWithoutPendingConfirmation()
    {
        await using var db = CreateDb();
        var confirmationService = new CapturingConfirmationService();
        var service = CreateService(db, confirmationService);
        var evt = new EventEntity
        {
            CalendarId = Guid.NewGuid(),
            Uid = "writeback@pim",
            Title = "Write back planning block",
            DtStart = new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero),
            DtEnd = new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero),
            Source = "outlook"
        };

        var confirmation = await service.CreateOutlookWritebackConfirmationAsync(
            UserId,
            evt,
            "write_to_outlook");

        Assert.NotNull(confirmationService.LastRequest);
        Assert.Equal(OperationRiskLevel.L3ExternalSourceOrWriteback, confirmationService.LastRequest.RiskLevel);
        Assert.Equal(UserId, confirmationService.LastRequest.RequestedByUserId);
        Assert.Equal("outlook", confirmationService.LastRequest.Source);
        Assert.True(confirmation.RequiresSecondLevelConfirmation);
        Assert.Empty(await db.Set<PendingConfirmationEntity>().ToListAsync());
    }

    private static OutlookSyncService CreateService(
        PimDbContext db,
        IOperationConfirmationService? confirmationService = null)
    {
        return new OutlookSyncService(
            db,
            new StubHttpClientFactory(),
            confirmationService ?? new CapturingConfirmationService());
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"outlook-graph-sync-foundation-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubMessageHandler())
            {
                BaseAddress = new Uri("https://graph.microsoft.com/v1.0")
            };
        }
    }

    private sealed class StubMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"value":[]}""")
            });
        }
    }

    private sealed class CapturingConfirmationService : IOperationConfirmationService
    {
        public CreateOperationConfirmationRequest? LastRequest { get; private set; }

        public Task<OperationConfirmationDto> CreateAsync(
            CreateOperationConfirmationRequest request,
            CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new OperationConfirmationDto(
                Guid.NewGuid(),
                request.RequestedByUserId,
                request.OperationType,
                request.Summary,
                request.RiskLevel,
                request.Source,
                request.PayloadJson,
                request.PreviewJson,
                OperationConfirmationStatus.Pending,
                request.ExpiresAt,
                DateTimeOffset.UtcNow,
                null,
                null,
                null,
                request.CorrelationId));
        }

        public Task<OperationConfirmationDto?> GetAsync(Guid id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OperationConfirmationDto>> ListPendingAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OperationConfirmationDto>> ListPendingForUserAsync(
            Guid? userId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationConfirmationDto> ConfirmAsync(
            Guid id,
            Guid? userId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationConfirmationDto> RejectAsync(
            Guid id,
            Guid? userId,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<OperationConfirmationDto> MarkExecutedAsync(
            Guid id,
            string resultJson,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> ExpireOldAsync(DateTimeOffset now, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
