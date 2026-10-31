using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Pim.Core.Ai;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

/// <summary>
/// R03/R05 admission regressions against a REAL API process.
///
/// .NET 8 WebApplicationFactory always forces UseTestServer, so Kestrel admission can only be
/// proven with an owned child process running the unmodified Pim.Api Program. Opt-in only via
/// PIM_ASR_ADMISSION_E2E=1; without it the fixture starts nothing and every case skips.
/// Synthetic-only: dedicated loopback PostgreSQL, dead loopback provider URL, no paid calls.
/// </summary>
public sealed class AiAsrAdmissionKestrelHost : IAsyncLifetime
{
    public const string OptInVariable = "PIM_ASR_ADMISSION_E2E";
    private const int PgPort = 55444;
    private const int ApiPort = 55446;
    private static readonly string ApiBase = "http://127.0.0.1:" + ApiPort + "/";

    private static string RepoRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static string ApiDll => Path.Combine(RepoRoot, "src", "Pim.Api", "bin", "Debug", "net8.0", "Pim.Api.dll");

    // Opt-in runs must point at an existing, caller-owned synthetic cluster. No personal path is
    // compiled in, and nothing is resolved (or created) unless PIM_ASR_ADMISSION_E2E=1.
    public const string WorkspaceRootVariable = "PIM_ASR_ADMISSION_WORKSPACE_ROOT";
    public const string PgRootVariable = "PIM_ASR_ADMISSION_PG_ROOT";

    private static string RequiredEnv(string name, string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"{OptInVariable}=1 requires {name} (absolute synthetic workspace/PG root)")
            : Path.GetFullPath(value);

    private readonly object _logLock = new();
    private readonly string _workspaceRoot;
    private readonly string _pgRoot;
    private readonly string _pgData;
    private readonly string _logPath;
    private string _dbPassword = string.Empty;
    private string _appDataRoot = string.Empty;
    private string _dataProtectionDir = string.Empty;
    private Process? _api;
    private string _pemPath = string.Empty;
    private string _connectionString = string.Empty;
    private bool _disposed;

    public bool OptedIn { get; } = Environment.GetEnvironmentVariable(OptInVariable) == "1";
    public static int ListeningPort => ApiPort;
    public Guid AdminUserId { get; private set; }
    public string AdminToken { get; private set; } = string.Empty;
    public Guid IsolatedProbeUserId { get; private set; }
    public string ProbeToken { get; private set; } = string.Empty;
    public Guid MultimodalModelId { get; private set; }
    public Guid MultimodalModelRevision { get; private set; }
    public string MultimodalEndpoint { get; private set; } = string.Empty;
    public Guid LegacyModelId { get; private set; }
    public Guid LegacyModelRevision { get; private set; }
    public string LegacyEndpoint { get; private set; } = string.Empty;

    public AiAsrAdmissionKestrelHost()
    {
        // Paths are only resolved for opted-in runs; skipped runs never touch the filesystem.
        _workspaceRoot = OptedIn
            ? RequiredEnv(WorkspaceRootVariable, Environment.GetEnvironmentVariable(WorkspaceRootVariable))
            : string.Empty;
        _pgRoot = OptedIn
            ? RequiredEnv(PgRootVariable, Environment.GetEnvironmentVariable(PgRootVariable))
            : string.Empty;
        _pgData = Path.Combine(_pgRoot, "data");
        _logPath = Path.Combine(_pgRoot, "host-20261031.log");
    }

    public Task InitializeAsync()
    {
        if (!OptedIn)
        {
            Log("host", "opt-in not set; nothing started");
            return Task.CompletedTask;
        }
        Start();
        return Task.CompletedTask;
    }

