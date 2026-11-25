using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Common;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.Module.Mcp.Services;
using Pim.UnitTests.Api;
using Pim.UnitTests.Harness.RealDb;
using Pim.UnitTests.Notes;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// R05 acceptance over a real PostgreSQL database and the real HTTP pipeline:
/// owner isolation, read-token write denial, package upload/download round-trip,
/// requestId replay, CAS rejection, conflict candidates, lifecycle recovery,
/// processing/result receipts and suggestion error visibility.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class NativeNoteMcpRealDbTests
{
    [SkippableFact]
    public async Task OwnerIsolationAndReadOnlyToken_AreEnforced()
    {
        await using var host = await McpHost.CreateAsync();
        var fixture = await host.SeedNoteAsync(host.UserA);

        var aList = await host.CallAsync(host.McpARead, "get_native_notes", new { });
        Assert.Equal(0, aList.Payload.GetProperty("code").GetInt32());
        Assert.Equal(1, aList.Payload.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(fixture.Request.NoteId.ToString("D"),
            (await host.ReadNoteAsync(host.McpARead, fixture.Request.NoteId))
                .GetProperty("data").GetProperty("noteId").GetString());

        var bList = await host.CallAsync(host.McpBWrite, "get_native_notes", new { });
        Assert.Equal(0, bList.Payload.GetProperty("data").GetProperty("total").GetInt32());

        foreach (var tool in new[]
                 {
                     "get_native_note", "get_native_note_versions", "get_native_note_results",
                     "get_native_note_processing", "get_native_note_package",
                     "get_native_note_suggestions",
                 })
        {
            var denied = await host.CallAsync(host.McpBWrite, tool,
                new { noteId = fixture.Request.NoteId });
            Assert.True(denied.IsError, $"{tool} must not expose another owner's note");
            Assert.Equal(404, denied.Payload.GetProperty("code").GetInt32());
        }

        var package = Package(fixture);
        var deniedUpload = await host.CallAsync(host.McpBWrite, "upload_native_note_package", new
        {
            noteId = fixture.Request.NoteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(package),
        });
        Assert.True(deniedUpload.IsError);
        Assert.Equal(404, deniedUpload.Payload.GetProperty("code").GetInt32());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_note_versions",
                new { noteId = fixture.Request.NoteId })).Payload.GetProperty("data")
            .GetProperty("total").GetInt32());

        var download = await host.CallAsync(host.McpARead, "get_native_note_package",
            new { noteId = fixture.Request.NoteId });
        Assert.False(download.IsError);
        var downloaded = download.Payload.GetProperty("data");
        var downloadedBytes = Convert.FromBase64String(
            downloaded.GetProperty("contentBase64").GetString()!);
        Assert.Equal(downloadedBytes.LongLength, downloaded.GetProperty("byteLength").GetInt64());
        Assert.Equal(Sha256(downloadedBytes), downloaded.GetProperty("contentSha256").GetString());
        var downloadedEntries = ReadZip(downloadedBytes);
        using (var manifest = JsonDocument.Parse(fixture.Request.ManifestJson))
        {
            var index = 0;
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = file.GetProperty("path").GetString()!;
                Assert.Equal(fixture.Content[index++], downloadedEntries[path]);
            }
        }

        var writeWithReadToken = await host.CallAsync(host.McpARead, "archive_native_note", new
        {
            noteId = fixture.Request.NoteId,
            requestId = Guid.NewGuid().ToString("D"),
        });
        Assert.True(writeWithReadToken.IsError);
        Assert.Equal(403, writeWithReadToken.Payload.GetProperty("code").GetInt32());
        var note = await host.ReadNoteAsync(host.McpARead, fixture.Request.NoteId);
        Assert.Equal("Active", note.GetProperty("data").GetProperty("status").GetString());

        var otherNote = await host.SeedNoteAsync(host.UserA);
        var versions = await host.CallAsync(host.McpARead, "get_native_note_versions",
            new { noteId = fixture.Request.NoteId });
        var versionsData = versions.Payload.GetProperty("data");
        var crossNotePromote = await host.CallAsync(host.McpAWrite,
            "promote_native_note_version", new
            {
                noteId = fixture.Request.NoteId,
                requestId = Guid.NewGuid().ToString("D"),
                versionId = otherNote.Request.VersionId.ToString("D"),
                expectedCurrentVersionId = versionsData.GetProperty("currentVersionId").GetGuid()
                    .ToString("D"),
                expectedRevision = versionsData.GetProperty("revision").GetGuid().ToString("D"),
            });
        Assert.True(crossNotePromote.IsError);
        Assert.Equal(404, crossNotePromote.Payload.GetProperty("code").GetInt32());
        Assert.Equal(versionsData.GetProperty("currentVersionId").GetGuid(),
            (await host.CallAsync(host.McpARead, "get_native_note_versions",
                    new { noteId = fixture.Request.NoteId })).Payload.GetProperty("data")
                .GetProperty("currentVersionId").GetGuid());
    }

    [SkippableFact]
    public async Task PackageUpload_RoundTripsAndReplaysIdempotently_WhileCasAndBadPackagesFailClosed()
    {
        await using var host = await McpHost.CreateAsync();
        var noteId = Guid.NewGuid();
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId);
        var package = Package(fixture);
        var requestId = Guid.NewGuid();
        var uploadArgs = new
        {
            noteId,
            requestId = requestId.ToString("D"),
            packageBase64 = Convert.ToBase64String(package),
            promote = true,
        };

        var uploaded = await host.CallAsync(host.McpAWrite, "upload_native_note_package", uploadArgs);
        Assert.False(uploaded.IsError);
        Assert.Equal(0, uploaded.Payload.GetProperty("code").GetInt32());
        var uploadReceipt = uploaded.Payload.GetProperty("data").GetProperty("receipt");
        Assert.Equal("applied", uploadReceipt.GetProperty("status").GetString());
        Assert.False(uploadReceipt.GetProperty("replayed").GetBoolean());
        var uploadResult = uploaded.Payload.GetProperty("data").GetProperty("result");
        var versionId = uploadResult.GetProperty("upload").GetProperty("versionId").GetGuid();
        Assert.Equal(versionId, uploadResult.GetProperty("currentVersionId").GetGuid());

        var replay = await host.CallAsync(host.McpAWrite, "upload_native_note_package", uploadArgs);
        Assert.False(replay.IsError);
        Assert.True(replay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        Assert.Equal(versionId, replay.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("upload").GetProperty("versionId").GetGuid());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_note_versions",
                new { noteId })).Payload.GetProperty("data").GetProperty("total").GetInt32());

        var mismatchedReplay = await host.CallAsync(host.McpAWrite, "upload_native_note_package",
            new
            {
                noteId,
                requestId = requestId.ToString("D"),
                packageBase64 = Convert.ToBase64String(package),
                promote = false,
            });
        Assert.True(mismatchedReplay.IsError);
        Assert.Equal(409, mismatchedReplay.Payload.GetProperty("code").GetInt32());

        var staleCurrent = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(package),
            expectedCurrentVersionId = Guid.NewGuid().ToString("D"),
        });
        Assert.True(staleCurrent.IsError);
        Assert.Equal(409, staleCurrent.Payload.GetProperty("code").GetInt32());
        var staleRevision = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(package),
            expectedRevision = Guid.NewGuid().ToString("D"),
        });
        Assert.True(staleRevision.IsError);
        Assert.Equal(409, staleRevision.Payload.GetProperty("code").GetInt32());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_note_versions",
                new { noteId })).Payload.GetProperty("data").GetProperty("total").GetInt32());

        var download = await host.CallAsync(host.McpARead, "get_native_note_package", new { noteId });
        Assert.False(download.IsError);
        var zipBytes = Convert.FromBase64String(
            download.Payload.GetProperty("data").GetProperty("contentBase64").GetString()!);
        var entries = ReadZip(zipBytes);
        var metadata = JsonNode.Parse(Encoding.UTF8.GetString(entries["material-version.json"]))!.AsObject();
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(entries["materials.json"]))!.AsObject();
        Assert.Equal(versionId.ToString("D"), metadata["id"]!.GetValue<string>());
        Assert.Equal(versionId.ToString("D"), manifest["versionId"]!.GetValue<string>());
        Assert.Equal(Sha256(entries["material-version.json"]),
            manifest["inputSha256"]!.GetValue<string>());
        using (var originalManifest = JsonDocument.Parse(fixture.Request.ManifestJson))
        {
            var index = 0;
            foreach (var file in originalManifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var path = file.GetProperty("path").GetString()!;
                Assert.True(entries.ContainsKey(path), $"downloaded package is missing {path}");
                Assert.Equal(fixture.Content[index++], entries[path]);
            }
        }

        var empty = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId, requestId = Guid.NewGuid().ToString("D"), packageBase64 = string.Empty,
        });
        Assert.True(empty.IsError);
        Assert.Equal(400, empty.Payload.GetProperty("code").GetInt32());
        var notZip = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("not a zip")),
        });
        Assert.True(notZip.IsError);
        Assert.Equal(400, notZip.Payload.GetProperty("code").GetInt32());
        var traversal = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(Package(fixture, "../escape.txt")),
        });
        Assert.True(traversal.IsError);
        Assert.Equal(400, traversal.Payload.GetProperty("code").GetInt32());
        var missingMetadata = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(Package(fixture, null, includeMaterialVersionJson: false)),
        });
        Assert.True(missingMetadata.IsError);
        Assert.Equal(400, missingMetadata.Payload.GetProperty("code").GetInt32());
        var wrongNote = await host.CallAsync(host.McpAWrite, "upload_native_note_package", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(Package(
                NoteMaterialUploadStoreTests.Fixture.Create(noteId: Guid.NewGuid()))),
        });
        Assert.True(wrongNote.IsError);
        Assert.Equal(400, wrongNote.Payload.GetProperty("code").GetInt32());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_notes", new { }))
            .Payload.GetProperty("data").GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task ConflictCandidatesCanBePromoted_AndLifecycleBlocksThenRestoresMutations()
    {
        await using var host = await McpHost.CreateAsync();
        var noteId = Guid.NewGuid();
        var first = await host.SeedNoteAsync(host.UserA,
            NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId));
        var second = NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId);
        await host.SeedNoteAsync(host.UserA, second);

        var versions = await host.CallAsync(host.McpARead, "get_native_note_versions", new { noteId });
        Assert.False(versions.IsError);
        var data = versions.Payload.GetProperty("data");
        Assert.Equal(2, data.GetProperty("total").GetInt32());
        Assert.True(data.GetProperty("hasConflict").GetBoolean());
        Assert.Single(data.GetProperty("conflictCandidates").EnumerateArray());
        var headVersion = data.GetProperty("currentVersionId").GetGuid();
        var conflictVersion = data.GetProperty("conflictCandidates").EnumerateArray()
            .Single().GetProperty("versionId").GetGuid();
        Assert.NotEqual(headVersion, conflictVersion);
        var revision = data.GetProperty("revision").GetGuid();

        var stale = await host.CallAsync(host.McpAWrite, "promote_native_note_version", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            versionId = conflictVersion,
            expectedCurrentVersionId = headVersion,
            expectedRevision = Guid.NewGuid().ToString("D"),
        });
        Assert.True(stale.IsError);
        Assert.Equal(409, stale.Payload.GetProperty("code").GetInt32());

        var promoted = await host.CallAsync(host.McpAWrite, "promote_native_note_version", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            versionId = conflictVersion,
            expectedCurrentVersionId = headVersion,
            expectedRevision = revision.ToString("D"),
        });
        Assert.False(promoted.IsError);
        var promotedData = promoted.Payload.GetProperty("data").GetProperty("result");
        Assert.Equal(conflictVersion, promotedData.GetProperty("currentVersionId").GetGuid());
        var afterPromote = await host.CallAsync(host.McpARead, "get_native_note_versions", new { noteId });
        Assert.False(afterPromote.Payload.GetProperty("data").GetProperty("hasConflict").GetBoolean());

        var active = await host.ReadNoteAsync(host.McpARead, noteId);
        var activeRevision = active.GetProperty("data").GetProperty("revision").GetGuid();
        var archiveRequestId = Guid.NewGuid().ToString("D");
        var archived = await host.CallAsync(host.McpAWrite, "archive_native_note", new
        {
            noteId, requestId = archiveRequestId, expectedRevision = activeRevision.ToString("D"),
        });
        Assert.False(archived.IsError);
        Assert.Equal("Archived", archived.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        var archiveReplay = await host.CallAsync(host.McpAWrite, "archive_native_note", new
        {
            noteId, requestId = archiveRequestId, expectedRevision = activeRevision.ToString("D"),
        });
        Assert.True(archiveReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        var blockedArchiveUpload = await host.CallAsync(host.McpAWrite,
            "upload_native_note_package", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                packageBase64 = Convert.ToBase64String(Package(first)),
            });
        Assert.True(blockedArchiveUpload.IsError);
        Assert.Equal(409, blockedArchiveUpload.Payload.GetProperty("code").GetInt32());

        var archivedRevision = archived.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("revision").GetGuid();
        var restored = await host.CallAsync(host.McpAWrite, "restore_native_note", new
        {
            noteId, requestId = Guid.NewGuid().ToString("D"),
            expectedRevision = archivedRevision.ToString("D"),
        });
        Assert.False(restored.IsError);
        Assert.Equal("Active", restored.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());

        var restoredRevision = restored.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("revision").GetGuid();
        var deleted = await host.CallAsync(host.McpAWrite, "delete_native_note", new
        {
            noteId, requestId = Guid.NewGuid().ToString("D"),
            expectedRevision = restoredRevision.ToString("D"),
        });
        Assert.False(deleted.IsError);
        Assert.Equal("Deleted", deleted.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        Assert.Equal(0, (await host.CallAsync(host.McpARead, "get_native_notes", new { }))
            .Payload.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_notes",
                new { status = "Deleted" })).Payload.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal("Deleted", (await host.ReadNoteAsync(host.McpARead, noteId))
            .GetProperty("data").GetProperty("status").GetString());

        var deletedRevision = deleted.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("revision").GetGuid();
        var restoredAgain = await host.CallAsync(host.McpAWrite, "restore_native_note", new
        {
            noteId, requestId = Guid.NewGuid().ToString("D"),
            expectedRevision = deletedRevision.ToString("D"),
        });
        Assert.False(restoredAgain.IsError);
        Assert.Equal("Active", restoredAgain.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_notes", new { }))
            .Payload.GetProperty("data").GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task ProcessingResultsAndSuggestionFailures_ExposeReceiptsWithoutFakingSuccess()
    {
        await using var host = await McpHost.CreateAsync();
        var fixture = await host.SeedNoteAsync(host.UserA);
        var noteId = fixture.Request.NoteId;
        var versionId = fixture.Request.VersionId;

        var processing = await host.CallAsync(host.McpARead, "get_native_note_processing",
            new { noteId });
        Assert.False(processing.IsError);
        Assert.Equal("get_native_note_processing",
            processing.Payload.GetProperty("data").GetProperty("receipt")
                .GetProperty("operation").GetString());
        Assert.False(string.IsNullOrWhiteSpace(processing.Payload.GetProperty("data")
            .GetProperty("receipt").GetProperty("message").GetString()));

        var results = await host.CallAsync(host.McpARead, "get_native_note_results", new { noteId });
        Assert.False(results.IsError);
        Assert.Equal("get_native_note_results",
            results.Payload.GetProperty("data").GetProperty("receipt")
                .GetProperty("operation").GetString());
        Assert.Empty(results.Payload.GetProperty("data").GetProperty("tasks").EnumerateArray());
        Assert.Empty(results.Payload.GetProperty("data").GetProperty("artifacts").EnumerateArray());

        var start = await host.CallAsync(host.McpAWrite, "start_native_note_processing", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            versionId = versionId.ToString("D"),
        });
        if (start.IsError)
        {
            Assert.Contains(start.Payload.GetProperty("code").GetInt32(), new[] { 409, 503 });
            Assert.False(start.Payload.TryGetProperty("data", out _));
        }
        else
        {
            var state = start.Payload.GetProperty("data").GetProperty("result")
                .GetProperty("status").GetProperty("state").GetString();
            Assert.NotEqual("Completed", state);
        }

        var afterStart = await host.CallAsync(host.McpARead, "get_native_note_processing",
            new { noteId });
        Assert.False(afterStart.IsError);
        var afterStartData = afterStart.Payload.GetProperty("data");
        Assert.Equal("get_native_note_processing",
            afterStartData.GetProperty("receipt").GetProperty("operation").GetString());
        var afterStartStatus = afterStartData.GetProperty("result").GetProperty("status");
        if (start.IsError)
        {
            Assert.True(
                !string.IsNullOrWhiteSpace(afterStartStatus.GetProperty("pendingReason").GetString())
                || afterStartStatus.GetProperty("issues").GetArrayLength() > 0,
                "A failed processing start must leave a visible pending reason or issue");
        }

        var retry = await host.CallAsync(host.McpAWrite, "retry_native_note_processing", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            taskId = Guid.NewGuid().ToString("D"),
            retryId = Guid.NewGuid().ToString("D"),
            expectedRevision = Guid.NewGuid().ToString("D"),
            stepKeys = new[] { "record" },
            consent = Consent(),
            acknowledgePossibleDuplicateCharge = true,
        });
        Assert.True(retry.IsError);
        Assert.Contains(retry.Payload.GetProperty("code").GetInt32(), new[] { 404, 409 });

        var adopt = await host.CallAsync(host.McpAWrite, "adopt_native_note_result", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            artifactId = Guid.NewGuid().ToString("D"),
            versionId = Guid.NewGuid().ToString("D"),
            expectedHeadRevision = Guid.NewGuid().ToString("D"),
        });
        Assert.True(adopt.IsError);
        Assert.Contains(adopt.Payload.GetProperty("code").GetInt32(), new[] { 404, 409 });

        var suggestions = await host.CallAsync(host.McpARead, "get_native_note_suggestions",
            new { noteId });
        Assert.False(suggestions.IsError);
        Assert.Equal(0, suggestions.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("total").GetInt32());

        var extraction = await host.CallAsync(host.McpAWrite,
            "extract_native_note_suggestions", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                artifactId = Guid.NewGuid().ToString("D"),
                versionId = Guid.NewGuid().ToString("D"),
                expectedContentSha256 = new string('0', 64),
                consent = Consent(),
                acknowledgePossibleCharge = false,
            });
        Assert.True(extraction.IsError);
        Assert.Contains(extraction.Payload.GetProperty("code").GetInt32(), new[] { 404, 409, 503 });

        var confirm = await host.CallAsync(host.McpAWrite, "confirm_native_note_suggestion", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            suggestionId = Guid.NewGuid().ToString("D"),
            expectedRevision = Guid.NewGuid().ToString("D"),
        });
        Assert.True(confirm.IsError);
        Assert.Equal(404, confirm.Payload.GetProperty("code").GetInt32());
        var dismiss = await host.CallAsync(host.McpAWrite, "dismiss_native_note_suggestion", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            suggestionId = Guid.NewGuid().ToString("D"),
            expectedRevision = Guid.NewGuid().ToString("D"),
        });
        Assert.True(dismiss.IsError);
        Assert.Equal(404, dismiss.Payload.GetProperty("code").GetInt32());
        Assert.Equal(0, (await host.CallAsync(host.McpARead, "get_native_note_suggestions",
                new { noteId })).Payload.GetProperty("data").GetProperty("result")
            .GetProperty("total").GetInt32());
    }

    private static object Consent() => new
    {
        modelId = Guid.NewGuid().ToString("D"),
        modelRevision = Guid.NewGuid().ToString("D"),
        endpoint = "http://127.0.0.1/unused",
        confirmed = true,
    };

    private static byte[] Package(NoteMaterialUploadStoreTests.Fixture fixture,
        string? extraEntry = null, bool includeMaterialVersionJson = true)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeMaterialVersionJson)
                AddEntry(archive, "material-version.json",
                    Encoding.UTF8.GetBytes(fixture.Request.InputJson));
            AddEntry(archive, "materials.json", Encoding.UTF8.GetBytes(fixture.Request.ManifestJson));
            using var manifest = JsonDocument.Parse(fixture.Request.ManifestJson);
            var index = 0;
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
                AddEntry(archive, file.GetProperty("path").GetString()!, fixture.Content[index++]);
            if (extraEntry is not null)
                AddEntry(archive, extraEntry, Encoding.UTF8.GetBytes("escape"));
        }
        return output.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static Dictionary<string, byte[]> ReadZip(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            using var stream = entry.Open();
            using var output = new MemoryStream();
            stream.CopyTo(output);
            result[entry.FullName] = output.ToArray();
        }
        return result;
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static HttpRequestMessage Rpc(string method, object @params)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method,
                @params,
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    private static JsonElement ReadRpc(HttpResponseMessage response)
    {
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
        var data = text.Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(data is null ? text : data[5..].Trim());
        return document.RootElement.Clone();
    }

    private sealed record ToolCall(bool IsError, JsonElement Payload);

    private sealed record McpHandle(Guid Id, string Token, HttpClient Client);

    private sealed class McpHost : IAsyncDisposable
    {
        private readonly TempMigrationDatabase _database;
        private readonly string _root;

        private McpHost(WebApplicationFactory<Program> factory, TempMigrationDatabase database,
            string root, HttpClient restA, HttpClient restB, McpHandle aRead, McpHandle aWrite,
            McpHandle bWrite, Guid userA, Guid userB)
        {
            Factory = factory;
            _database = database;
            _root = root;
            RestA = restA;
            RestB = restB;
            McpARead = aRead.Client;
            McpAWrite = aWrite.Client;
            McpBWrite = bWrite.Client;
            UserA = userA;
            UserB = userB;
        }

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient RestA { get; }
        public HttpClient RestB { get; }
        public HttpClient McpARead { get; }
        public HttpClient McpAWrite { get; }
        public HttpClient McpBWrite { get; }
        public Guid UserA { get; }
        public Guid UserB { get; }
        public NoteMaterialUploadStore Store => Factory.Services.GetRequiredService<NoteMaterialUploadStore>();

        public static async Task<McpHost> CreateAsync()
        {
            var database = await TempMigrationDatabase.CreateAsync("test_native_note_mcp");
            await database.MigrateAsync();
            var root = Path.Combine(Path.GetTempPath(), "pim-native-note-mcp-pg",
                Guid.NewGuid().ToString("N"));
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test")
                    .UseSetting("DisableHangfire", "true")
                    .UseSetting("Database:Migrations:FailFast", "false")
                    .UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz")
                    .UseSetting("ConnectionStrings:DefaultConnection", database.ConnectionString)
                    .UseSetting("NativeNotes:StorageRoot", Path.Combine(root, "materials"))
                    .UseSetting("ManagedStorage:Root", Path.Combine(root, "binary"))
                    .UseSetting("DataProtection:KeysPath", Path.Combine(root, "keys"))
                    .ConfigureServices(services =>
                    {
                        var descriptor = services.SingleOrDefault(d =>
                            d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                        if (descriptor is not null) services.Remove(descriptor);
                        services.AddDbContext<PimDbContext>(options =>
                            options.UseNpgsql(database.ConnectionString));
                    });
            });

            var sessionA = await AuthRegistrationTestHost.SeedAndLoginAsync(factory,
                "native-note-mcp-a-" + Guid.NewGuid().ToString("N"), "admin");
            var userBName = "native-note-mcp-b-" + Guid.NewGuid().ToString("N");
            await AuthRegistrationTestHost.SeedUserAsync(factory, userBName, "user");
            var sessionB = await AuthRegistrationTestHost.LoginOkAsync(factory, userBName);
            var restA = AuthRegistrationTestHost.Authed(factory, sessionA.AccessToken);
            var restB = AuthRegistrationTestHost.Authed(factory, sessionB.AccessToken);
            var aRead = await CreateMcpAsync(restA, factory, "a-read", writes: false);
            var aWrite = await CreateMcpAsync(restA, factory, "a-write", writes: true);
            var bWrite = await CreateMcpAsync(restB, factory, "b-write", writes: true);
            return new McpHost(factory, database, root, restA, restB, aRead, aWrite, bWrite,
                sessionA.UserId, sessionB.UserId);
        }

        public async Task<NoteMaterialUploadStoreTests.Fixture> SeedNoteAsync(Guid owner,
            NoteMaterialUploadStoreTests.Fixture? fixture = null)
        {
            fixture ??= NoteMaterialUploadStoreTests.Fixture.Create();
            await Store.BeginAsync(owner, fixture.Request);
            for (var file = 0; file < fixture.Content.Count; file++)
            {
                var bytes = fixture.Content[file];
                for (var offset = 0; offset < bytes.Length;
                     offset += NoteMaterialUploadOptions.PartSizeBytes)
                {
                    var chunk = bytes.AsSpan(offset,
                        Math.Min(NoteMaterialUploadOptions.PartSizeBytes, bytes.Length - offset))
                        .ToArray();
                    await Store.UploadPartAsync(owner, fixture.Request.VersionId, file,
                        offset / NoteMaterialUploadOptions.PartSizeBytes, Sha256(chunk),
                        new MemoryStream(chunk, writable: false));
                }
            }
            await Store.CompleteAsync(owner, fixture.Request.VersionId);
            return fixture;
        }

        public async Task<JsonElement> ReadNoteAsync(HttpClient mcp, Guid noteId)
        {
            var call = await CallAsync(mcp, "get_native_note", new { noteId });
            Assert.False(call.IsError, call.Payload.GetRawText());
            return call.Payload;
        }

        public async Task<ToolCall> CallAsync(HttpClient mcp, string name, object args)
        {
            using var response = await mcp.SendAsync(Rpc("tools/call", new
            {
                name,
                arguments = args,
            }));
            var wire = ReadRpc(await response);
            Assert.False(wire.TryGetProperty("error", out _), wire.GetRawText());
            var result = wire.GetProperty("result");
            var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
            var text = result.GetProperty("content").EnumerateArray()
                .First(item => item.GetProperty("type").GetString() == "text")
                .GetProperty("text").GetString();
            using var payload = JsonDocument.Parse(text!);
            return new ToolCall(isError, payload.RootElement.Clone());
        }

        public async ValueTask DisposeAsync()
        {
            McpARead.Dispose();
            McpAWrite.Dispose();
            McpBWrite.Dispose();
            RestA.Dispose();
            RestB.Dispose();
            await Factory.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static async Task<McpHandle> CreateMcpAsync(HttpClient rest,
            WebApplicationFactory<Program> factory, string name, bool writes)
        {
            var created = await SuccessAsync(await rest.PostAsJsonAsync("/api/v1/mcp/clients",
                new { name }));
            var id = created.GetProperty("client").GetProperty("id").GetGuid();
            var token = created.GetProperty("token").GetString()!;
            if (writes)
            {
                var permissions = McpToolCatalog.DefaultPermissions();
                permissions["write"] = permissions["write"]
                    .ToDictionary(item => item.Key, _ => true, StringComparer.Ordinal);
                await SuccessAsync(await rest.PutAsJsonAsync($"/api/v1/mcp/clients/{id}",
                    new { permissions }));
            }
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return new McpHandle(id, token, client);
        }

        private static async Task<JsonElement> SuccessAsync(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {text}");
            using var document = JsonDocument.Parse(text);
            Assert.Equal(0, document.RootElement.GetProperty("code").GetInt32());
            return document.RootElement.GetProperty("data").Clone();
        }
    }
}
