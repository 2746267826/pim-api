using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Audit;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Calendar;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Xunit;

namespace Pim.UnitTests.Calendar;

public sealed class OutlookCalendarApiContractTests : IAsyncLifetime
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ClientIdStr = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static readonly Guid ClientId = Guid.Parse(ClientIdStr);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PimDbContext _db = null!;
    private string _dbName = null!;

    public async Task InitializeAsync()
    {
        _dbName = "contract-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddAuthentication().AddCookie("TestScheme", o => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.AddScoped<ICurrentUserService>(_ => new FakeCurrentUser(UserId));

        builder.Services.AddDbContext<PimDbContext>(opts =>
            opts.UseInMemoryDatabase(_dbName));

        // Register stub TimeProvider BEFORE module so TryAddSingleton keeps it
        builder.Services.AddSingleton<TimeProvider>(new StubContractTimeProvider());

        var graphHandler = new ContractGraphHandler();
        builder.Services.AddSingleton(graphHandler);

        var module = new CalendarModule();
        module.RegisterServices(builder.Services, builder.Configuration);

        // Override fakes AFTER module (last registration wins)
        builder.Services.AddScoped<IOutlookAccessTokenProvider>(_ => new FakeContractTokenProvider());
        builder.Services.AddScoped<IMsalPublicClientAdapter>(_ => new StubMsalClient());
        builder.Services.AddHttpClient("outlook").ConfigurePrimaryHttpMessageHandler(() => graphHandler);
        builder.Services.AddScoped<GraphCalendarClient>();
        builder.Services.AddScoped<OutlookCalendarSyncService>();
        builder.Services.AddScoped<OutlookEventWriteService>();
        builder.Services.AddScoped<OutlookCalendarSyncJob>();

        builder.Services.AddSingleton<ISecretProtector, FakeSecretProtector>();
        builder.Services.AddScoped<OutlookTokenCacheStore>();
        builder.Services.AddSingleton<OutlookAuthorizationSessionRunner>();
        builder.Services.AddScoped<CalendarAuditWriter>();
        builder.Services.AddScoped<IAuditLogService>(_ => new FakeAuditLogService());

        builder.Services.AddLogging(l => l.ClearProviders());

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (DomainException ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(ex.ErrorCode, ex.Message));
            }
            catch (GraphRequestException ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(02099, ex.Message));
            }
        });

        app.Use(async (ctx, next) =>
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, UserId.ToString())
            }, "TestScheme"));
            await next();
        });

        app.UseAuthentication();
        app.UseAuthorization();

        module.MapEndpoints(app);

        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        _db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(_dbName).Options);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _client.Dispose();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Settings_Get_ReturnsNotConfigured_WhenNoClientId()
    {
        var resp = await _client.GetAsync("/api/v1/calendar/outlook/settings");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResponse<OutlookSettingsResponse>>();
        Assert.NotNull(api?.Data);
        Assert.Equal("not-configured", api.Data.UiStatus);
        Assert.Null(api.Data.ClientId);
    }

    [Fact]
    public async Task Settings_Put_AcceptsOnlyClientId_ValidatesAsUuid()
    {
        var resp = await _client.PutAsJsonAsync("/api/v1/calendar/outlook/settings",
            new UpdateOutlookClientIdRequest(ClientId));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var getResp = await _client.GetAsync("/api/v1/calendar/outlook/settings");
        var api = await getResp.Content.ReadFromJsonAsync<ApiResponse<OutlookSettingsResponse>>();
        Assert.NotNull(api?.Data);
        Assert.Equal(ClientIdStr, api.Data.ClientId);
        Assert.Equal("common", api.Data.TenantId);
        Assert.Contains("Calendars.ReadWrite", api.Data.Scopes);
        Assert.Equal("not-connected", api.Data.Status);
    }

    [Fact]
    public async Task Settings_Put_RejectsInvalidClientId()
    {
        var resp = await _client.PutAsJsonAsync("/api/v1/calendar/outlook/settings",
            new { ClientId = "not-a-uuid" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(false, null, "not-configured")]
    [InlineData(true, "not-connected", "failed")]
    [InlineData(true, "waiting-for-user", "waiting-auth")]
    [InlineData(true, "connected", "connected")]
    [InlineData(true, "reauth-required", "reauth-required")]
    [InlineData(true, "failed", "failed")]
    public async Task Settings_DerivesUiStatus(bool hasClientId, string? state, string expected)
    {
        if (hasClientId)
        {
            await _client.PutAsJsonAsync("/api/v1/calendar/outlook/settings",
                new UpdateOutlookClientIdRequest(ClientId));
            if (state is not null)
            {
                var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
                connection.Status = state;
                await _db.SaveChangesAsync();
            }
        }

        var resp = await _client.GetAsync("/api/v1/calendar/outlook/settings");
        var api = await resp.Content.ReadFromJsonAsync<ApiResponse<OutlookSettingsResponse>>();
        Assert.Equal(expected, api!.Data!.UiStatus);

        if (hasClientId)
        {
            _db.ChangeTracker.Clear();
            var connection = await _db.Set<OutlookConnectionEntity>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.UserId == UserId);
            Assert.NotNull(connection);
            Assert.Equal(state, connection.Status);
        }
    }

    [Fact]
    public async Task DeviceCode_StartAndPoll_CreatesSessionAndReturnsStatus()
    {
        await _client.PutAsJsonAsync("/api/v1/calendar/outlook/settings",
            new UpdateOutlookClientIdRequest(ClientId));

        var resp = await _client.PostAsync("/api/v1/calendar/outlook/device-code", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResponse<OutlookAuthorizationSessionResponse>>();
        Assert.NotNull(api?.Data);
        Assert.NotEqual(Guid.Empty, api.Data.Id);
        Assert.Equal("waiting-for-user", api.Data.Status);
        Assert.NotNull(api.Data.VerificationUri);
        Assert.NotNull(api.Data.UserCode);
        Assert.NotNull(api.Data.ExpiresAt);

        var pollResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/device-code/poll",
            new OutlookAuthorizationSessionRequest(api.Data.Id));
        Assert.Equal(HttpStatusCode.OK, pollResp.StatusCode);

        using var otherClient = CreateClientForUser(OtherUserId);
        var otherPollResp = await otherClient.PostAsJsonAsync("/api/v1/calendar/outlook/device-code/poll",
            new OutlookAuthorizationSessionRequest(api.Data.Id));
        Assert.Equal(HttpStatusCode.NotFound, otherPollResp.StatusCode);
    }

    [Fact]
    public async Task DeviceCode_Cancel_CancelsSession()
    {
        await _client.PutAsJsonAsync("/api/v1/calendar/outlook/settings",
            new UpdateOutlookClientIdRequest(ClientId));
        var startResp = await _client.PostAsync("/api/v1/calendar/outlook/device-code", null);
        var startApi = await startResp.Content.ReadFromJsonAsync<ApiResponse<OutlookAuthorizationSessionResponse>>();
        var sessionId = startApi!.Data!.Id;

        var cancelResp = await _client.PostAsync(
            $"/api/v1/calendar/outlook/device-code/{sessionId}/cancel", null);
        Assert.True(cancelResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound);

        using var otherClient = CreateClientForUser(OtherUserId);
        var otherCancel = await otherClient.PostAsync(
            $"/api/v1/calendar/outlook/device-code/{sessionId}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, otherCancel.StatusCode);
    }

    [Fact]
    public async Task Discover_RequiresConnection()
    {
        var resp = await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Discover_ReturnsBindings_WhenConnected()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();

        var resp = await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResponse<List<OutlookCalendarBindingResponse>>>();
        Assert.NotNull(api?.Data);
        Assert.NotEmpty(api.Data);
    }

    [Fact]
    public async Task Selection_UpdatesBindings()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();

        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);
        var bindings = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == _db.Set<OutlookConnectionEntity>()
                .Where(c => c.UserId == UserId).Select(c => c.Id).First())
            .ToListAsync();
        Assert.NotEmpty(bindings);

        var selResp = await _client.PutAsJsonAsync("/api/v1/calendar/outlook/calendars/selection",
            new { SelectedBindingIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, selResp.StatusCode);

        var api = await selResp.Content.ReadFromJsonAsync<ApiResponse<List<OutlookCalendarBindingResponse>>>();
        Assert.NotNull(api?.Data);
        Assert.All(api.Data, b => Assert.False(b.IsSelected));

        _db.ChangeTracker.Clear();
        foreach (var b in bindings)
        {
            var refreshed = await _db.Set<OutlookCalendarBindingEntity>().AsNoTracking().FirstAsync(x => x.Id == b.Id);
            Assert.False(refreshed.IsSelected);
        }
    }

    [Fact]
    public async Task Sync_NormalMode_ReturnsBatch()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var syncResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest(Mode: "normal"));
        Assert.Equal(HttpStatusCode.OK, syncResp.StatusCode);
        var api = await syncResp.Content.ReadFromJsonAsync<ApiResponse<OutlookSyncBatchResponse>>();
        Assert.NotNull(api?.Data);
        Assert.NotNull(api.Data.Status);
    }

    [Fact]
    public async Task Sync_InvalidMode_ReturnsError()
    {
        await SeedConnectedAsync();
        var syncResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest(Mode: "invalid-mode"));
        Assert.Equal(HttpStatusCode.BadRequest, syncResp.StatusCode);
    }

    [Fact]
    public async Task Sync_CancelBatch_SetsCancelRequested()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        var batch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = connection.Id,
            Mode = "normal",
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync();

        var cancelResp = await _client.PostAsync(
            $"/api/v1/calendar/outlook/sync/{batch.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        _db.ChangeTracker.Clear();
        var cancelledBatch = await _db.Set<OutlookSyncBatchEntity>().AsNoTracking().FirstAsync(b => b.Id == batch.Id);
        Assert.True(cancelledBatch.CancelRequested);
    }

    [Fact]
    public async Task Writeback_Conflict_Returns409()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var binding = await _db.Set<OutlookCalendarBindingEntity>()
            .FirstAsync(b => b.ConnectionId == _db.Set<OutlookConnectionEntity>()
                .Where(c => c.UserId == UserId).Select(c => c.Id).First());

        var evt = new EventEntity
        {
            CalendarId = binding.PimCalendarId,
            Title = "Test Event",
            DtStart = DateTimeOffset.UtcNow,
            DtEnd = DateTimeOffset.UtcNow.AddHours(1),
            Uid = Guid.NewGuid().ToString(),
            Source = "outlook",
            OutlookEventId = "graph-event-1",
            OutlookCalendarBindingId = binding.Id,
            OutlookConnectionId = binding.ConnectionId
        };
        _db.Set<EventEntity>().Add(evt);
        await _db.SaveChangesAsync();

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.PreconditionFailed);

        var writeResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/events/writeback",
            new OutlookWriteRequest(
                Operation: "update",
                CalendarBindingId: binding.Id,
                EventId: evt.Id,
                Draft: new CreateEventRequest(
                    CalendarId: binding.PimCalendarId,
                    Title: "Updated Event",
                    Description: null,
                    Location: null,
                    DtStart: DateTimeOffset.UtcNow,
                    DtEnd: DateTimeOffset.UtcNow.AddHours(1),
                    RRule: null),
                Scope: "instance",
                ClientOperationId: Guid.NewGuid(),
                ExpectedEtag: "old-etag"));
        Assert.Equal(HttpStatusCode.Conflict, writeResp.StatusCode);
    }

    [Fact]
    public async Task Check_WhenConnected_ReturnsStatus()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.NextResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"id":"u1","displayName":"Test","userPrincipalName":"test@example.com"}""",
                System.Text.Encoding.UTF8, "application/json")
        };

        var resp = await _client.PostAsync("/api/v1/calendar/outlook/check", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Disconnect_ClearsState_ButPreservesCalendars()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        connection.AccessTokenEncrypted = [1, 2, 3];
        connection.MsalCacheEncrypted = [4, 5, 6];
        await _db.SaveChangesAsync();

        var resp = await _client.PostAsync("/api/v1/calendar/outlook/disconnect", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Set<OutlookConnectionEntity>().AsNoTracking().FirstAsync(c => c.UserId == UserId);
        Assert.Equal("not-connected", reloaded.Status);
        Assert.Equal("missing", reloaded.TokenHealth);
        Assert.Empty(reloaded.AccessTokenEncrypted);
    }

    [Fact]
    public async Task LocalData_Preview_ReturnsCounts()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var resp = await _client.GetAsync("/api/v1/calendar/outlook/local-data/preview");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResponse<OutlookLocalDataPreview>>();
        Assert.NotNull(api?.Data);
    }

    [Fact]
    public async Task LocalData_Delete_RemovesOutlookData_WithoutGraphCalls()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        var graphRequestCountBefore = handler.Requests.Count;

        var resp = await _client.DeleteAsync("/api/v1/calendar/outlook/local-data");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(graphRequestCountBefore, handler.Requests.Count);

        _db.ChangeTracker.Clear();
        var connection = await _db.Set<OutlookConnectionEntity>().AsNoTracking().FirstAsync(c => c.UserId == UserId);
        Assert.Equal("not-connected", connection.Status);
        Assert.Empty(connection.AccessTokenEncrypted);

        var bindings = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id).AsNoTracking().ToListAsync();
        Assert.Empty(bindings);
    }

    [Fact]
    public async Task Disconnect_SetsCancelRequestedOnRunningNonWritebackBatches()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        connection.AccessTokenEncrypted = [1, 2, 3];
        connection.MsalCacheEncrypted = [4, 5, 6];
        var runningBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = connection.Id,
            Mode = "normal",
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var writebackBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = connection.Id,
            Mode = "writeback",
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-4)
        };
        _db.Set<OutlookSyncBatchEntity>().AddRange(runningBatch, writebackBatch);
        await _db.SaveChangesAsync();
        var oldUpdatedAt = runningBatch.UpdatedAt;

        var resp = await _client.PostAsync("/api/v1/calendar/outlook/disconnect", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        _db.ChangeTracker.Clear();
        var reloaded = await _db.Set<OutlookConnectionEntity>().AsNoTracking().FirstAsync(c => c.UserId == UserId);
        Assert.Equal("not-connected", reloaded.Status);
        Assert.Equal("missing", reloaded.TokenHealth);
        Assert.Empty(reloaded.AccessTokenEncrypted);

        var cancelledBatch = await _db.Set<OutlookSyncBatchEntity>().AsNoTracking()
            .FirstAsync(b => b.Id == runningBatch.Id);
        Assert.Equal("running", cancelledBatch.Status);
        Assert.True(cancelledBatch.CancelRequested);
        Assert.True(cancelledBatch.UpdatedAt > oldUpdatedAt);

        var preservedWriteback = await _db.Set<OutlookSyncBatchEntity>().AsNoTracking()
            .FirstAsync(b => b.Id == writebackBatch.Id);
        Assert.False(preservedWriteback.CancelRequested);
        Assert.Equal("running", preservedWriteback.Status);
    }

    [Fact]
    public async Task Sync_AfterDisconnect_ReturnsDomainError()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var disconnectResp = await _client.PostAsync("/api/v1/calendar/outlook/disconnect", null);
        Assert.Equal(HttpStatusCode.OK, disconnectResp.StatusCode);

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        var requestCountBefore = handler.Requests.Count;

        var syncResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest(Mode: "normal"));
        Assert.Equal(HttpStatusCode.BadRequest, syncResp.StatusCode);

        Assert.Equal(requestCountBefore, handler.Requests.Count);

        var body = await syncResp.Content.ReadFromJsonAsync<ApiResponse<string>>();
        Assert.NotNull(body);
        Assert.Equal(02005, body.Code);
        Assert.Contains("未连接", body.Message);
    }

    [Fact]
    public async Task Sync_Cancel_TerminalBatch_Returns404()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        var batch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = connection.Id,
            Mode = "normal",
            Status = "completed",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync();

        var cancelResp = await _client.PostAsync(
            $"/api/v1/calendar/outlook/sync/{batch.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, cancelResp.StatusCode);
    }

    [Fact]
    public async Task Sync_Cancel_OtherUserBatch_Returns404()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        var batch = new OutlookSyncBatchEntity
        {
            UserId = OtherUserId,
            ConnectionId = connection.Id,
            Mode = "normal",
            Status = "running",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync();

        var cancelResp = await _client.PostAsync(
            $"/api/v1/calendar/outlook/sync/{batch.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, cancelResp.StatusCode);
    }

    [Fact]
    public async Task Selection_RequestAndResponse_Contract()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var bindings = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == _db.Set<OutlookConnectionEntity>()
                .Where(c => c.UserId == UserId).Select(c => c.Id).First())
            .ToListAsync();
        var selectedIds = bindings.Select(b => b.Id).Take(1).ToList();

        var resp = await _client.PutAsJsonAsync("/api/v1/calendar/outlook/calendars/selection",
            new { SelectedBindingIds = selectedIds });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("isSelected", body);
        Assert.DoesNotContain("bindingIds", body);

        var api = await resp.Content.ReadFromJsonAsync<ApiResponse<List<OutlookCalendarBindingResponse>>>();
        Assert.NotNull(api?.Data);
        Assert.NotEmpty(api.Data);
        var selected = api.Data.Where(b => b.IsSelected).ToList();
        Assert.Single(selected);
        Assert.Equal(selectedIds[0], selected[0].Id);
    }

    // ===== C1: Sync mode endpoints via HTTP =====

    [Fact]
    public async Task Sync_FullResourcesMode_ReturnsBatch()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""",
                System.Text.Encoding.UTF8, "application/json")
        });

        var syncResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest(Mode: "full-resources"));
        Assert.Equal(HttpStatusCode.OK, syncResp.StatusCode);
        var api = await syncResp.Content.ReadFromJsonAsync<ApiResponse<OutlookSyncBatchResponse>>();
        Assert.NotNull(api?.Data);
        Assert.Equal("full-resources", api.Data.Mode);
        Assert.NotNull(api.Data.Status);
    }

    [Fact]
    public async Task Sync_RangeInstancesMode_WithValidRange_ReturnsBatch()
    {
        await SeedConnectedAsync();
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""",
                System.Text.Encoding.UTF8, "application/json")
        });

        var rangeStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var rangeEnd = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var syncResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest("range-instances", RangeStart: rangeStart, RangeEnd: rangeEnd));
        Assert.Equal(HttpStatusCode.OK, syncResp.StatusCode);
        var api = await syncResp.Content.ReadFromJsonAsync<ApiResponse<OutlookSyncBatchResponse>>();
        Assert.NotNull(api?.Data);
        Assert.Equal("range-instances", api.Data.Mode);
        Assert.Equal(rangeStart, api.Data.RequestedWindowStart);
        Assert.Equal(rangeEnd, api.Data.RequestedWindowEnd);
    }

    [Fact]
    public async Task Sync_RangeInstancesMode_WithoutRange_ReturnsError()
    {
        await SeedConnectedAsync();
        var resp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest(Mode: "range-instances"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ===== C2: One-calendar retry through HTTP =====

    [Fact]
    public async Task Sync_RetryOfBatch_ProcessesFailedBindingAndLinksBatch()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        SeedGraphCalendars();
        await _client.PostAsync("/api/v1/calendar/outlook/calendars/discover", null);

        var binding = await _db.Set<OutlookCalendarBindingEntity>()
            .FirstAsync(b => b.ConnectionId == connection.Id);

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.Requests.Clear();

        var originalBatch = new OutlookSyncBatchEntity
        {
            UserId = UserId,
            ConnectionId = connection.Id,
            Mode = "normal",
            Status = "partial",
            StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
            PerCalendarJson = System.Text.Json.JsonSerializer.Serialize(new object[]
            {
                new { bindingId = binding.Id.ToString(), status = "failed", readCount = 0, createdCount = 0, updatedCount = 0, deletedCount = 0, failureCount = 1, changes = Array.Empty<object>(), failures = Array.Empty<object>() }
            })
        };
        _db.Set<OutlookSyncBatchEntity>().Add(originalBatch);
        await _db.SaveChangesAsync();

        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""",
                System.Text.Encoding.UTF8, "application/json")
        });

        var retryResp = await _client.PostAsJsonAsync("/api/v1/calendar/outlook/sync",
            new OutlookSyncRequest("normal", RetryOfBatchId: originalBatch.Id));
        Assert.Equal(HttpStatusCode.OK, retryResp.StatusCode);
        var retryApi = await retryResp.Content.ReadFromJsonAsync<ApiResponse<OutlookSyncBatchResponse>>();
        Assert.NotNull(retryApi?.Data);
        Assert.NotEqual(originalBatch.Id, retryApi.Data.Id);

        Assert.NotEmpty(handler.Requests);
        var calRequest = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains(binding.GraphCalendarId, calRequest);

        Assert.NotNull(retryApi.Data.PerCalendarJson);
        Assert.Contains(binding.Id.ToString(), retryApi.Data.PerCalendarJson);
        Assert.Contains("retryOfBatchId", retryApi.Data.PerCalendarJson);
    }

    // ===== C3: Paged history =====

    [Fact]
    public async Task Sync_Batches_PagedHistory_ReturnsCorrectPage()
    {
        await SeedConnectedAsync();
        var connection = await _db.Set<OutlookConnectionEntity>().FirstAsync(c => c.UserId == UserId);
        var now = DateTimeOffset.UtcNow;

        for (int i = 0; i < 3; i++)
        {
            _db.Set<OutlookSyncBatchEntity>().Add(new OutlookSyncBatchEntity
            {
                UserId = UserId,
                ConnectionId = connection.Id,
                Mode = "normal",
                Status = "completed",
                StartedAt = now.AddHours(-i),
                FinishedAt = now.AddHours(-i).AddMinutes(5),
                UpdatedAt = now.AddHours(-i)
            });
        }

        _db.Set<OutlookSyncBatchEntity>().Add(new OutlookSyncBatchEntity
        {
            UserId = OtherUserId,
            ConnectionId = Guid.NewGuid(),
            Mode = "normal",
            Status = "completed",
            StartedAt = now.AddHours(-1),
            FinishedAt = now.AddHours(-1).AddMinutes(5),
            UpdatedAt = now.AddHours(-1)
        });
        await _db.SaveChangesAsync();

        var resp = await _client.GetAsync("/api/v1/calendar/outlook/sync/batches?page=2&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("data", out var data));
        Assert.Equal(3, data.GetProperty("total").GetInt32());
        Assert.Equal(2, data.GetProperty("page").GetInt32());
        Assert.Equal(1, data.GetProperty("pageSize").GetInt32());

        var items = data.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.True(items[0].TryGetProperty("id", out _));
        Assert.True(items[0].TryGetProperty("status", out _));
    }

    // ===== C4: Check repeated 401 =====

    [Fact]
    public async Task Check_Repeated401_SetsReauthRequired()
    {
        await SeedConnectedAsync();

        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var resp = await _client.PostAsync("/api/v1/calendar/outlook/check", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        _db.ChangeTracker.Clear();
        var connection = await _db.Set<OutlookConnectionEntity>().AsNoTracking()
            .FirstAsync(c => c.UserId == UserId);
        Assert.Equal("reauth-required", connection.Status);
        Assert.Equal("interaction-required", connection.TokenHealth);

        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("access_token", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("device_code", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deviceCode", body, StringComparison.OrdinalIgnoreCase);
    }

    // ===== D: ListRunnableUsersAsync query coverage =====

    [Fact]
    public async Task ListRunnableUsersAsync_IncludesConnectedSelectedActive()
    {
        var dbName = "runnable-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        using var db = new PimDbContext(dbOptions);

        var connA = new OutlookConnectionEntity
        {
            UserId = Guid.NewGuid(), ClientId = ClientIdStr, Status = "connected", TokenHealth = "healthy"
        };
        db.Set<OutlookConnectionEntity>().Add(connA);
        var calA = new CalendarEntity { UserId = connA.UserId, Name = "A", Source = "outlook" };
        db.Set<CalendarEntity>().Add(calA);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connA.Id, PimCalendarId = calA.Id,
            GraphCalendarId = "cal-a", Name = "A", IsSelected = true, RemoteState = "active"
        });
        await db.SaveChangesAsync();

        var svc = new OutlookCalendarSyncService(
            db, null!, new StubContractTimeProvider(), NullLogger<OutlookCalendarSyncService>.Instance);
        var users = await svc.ListRunnableUsersAsync(CancellationToken.None);

        Assert.Single(users);
        Assert.Equal(connA.UserId, users[0]);
    }

    [Fact]
    public async Task ListRunnableUsersAsync_ExcludesDisconnectedUnselectedMissing()
    {
        var dbName = "runnable-exclude-" + Guid.NewGuid();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(dbName).Options;

        using var db = new PimDbContext(dbOptions);

        var disconnectedUser = Guid.NewGuid();
        var connDc = new OutlookConnectionEntity
        {
            UserId = disconnectedUser, ClientId = ClientIdStr, Status = "not-connected", TokenHealth = "missing"
        };
        db.Set<OutlookConnectionEntity>().Add(connDc);
        var calDc = new CalendarEntity { UserId = disconnectedUser, Name = "DC", Source = "outlook" };
        db.Set<CalendarEntity>().Add(calDc);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connDc.Id, PimCalendarId = calDc.Id,
            GraphCalendarId = "cal-dc", Name = "DC", IsSelected = true, RemoteState = "active"
        });
        await db.SaveChangesAsync();

        var unselectedUser = Guid.NewGuid();
        var connUs = new OutlookConnectionEntity
        {
            UserId = unselectedUser, ClientId = ClientIdStr, Status = "connected", TokenHealth = "healthy"
        };
        db.Set<OutlookConnectionEntity>().Add(connUs);
        var calUs = new CalendarEntity { UserId = unselectedUser, Name = "US", Source = "outlook" };
        db.Set<CalendarEntity>().Add(calUs);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connUs.Id, PimCalendarId = calUs.Id,
            GraphCalendarId = "cal-us", Name = "US", IsSelected = false, RemoteState = "active"
        });
        await db.SaveChangesAsync();

        var missingUser = Guid.NewGuid();
        var connRm = new OutlookConnectionEntity
        {
            UserId = missingUser, ClientId = ClientIdStr, Status = "connected", TokenHealth = "healthy"
        };
        db.Set<OutlookConnectionEntity>().Add(connRm);
        var calRm = new CalendarEntity { UserId = missingUser, Name = "RM", Source = "outlook" };
        db.Set<CalendarEntity>().Add(calRm);
        await db.SaveChangesAsync();
        db.Set<OutlookCalendarBindingEntity>().Add(new OutlookCalendarBindingEntity
        {
            ConnectionId = connRm.Id, PimCalendarId = calRm.Id,
            GraphCalendarId = "cal-rm", Name = "RM", IsSelected = true, RemoteState = "remote-missing"
        });
        await db.SaveChangesAsync();

        var svc = new OutlookCalendarSyncService(
            db, null!, new StubContractTimeProvider(), NullLogger<OutlookCalendarSyncService>.Instance);
        var users = await svc.ListRunnableUsersAsync(CancellationToken.None);

        Assert.Empty(users);
    }

    private HttpClient CreateClientForUser(Guid userId)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication().AddCookie("T", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.AddScoped<ICurrentUserService>(_ => new FakeCurrentUser(userId));
        builder.Services.AddDbContext<PimDbContext>(opts =>
            opts.UseInMemoryDatabase(_dbName));
        builder.Services.AddSingleton<TimeProvider>(new StubContractTimeProvider());
        var h = new ContractGraphHandler();
        builder.Services.AddSingleton(h);
        var m = new CalendarModule();
        m.RegisterServices(builder.Services, builder.Configuration);
        builder.Services.AddScoped<IOutlookAccessTokenProvider>(_ => new FakeContractTokenProvider());
        builder.Services.AddScoped<IMsalPublicClientAdapter>(_ => new StubMsalClient());
        builder.Services.AddHttpClient("outlook").ConfigurePrimaryHttpMessageHandler(() => h);
        builder.Services.AddScoped<GraphCalendarClient>();
        builder.Services.AddScoped<OutlookCalendarSyncService>();
        builder.Services.AddScoped<OutlookEventWriteService>();
        builder.Services.AddScoped<OutlookCalendarSyncJob>();
        builder.Services.AddSingleton<ISecretProtector, FakeSecretProtector>();
        builder.Services.AddScoped<OutlookTokenCacheStore>();
        builder.Services.AddSingleton<OutlookAuthorizationSessionRunner>();
        builder.Services.AddScoped<CalendarAuditWriter>();
        builder.Services.AddScoped<IAuditLogService>(_ => new FakeAuditLogService());
        builder.Services.AddLogging(l => l.ClearProviders());
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (DomainException ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(ex.ErrorCode, ex.Message));
            }
        });
        app.Use(async (ctx, next) =>
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
            }, "T"));
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        m.MapEndpoints(app);
        app.StartAsync().GetAwaiter().GetResult();
        return app.GetTestClient();
    }

    private async Task SeedConnectedAsync()
    {
        _db.Set<OutlookConnectionEntity>().Add(new OutlookConnectionEntity
        {
            UserId = UserId,
            ClientId = ClientIdStr,
            Status = "connected",
            TokenHealth = "healthy"
        });
        await _db.SaveChangesAsync();
    }

    private void SeedGraphCalendars()
    {
        var handler = _app.Services.GetRequiredService<ContractGraphHandler>();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"value":[]}""",
                System.Text.Encoding.UTF8, "application/json")
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"value":[{"id":"cal-1","name":"Calendar 1","color":"auto","owner":{"name":"U","address":"u@t"},"isDefaultCalendar":true,"canEdit":true,"canViewPrivateItems":true}]}""",
                System.Text.Encoding.UTF8, "application/json")
        });
    }
}