    public void Start()
    {
        Directory.CreateDirectory(_pgRoot);
        Assert.True(File.Exists(ApiDll), $"API build output missing: {ApiDll}");
        Assert.True(File.Exists(Path.Combine(_pgData, "PG_VERSION")), $"Synthetic PG cluster missing: {_pgData}");
        AssertOwnedPg();

        Directory.CreateDirectory(Path.Combine(_pgRoot, "keys"));
        _pemPath = Path.Combine(_pgRoot, "keys", "jwt_private.pem");
        if (!File.Exists(_pemPath))
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(_pemPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        // Per-run database identity: no reuse of rows from a previous run.
        var dbName = "pim_adm_" + Guid.NewGuid().ToString("N")[..16];
        _dbPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(18));
        var dbUser = "pim_adm_" + Guid.NewGuid().ToString("N")[..8];
        var adminConnection = $"Host=127.0.0.1;Port={PgPort};Database=postgres;Username=pim;Timeout=10";
        using (var admin = new NpgsqlConnection(adminConnection))
        {
            admin.Open();
            using (var role = new NpgsqlCommand(
                $"CREATE ROLE {dbUser} LOGIN PASSWORD '{_dbPassword}'", admin))
                role.ExecuteNonQuery();
            using (var create = new NpgsqlCommand($"CREATE DATABASE {dbName} OWNER {dbUser}", admin))
                create.ExecuteNonQuery();
        }
        using (var owner = new NpgsqlConnection(
            $"Host=127.0.0.1;Port={PgPort};Database={dbName};Username={dbUser};Password={_dbPassword}"))
        {
            owner.Open();
            using var schema = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS public AUTHORIZATION CURRENT_USER", owner);
            schema.ExecuteNonQuery();
        }
        _connectionString =
            $"Host=127.0.0.1;Port={PgPort};Database={dbName};Username={dbUser};Password={_dbPassword};Include Error Detail=true";
        Log("host", $"db {dbName} owner {dbUser}");

        _appDataRoot = Path.Combine(_pgRoot, "app-data", dbName);
        var appData = _appDataRoot;
        Directory.CreateDirectory(appData);
        // Mirrors Program.cs: the API stores keys under appData/<run>/dp with application name "Pim".
        _dataProtectionDir = Path.Combine(appData, "dp");
        var info = new ProcessStartInfo("dotnet")
        {
            Arguments = $"\"{ApiDll}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ApiDll)!,
        };
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Test";
        info.Environment["ASPNETCORE_URLS"] = ApiBase;
        info.Environment["ConnectionStrings__DefaultConnection"] = _connectionString;
        info.Environment["Jwt__PrivateKeyPath"] = _pemPath;
        info.Environment["DataProtection__KeysPath"] = _dataProtectionDir;
        info.Environment["ManagedStorage__Root"] = Path.Combine(appData, "files");
        info.Environment["DisableHangfire"] = "true";
        info.Environment["GitHub__Repo"] = "invalid/invalid-test-repo-xyz";
        info.Environment["Database__Migrations__FailFast"] = "false";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        Assert.False(ProbeOnce(ApiBase + "health/live") == true, $"Refusing to reuse an unowned API on {ApiBase}");
        _api = Process.Start(info) ?? throw new InvalidOperationException("Pim.Api child failed to start");
        Log("host", $"api pid {_api.Id}");
        _api.OutputDataReceived += (_, e) => { if (e.Data is not null) Log("api", e.Data); };
        _api.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log("api-err", e.Data); };
        _api.BeginOutputReadLine();
        _api.BeginErrorReadLine();

