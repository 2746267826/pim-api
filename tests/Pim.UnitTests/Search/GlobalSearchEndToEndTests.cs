using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Notes;
using Pim.Infrastructure.Notes;
using Pim.Module.Calendar.Entities;
using Pim.UnitTests.Api;
using Xunit;

namespace Pim.UnitTests.Search;

/// <summary>
/// Q08 RED baseline: the old endpoint only knew the calendar provider, returned no
/// domain groups, and could not search native notes. This test uses only HTTP/JSON
/// and the established note-material API, so the same source compiles on master and
/// demonstrates the missing cross-domain behavior before the implementation.
/// </summary>
public sealed class GlobalSearchEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "pim-global-search-e2e", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OwnerSeesTaskEventAndNativeNoteInOneGroupedResponse()
    {
        using var factory = CreateHost();
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(
            factory, "search-owner-" + Guid.NewGuid().ToString("N"));
        var other = await AuthRegistrationTestHost.SeedAndLoginAsync(
            factory, "search-other-" + Guid.NewGuid().ToString("N"));
        using var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);
        var note = await CreateNoteAsync(factory, session.UserId, "研究方法笔记", "课堂材料正文");
        await SeedCalendarObjectsAsync(factory, session.UserId);
        await CreateNoteAsync(factory, other.UserId, "研究方法他人笔记", "他人正文");
        await SeedCalendarObjectsAsync(factory, other.UserId);

        using var response = await client.GetAsync(
            $"/api/v1/search?q={Uri.EscapeDataString("研究方法")}&page=1&pageSize=20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        var data = root.GetProperty("data");
        var items = data.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "event", "note", "task" },
            items.Select(item => item.GetProperty("domain").GetString()!)
                .OrderBy(domain => domain, StringComparer.Ordinal).ToArray());
        Assert.Equal(3, data.GetProperty("totalCount").GetInt32());

        var groups = data.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Equal(new[] { "note", "task", "event" },
            groups.Select(group => group.GetProperty("domain").GetString()!).ToArray());
        Assert.All(groups, group => Assert.Equal(1, group.GetProperty("totalCount").GetInt32()));

        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("snippet").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("url").GetString()));
            Assert.Equal("server", item.GetProperty("sourceState").GetString());
            Assert.NotEmpty(item.GetProperty("matchedFields").EnumerateArray());
        });
        Assert.Contains(items, item =>
            item.GetProperty("url").GetString() == $"/native-notes?note={note.NoteId:D}");

        var body = await response.Content.ReadAsStringAsync();
        Assert.False(body.Contains(note.VersionId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.False(body.Contains("inputJson", StringComparison.OrdinalIgnoreCase));
        Assert.False(body.Contains("manifestJson", StringComparison.OrdinalIgnoreCase));
        Assert.False(body.Contains("storagePath", StringComparison.OrdinalIgnoreCase));
    }

    private WebApplicationFactory<Program> CreateHost()
        => AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("search-e2e"))
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("NativeNotes:StorageRoot", Path.Combine(_root, "materials"));
                builder.UseSetting("ManagedStorage:Root", Path.Combine(_root, "managed-storage"));
                builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
                builder.UseSetting("Database:Migrations:FailFast", "false");
            });

    private static async Task SeedCalendarObjectsAsync(
        WebApplicationFactory<Program> factory, Guid owner)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Pim.Infrastructure.Data.PimDbContext>();
        var calendar = new CalendarEntity { UserId = owner, Name = "搜索日历" };
        var task = new TaskEntity
        {
            UserId = owner,
            Title = "研究方法任务",
            Due = DateTimeOffset.Parse("2026-12-01T09:00:00Z")
        };
        var calendarEvent = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Title = "研究方法日程",
            DtStart = DateTimeOffset.Parse("2026-12-01T10:00:00Z"),
            DtEnd = DateTimeOffset.Parse("2026-12-01T11:00:00Z")
        };
        db.AddRange(calendar, task, calendarEvent);
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid NoteId, Guid VersionId)> CreateNoteAsync(
        WebApplicationFactory<Program> factory, Guid owner, string title, string body)
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

        var store = factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        await store.BeginAsync(owner, new BeginNoteMaterialUploadRequest(
            versionId, noteId, null, inputJson, manifestJson, true));
        await store.UploadPartAsync(owner, versionId, 0, 0, Hash(noteBytes),
            new MemoryStream(noteBytes), default);
        await store.UploadPartAsync(owner, versionId, 1, 0, Hash(markdownBytes),
            new MemoryStream(markdownBytes), default);
        await store.CompleteAsync(owner, versionId);
        return (noteId, versionId);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
