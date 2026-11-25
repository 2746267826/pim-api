using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Search;
using Xunit;

namespace Pim.UnitTests.Search;

public sealed class SearchProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pim-search-provider-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CalendarProvidersSearchApprovedFieldsAndKeepOwnersIsolated()
    {
        await using var db = CreateDb();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var calendar = new CalendarEntity { UserId = owner, Name = "工作" };
        var otherCalendar = new CalendarEntity { UserId = other, Name = "他人" };
        var taskBook = new TaskBookEntity { UserId = owner, Name = "研究任务本" };
        var task = new TaskEntity
        {
            Id = Guid.NewGuid(),
            UserId = owner,
            Title = "课程作业",
            Description = "补充课堂笔记",
            Due = DateTimeOffset.Parse("2026-12-01T09:00:00Z"),
            DtStart = DateTimeOffset.Parse("2026-12-02T09:00:00Z"),
            PlannedEnd = DateTimeOffset.Parse("2026-12-02T10:00:00Z"),
            TaskBook = taskBook
        };
        var otherTask = new TaskEntity
        {
            UserId = other,
            Title = "课程作业",
            Description = "另一个账号"
        };
        var checklist = new TaskChecklistItemEntity
        {
            TaskId = task.Id,
            UserId = owner,
            Title = "核对清单关键词"
        };
        var ev = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Title = "课程评审",
            Description = "地点讨论",
            Location = "教学楼 A",
            AttendeesJson = """[{"name":"张老师","email":"teacher@example.invalid"}]""",
            OnlineMeetingProvider = "腾讯会议",
            OnlineMeetingUrl = "https://meeting.example.invalid/q08",
            RRule = "FREQ=WEEKLY",
            DtStart = DateTimeOffset.Parse("2026-12-01T10:00:00Z"),
            DtEnd = DateTimeOffset.Parse("2026-12-01T11:00:00Z")
        };
        var otherEvent = new EventEntity
        {
            Calendar = otherCalendar,
            CalendarId = otherCalendar.Id,
            Title = "课程评审",
            DtStart = DateTimeOffset.Parse("2026-12-01T10:00:00Z"),
            DtEnd = DateTimeOffset.Parse("2026-12-01T11:00:00Z")
        };
        db.AddRange(calendar, otherCalendar, taskBook, task, otherTask, checklist, ev, otherEvent);
        await db.SaveChangesAsync();
        var user = new FixedCurrentUserService(owner);
        var query = new SearchProviderQuery("课程", ["课程"], SearchMatchMode.All);

        var taskResult = await new TaskSearchProvider(db, user).SearchAsync(query, default);
        var eventResult = await new EventSearchProvider(db, user).SearchAsync(query, default);

        Assert.Equal(1, taskResult.Total);
        Assert.Equal(task.Id.ToString("D"), Assert.Single(taskResult.Items).Id);
        Assert.Equal(1, eventResult.Total);
        Assert.Equal(ev.Id.ToString("D"), Assert.Single(eventResult.Items).Id);
        Assert.Equal($"/tasks?task={task.Id:D}", taskResult.Items[0].Url);
        Assert.Equal($"/calendar?event={ev.Id:D}", eventResult.Items[0].Url);

        var checklistResult = await new TaskSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("清单关键词", ["清单关键词"], SearchMatchMode.All), default);
        Assert.Contains("清单", Assert.Single(checklistResult.Items).MatchedFields!);

        var eventFieldResult = await new EventSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("张老师", ["张老师"], SearchMatchMode.All), default);
        Assert.Contains("参与人", Assert.Single(eventFieldResult.Items).MatchedFields!);

        var taskBookResult = await new TaskSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("研究任务本", ["研究任务本"], SearchMatchMode.All), default);
        Assert.Contains("任务本", Assert.Single(taskBookResult.Items).MatchedFields!);

        var dueResult = await new TaskSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("2026-12-01", ["2026-12-01"], SearchMatchMode.All), default);
        Assert.Contains("截止时间", Assert.Single(dueResult.Items).MatchedFields!);

        var scheduleResult = await new TaskSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("2026-12-02 09:00", ["2026-12-02", "09:00"],
                SearchMatchMode.All), default);
        Assert.Contains("安排", Assert.Single(scheduleResult.Items).MatchedFields!);

        var anyResult = await new TaskSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("补充 不存在", ["补充", "不存在"], SearchMatchMode.Any), default);
        Assert.Contains("描述", Assert.Single(anyResult.Items).MatchedFields!);
        var allResult = await new TaskSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("补充 不存在", ["补充", "不存在"], SearchMatchMode.All), default);
        Assert.Empty(allResult.Items);

        var locationResult = await new EventSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("教学楼 A", ["教学楼 A"], SearchMatchMode.All), default);
        Assert.Contains("地点", Assert.Single(locationResult.Items).MatchedFields!);

        var meetingResult = await new EventSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("腾讯会议", ["腾讯会议"], SearchMatchMode.All), default);
        Assert.Contains("会议链接", Assert.Single(meetingResult.Items).MatchedFields!);

        var recurrenceResult = await new EventSearchProvider(db, user).SearchAsync(
            new SearchProviderQuery("2026-12-01", ["2026-12-01"], SearchMatchMode.All), default);
        Assert.Contains("重复日程", Assert.Single(recurrenceResult.Items).MatchedFields!);
    }

    [Fact]
    public async Task NoteProviderSearchesCurrentBodyDerivedAndTranscriptOnlyForOwner()
    {
        await using var db = CreateDb();
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = _root });
        var materials = new NoteMaterialUploadStore(options);
        var derivedStore = new NoteDerivedStore(options);
        var asrStore = new NoteAsrTaskStore(options);
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var ownNote = await CreateNoteAsync(materials, owner, "研究方法", "课堂材料正文");
        await CreateNoteAsync(materials, other, "研究方法", "其他账号正文");
        await CreateNoteAsync(materials, owner, "字段完整性", "普通正文",
            summary: "总结检索词", overview: "概览检索词", tags: ["标签检索词"]);
        var recordVersion = Guid.NewGuid();
        var recordArtifact = await derivedStore.EnsureArtifactAsync(owner, ownNote.NoteId,
            Pim.Core.Notes.NoteDerivedArtifactKind.CompleteRecord, default);
        await derivedStore.AppendGeneratedAsync(owner, recordArtifact.ArtifactId,
            new NoteDerivedVersionDraft(recordVersion,
                Pim.Core.Notes.NoteDerivedArtifactKind.CompleteRecord, null, null,
                ownNote.VersionId, null, "批次B 完整记录", "input", "manifest",
                "note-derived-v1", "prompt-v1", null, null, null, null, null), default);
        var generated = await derivedStore.GetArtifactAsync(owner, recordArtifact.ArtifactId, default);
        await derivedStore.SelectHeadAsync(owner, recordArtifact.ArtifactId,
            new Pim.Core.Notes.SelectNoteDerivedVersionRequest(generated.HeadRevision, recordVersion), default);
        db.NoteAsrSegmentEvidence.Add(new NoteAsrSegmentEvidenceEntity
        {
            OwnerId = owner,
            VersionId = ownNote.VersionId,
            TaskId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            Route = "local",
            SourceSha256 = new string('a', 64),
            ResultText = "课堂转写文字",
            ResultSha256 = new string('b', 64)
        });
        await db.SaveChangesAsync();
        var provider = new NoteSearchProvider(db, new FixedCurrentUserService(owner),
            materials, derivedStore, asrStore, NullLogger<NoteSearchProvider>.Instance);

        var body = await provider.SearchAsync(
            new SearchProviderQuery("课堂材料", ["课堂材料"], SearchMatchMode.All), default);
        var bodyHit = Assert.Single(body.Items);
        Assert.Equal(ownNote.NoteId.ToString("D"), bodyHit.Id);
        Assert.Equal($"/native-notes?note={ownNote.NoteId:D}", bodyHit.Url);
        Assert.Contains("正文", bodyHit.MatchedFields!);

        var derived = await provider.SearchAsync(
            new SearchProviderQuery("批次B", ["批次B"], SearchMatchMode.All), default);
        Assert.Contains("完整记录", Assert.Single(derived.Items).MatchedFields!);

        var transcript = await provider.SearchAsync(
            new SearchProviderQuery("课堂转写", ["课堂转写"], SearchMatchMode.All), default);
        Assert.Contains("转写", Assert.Single(transcript.Items).MatchedFields!);

        var summary = await provider.SearchAsync(
            new SearchProviderQuery("总结检索词", ["总结检索词"], SearchMatchMode.All), default);
        Assert.Contains("总结", Assert.Single(summary.Items).MatchedFields!);

        var overview = await provider.SearchAsync(
            new SearchProviderQuery("概览检索词", ["概览检索词"], SearchMatchMode.All), default);
        Assert.Contains("概览", Assert.Single(overview.Items).MatchedFields!);

        var tags = await provider.SearchAsync(
            new SearchProviderQuery("标签检索词", ["标签检索词"], SearchMatchMode.All), default);
        Assert.Contains("标签", Assert.Single(tags.Items).MatchedFields!);

        var crossFieldAll = await provider.SearchAsync(
            new SearchProviderQuery("总结检索词 标签检索词",
                ["总结检索词", "标签检索词"], SearchMatchMode.All), default);
        Assert.Single(crossFieldAll.Items);

        var otherProvider = new NoteSearchProvider(db, new FixedCurrentUserService(other),
            materials, derivedStore, asrStore, NullLogger<NoteSearchProvider>.Instance);
        var otherResult = await otherProvider.SearchAsync(
            new SearchProviderQuery("课堂材料", ["课堂材料"], SearchMatchMode.All), default);
        Assert.Empty(otherResult.Items);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(EventEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"search-provider-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static async Task<(Guid NoteId, Guid VersionId)> CreateNoteAsync(
        NoteMaterialUploadStore store, Guid owner, string title, string body,
        string? summary = null, string? overview = null, string[]? tags = null)
    {
        var noteId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = noteId.ToString("D"),
            title,
            revision = 1,
            createdAtMillis = 1_700_000_000_000L,
            summary,
            overview,
            tags = tags ?? [],
            blocks = new[]
            {
                new { id = Guid.NewGuid().ToString("D"), kind = "markdown", text = body }
            }
        });
        var inputJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = versionId.ToString("D"),
            createdAtMillis = 1_700_000_000_000L,
            note = JsonSerializer.Deserialize<JsonElement>(noteJson),
            recordings = Array.Empty<object>()
        });
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var markdownBytes = Encoding.UTF8.GetBytes(body);
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = Hash(inputJson),
            files = new[]
            {
                new { path = "note.json", kind = "Document", sha256 = Hash(noteJson), sizeBytes = noteBytes.Length },
                new { path = "document.md", kind = "Markdown", sha256 = Hash(body), sizeBytes = markdownBytes.Length }
            }
        });
        var request = new Pim.Core.Notes.BeginNoteMaterialUploadRequest(
            versionId, noteId, null, inputJson, manifestJson, true);
        await store.BeginAsync(owner, request);
        await UploadAsync(store, owner, versionId, 0, noteBytes);
        await UploadAsync(store, owner, versionId, 1, markdownBytes);
        await store.CompleteAsync(owner, versionId);
        return (noteId, versionId);
    }

    private static async Task UploadAsync(NoteMaterialUploadStore store, Guid owner,
        Guid versionId, int index, byte[] bytes)
    {
        await store.UploadPartAsync(owner, versionId, index, 0, Hash(bytes),
            new MemoryStream(bytes), default);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }
}