        Assert.True(WaitFor(ApiBase + "health/live", TimeSpan.FromMinutes(2)), "API listener never bound");
        Assert.True(WaitFor(ApiBase + "health/ready", TimeSpan.FromMinutes(2)), "API /health/ready never returned 200");
        Seed();
        AdminToken = MintToken(AdminUserId, "qa-admission", "admin");
        ProbeToken = MintToken(IsolatedProbeUserId, "qa-admission-probe", "admin");
    }

    private void AssertOwnedPg()
    {
        using var conn = new NpgsqlConnection($"Host=127.0.0.1;Port={PgPort};Database=postgres;Username=pim;Timeout=10");
        conn.Open();
        using var cmd = new NpgsqlCommand("select current_setting('data_directory')", conn);
        var dir = (string)cmd.ExecuteScalar()!;
        Assert.Equal(Path.GetFullPath(_pgData), Path.GetFullPath(dir), ignoreCase: true);
        Log("host", $"pg data_directory {dir}");
    }

    private void Seed()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(_connectionString).Options;
        using var db = new PimDbContext(options);
        AdminUserId = Guid.NewGuid();
        IsolatedProbeUserId = Guid.NewGuid();
        db.Users.Add(new UserEntity
        {
            Id = AdminUserId,
            Username = "qa-admission-" + AdminUserId.ToString("N")[..10],
            Email = $"qa-admission-{AdminUserId:N}@example.invalid",
            PasswordHash = "synthetic-not-a-login",
            DisplayName = "qa admission",
            Role = "admin",
            IsActive = true,
        });
        db.Users.Add(new UserEntity
        {
            Id = IsolatedProbeUserId,
            Username = "qa-admission-probe-" + IsolatedProbeUserId.ToString("N")[..10],
            Email = $"qa-admission-probe-{IsolatedProbeUserId:N}@example.invalid",
            PasswordHash = "synthetic-not-a-login",
            DisplayName = "qa admission probe",
            Role = "admin",
            IsActive = true,
        });

        var multimodal = NewProfile("qa multimodal", "mimo-multimodal", "mimo-v2.6-flash");
        var legacy = NewProfile("qa legacy", "mimo-asr", "mimo-v2.5-asr");
        db.AiModelProfiles.AddRange(multimodal, legacy);
        // Upsert the singleton so a re-used run directory cannot duplicate the runtime row.
        db.AiModelRuntime.Add(new AiModelRuntimeEntity
        {
            Id = AiModelRuntimeEntity.SingletonId,
            Enabled = true,
            TranscriptionModelId = multimodal.Id,
            Revision = Guid.NewGuid(),
        });
        db.SaveChanges();

        using (var services = BuildSecretServices())
        using (var scope = services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var key = protector.Protect("qa-synthetic-key-not-paid");
            foreach (var profile in db.AiModelProfiles)
                profile.ApiKeySecret = key;
            db.SaveChanges();
        }

        MultimodalModelId = multimodal.Id;
        MultimodalModelRevision = multimodal.Revision;
        MultimodalEndpoint = EndpointFor(multimodal);
        LegacyModelId = legacy.Id;
        LegacyModelRevision = legacy.Revision;
        LegacyEndpoint = EndpointFor(legacy);
        Log("host", $"admin={AdminUserId} multimodal={multimodal.Id}/{multimodal.Revision} " +
                    $"legacy={legacy.Id}/{legacy.Revision}");
    }

    // Same derivation as AiAsrCallService: preset normalizes the profile root, then the preset
    // operation path is appended. Both synthetic profiles use the "chat/completions" operation.
    private static string EndpointFor(AiModelProfileEntity profile) =>
        new Uri(new Uri(profile.BaseUrl), "chat/completions").AbsoluteUri;

    private ServiceProvider BuildSecretServices()
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Pim")
            .PersistKeysToFileSystem(new DirectoryInfo(_dataProtectionDir));
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services.BuildServiceProvider();
    }

    private static AiModelProfileEntity NewProfile(string name, string preset, string model) => new()
    {
        Name = name,
        NormalizedName = name,
        BaseUrl = "http://127.0.0.1:9/v1/",
        Model = model,
        Enabled = true,
        Capabilities = AiModelCapabilityFlags.Transcription,
        AsrPresetId = preset,
        TimeoutSeconds = 5,
        Revision = Guid.NewGuid(),
        ApiKeySecret = "",
    };

    public HttpClient CreateClient(string? token, TimeSpan? timeout = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(ApiBase), Timeout = timeout ?? TimeSpan.FromMinutes(3) };
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public PimDbContext OpenDb() => new(
        new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(_connectionString).Options);

    public string MintToken(Guid userId, string username, string role)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(_pemPath));
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "pim",
            Audience = "pim-client",
            Expires = DateTime.UtcNow.AddHours(2),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: true)),
                SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            }),
        });
        return handler.WriteToken(token);
    }

    public void Log(string source, string line)
    {
        lock (_logLock)
        {
            try { File.AppendAllText(_logPath, $"{DateTime.UtcNow:O} [{source}] {line}{Environment.NewLine}"); }
            catch (IOException) { }
        }
    }

    private static bool WaitFor(string url, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ProbeOnce(url) == true) return true;
            Thread.Sleep(300);
        }
        return false;
    }

    private static bool? ProbeOnce(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = client.Send(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public Task DisposeAsync()
    {
        if (_disposed) return Task.CompletedTask;
        _disposed = true;
        if (_api is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { process.WaitForExit(20_000); } catch (InvalidOperationException) { }
        }
        _api?.Dispose();
        Log("host", "api disposed");
        return Task.CompletedTask;
    }
}
/// <summary>
/// Five R03/R05 admission boundaries, each asserted against a real HTTP response from the
/// owned API child process. Opt-in only; without PIM_ASR_ADMISSION_E2E=1 they skip.
/// </summary>
public sealed class AiAsrAdmissionKestrelTests : IClassFixture<AiAsrAdmissionKestrelHost>
{
    private const long Mib = 1024L * 1024L;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    private readonly AiAsrAdmissionKestrelHost _host;
    private readonly ITestOutputHelper _output;

    public AiAsrAdmissionKestrelTests(AiAsrAdmissionKestrelHost host, ITestOutputHelper output)
    {
        _host = host;
        _output = output;
    }

    private void RequireOptIn() =>
        Skip.IfNot(_host.OptedIn,
            $"{AiAsrAdmissionKestrelHost.OptInVariable}=1 not set; real-process admission tests skipped");

    [SkippableFact]
    public async Task AsrJson_OverRequestBudget_IsRejectedWith413BeforeHandler()
    {
        RequireOptIn();
        await using var beforeDb = _host.OpenDb();
        var logsBefore = await beforeDb.AiRequestLogs.AsNoTracking()
            .CountAsync(x => x.UserId == _host.IsolatedProbeUserId);

        // Declared-content-length admission probe: real HTTP headers declare 74 MiB but no large
        // body is uploaded, so Kestrel must reject before handler/binder. This is not a full-body
        // upload test; it must read a real 413 (a connection reset is not success).
        var requestId = Guid.NewGuid();
        using var cancelled = new CancellationTokenSource(Deadline);
        var (status, body) = await OversizedContentLengthProbeAsync(requestId, cancelled.Token);
        _host.Log("case-413", $"status={status} body={body}");
        _output.WriteLine($"[413-overbudget] status={status} body={body}");
        Assert.Equal(413, status);

        // Dedicated owner request-log count is unchanged: no handler-created audit row.
        await using var db = _host.OpenDb();
        var logsAfter = await db.AiRequestLogs.AsNoTracking()
            .CountAsync(x => x.UserId == _host.IsolatedProbeUserId);
        Assert.Equal(logsBefore, logsAfter);
        _host.Log("case-413", $"probe owner logs unchanged {logsBefore}->{logsAfter}");
    }

    [SkippableFact]
    public async Task AsrJson_Decoded50MiB_PassesAdmissionAndDecodesAudio()
    {
        RequireOptIn();
        var requestId = Guid.NewGuid();
        using var client = _host.CreateClient(_host.AdminToken, Deadline);
        using var content = JsonContent.Create(new
        {
            clientRequestId = requestId,
            expectedUserId = _host.AdminUserId,
            audio = new { name = "qa-50mib.wav", format = "wav", base64 = Convert.ToBase64String(SyntheticWav(50 * Mib)) },
            consent = new
            {
                modelId = _host.MultimodalModelId,
                modelRevision = _host.MultimodalModelRevision,
                endpoint = _host.MultimodalEndpoint,
                presetId = "mimo-multimodal",
                presetVersion = "1",
                confirmed = true,
            },
            language = "auto",
            modelId = _host.MultimodalModelId,
        });
        using var cancelled = new CancellationTokenSource(Deadline);
        var response = await client.PostAsync("/api/v1/ai/asr-calls", content, cancelled.Token);
        var body = await ReadBoundedAsync(response, 32 * 1024, cancelled.Token);
        _host.Log("case-50mib", $"status={(int)response.StatusCode} body={body}");
        _output.WriteLine($"[50mib-admission] status={(int)response.StatusCode} body={body}");

        // Accepted by Kestrel + the 72 MiB endpoint budget + JSON binding, and the real service
        // reaches the dead loopback provider attempt. The wire JSON encoder fix (non-HTML escaping
        // of Base64 '+') keeps the finite request audit budget from rejecting this 50 MiB payload.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var result = System.Text.Json.JsonDocument.Parse(body))
        {
            var data = result.RootElement.GetProperty("data");
            Assert.Equal("Failed", data.GetProperty("status").GetString());
            var message = data.GetProperty("errorMessage").GetString();
            Assert.Contains("连接异常", message, StringComparison.Ordinal);
            Assert.DoesNotContain("超过大小上限", message, StringComparison.Ordinal);
        }

        // Real evidence that the 50 MiB body was decoded: the persisted audio row matches.
        await using var db = _host.OpenDb();
        var log = await db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.ClientRequestId == requestId && x.UserId == _host.AdminUserId);
        var audio = await db.AiCallAudio.AsNoTracking().SingleOrDefaultAsync(x => x.LogId == log.Id);
        Assert.NotNull(audio);
        Assert.Equal(50 * Mib, audio!.SizeBytes);
        Assert.Equal("failed", log.Status);
        Assert.Equal("provider_50206", log.ErrorCode);
        using (var metadata = System.Text.Json.JsonDocument.Parse(log.MetadataJson))
            Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean(),
                "50 MiB must reach a real provider attempt");
        _host.Log("case-50mib", $"persisted audio bytes={audio.SizeBytes} log status={log.Status} " +
                                $"error={log.ErrorCode} meta={log.MetadataJson}");
    }

    [SkippableFact]
    public async Task AsrJson_LegacyProfileOver4MiB_IsRejectedByPresetBudget()
    {
        RequireOptIn();
        using var client = _host.CreateClient(_host.AdminToken, Deadline);
        using var content = JsonContent.Create(new
        {
            clientRequestId = Guid.NewGuid(),
            expectedUserId = _host.AdminUserId,
            audio = new { name = "qa-5mib.wav", format = "wav", base64 = Convert.ToBase64String(SyntheticWav(5 * Mib)) },
            consent = new
            {
                modelId = _host.LegacyModelId,
                modelRevision = _host.LegacyModelRevision,
                endpoint = "http://127.0.0.1:9/v1/chat/completions",
                presetId = "mimo-asr",
                presetVersion = "1",
                confirmed = true,
            },
            language = "auto",
            modelId = _host.LegacyModelId,
        });
        using var cancelled = new CancellationTokenSource(Deadline);
        var response = await client.PostAsync("/api/v1/ai/asr-calls", content, cancelled.Token);
        var body = await ReadBoundedAsync(response, 16 * 1024, cancelled.Token);
        _host.Log("case-4mib", $"status={(int)response.StatusCode} body={body}");
        _output.WriteLine($"[4mib-legacy] status={(int)response.StatusCode} body={body}");
        // AiAsrMaterialService rejects a non-preset-supporting entry with published 40001; the
        // error mapping is a contract, not something this test may "correct".
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(40001, json.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("4MiB", json.RootElement.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task BatchMultipart_Over30MiB_BindsFlatFieldsAndReachesBusinessGate()
    {
        RequireOptIn();
        using var client = _host.CreateClient(_host.AdminToken, Deadline);
        using var content = FlatBatch(31 * Mib);
        using var cancelled = new CancellationTokenSource(Deadline);
        var response = await client.PostAsync("/api/v1/native-notes/asr-routing/batches/", content, cancelled.Token);
        var body = await ReadBoundedAsync(response, 16 * 1024, cancelled.Token);
        _host.Log("case-batch31", $"status={(int)response.StatusCode} body={body}");
        _output.WriteLine($"[batch-31mib] status={(int)response.StatusCode} body={body}");
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(40901, json.RootElement.GetProperty("code").GetInt32());
        var message = json.RootElement.GetProperty("message").GetString();
        Assert.Contains("MiMo Batch", message, StringComparison.Ordinal);
        Assert.Contains("音频能力", message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task BatchMultipart_Over132MiB_IsRejectedWith413()
    {
        RequireOptIn();
        using var client = _host.CreateClient(_host.AdminToken, Deadline);
        using var content = FlatBatch(133 * Mib);
        using var cancelled = new CancellationTokenSource(Deadline);
        var response = await client.PostAsync("/api/v1/native-notes/asr-routing/batches/", content, cancelled.Token);
        var body = await ReadBoundedAsync(response, 8 * 1024, cancelled.Token);
        _host.Log("case-batch133", $"status={(int)response.StatusCode} body={body}");
        _output.WriteLine($"[batch-133mib] status={(int)response.StatusCode} body={body}");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        while (total < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, maxBytes - total), ct);
            if (read == 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private async Task<(int Status, string Response)> OversizedContentLengthProbeAsync(
        Guid requestId, CancellationToken ct)
    {
        using var socket = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(IPAddress.Loopback,
            AiAsrAdmissionKestrelHost.ListeningPort, timeout.Token);
        await using var stream = socket.GetStream();
        var headers = string.Join("\r\n",
            "POST /api/v1/ai/asr-calls HTTP/1.1",
            $"Host: 127.0.0.1:{AiAsrAdmissionKestrelHost.ListeningPort}",
            $"Authorization: Bearer {_host.ProbeToken}",
            "Content-Type: application/json",
            $"Content-Length: {74 * Mib}",
            $"X-QA-ClientRequestId: {requestId:D}",
            "Connection: close",
            "",
            "");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), timeout.Token);

        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token);
            if (read == 0) break;
            total += read;
        }
        var response = Encoding.ASCII.GetString(buffer, 0, total);
        var firstLine = response.Split("\r\n", StringSplitOptions.None)[0].Split(' ');
        if (firstLine.Length < 2 || !int.TryParse(firstLine[1], out var status))
            throw new InvalidOperationException($"Unexpected HTTP response: {response[..Math.Min(response.Length, 512)]}");
        return (status, response[..Math.Min(response.Length, 2048)]);
    }

    private static byte[] SyntheticWav(long totalBytes)
    {
        var bytes = new byte[totalBytes];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (int)(totalBytes - 8));
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 16_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 32_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (short)16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.TryWriteBytes(bytes.AsSpan(40), (int)(totalBytes - 44));
        for (var i = 44; i < totalBytes; i++) bytes[i] = (byte)(i * 31);
        return bytes;
    }

    private static HttpContent FlatBatch(long fileBytes)
    {
        var content = new MultipartFormDataContent("qa-" + Guid.NewGuid().ToString("N"));
        content.Add(new StringContent(Guid.NewGuid().ToString()), "taskId");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "attemptId");
        content.Add(new StringContent(Guid.NewGuid().ToString()), "modelId");
        content.Add(new StringContent("qa-consent-" + Guid.NewGuid().ToString("N")[..8]), "consentRef");
        content.Add(new StringContent("true"), "batchScopeConfirmed");
        content.Add(new StringContent("24h"), "completionWindow");
        var file = new StreamContent(new PaddedStream([], fileBytes, []));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        content.Add(file, "file", "qa-batch.jsonl");
        return content;
    }

    private sealed class PaddedStream(byte[] prefix, long padding, byte[] suffix) : Stream
    {
        private readonly MemoryStream _head = new(prefix);
        private readonly MemoryStream _tail = new(suffix);
        private long _remaining = padding;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_head.Position < _head.Length) return _head.Read(buffer.Span);
            if (_remaining > 0)
            {
                var take = (int)Math.Min(buffer.Length, _remaining);
                buffer.Span[..take].Fill((byte)'\n');
                _remaining -= take;
                return take;
            }
            if (_tail.Position < _tail.Length) return _tail.Read(buffer.Span);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