internal sealed class StubMsalClient : IMsalPublicClientAdapter
{
    private bool _firstCall = true;

    public Task<MsalAuthenticationResult> AcquireTokenWithDeviceCodeAsync(
        OutlookAuthContext context,
        Func<OutlookDeviceCodePrompt, Task> handlePrompt,
        CancellationToken ct)
    {
        handlePrompt(new OutlookDeviceCodePrompt("TEST-CODE", "https://example.com",
            DateTimeOffset.UtcNow.AddMinutes(15), "Test prompt"));
        return Task.FromResult(new MsalAuthenticationResult(
            "test-token", "home-id", "test@example.com", "Test User",
            DateTimeOffset.UtcNow.AddHours(1), ["Calendars.ReadWrite"]));
    }

    public Task<MsalAuthenticationResult> AcquireTokenSilentAsync(
        OutlookAuthContext context, bool forceRefresh, CancellationToken ct)
        => Task.FromResult(new MsalAuthenticationResult(
            "silent-token", "home-id", "test@example.com", "Test",
            DateTimeOffset.UtcNow.AddHours(1), ["Calendars.ReadWrite"]));
}

internal sealed class FakeCurrentUser : ICurrentUserService
{
    public FakeCurrentUser(Guid userId) => UserId = userId;
    public Guid? UserId { get; }
    public string? Role => null;
}

internal sealed class StubContractTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() =>
        new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class FakeContractTokenProvider : IOutlookAccessTokenProvider
{
    public Task<string> AcquireAccessTokenAsync(Guid connectionId, bool forceRefresh, CancellationToken ct)
        => Task.FromResult("contract-test-token");
}

internal sealed class FakeAuditLogService : IAuditLogService
{
    public Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
        => Task.FromResult(new AuditLogDto(
            Guid.NewGuid(), Guid.NewGuid(), AuditActorType.Daemon,
            "test", "calendar", null, "calendar-sync",
            AuditResult.Success, null, DateTimeOffset.UtcNow));
}

internal sealed class ContractGraphHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _queue = new();
    public List<HttpRequestMessage> Requests { get; } = [];
    public HttpResponseMessage? NextResponse { get; set; }

    public void QueueResponse(HttpResponseMessage resp) => _queue.Enqueue(resp);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        if (_queue.TryDequeue(out var queued))
            return Task.FromResult(queued);
        if (NextResponse is not null)
        {
            var resp = NextResponse;
            NextResponse = null;
            return Task.FromResult(resp);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        });
    }
}
