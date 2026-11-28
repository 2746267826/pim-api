using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.DTOs;
using Pim.Core.Common;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Calendar.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.RecycleBin;

/// <summary>
/// Real HTTP host (routing, JSON binding, JWT authorization) on a disposable PostgreSQL
/// database. Proves the durable operationId receipt replay and 409 payload conflict through
/// the same endpoint the Web client calls; it is not a browser test.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class RecycleBinRealDbHttpTests
{
    [SkippableFact]
    public async Task Restore_SameOperationIdOverRealHttpAndPostgres_ReplaysReceipt()
    {
        RealDbTestConnection.SkipIfUnavailable();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);

        await using var temp = await TempMigrationDatabase.CreateAsync("wo_q03_recycle_http");
        await temp.MigrateAsync();
        var root = Path.Combine(
            Path.GetTempPath(), "pim-q03-recycle-http", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("DisableHangfire", "true");
                builder.UseSetting("Database:Migrations:FailFast", "false");
                builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
                builder.UseSetting("ConnectionStrings:DefaultConnection", temp.ConnectionString);
                builder.UseSetting("DataProtection:KeysPath", Path.Combine(root, "dp"));
                builder.UseSetting("ManagedStorage:Root", Path.Combine(root, "managed"));
                builder.UseSetting("NativeNotes:StorageRoot", Path.Combine(root, "notes"));
            });

            var userId = Guid.NewGuid();
            // PostgreSQL timestamptz has microsecond precision; align the CAS token so
            // the value read back by the API is exactly the value sent by this test.
            var deletedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var calendar = new CalendarEntity
            {
                UserId = userId,
                Name = "真实 HTTP 日历",
                Kind = "calendar",
                Color = "#2563EB"
            };
            var evt = new EventEntity
            {
                Calendar = calendar,
                CalendarId = calendar.Id,
                Title = "真实 HTTP 已删日程",
                Uid = Guid.NewGuid().ToString("N"),
                DtStart = deletedAt.AddHours(1),
                DtEnd = deletedAt.AddHours(2),
                DeletedAt = deletedAt,
                DeletedByOperationId = Guid.NewGuid(),
                DeletedByOperationKind = "single"
            };
            temp.Db.Users.Add(new UserEntity
            {
                Id = userId,
                Username = "q03-http-realdb",
                Email = "q03-http-realdb@example.com",
                PasswordHash = "hash",
                Role = "user",
                IsActive = true
            });
            temp.Db.AddRange(calendar, evt);
            await temp.Db.SaveChangesAsync();

            var jwt = factory.Services.GetRequiredService<JwtService>();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", jwt.GenerateAccessToken(userId, "q03-http-realdb", "user"));
            var operationId = Guid.NewGuid();
            var request = new RecycleBinBatchRequest(
            [
                new RecycleBinItemRef(RecycleBinDomains.Calendar, evt.Id, deletedAt)
            ], operationId);

            var firstResponse = await client.PostAsJsonAsync(
                "/api/v1/recycle-bin/restore", request);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            var first = (await firstResponse.Content
                .ReadFromJsonAsync<ApiResponse<RecycleBinBatchReceiptDto>>())!.Data!;
            Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(first.Items).Status);
            Assert.Null((await temp.Db.Set<EventEntity>().IgnoreQueryFilters()
                .AsNoTracking().SingleAsync(row => row.Id == evt.Id)).DeletedAt);

            var replayResponse = await client.PostAsJsonAsync(
                "/api/v1/recycle-bin/restore", request);
            Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
            var replay = (await replayResponse.Content
                .ReadFromJsonAsync<ApiResponse<RecycleBinBatchReceiptDto>>())!.Data!;
            Assert.Equal(first.ReceiptId, replay.ReceiptId);
            Assert.Equal(operationId, replay.OperationId);
            Assert.Equal(first.Items, replay.Items);
            Assert.Equal(1, await temp.Db.RecycleBinOperations.AsNoTracking()
                .CountAsync(row => row.UserId == userId && row.OperationId == operationId));

            var conflictResponse = await client.PostAsJsonAsync(
                "/api/v1/recycle-bin/restore",
                new RecycleBinBatchRequest(
                [
                    new RecycleBinItemRef(
                        RecycleBinDomains.Calendar,
                        evt.Id,
                        deletedAt.AddSeconds(-1))
                ], operationId));
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            Assert.Equal(40902,
                (await conflictResponse.Content
                    .ReadFromJsonAsync<ApiResponse<string>>())!.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
