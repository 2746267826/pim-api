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
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Core.Notes;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.Module.Mcp.Services;
using Pim.Module.NoteTaskSuggestions.Entities;
using Pim.Module.NoteTaskSuggestions.DTOs;
using Pim.Module.NoteTaskSuggestions.Jobs;
using Pim.Module.NoteTaskSuggestions.Services;
using Pim.UnitTests.Api;
using Pim.UnitTests.Harness.RealDb;
using Pim.UnitTests.Notes;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// R05 acceptance over a real PostgreSQL database and the real HTTP pipeline:
/// owner isolation, read-token write denial, package upload/download round-trip,
/// requestId replay, CAS rejection, conflict candidates, recycle-bin recovery,
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

        var bList = await host.CallAsync(host.McpBRead, "get_native_notes", new { });
        Assert.Equal(0, bList.Payload.GetProperty("data").GetProperty("total").GetInt32());

        foreach (var tool in new[]
                 {
                     "get_native_note", "get_native_note_versions", "get_native_note_results",
                     "get_native_note_processing", "get_native_note_package",
                     "get_native_note_suggestions",
                 })
        {
            var denied = await host.CallAsync(host.McpBRead, tool,
                new { noteId = fixture.Request.NoteId });
            Assert.True(denied.IsError, $"{tool} must not expose another owner's note");
            Assert.Equal(404, denied.Payload.GetProperty("code").GetInt32());
        }

        var package = Package(fixture);
        var crossOwnerUpload = await host.CallAsync(host.McpBWrite, "upload_native_note_package", new
        {
            noteId = fixture.Request.NoteId,
            requestId = Guid.NewGuid().ToString("D"),
            packageBase64 = Convert.ToBase64String(package),
        });
        // Note ids are owner-scoped. B may create its own note with the same client-supplied id,
        // but it must never see or replace A's material/head.
        Assert.False(crossOwnerUpload.IsError, crossOwnerUpload.Payload.GetRawText());
        var ownerAVersions = (await host.CallAsync(host.McpARead, "get_native_note_versions",
                new { noteId = fixture.Request.NoteId })).Payload.GetProperty("data");
        var ownerBVersions = (await host.CallAsync(host.McpBRead, "get_native_note_versions",
                new { noteId = fixture.Request.NoteId })).Payload.GetProperty("data");
        Assert.Equal(1, ownerAVersions.GetProperty("total").GetInt32());
        Assert.Equal(1, ownerBVersions.GetProperty("total").GetInt32());
        Assert.Equal(fixture.Request.VersionId,
            ownerAVersions.GetProperty("currentVersionId").GetGuid());
        Assert.NotEqual(ownerAVersions.GetProperty("currentVersionId").GetGuid(),
            ownerBVersions.GetProperty("currentVersionId").GetGuid());

        var download = await host.CallAsync(host.McpARead, "get_native_note_package",
            new { noteId = fixture.Request.NoteId });
        Assert.False(download.IsError, download.Payload.GetRawText());
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

        var writeWithReadToken = await host.CallAsync(host.McpARead, "delete_native_note", new
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
    public async Task VerifiedReadTokens_AreBoundToExactNativeNoteGetRoutes()
    {
        await using var host = await McpHost.CreateAsync();
        var fixture = await host.SeedNoteAsync(host.UserA);
        var noteId = fixture.Request.NoteId;
        const string listPath = "/api/v1/native-notes/mcp/notes";
        var detailPath = $"{listPath}/{noteId:D}";

        // A real /mcp/verify JWT for an ordinary read tool must not inherit any native-note
        // route merely because the method is GET.
        foreach (var tool in new[] { "get_tasks", "get_events", "get_quick_notes" })
        {
            using var client = await host.VerifyReadTokenAsync(tool);
            foreach (var path in new[]
                     {
                         listPath,
                         detailPath,
                         detailPath + "/versions",
                         detailPath + "/package",
                         "/api/v1/native-notes/upload-limits",
                     })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<ApiResponse<string>>();
                Assert.Equal(40302, body!.Code);
            }
        }

        using var listClient = await host.VerifyReadTokenAsync("get_native_notes");
        Assert.Equal(HttpStatusCode.OK, (await listClient.GetAsync(listPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await listClient.GetAsync(detailPath)).StatusCode);

        using var detailClient = await host.VerifyReadTokenAsync("get_native_note");
        Assert.Equal(HttpStatusCode.OK, (await detailClient.GetAsync(detailPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await detailClient.GetAsync(listPath)).StatusCode);

        using var versionsClient = await host.VerifyReadTokenAsync("get_native_note_versions");
        Assert.Equal(HttpStatusCode.OK,
            (await versionsClient.GetAsync(detailPath + "/versions")).StatusCode);
        // Unmapped native-note GETs stay denied even for a native-note read tool.
        using var unmapped = await versionsClient.GetAsync("/api/v1/native-notes/upload-limits");
        Assert.Equal(HttpStatusCode.Forbidden, unmapped.StatusCode);
        Assert.Equal(40302,
            (await unmapped.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);

        using var operationClient = await host.VerifyReadTokenAsync("get_native_note_operation");
        using var missingOperation = await operationClient.GetAsync(
            $"{detailPath}/operations/{Guid.NewGuid():D}");
        Assert.Equal(HttpStatusCode.NotFound, missingOperation.StatusCode);
        Assert.Equal(40401,
            (await missingOperation.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await operationClient.GetAsync(detailPath + "/versions")).StatusCode);
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

        var emptyHeadCas = await host.CallAsync(host.McpAWrite, "upload_native_note_package",
            new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                packageBase64 = Convert.ToBase64String(package),
                expectedCurrentVersionId = Guid.NewGuid().ToString("D"),
            });
        Assert.True(emptyHeadCas.IsError, emptyHeadCas.Payload.GetRawText());
        Assert.Equal(409, emptyHeadCas.Payload.GetProperty("code").GetInt32());
        var emptyRevisionCas = await host.CallAsync(host.McpAWrite,
            "upload_native_note_package", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                packageBase64 = Convert.ToBase64String(package),
                expectedRevision = Guid.NewGuid().ToString("D"),
            });
        Assert.True(emptyRevisionCas.IsError, emptyRevisionCas.Payload.GetRawText());
        Assert.Equal(409, emptyRevisionCas.Payload.GetProperty("code").GetInt32());
        Assert.Equal(0, (await host.CallAsync(host.McpARead, "get_native_notes", new { }))
            .Payload.GetProperty("data").GetProperty("total").GetInt32());

        var uploaded = await host.CallAsync(host.McpAWrite, "upload_native_note_package", uploadArgs);
        Assert.False(uploaded.IsError, uploaded.Payload.GetRawText());
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

        var activity = await host.RestA.GetFromJsonAsync<JsonElement>("/api/v1/mcp/activity");
        Assert.Equal(0, activity.GetProperty("code").GetInt32());
        var uploadActivity = activity.GetProperty("data").EnumerateArray()
            .Last(item => item.GetProperty("toolName").GetString() == "upload_native_note_package");
        Assert.Contains("[redacted base64", uploadActivity.GetProperty("argumentsSummary").GetString());
        Assert.DoesNotContain(Convert.ToBase64String(package)[..32],
            uploadActivity.GetProperty("argumentsSummary").GetString());

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
        var mismatchedRevisionReplay = await host.CallAsync(host.McpAWrite,
            "upload_native_note_package", new
            {
                noteId,
                requestId = requestId.ToString("D"),
                packageBase64 = Convert.ToBase64String(package),
                promote = true,
                expectedRevision = Guid.NewGuid().ToString("D"),
            });
        Assert.True(mismatchedRevisionReplay.IsError);
        Assert.Equal(409, mismatchedRevisionReplay.Payload.GetProperty("code").GetInt32());

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
        Assert.False(download.IsError, download.Payload.GetRawText());
        var zipBytes = Convert.FromBase64String(
            download.Payload.GetProperty("data").GetProperty("contentBase64").GetString()!);
        var entries = ReadZip(zipBytes);
        var metadata = JsonNode.Parse(Encoding.UTF8.GetString(entries["material-version.json"]))!.AsObject();
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(entries["materials.json"]))!.AsObject();
        Assert.Equal(versionId.ToString("D"), metadata["id"]!.GetValue<string>());
        Assert.Equal(versionId.ToString("D"), manifest["versionId"]!.GetValue<string>());
        Assert.Equal(Sha256(entries["material-version.json"]),
            manifest["inputSha256"]!.GetValue<string>());
        using (var materialVersion = JsonDocument.Parse(entries["material-version.json"]))
        {
            Assert.Equal(entries["note.json"], Encoding.UTF8.GetBytes(
                materialVersion.RootElement.GetProperty("note").GetRawText()));
        }

        var reassembled = new List<byte>();
        var chunkOffset = 0;
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var chunkCall = await host.CallAsync(host.McpARead, "get_native_note_package", new
            {
                noteId, offset = chunkOffset, maxBytes = 97,
            });
            Assert.False(chunkCall.IsError, chunkCall.Payload.GetRawText());
            var chunkData = chunkCall.Payload.GetProperty("data");
            var chunk = Convert.FromBase64String(
                chunkData.GetProperty("contentBase64").GetString()!);
            Assert.Equal(chunkOffset, chunkData.GetProperty("offset").GetInt32());
            Assert.Equal(chunk.Length, chunkData.GetProperty("chunkByteLength").GetInt64());
            Assert.Equal(Sha256(chunk), chunkData.GetProperty("chunkSha256").GetString());
            Assert.Equal(zipBytes.LongLength, chunkData.GetProperty("byteLength").GetInt64());
            Assert.Equal(zipBytes.LongLength,
                chunkData.GetProperty("totalByteLength").GetInt64());
            Assert.Equal(Sha256(zipBytes), chunkData.GetProperty("contentSha256").GetString());
            Assert.Equal(Sha256(zipBytes), chunkData.GetProperty("packageSha256").GetString());
            reassembled.AddRange(chunk);
            if (!chunkData.GetProperty("hasMore").GetBoolean())
                break;
            chunkOffset = chunkData.GetProperty("nextOffset").GetInt32();
            Assert.Equal(reassembled.Count, chunkOffset);
        }
        Assert.Equal(zipBytes, reassembled);

        var negativeOffset = await host.CallAsync(host.McpARead, "get_native_note_package",
            new { noteId, offset = -1 });
        Assert.True(negativeOffset.IsError);
        Assert.Equal(400, negativeOffset.Payload.GetProperty("code").GetInt32());
        var pastEnd = await host.CallAsync(host.McpARead, "get_native_note_package",
            new { noteId, offset = zipBytes.Length });
        Assert.True(pastEnd.IsError);
        Assert.Equal(400, pastEnd.Payload.GetProperty("code").GetInt32());
        var emptyChunk = await host.CallAsync(host.McpARead, "get_native_note_package",
            new { noteId, maxBytes = 0 });
        Assert.True(emptyChunk.IsError);
        Assert.Equal(400, emptyChunk.Payload.GetProperty("code").GetInt32());
        var oversizedChunk = await host.CallAsync(host.McpARead, "get_native_note_package",
            new { noteId, maxBytes = 16 * 1024 * 1024 + 1 });
        Assert.True(oversizedChunk.IsError);
        Assert.Equal(400, oversizedChunk.Payload.GetProperty("code").GetInt32());

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
        var missingManifest = await host.CallAsync(host.McpAWrite,
            "upload_native_note_package", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                packageBase64 = Convert.ToBase64String(Package(fixture,
                    includeMaterialsJson: false)),
            });
        Assert.True(missingManifest.IsError);
        Assert.Equal(400, missingManifest.Payload.GetProperty("code").GetInt32());
        var duplicateEntry = await host.CallAsync(host.McpAWrite,
            "upload_native_note_package", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                packageBase64 = Convert.ToBase64String(Package(fixture, extraEntry: "note.json")),
            });
        Assert.True(duplicateEntry.IsError);
        Assert.Equal(400, duplicateEntry.Payload.GetProperty("code").GetInt32());
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
    public async Task FailedOperationIsRecordedAndQueryableByRequestId()
    {
        await using var host = await McpHost.CreateAsync();
        var noteId = Guid.NewGuid();
        var requestId = Guid.NewGuid().ToString("D");
        var uploadArgs = new
        {
            noteId,
            requestId,
            packageBase64 = "not-base64",
        };

        var failed = await host.CallAsync(host.McpAWrite, "upload_native_note_package",
            uploadArgs);
        Assert.True(failed.IsError);
        var failureDetails = failed.Payload.GetProperty("details");
        Assert.Equal(40001, failureDetails.GetProperty("code").GetInt32());
        var receipt = failureDetails.GetProperty("data").GetProperty("receipt");
        var operationId = receipt.GetProperty("operationId").GetString();
        Assert.StartsWith("nop_", operationId, StringComparison.Ordinal);
        Assert.Equal("failed", receipt.GetProperty("status").GetString());
        Assert.False(receipt.TryGetProperty("requestId", out _));
        Assert.DoesNotContain("\"requestId\"", failed.Payload.GetRawText(),
            StringComparison.OrdinalIgnoreCase);

        var lookup = await host.CallAsync(host.McpARead, "get_native_note_operation",
            new { noteId, requestId });
        Assert.False(lookup.IsError, lookup.Payload.GetRawText());
        var status = lookup.Payload.GetProperty("data");
        Assert.Equal("failed", status.GetProperty("state").GetString());
        Assert.Equal(operationId, status.GetProperty("operationId").GetString());
        Assert.Equal("failed", status.GetProperty("receipt").GetProperty("status").GetString());
        Assert.Equal(operationId, status.GetProperty("receipt").GetProperty("operationId").GetString());

        var replay = await host.CallAsync(host.McpAWrite, "upload_native_note_package",
            uploadArgs);
        Assert.True(replay.IsError);
        var replayReceipt = replay.Payload.GetProperty("details").GetProperty("data")
            .GetProperty("receipt");
        Assert.Equal(operationId, replayReceipt.GetProperty("operationId").GetString());
        Assert.Equal("failed", replayReceipt.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task ExpiredReplayThroughMcpReturnsActionableConflict()
    {
        await using var host = await McpHost.CreateAsync();
        var noteId = Guid.NewGuid();
        var fixture = NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId);
        var package = Package(fixture);
        var requestId = Guid.NewGuid().ToString("D");
        var uploadArgs = new
        {
            noteId,
            requestId,
            packageBase64 = Convert.ToBase64String(package),
            promote = true,
        };

        var uploaded = await host.CallAsync(host.McpBWrite,
            "upload_native_note_package", uploadArgs);
        Assert.False(uploaded.IsError, uploaded.Payload.GetRawText());

        await using (var scope = host.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var row = await db.NoteMcpOperations.SingleAsync(x =>
                x.OwnerId == host.UserB && x.RequestId == Guid.Parse(requestId));
            row.CompletedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(31);
            await db.SaveChangesAsync();
        }

        var lookup = await host.CallAsync(host.McpBRead, "get_native_note_operation",
            new { noteId, requestId });
        Assert.False(lookup.IsError, lookup.Payload.GetRawText());
        var status = lookup.Payload.GetProperty("data");
        Assert.Equal("succeeded", status.GetProperty("state").GetString());
        var receipt = status.GetProperty("receipt");
        Assert.Equal("applied", receipt.GetProperty("status").GetString());
        Assert.Contains("超过保留期", receipt.GetProperty("message").GetString());
        Assert.DoesNotContain(requestId, lookup.Payload.GetRawText(), StringComparison.Ordinal);

        var replay = await host.CallAsync(host.McpBWrite,
            "upload_native_note_package", uploadArgs);
        Assert.True(replay.IsError, replay.Payload.GetRawText());
        Assert.Equal(409, replay.Payload.GetProperty("code").GetInt32());
        var details = replay.Payload.GetProperty("details");
        Assert.Equal(40901, details.GetProperty("code").GetInt32());
        var message = details.GetProperty("message").GetString();
        Assert.Contains("保留期", message);
        Assert.Contains("请求标识", message);
        Assert.DoesNotContain("requestId", details.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(requestId, replay.Payload.GetRawText(), StringComparison.Ordinal);

        await using var verifyScope = host.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var expired = await verifyDb.NoteMcpOperations.AsNoTracking().SingleAsync(x =>
            x.OwnerId == host.UserB && x.RequestId == Guid.Parse(requestId));
        Assert.Equal("completed", expired.State);
        Assert.Null(expired.ResponseJson);
        Assert.NotNull(expired.ResponsePurgedAt);
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

        var pending = NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId);
        await host.Store.BeginAsync(host.UserA, pending.Request);
        var cancelRequestId = Guid.NewGuid().ToString("D");
        var cancelled = await host.CallAsync(host.McpAWrite, "cancel_native_note_upload", new
        {
            noteId, requestId = cancelRequestId, versionId = pending.Request.VersionId,
        });
        Assert.False(cancelled.IsError, cancelled.Payload.GetRawText());
        Assert.True(cancelled.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("removed").GetBoolean());
        var cancelReplay = await host.CallAsync(host.McpAWrite, "cancel_native_note_upload", new
        {
            noteId, requestId = cancelRequestId, versionId = pending.Request.VersionId,
        });
        Assert.True(cancelReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        Assert.Equal(2, (await host.CallAsync(host.McpARead, "get_native_note_versions",
                new { noteId })).Payload.GetProperty("data").GetProperty("total").GetInt32());

        var active = await host.ReadNoteAsync(host.McpARead, noteId);
        var activeRevision = active.GetProperty("data").GetProperty("revision").GetGuid();
        var deleteRequestId = Guid.NewGuid().ToString("D");
        var recycled = await host.CallAsync(host.McpAWrite, "delete_native_note", new
        {
            noteId, requestId = deleteRequestId, expectedRevision = activeRevision.ToString("D"),
        });
        Assert.False(recycled.IsError, recycled.Payload.GetRawText());
        Assert.Equal("Recycled", recycled.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        var recycleReplay = await host.CallAsync(host.McpAWrite, "delete_native_note", new
        {
            noteId, requestId = deleteRequestId, expectedRevision = activeRevision.ToString("D"),
        });
        Assert.True(recycleReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        var blockedRecycleUpload = await host.CallAsync(host.McpAWrite,
            "upload_native_note_package", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                packageBase64 = Convert.ToBase64String(Package(first)),
            });
        Assert.True(blockedRecycleUpload.IsError);
        Assert.Equal(409, blockedRecycleUpload.Payload.GetProperty("code").GetInt32());

        var recycledRevision = recycled.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("revision").GetGuid();
        var restored = await host.CallAsync(host.McpAWrite, "restore_native_note", new
        {
            noteId, requestId = Guid.NewGuid().ToString("D"),
            expectedRevision = recycledRevision.ToString("D"),
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
        Assert.Equal("Recycled", deleted.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        Assert.Equal(0, (await host.CallAsync(host.McpARead, "get_native_notes", new { }))
            .Payload.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(1, (await host.CallAsync(host.McpARead, "get_native_notes",
                new { status = "Recycled" })).Payload.GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal("Recycled", (await host.ReadNoteAsync(host.McpARead, noteId))
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
        Assert.Empty(results.Payload.GetProperty("data").GetProperty("artifacts").EnumerateArray());
        Assert.NotEqual("Completed",
            results.Payload.GetProperty("data").GetProperty("processing")
                .GetProperty("state").GetString());

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
                .GetProperty("state").GetString();
            Assert.NotEqual("Completed", state);
        }

        var afterStart = await host.CallAsync(host.McpARead, "get_native_note_processing",
            new { noteId });
        Assert.False(afterStart.IsError);
        var afterStartData = afterStart.Payload.GetProperty("data");
        Assert.Equal("get_native_note_processing",
            afterStartData.GetProperty("receipt").GetProperty("operation").GetString());
        var afterStartStatus = afterStartData.GetProperty("result");
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
            versionId = versionId.ToString("D"),
            stepKeys = new[] { "record" },
            consent = Consent(),
            acknowledgePossibleDuplicateCharge = true,
        });
        Assert.True(retry.IsError);
        Assert.Contains(retry.Payload.GetProperty("code").GetInt32(), new[] { 404, 409 });
        var retryWithoutSteps = await host.CallAsync(host.McpAWrite,
            "retry_native_note_processing", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                versionId = versionId.ToString("D"),
                stepKeys = Array.Empty<string>(),
                consent = Consent(),
                acknowledgePossibleDuplicateCharge = false,
            });
        Assert.True(retryWithoutSteps.IsError);
        Assert.Equal(400, retryWithoutSteps.Payload.GetProperty("code").GetInt32());

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
                acknowledgePossibleCharge = true,
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

    [SkippableFact]
    public async Task AdoptAndSuggestionMutations_SucceedWithReceiptsAndIdempotentReplay()
    {
        await using var host = await McpHost.CreateAsync();
        var fixture = await host.SeedNoteAsync(host.UserA);
        var noteId = fixture.Request.NoteId;

        var artifact = await host.DerivedStore.EnsureArtifactAsync(host.UserA, noteId,
            NoteDerivedArtifactKind.CompleteRecord, default);
        var derivedVersionId = Guid.NewGuid();
        await host.DerivedStore.AppendGeneratedAsync(host.UserA, artifact.ArtifactId,
            new NoteDerivedVersionDraft(
                derivedVersionId,
                NoteDerivedArtifactKind.CompleteRecord,
                null,
                null,
                fixture.Request.VersionId,
                null,
                "# 采用测试\n\n已覆盖真实 PostgreSQL 派生版本。",
                new string('a', 64),
                new string('b', 64),
                "plan-v1",
                "prompt-v1",
                null,
                null,
                null,
                null,
                null),
            default);

        var adoptRequestId = Guid.NewGuid();
        var adoptArgs = new
        {
            noteId,
            requestId = adoptRequestId.ToString("D"),
            artifactId = artifact.ArtifactId.ToString("D"),
            versionId = derivedVersionId.ToString("D"),
            expectedHeadRevision = artifact.HeadRevision.ToString("D"),
        };
        var adopted = await host.CallAsync(host.McpAWrite, "adopt_native_note_result", adoptArgs);
        Assert.False(adopted.IsError, adopted.Payload.GetRawText());
        var adoptedData = adopted.Payload.GetProperty("data");
        Assert.Equal("adopt_native_note_result",
            adoptedData.GetProperty("receipt").GetProperty("operation").GetString());
        Assert.False(adoptedData.GetProperty("receipt").GetProperty("replayed").GetBoolean());
        Assert.Equal(derivedVersionId,
            adoptedData.GetProperty("result").GetProperty("versionId").GetGuid());
        Assert.Equal(derivedVersionId,
            adoptedData.GetProperty("result").GetProperty("artifact")
                .GetProperty("headVersionId").GetGuid());
        Assert.False(adoptedData.GetProperty("result").GetProperty("hasConflict").GetBoolean());

        var adoptedReplay = await host.CallAsync(host.McpAWrite, "adopt_native_note_result",
            adoptArgs);
        Assert.False(adoptedReplay.IsError, adoptedReplay.Payload.GetRawText());
        Assert.True(adoptedReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        Assert.Equal(derivedVersionId, adoptedReplay.Payload.GetProperty("data")
            .GetProperty("result").GetProperty("artifact").GetProperty("headVersionId").GetGuid());
        var adoptConflict = await host.CallAsync(host.McpAWrite, "adopt_native_note_result",
            new
            {
                noteId,
                requestId = adoptRequestId.ToString("D"),
                artifactId = artifact.ArtifactId.ToString("D"),
                versionId = Guid.NewGuid().ToString("D"),
                expectedHeadRevision = artifact.HeadRevision.ToString("D"),
            });
        Assert.True(adoptConflict.IsError);
        Assert.Equal(409, adoptConflict.Payload.GetProperty("code").GetInt32());

        var dismissTarget = await host.SeedSuggestionAsync(host.UserA, noteId, "待忽略建议");
        var dismissRequestId = Guid.NewGuid();
        var dismissArgs = new
        {
            noteId,
            requestId = dismissRequestId.ToString("D"),
            suggestionId = dismissTarget.SuggestionId.ToString("D"),
            expectedRevision = dismissTarget.Revision.ToString("D"),
        };
        var dismissed = await host.CallAsync(host.McpAWrite,
            "dismiss_native_note_suggestion", dismissArgs);
        Assert.False(dismissed.IsError, dismissed.Payload.GetRawText());
        Assert.Equal("Dismissed", dismissed.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        var dismissedReplay = await host.CallAsync(host.McpAWrite,
            "dismiss_native_note_suggestion", dismissArgs);
        Assert.True(dismissedReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());

        var confirmTarget = await host.SeedSuggestionAsync(host.UserA, noteId, "待确认建议");
        var confirmRequestId = Guid.NewGuid();
        var confirmArgs = new
        {
            noteId,
            requestId = confirmRequestId.ToString("D"),
            suggestionId = confirmTarget.SuggestionId.ToString("D"),
            expectedRevision = confirmTarget.Revision.ToString("D"),
        };
        var confirmed = await host.CallAsync(host.McpAWrite,
            "confirm_native_note_suggestion", confirmArgs);
        Assert.False(confirmed.IsError, confirmed.Payload.GetRawText());
        var confirmedResult = confirmed.Payload.GetProperty("data").GetProperty("result");
        Assert.Equal("Confirmed", confirmedResult.GetProperty("status").GetString());
        var taskId = confirmedResult.GetProperty("taskId").GetGuid();
        Assert.NotEqual(Guid.Empty, taskId);
        var confirmedReplay = await host.CallAsync(host.McpAWrite,
            "confirm_native_note_suggestion", confirmArgs);
        Assert.True(confirmedReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        Assert.Equal(taskId, confirmedReplay.Payload.GetProperty("data")
            .GetProperty("result").GetProperty("taskId").GetGuid());

        var ownerBCannotConfirm = await host.CallAsync(host.McpBWrite,
            "confirm_native_note_suggestion", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                suggestionId = confirmTarget.SuggestionId.ToString("D"),
                expectedRevision = confirmedResult.GetProperty("revision").GetGuid().ToString("D"),
            });
        Assert.True(ownerBCannotConfirm.IsError);
        Assert.Equal(404, ownerBCannotConfirm.Payload.GetProperty("code").GetInt32());

        var suggestions = await host.CallAsync(host.McpARead, "get_native_note_suggestions",
            new { noteId });
        Assert.False(suggestions.IsError);
        Assert.Equal(2, suggestions.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task RecycledNoteBlocksRestSuggestionMutationsAndStopsQueuedExtraction()
    {
        await using var host = await McpHost.CreateAsync();
        var fixture = await host.SeedNoteAsync(host.UserA);
        var noteId = fixture.Request.NoteId;
        var snapshot = await host.Store.GetMcpSnapshotAsync(host.UserA, noteId, 1);
        await host.Store.RecycleAsync(host.UserA, noteId, snapshot.Revision);

        var extraction = new NoteTaskSuggestionExtractionEntity
        {
            UserId = host.UserA,
            RequestId = Guid.NewGuid(),
            RequestFingerprint = NoteTaskSuggestionService.Hash("recycled-rest"),
            NoteId = noteId,
            ArtifactId = Guid.NewGuid(),
            ArtifactVersionId = fixture.Request.VersionId,
            SourceContentSha256 = new string('a', 64),
            SourceInputSha256 = new string('b', 64),
            SourceManifestSha256 = new string('c', 64),
            SourceContentMarkdown = "recycled note content",
            ModelProfileId = Guid.NewGuid(),
            ModelRevision = Guid.NewGuid(),
            ModelEndpoint = "https://synthetic.invalid/v1",
            PromptVersion = NoteTaskSuggestionPrompt.Version,
            Status = NoteTaskSuggestionExtractionStatuses.Queued,
        };
        var suggestion = new NoteTaskSuggestionEntity
        {
            UserId = host.UserA,
            ExtractionId = extraction.Id,
            NoteId = noteId,
            ArtifactId = extraction.ArtifactId,
            ArtifactVersionId = extraction.ArtifactVersionId,
            SourceContentSha256 = extraction.SourceContentSha256,
            SourceInputSha256 = extraction.SourceInputSha256,
            SourceManifestSha256 = extraction.SourceManifestSha256,
            Title = "回收站建议",
            Kind = NoteTaskSuggestionKinds.Task,
            Priority = 5,
            Status = NoteTaskSuggestionStatuses.Draft,
        };
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            db.AddRange(extraction, suggestion);
            await db.SaveChangesAsync();
        }

        var start = new StartNoteTaskSuggestionExtractionRequest(
            Guid.NewGuid(), host.UserA, noteId, extraction.ArtifactId,
            fixture.Request.VersionId, extraction.SourceContentSha256,
            new NoteDerivedGenerationConsent(
                Guid.NewGuid(), Guid.NewGuid(), "https://text.invalid/v1", true),
            AcknowledgePossibleCharge: true);
        var extractionResponse = await host.RestA.PostAsJsonAsync(
            "/api/v1/native-notes/task-suggestions/extractions", start);
        Assert.Equal(HttpStatusCode.Conflict, extractionResponse.StatusCode);
        Assert.Equal(40901,
            (await extractionResponse.Content.ReadFromJsonAsync<ApiResponse<string>>())!.Code);

        var update = new StringContent(JsonSerializer.Serialize(new
        {
            expectedRevision = suggestion.Revision,
            title = "不应写入",
            priority = 9,
        }), Encoding.UTF8, "application/json");
        var updateResponse = await host.RestA.PutAsync(
            $"/api/v1/native-notes/task-suggestions/{suggestion.Id}", update);
        Assert.Equal(HttpStatusCode.Conflict, updateResponse.StatusCode);

        var confirmResponse = await host.RestA.PostAsJsonAsync(
            $"/api/v1/native-notes/task-suggestions/{suggestion.Id}/confirm",
            new ConfirmNoteTaskSuggestionRequest(Guid.NewGuid(), suggestion.Revision));
        Assert.Equal(HttpStatusCode.Conflict, confirmResponse.StatusCode);

        var dismissResponse = await host.RestA.PostAsJsonAsync(
            $"/api/v1/native-notes/task-suggestions/{suggestion.Id}/dismiss",
            new DismissNoteTaskSuggestionRequest(suggestion.Revision));
        Assert.Equal(HttpStatusCode.Conflict, dismissResponse.StatusCode);

        using (var scope = host.Factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider
                .GetRequiredService<NoteTaskSuggestionExtractionJob>();
            await job.RunAsync(host.UserA, extraction.Id, default);
        }

        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var stopped = await db.Set<NoteTaskSuggestionExtractionEntity>()
                .AsNoTracking().SingleAsync(x => x.Id == extraction.Id);
            Assert.Equal(NoteTaskSuggestionExtractionStatuses.Failed, stopped.Status);
            Assert.Equal("note_recycled", stopped.ErrorCode);
            var unchanged = await db.Set<NoteTaskSuggestionEntity>()
                .AsNoTracking().SingleAsync(x => x.Id == suggestion.Id);
            Assert.Equal(NoteTaskSuggestionStatuses.Draft, unchanged.Status);
            Assert.Equal("回收站建议", unchanged.Title);
            Assert.Equal(1, await db.Set<NoteTaskSuggestionExtractionEntity>()
                .CountAsync(x => x.UserId == host.UserA && x.NoteId == noteId));
        }

        await host.Store.RestoreAsync(host.UserA, noteId, null);
        using (var scope = host.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var afterRestore = await db.Set<NoteTaskSuggestionExtractionEntity>()
                .AsNoTracking().SingleAsync(x => x.Id == extraction.Id);
            Assert.Equal(NoteTaskSuggestionExtractionStatuses.Failed, afterRestore.Status);
        }
    }

    [SkippableFact]
    public async Task HeadOnlyPromotionInvalidatesRevisionAndRejectsStaleLifecycleCas()
    {
        await using var host = await McpHost.CreateAsync();
        var noteId = Guid.NewGuid();
        var first = await host.SeedNoteAsync(host.UserA,
            NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId));
        var second = NoteMaterialUploadStoreTests.Fixture.Create(noteId: noteId,
            baseId: first.Request.VersionId);
        await host.SeedNoteAsync(host.UserA, second, makeCurrent: false);

        var before = await host.CallAsync(host.McpARead, "get_native_note_versions", new { noteId });
        Assert.False(before.IsError, before.Payload.GetRawText());
        var beforeData = before.Payload.GetProperty("data");
        Assert.Equal(first.Request.VersionId, beforeData.GetProperty("currentVersionId").GetGuid());
        Assert.Equal(2, beforeData.GetProperty("total").GetInt32());
        Assert.False(beforeData.GetProperty("hasConflict").GetBoolean());
        var staleRevision = beforeData.GetProperty("revision").GetGuid();
        var secondState = beforeData.GetProperty("versions").EnumerateArray()
            .Single(item => item.GetProperty("versionId").GetGuid() == second.Request.VersionId);
        Assert.Equal("Uploaded", secondState.GetProperty("phase").GetString());
        Assert.False(secondState.GetProperty("hasConflict").GetBoolean());

        var promoted = await host.CallAsync(host.McpAWrite, "promote_native_note_version", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            versionId = second.Request.VersionId,
            expectedCurrentVersionId = first.Request.VersionId,
            expectedRevision = staleRevision.ToString("D"),
        });
        Assert.False(promoted.IsError, promoted.Payload.GetRawText());
        var promotedRevision = promoted.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("revision").GetGuid();

        var after = await host.CallAsync(host.McpARead, "get_native_note_versions", new { noteId });
        Assert.False(after.IsError, after.Payload.GetRawText());
        var afterData = after.Payload.GetProperty("data");
        Assert.Equal(second.Request.VersionId, afterData.GetProperty("currentVersionId").GetGuid());
        var newRevision = afterData.GetProperty("revision").GetGuid();
        Assert.NotEqual(staleRevision, newRevision);
        Assert.Equal(newRevision, promotedRevision);
        Assert.False(afterData.GetProperty("hasConflict").GetBoolean());
        Assert.Equal("Uploaded", afterData.GetProperty("versions").EnumerateArray()
            .Single(item => item.GetProperty("versionId").GetGuid() == second.Request.VersionId)
            .GetProperty("phase").GetString());

        var staleRecycle = await host.CallAsync(host.McpAWrite, "delete_native_note", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            expectedRevision = staleRevision.ToString("D"),
        });
        Assert.True(staleRecycle.IsError);
        Assert.Equal(409, staleRecycle.Payload.GetProperty("code").GetInt32());

        var recycled = await host.CallAsync(host.McpAWrite, "delete_native_note", new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            expectedRevision = newRevision.ToString("D"),
        });
        Assert.False(recycled.IsError, recycled.Payload.GetRawText());
        Assert.Equal("Recycled", recycled.Payload.GetProperty("data").GetProperty("result")
            .GetProperty("status").GetString());
        var afterRecycle = await host.CallAsync(host.McpARead, "get_native_note_versions",
            new { noteId });
        Assert.Equal(second.Request.VersionId,
            afterRecycle.Payload.GetProperty("data").GetProperty("currentVersionId").GetGuid());
    }

    [SkippableFact]
    public async Task TextOnlyProcessingStartsAndRetriesFailedStepWithIdempotentReceipt()
    {
        await using var host = await McpHost.CreateAsync();
        await host.ConfigureTextModelAsync();
        var fixture = await host.SeedNoteAsync(host.UserA, TextOnlyFixture(Guid.NewGuid()));
        var noteId = fixture.Request.NoteId;
        var versions = await host.CallAsync(host.McpARead, "get_native_note_versions",
            new { noteId });
        var revision = versions.Payload.GetProperty("data").GetProperty("revision").GetGuid();

        var startArgs = new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            versionId = fixture.Request.VersionId.ToString("D"),
            expectedRevision = revision.ToString("D"),
        };
        var started = await host.CallAsync(host.McpAWrite, "start_native_note_processing",
            startArgs);
        Assert.False(started.IsError, started.Payload.GetRawText());
        var startedData = started.Payload.GetProperty("data");
        Assert.Equal("start_native_note_processing",
            startedData.GetProperty("receipt").GetProperty("operation").GetString());
        Assert.False(startedData.GetProperty("receipt").GetProperty("replayed").GetBoolean());
        var startedStatus = startedData.GetProperty("result");
        Assert.Equal(fixture.Request.VersionId, startedStatus.GetProperty("versionId").GetGuid());
        Assert.NotEqual("Completed", startedStatus.GetProperty("state").GetString());
        Assert.NotEqual("NotApplicable",
            startedStatus.GetProperty("organization").GetProperty("state").GetString());
        var startedOrganizationRevision = startedStatus.GetProperty("organization")
            .GetProperty("revision").GetGuid();
        Assert.NotEqual(Guid.Empty, startedOrganizationRevision);

        var startedReplay = await host.CallAsync(host.McpAWrite,
            "start_native_note_processing", startArgs);
        Assert.False(startedReplay.IsError, startedReplay.Payload.GetRawText());
        Assert.True(startedReplay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());

        var tasks = await host.DerivedStore.ListTasksAsync(host.UserA, noteId, default);
        var task = Assert.Single(tasks);
        Assert.False(task.Blocked);
        Assert.NotEmpty(task.Steps);
        var failedKey = task.Steps[0].Key;
        var taskId = task.Request.TaskId;
        var updated = await host.DerivedStore.UpdateTaskAsync(host.UserA, taskId, state =>
        {
            var attempt = state.Steps.Single(step => step.Key == failedKey).Attempts[^1];
            attempt.Status = "Failed";
            attempt.ErrorCode = "synthetic_failure";
            attempt.ErrorMessage = "synthetic focused retry input";
        }, default);
        Assert.Equal(startedOrganizationRevision, task.Revision);
        Assert.NotEqual(task.Revision, updated.Revision);
        var consent = updated.Request.Consent;
        var retryConsent = new
        {
            modelId = consent.ModelId.ToString("D"),
            modelRevision = consent.ModelRevision.ToString("D"),
            endpoint = consent.Endpoint,
            confirmed = true,
        };
        var staleRetry = await host.CallAsync(host.McpAWrite,
            "retry_native_note_processing", new
            {
                noteId,
                requestId = Guid.NewGuid().ToString("D"),
                versionId = fixture.Request.VersionId.ToString("D"),
                expectedRevision = startedOrganizationRevision.ToString("D"),
                stepKeys = new[] { failedKey },
                consent = retryConsent,
                acknowledgePossibleDuplicateCharge = true,
            });
        Assert.True(staleRetry.IsError, staleRetry.Payload.GetRawText());
        Assert.Equal(409, staleRetry.Payload.GetProperty("code").GetInt32());
        var afterStale = await host.DerivedStore.ReadTaskAsync(host.UserA, taskId, default);
        Assert.Equal(updated.Revision, afterStale.Revision);

        var currentProcessing = await host.CallAsync(host.McpARead,
            "get_native_note_processing", new
            {
                noteId,
                versionId = fixture.Request.VersionId.ToString("D"),
            });
        Assert.False(currentProcessing.IsError, currentProcessing.Payload.GetRawText());
        var currentRevision = currentProcessing.Payload.GetProperty("data")
            .GetProperty("result").GetProperty("organization").GetProperty("revision")
            .GetGuid();
        Assert.Equal(updated.Revision, currentRevision);
        var retryArgs = new
        {
            noteId,
            requestId = Guid.NewGuid().ToString("D"),
            versionId = fixture.Request.VersionId.ToString("D"),
            expectedRevision = currentRevision.ToString("D"),
            stepKeys = new[] { failedKey },
            consent = retryConsent,
            acknowledgePossibleDuplicateCharge = true,
        };
        var retried = await host.CallAsync(host.McpAWrite, "retry_native_note_processing",
            retryArgs);
        Assert.False(retried.IsError, retried.Payload.GetRawText());
        var retriedData = retried.Payload.GetProperty("data");
        Assert.Equal("retry_native_note_processing",
            retriedData.GetProperty("receipt").GetProperty("operation").GetString());
        Assert.False(retriedData.GetProperty("receipt").GetProperty("replayed").GetBoolean());
        var retriedProcessing = retriedData.GetProperty("result").GetProperty("processing");
        Assert.Equal(fixture.Request.VersionId,
            retriedProcessing.GetProperty("versionId").GetGuid());
        Assert.NotEqual("Completed", retriedProcessing.GetProperty("state").GetString());
        var organization = retriedProcessing.GetProperty("organization");
        Assert.True(organization.GetProperty("failedItems").GetInt32() >= 0);
        Assert.DoesNotContain("taskId", retriedData.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attemptId", retriedData.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stepId", retriedData.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var replay = await host.CallAsync(host.McpAWrite, "retry_native_note_processing",
            retryArgs);
        Assert.False(replay.IsError, replay.Payload.GetRawText());
        Assert.True(replay.Payload.GetProperty("data").GetProperty("receipt")
            .GetProperty("replayed").GetBoolean());
        var afterReplay = await host.DerivedStore.ReadTaskAsync(host.UserA, taskId, default);
        Assert.Equal(2, afterReplay.Steps.Single(step => step.Key == failedKey).Attempts.Count);
    }

    private static object Consent() => new
    {
        modelId = Guid.NewGuid().ToString("D"),
        modelRevision = Guid.NewGuid().ToString("D"),
        endpoint = "http://127.0.0.1/unused",
        confirmed = true,
    };

    private static byte[] Package(NoteMaterialUploadStoreTests.Fixture fixture,
        string? extraEntry = null, bool includeMaterialVersionJson = true,
        bool includeMaterialsJson = true)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includeMaterialVersionJson)
                AddEntry(archive, "material-version.json",
                    Encoding.UTF8.GetBytes(fixture.Request.InputJson));
            if (includeMaterialsJson)
                AddEntry(archive, "materials.json",
                    Encoding.UTF8.GetBytes(fixture.Request.ManifestJson));
            using var manifest = JsonDocument.Parse(fixture.Request.ManifestJson);
            var index = 0;
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
                AddEntry(archive, file.GetProperty("path").GetString()!, fixture.Content[index++]);
            if (extraEntry is not null)
                AddEntry(archive, extraEntry, Encoding.UTF8.GetBytes("escape"));
        }
        return output.ToArray();
    }

    private static NoteMaterialUploadStoreTests.Fixture TextOnlyFixture(Guid noteId,
        Guid? baseVersionId = null)
    {
        var versionId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        const string text = "## 处理测试\n\n这是仅文本的固定材料，不依赖任何外部 AI 调用。";
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = noteId.ToString("D"),
            title = "处理测试笔记",
            revision = 1,
            createdAtMillis = 1,
            blocks = new[]
            {
                new { id = blockId.ToString("D"), kind = "markdown", text },
            },
        });
        // Keep the note JSON byte-for-byte identical to the embedded note object so the
        // manifest inputSha256 matches what NoteMaterialManifestValidator reads back.
        var inputJson =
            $"{{\"schemaVersion\":1,\"id\":\"{versionId:D}\",\"createdAtMillis\":1," +
            $"\"note\":{noteJson},\"recordings\":[]}}";
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var markdownBytes = Encoding.UTF8.GetBytes(text);
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = Sha256(Encoding.UTF8.GetBytes(inputJson)),
            files = new[]
            {
                new
                {
                    path = "note.json",
                    kind = "Document",
                    sha256 = Sha256(noteBytes),
                    sizeBytes = noteBytes.Length,
                },
                new
                {
                    path = "document.md",
                    kind = "Markdown",
                    sha256 = Sha256(markdownBytes),
                    sizeBytes = markdownBytes.Length,
                },
            },
        });
        return new(
            new BeginNoteMaterialUploadRequest(versionId, noteId, baseVersionId, inputJson,
                manifestJson, true),
            [noteBytes, markdownBytes]);
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
            McpHandle bRead, McpHandle bWrite, Guid userA, Guid userB)
        {
            Factory = factory;
            _database = database;
            _root = root;
            RestA = restA;
            RestB = restB;
            McpARead = aRead.Client;
            McpAWrite = aWrite.Client;
            McpBRead = bRead.Client;
            McpBWrite = bWrite.Client;
            UserA = userA;
            UserB = userB;
        }

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient RestA { get; }
        public HttpClient RestB { get; }
        public HttpClient McpARead { get; }
        public HttpClient McpAWrite { get; }
        public HttpClient McpBRead { get; }
        public HttpClient McpBWrite { get; }
        public Guid UserA { get; }
        public Guid UserB { get; }
        public NoteMaterialUploadStore Store => Factory.Services.GetRequiredService<NoteMaterialUploadStore>();
        public NoteDerivedStore DerivedStore => Factory.Services.GetRequiredService<NoteDerivedStore>();

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
            var aRead = await CreateMcpAsync(restA, factory, "a-read", writes: false, reads: true);
            var aWrite = await CreateMcpAsync(restA, factory, "a-write", writes: true);
            var bRead = await CreateMcpAsync(restB, factory, "b-read", writes: false, reads: true);
            var bWrite = await CreateMcpAsync(restB, factory, "b-write", writes: true);
            return new McpHost(factory, database, root, restA, restB, aRead, aWrite, bRead, bWrite,
                sessionA.UserId, sessionB.UserId);
        }

        public async Task<Guid> ConfigureTextModelAsync()
        {
            var model = await SuccessAsync(await RestA.PostAsJsonAsync("/api/v1/ai/models",
                new SaveAiModelRequest(
                    "native-note-mcp-text-" + Guid.NewGuid().ToString("N"),
                    "https://synthetic.invalid/v1", "native-note-test", true,
                    [AiModelCapability.Text], 30, 1024, "synthetic-native-note-text-key")));
            var modelId = model.GetProperty("id").GetGuid();
            var settings = await SuccessAsync(await RestA.GetAsync("/api/v1/ai/model-settings"));
            await SuccessAsync(await RestA.PutAsJsonAsync("/api/v1/ai/model-settings",
                new SaveAiModelSettingsRequest(settings.GetProperty("revision").GetGuid(), true,
                    modelId, null, null)));
            return modelId;
        }

        public async Task<NoteMaterialUploadStoreTests.Fixture> SeedNoteAsync(Guid owner,
            NoteMaterialUploadStoreTests.Fixture? fixture = null, bool makeCurrent = true)
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
            await Store.CompleteAsync(owner, fixture.Request.VersionId, makeCurrent);
            return fixture;
        }

        public async Task<JsonElement> ReadNoteAsync(HttpClient mcp, Guid noteId)
        {
            var call = await CallAsync(mcp, "get_native_note", new { noteId });
            Assert.False(call.IsError, call.Payload.GetRawText());
            return call.Payload;
        }

        /// <summary>
        /// Creates a real client token, optionally enables one native-note read tool, exchanges it
        /// through POST /api/v1/mcp/verify, and returns an HttpClient carrying the mcp_tool JWT.
        /// </summary>
        public async Task<HttpClient> VerifyReadTokenAsync(string tool)
        {
            var created = await SuccessAsync(await RestA.PostAsJsonAsync("/api/v1/mcp/clients",
                new { name = "verified-read-" + Guid.NewGuid().ToString("N") }));
            var clientId = created.GetProperty("client").GetProperty("id").GetGuid();
            var rawToken = created.GetProperty("token").GetString()!;
            if (McpToolCatalog.ReadTools.Any(item => item.Name == tool &&
                    item.Group == "native-notes"))
            {
                var permissions = McpToolCatalog.DefaultPermissions();
                permissions["read"][tool] = true;
                await SuccessAsync(await RestA.PutAsJsonAsync(
                    $"/api/v1/mcp/clients/{clientId}", new { permissions }));
            }

            using var verifier = Factory.CreateClient();
            verifier.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", rawToken);
            var verified = await SuccessAsync(await verifier.PostAsJsonAsync(
                "/api/v1/mcp/verify", new { tool }));
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", verified.GetProperty("accessToken").GetString());
            return client;
        }

        public async Task<(Guid SuggestionId, Guid Revision)> SeedSuggestionAsync(Guid owner,
            Guid noteId, string title)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var now = DateTimeOffset.UtcNow;
            var extraction = new NoteTaskSuggestionExtractionEntity
            {
                UserId = owner,
                RequestId = Guid.NewGuid(),
                RequestFingerprint = new string('0', 64),
                NoteId = noteId,
                ArtifactId = Guid.NewGuid(),
                ArtifactVersionId = Guid.NewGuid(),
                SourceContentSha256 = new string('0', 64),
                SourceInputSha256 = new string('0', 64),
                SourceManifestSha256 = new string('0', 64),
                SourceContentMarkdown = "# 测试",
                ModelProfileId = Guid.NewGuid(),
                ModelRevision = Guid.NewGuid(),
                ModelEndpoint = "http://127.0.0.1/unused",
                PromptVersion = "test",
                Status = NoteTaskSuggestionExtractionStatuses.Succeeded,
                CreatedAt = now,
                UpdatedAt = now,
                FinishedAt = now,
            };
            var suggestion = new NoteTaskSuggestionEntity
            {
                UserId = owner,
                ExtractionId = extraction.Id,
                Ordinal = 0,
                NoteId = noteId,
                ArtifactId = extraction.ArtifactId,
                ArtifactVersionId = extraction.ArtifactVersionId,
                SourceContentSha256 = extraction.SourceContentSha256,
                SourceInputSha256 = extraction.SourceInputSha256,
                SourceManifestSha256 = extraction.SourceManifestSha256,
                Title = title,
                Kind = NoteTaskSuggestionKinds.Task,
                Description = "真实 HTTP + PostgreSQL 测试建议",
                Priority = 5,
                Status = NoteTaskSuggestionStatuses.Draft,
                Revision = Guid.NewGuid(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Set<NoteTaskSuggestionExtractionEntity>().Add(extraction);
            db.Set<NoteTaskSuggestionEntity>().Add(suggestion);
            await db.SaveChangesAsync();
            return (suggestion.Id, suggestion.Revision);
        }

        public async Task<ToolCall> CallAsync(HttpClient mcp, string name, object args)
        {
            using var response = await mcp.SendAsync(Rpc("tools/call", new
            {
                name,
                arguments = args,
            }));
            var wire = ReadRpc(response);
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
            McpBRead.Dispose();
            McpBWrite.Dispose();
            RestA.Dispose();
            RestB.Dispose();
            await Factory.DisposeAsync();
            await _database.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static async Task<McpHandle> CreateMcpAsync(HttpClient rest,
            WebApplicationFactory<Program> factory, string name, bool writes, bool reads = false)
        {
            var created = await SuccessAsync(await rest.PostAsJsonAsync("/api/v1/mcp/clients",
                new { name }));
            var id = created.GetProperty("client").GetProperty("id").GetGuid();
            var token = created.GetProperty("token").GetString()!;
            if (writes || reads)
            {
                var permissions = McpToolCatalog.DefaultPermissions();
                var section = writes ? "write" : "read";
                permissions[section] = permissions[section]
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
