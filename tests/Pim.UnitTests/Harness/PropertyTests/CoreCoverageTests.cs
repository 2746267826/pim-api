using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Caching;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Core.Today;
using Xunit;
using System.Security.Claims;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class CoreCoverageTests
{
    // ---- ApiResponse ----
    [Fact] public void ApiResponse_Ok_SetsCodeZero() { var r = ApiResponse<string>.Ok("hi"); Assert.Equal(0, r.Code); Assert.Equal("hi", r.Data); Assert.Equal("success", r.Message); }
    [Fact] public void ApiResponse_Error_SetsCodeAndMessage() { var r = ApiResponse<string>.Error(40401, "not found"); Assert.Equal(40401, r.Code); Assert.Null(r.Data); }
    [Fact] public void ApiResponse_Ok_TimestampIsRecent() { var r = ApiResponse<int>.Ok(1); Assert.True((DateTimeOffset.UtcNow - r.Timestamp).TotalSeconds < 5); }

    // ---- PagedResult ----
    [Fact] public void PagedResult_HoldsFields() { var r = new PagedResult<int>(new[] { 1, 2 }, 1, 10, 2, 1); Assert.Equal(2, r.TotalCount); Assert.Equal(1, r.TotalPages); }
    [Fact] public void PagedResult_EmptyPage() { var r = new PagedResult<string>(Array.Empty<string>(), 2, 10, 0, 0); Assert.Empty(r.Items); }

    // ---- DomainException ----
    [Fact] public void DomainException_HoldsErrorCode() { var ex = new DomainException(40101, "unauth"); Assert.Equal(40101, ex.ErrorCode); Assert.Equal("unauth", ex.Message); }
    [Fact] public void DomainException_IsException() { var ex = new DomainException(50001, "err"); Assert.IsAssignableFrom<Exception>(ex); }

    // ---- TodaySectionStatuses / TodayLinkRels ----
    [Fact] public void TodaySectionStatuses_Constants() { Assert.Equal("available", TodaySectionStatuses.Available); Assert.Equal("unavailable", TodaySectionStatuses.Unavailable); Assert.Equal("empty", TodaySectionStatuses.Empty); Assert.Equal("warning", TodaySectionStatuses.Warning); }
    [Fact] public void TodayLinkRels_Constants() { Assert.Equal("self", TodayLinkRels.Self); Assert.Equal("details", TodayLinkRels.Details); }

    // ---- TodayQuery / DTOs ----
    [Fact] public void TodayQuery_HoldsDates() { var q = new TodayQuery(new DateOnly(2026, 7, 6), new DateOnly(2026, 7, 5)); Assert.Equal(new DateOnly(2026, 7, 6), q.Date); }
    [Fact] public void TodaySectionDto_HoldsError() { var err = new TodaySectionErrorDto("c", "m"); var dto = new TodaySectionDto("id", "kind", "normal", DateTimeOffset.UtcNow, new { }, Array.Empty<TodayLinkDto>(), err); Assert.Equal("c", dto.Error!.Code); }

    // ---- HealthScoreHelper ----
    [Fact] public void HealthScore_ClampScore_Bounds() { Assert.Equal(0, HealthScoreHelper.ClampScore(-10)); Assert.Equal(100, HealthScoreHelper.ClampScore(200)); Assert.Equal(80, HealthScoreHelper.ClampScore(80.4)); }
    [Fact] public void HealthScore_ScoreToStatus_Maps() { Assert.Equal(PimHealthStatus.Healthy, HealthScoreHelper.ScoreToStatus(80)); Assert.Equal(PimHealthStatus.Warning, HealthScoreHelper.ScoreToStatus(60)); Assert.Equal(PimHealthStatus.Critical, HealthScoreHelper.ScoreToStatus(10)); }
    [Fact] public void HealthScore_IsConsistent() { Assert.True(HealthScoreHelper.IsConsistent(90, PimHealthStatus.Healthy)); Assert.False(HealthScoreHelper.IsConsistent(90, PimHealthStatus.Critical)); }

    // ---- Ai Dtos ----
    [Fact] public void AiGatewayRequest_EffectiveMaxAttempts_Clamps() { var r1 = new AiGatewayRequest("m", "p", "t", "id", Array.Empty<AiMessage>(), MaxAttempts: null); Assert.Equal(1, r1.EffectiveMaxAttempts); var r2 = new AiGatewayRequest("m", "p", "t", "id", Array.Empty<AiMessage>(), MaxAttempts: 10); Assert.Equal(2, r2.EffectiveMaxAttempts); }
    [Fact] public void AiGatewayRequest_EffectiveMaxAttempts_ZeroClampsToOne() { var r = new AiGatewayRequest("m", "p", "t", "id", Array.Empty<AiMessage>(), MaxAttempts: 0); Assert.Equal(1, r.EffectiveMaxAttempts); }
    [Fact] public void AiResult_FailedValidation_SetsFields() { var r = AiResult.FailedValidation(Guid.NewGuid(), new[] { "e1" }); Assert.Equal(AiRequestStatus.FailedValidation, r.Status); Assert.Contains("e1", r.SchemaValidationErrors); }

    // ---- MemoryAggregateResultCache ResolveTtl ----
    [Fact] public void Cache_ResolveTtl_BeforeSix_Returns30Min() { var c = CreateCache(); var shanghaiBefore6 = new DateTimeOffset(2026, 7, 6, 21, 0, 0, TimeSpan.Zero); // 21 UTC = 05 shanghai next day
        var ttl = c.ResolveTtl(shanghaiBefore6); Assert.Equal(TimeSpan.FromMinutes(30), ttl); }
    [Fact] public void Cache_ResolveTtl_AfterSix_Returns5Min() { var c = CreateCache(); var shanghaiAfter6 = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero); // 08 shanghai
        var ttl = c.ResolveTtl(shanghaiAfter6); Assert.Equal(TimeSpan.FromMinutes(5), ttl); }
    [Fact] public async Task Cache_GetOrCreate_CachesResult() { var c = CreateCache(); int calls = 0; var r1 = await c.GetOrCreateAsync("k1", false, () => Task.FromResult(++calls), CancellationToken.None); var r2 = await c.GetOrCreateAsync("k1", false, () => Task.FromResult(++calls), CancellationToken.None); Assert.Equal(1, r1); Assert.Equal(1, r2); Assert.Equal(1, calls); }
    [Fact] public async Task Cache_GetOrCreate_Force_BypassesCache() { var c = CreateCache(); int calls = 0; await c.GetOrCreateAsync("k2", false, () => Task.FromResult(++calls), CancellationToken.None); await c.GetOrCreateAsync("k2", true, () => Task.FromResult(++calls), CancellationToken.None); Assert.Equal(2, calls); }
    [Fact] public async Task Cache_EvictByPrefix_RemovesMatching() { var c = CreateCache(); int calls = 0; await c.GetOrCreateAsync("u:anon|/api/v1/today/sections?x=1", false, () => Task.FromResult(++calls), CancellationToken.None); c.EvictByPrefix("/api/v1/today"); var r = await c.GetOrCreateAsync("u:anon|/api/v1/today/sections?x=1", false, () => Task.FromResult(++calls), CancellationToken.None); Assert.Equal(2, calls); }

    // ---- AggregateResultCacheKeys.Build ----
    [Fact] public void CacheKeys_Build_ExcludesForceParam() { var ctx = CreateHttpContext("anon", "/api/v1/today/sections?force=true&date=2026-07-06"); var key = AggregateResultCacheKeys.Build(ctx.Request); Assert.DoesNotContain("force", key); Assert.Contains("date", key); }
    [Fact] public void CacheKeys_Build_IncludesUserId() { var ctx = CreateHttpContext("user-123", "/api/v1/pc/summary"); var key = AggregateResultCacheKeys.Build(ctx.Request); Assert.Contains("user-123", key); }
    [Fact] public void CacheKeys_Build_AnonWhenNoUser() { var ctx = CreateHttpContext(null, "/api/v1/pc/summary"); var key = AggregateResultCacheKeys.Build(ctx.Request); Assert.Contains("anon", key); }
    [Fact] public void CacheKeys_Build_OverridesReplaceQuery() { var ctx = CreateHttpContext("anon", "/api/v1/pc/summary?date=2026-07-06"); var key = AggregateResultCacheKeys.Build(ctx.Request, overrides: new[] { new KeyValuePair<string,string>("date","2026-07-07") }); Assert.Contains("2026-07-07", key); Assert.DoesNotContain("2026-07-06", key); }
    [Fact] public void CacheKeys_Build_SortsQueryParams() { var ctx = CreateHttpContext("anon", "/api/v1/pc/summary?z=2&a=1"); var key = AggregateResultCacheKeys.Build(ctx.Request); var idx_a = key.IndexOf("a=1"); var idx_z = key.IndexOf("z=2"); Assert.True(idx_a < idx_z); }

    // ---- IAggregateResultCache AddAggregateResultCaching extension ----
    [Fact] public void AddAggregateResultCaching_RegistersServices() { var services = new ServiceCollection(); services.AddAggregateResultCaching(); var sp = services.BuildServiceProvider(); Assert.NotNull(sp.GetService<IAggregateResultCache>()); }

    // ---- PimHealthStatus enum ----
    [Fact] public void PimHealthStatus_Values() { Assert.Equal(0, (int)PimHealthStatus.Unknown); Assert.Equal(1, (int)PimHealthStatus.Healthy); }

    // ---- EndpointStatusDto ----
    [Fact] public void EndpointStatusDto_HoldsFields() { var dto = new Pim.Core.Endpoints.EndpointStatusDto("d1", "android", "1.0", "Ok", 0, 0, null); Assert.Equal("d1", dto.DeviceId); }
    [Fact] public void EndpointDtos_AllCovered() { var hb = new Pim.Core.Endpoints.EndpointHeartbeatRequest("android", "1.0"); var q = new Pim.Core.Endpoints.EndpointCollectionQualityDto("d1","android","Ok",0, DateTimeOffset.UtcNow); var act = new Pim.Core.Endpoints.EndpointNotificationActionRequest("approve","low"); var resp = new Pim.Core.Endpoints.EndpointNotificationActionResponse("ok"); Assert.Equal("android", hb.Platform); Assert.Equal(0, q.IssueCount); Assert.Equal("ok", resp.Result); }
    [Fact] public void PlanningDtos_AllCovered() { var p = new Pim.Core.Planning.DomainProjectDto(Guid.NewGuid(),"n",null,"active"); var tb = new Pim.Core.Planning.TaskBookDto(Guid.NewGuid(),null,"n","k","active"); var ci = new Pim.Core.Planning.TaskChecklistItemDto(Guid.NewGuid(),Guid.NewGuid(),"t",false,1); var hr = new Pim.Core.Planning.HabitRoutineDto(Guid.NewGuid(),"t",Pim.Core.Planning.HabitCadence.Daily,"s","active"); var ho = new Pim.Core.Planning.HabitOccurrenceDto(Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(1),"done"); var aw = new Pim.Core.Planning.AvailabilityWindowDto(Guid.NewGuid(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(1),"k","s"); var ph = new Pim.Core.Planning.AiPlanningPlaceholderDto(Guid.NewGuid(),"t",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(1),"r",null); Assert.Equal("n", p.Name); Assert.NotNull(ph); }
    [Fact] public void OperationsDtos_Covered() { var b = new Pim.Core.Operations.BackgroundJobSummaryDto(PimHealthStatus.Healthy,1,2,3,4,DateTimeOffset.UtcNow,"ok"); var s = new Pim.Core.Operations.SystemStatusSummaryDto(PimHealthStatus.Healthy,"l","m",DateTimeOffset.UtcNow); var comp = new Pim.Core.Operations.StatusComponentDto("k","n",StatusComponentKind.Api,PimHealthStatus.Healthy,"m",DateTimeOffset.UtcNow,new Dictionary<string,string>()); var detail = new Pim.Core.Operations.SystemStatusDetailDto(s,new[]{comp},new[]{"next"}); Assert.Equal(1, b.Processing); Assert.Single(detail.Components); }
    [Fact] public void AiDtos_Covered() { var msg = new AiMessage(AiMessageRole.User,"hi"); var usage = new AiTokenUsage(1,2,3,1.5m,"USD"); var schema = new AiSchemaDefinition("n","v1","{}","d"); var status = new AiStatusDto(true,"p","url","m",null,null,null); var filter = new AiRequestLogFilter(null,null,null,null,null,null,null,null,null); var group = new AiUsageGroupDto("k",1,1,0,1,1,2,1m); var summary = new AiUsageSummaryDto(1,1,0,1,1,2,1m,new[]{group},new[]{group},new[]{group},new[]{group}); Assert.Equal("hi", msg.Content); Assert.Equal("n", schema.Name); }
    [Fact] public void AuditDtos_Covered() { var v = new Pim.Core.Audit.AuditVersionDto(Guid.NewGuid(),"t",Guid.NewGuid(),null,"s","a","{}","{}","[]",DateTimeOffset.UtcNow); var tl = new Pim.Core.Audit.AuditTimelineResponse(new[]{v}); var rp = new Pim.Core.Audit.RestorePreviewResponse("t",Guid.NewGuid(),"s",false,new[]{"f"}); var ex = new Pim.Core.Audit.AuditExportResponse("f","ct","c"); Assert.Single(tl.Items); Assert.Equal("f", ex.FileName); }
    [Fact] public void TodayDtos_RegistryAndLinks() { var link = new TodayLinkDto("self","/x"); var item = new TodaySectionRegistryItemDto("id","kind","available",new[]{link}); var reg = new TodaySectionRegistryDto("2026-07-06","2026-07-06",DateTimeOffset.UtcNow,new[]{item}); Assert.Single(reg.Sections); }

    private static MemoryAggregateResultCache CreateCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
        return new MemoryAggregateResultCache(cache, TimeProvider.System);
    }
    private static DefaultHttpContext CreateHttpContext(string? userId, string pathAndQuery)
    {
        var ctx = new DefaultHttpContext();
        var qIdx = pathAndQuery.IndexOf('?');
        ctx.Request.Path = qIdx >= 0 ? pathAndQuery[..qIdx] : pathAndQuery;
        ctx.Request.QueryString = qIdx >= 0 ? new QueryString(pathAndQuery[qIdx..]) : QueryString.Empty;
        if (userId != null) ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }));
        return ctx;
    }
}
