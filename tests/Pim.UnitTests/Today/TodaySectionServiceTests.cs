using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;
using Pim.Api.Endpoints;
using Pim.Api.Today;
using Pim.Core.Today;
using Pim.Core.Common;
using Xunit;

namespace Pim.UnitTests.Today;

public class TodaySectionServiceTests
{
    [Fact]
    public void TodayEndpointPaths_AreStable()
    {
        Assert.Equal("/api/v1/today/sections", TodayEndpointPaths.Sections);
        Assert.Equal("/api/v1/today/sections/calendar.schedule", TodayEndpointPaths.Section("calendar.schedule"));
        Assert.Equal("/api/v1/today/sections/pc.activity%3Fdebug%3D1", TodayEndpointPaths.Section("pc.activity?debug=1"));
    }

    [Fact]
    public void ToInvalidDateResult_ReturnsBadRequest()
    {
        var result = Assert.IsType<BadRequest<ApiResponse<string>>>(TodayEndpoints.ToInvalidDateResult());

        Assert.NotNull(result.Value);
        Assert.Equal(400, result.Value.Code);
        Assert.Equal("Invalid Today date. Expected YYYY-MM-DD or a parseable date/time value.", result.Value.Message);
    }

    [Fact]
    public async Task GetRegistryAsync_ReturnsProviderMetadataWithoutUiFields()
    {
        var service = CreateService(new FakeProvider("calendar.schedule", "calendar.schedule"));

        var registry = await service.GetRegistryAsync("2026-05-25", CancellationToken.None);

        Assert.Equal("2026-05-25", registry.Date);
        Assert.Equal("2026-05-25", registry.PcBusinessDate);

        var section = Assert.Single(registry.Sections);
        Assert.Equal("calendar.schedule", section.Id);
        Assert.Equal("calendar.schedule", section.Kind);
        Assert.Equal(TodaySectionStatuses.Available, section.Status);
        Assert.DoesNotContain(section.Links, link => link.Rel == TodayLinkRels.Details);

        var self = Assert.Single(section.Links, link => link.Rel == TodayLinkRels.Self);
        Assert.Equal("/api/v1/today/sections/calendar.schedule?date=2026-05-25", self.Href);
    }

    [Fact]
    public async Task GetRegistryAsync_UsesPreviousPcBusinessDateBeforeFourAm()
    {
        var service = CreateService(new FakeProvider("calendar.schedule", "calendar.schedule"));

        var registry = await service.GetRegistryAsync("2026-05-25T03:30:00", CancellationToken.None);

        Assert.Equal("2026-05-25", registry.Date);
        Assert.Equal("2026-05-24", registry.PcBusinessDate);
    }

    [Fact]
    public async Task GetRegistryAsync_UsesPreviousPcBusinessDateForExplicitAmTimeBeforeFourAm()
    {
        var service = CreateService(new FakeProvider("calendar.schedule", "calendar.schedule"));

        var registry = await service.GetRegistryAsync("2026-05-25 3 AM", CancellationToken.None);

        Assert.Equal("2026-05-25", registry.Date);
        Assert.Equal("2026-05-24", registry.PcBusinessDate);
    }

    [Fact]
    public async Task GetSectionAsync_ReturnsProviderPayload()
    {
        var provider = new FakeProvider("operations.health", "operations.health");
        var service = CreateService(provider);

        var section = await service.GetSectionAsync("operations.health", "2026-05-25", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Equal("operations.health", section.Id);
        Assert.Equal("operations.health", section.Kind);
        Assert.Equal(TodaySectionStatuses.Normal, section.Status);
        Assert.Equal(1, provider.BuildCount);
    }

    [Fact]
    public async Task GetSectionAsync_ReturnsNull_ForUnknownSection()
    {
        var service = CreateService(new FakeProvider("operations.health", "operations.health"));

        var section = await service.GetSectionAsync("unknown.section", "2026-05-25", CancellationToken.None);

        Assert.Null(section);
    }

    [Fact]
    public async Task GetSectionAsync_ReturnsUnavailable_WhenProviderThrows()
    {
        var service = CreateService(new ThrowingProvider("operations.health", "operations.health"));

        var section = await service.GetSectionAsync("operations.health", "2026-05-25", CancellationToken.None);

        Assert.NotNull(section);
        Assert.Equal(TodaySectionStatuses.Unavailable, section.Status);
        Assert.NotNull(section.Error);
        Assert.Equal("section_unavailable", section.Error.Code);
        Assert.Equal("此今日模块暂时不可用。", section.Error.Message);
        Assert.DoesNotContain("boom", section.Error.Message);
    }

    [Fact]
    public async Task GetSectionAsync_ThrowsOperationCanceledException_WhenProviderCancellationIsRequested()
    {
        var service = CreateService(new CancelingProvider("operations.health", "operations.health"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetSectionAsync("operations.health", "2026-05-25", cts.Token));
    }

    private static TodaySectionService CreateService(params ITodaySectionProvider[] providers)
        => new(providers, NullLogger<TodaySectionService>.Instance);

    private sealed class FakeProvider(string sectionId, string kind) : ITodaySectionProvider
    {
        public string SectionId { get; } = sectionId;

        public string Kind { get; } = kind;

        public int BuildCount { get; private set; }

        public Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
        {
            BuildCount++;

            return Task.FromResult(new TodaySectionDto(
                SectionId,
                Kind,
                TodaySectionStatuses.Normal,
                DateTimeOffset.UtcNow,
                new { query.Date },
                [],
                null));
        }
    }

    private sealed class ThrowingProvider(string sectionId, string kind) : ITodaySectionProvider
    {
        public string SectionId { get; } = sectionId;

        public string Kind { get; } = kind;

        public Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    private sealed class CancelingProvider(string sectionId, string kind) : ITodaySectionProvider
    {
        public string SectionId { get; } = sectionId;

        public string Kind { get; } = kind;

        public Task<TodaySectionDto> BuildAsync(TodayQuery query, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Expected cancellation.");
        }
    }
}
