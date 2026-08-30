using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pim.Core.Ai;
using Pim.Core.Operations;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Endpoints;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Infrastructure.Storage;
using Pim.Infrastructure.TextExtraction;

namespace Pim.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPimInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // EF Core
        services.AddDbContext<PimDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.EnableRetryOnFailure(3)));
        services.AddScoped<PimMigrationAdoptionService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<AuditVersionService>();
        services.AddScoped<IOperationConfirmationService, OperationConfirmationService>();
        services.AddScoped<IDaemonHeartbeatService, DaemonHeartbeatService>();
        services.AddScoped<EndpointStatusService>();
        services.AddScoped<ISystemStatusService, SystemStatusService>();
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.AddScoped<IAiGateway, AiGateway>();
        services.AddScoped<IAiUsageService, AiUsageService>();
        services.AddScoped<IAiProviderHealthService, AiProviderHealthService>();
        services.AddScoped<IAiRequestLogWriter, AiRequestLogWriter>();
        services.AddSingleton<IAiSchemaRegistry, AiSchemaRegistry>();
        services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();
        services.AddHttpClient("litellm-health");
        var conn = configuration.GetConnectionString("DefaultConnection");
        var disableHangfire = bool.TryParse(configuration["DisableHangfire"], out var d) && d;
        if (disableHangfire)
        {
            using var lf = LoggerFactory.Create(builder => { });
            var logger = lf.CreateLogger("Hangfire");
            logger.LogWarning("Hangfire disabled explicitly via DisableHangfire=true");
            services.AddScoped<IHangfireMonitoringClient, NoopHangfireMonitoringClient>();
        }
        else if (string.IsNullOrWhiteSpace(conn))
        {
            using var lf = LoggerFactory.Create(builder => { });
            var logger = lf.CreateLogger("Hangfire");
            logger.LogWarning("Hangfire disabled: connection string not configured or empty");
            services.AddScoped<IHangfireMonitoringClient, NoopHangfireMonitoringClient>();
        }
        else
        {
            services.AddHangfire(config =>
                config.UsePostgreSqlStorage(options =>
                    options.UseNpgsqlConnection(conn)));
            services.AddHangfireServer();
            services.AddScoped<IHangfireMonitoringClient, HangfireMonitoringClient>();
        }
        services.AddScoped<IBackgroundJobStatusService, HangfireJobStatusService>();
        services.AddScoped<Stage0DiagnosticJob>();
        var dataProtectionKeysPath = configuration["DataProtection:KeysPath"]
            ?? "/data/keys/data-protection";
        Directory.CreateDirectory(dataProtectionKeysPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
            .SetApplicationName("Pim");
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();

        // Auth
        services.AddSingleton<JwtService>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        // Storage (optional — skip if MinIO is not configured)
        var minioEndpoint = configuration["Minio:Endpoint"];
        var minioAccess = configuration["Minio:AccessKey"];
        var minioSecret = configuration["Minio:SecretKey"];
        if (!string.IsNullOrWhiteSpace(minioEndpoint) && !string.IsNullOrWhiteSpace(minioAccess) && !string.IsNullOrWhiteSpace(minioSecret))
        {
            services.AddSingleton(sp => new MinioStorage(
                minioEndpoint,
                minioAccess!,
                minioSecret!));
        }

        services.AddSingleton(sp => new KopiaService(
            configuration["Kopia:RepositoryPath"]!,
            configuration["Kopia:Password"]!));

        // Tika (optional — when BaseUrl empty, extraction will fallback with clear error)
        var tikaBaseUrl = configuration["Tika:BaseUrl"];
        services.AddHttpClient<TikaClient>(client =>
        {
            if (!string.IsNullOrWhiteSpace(tikaBaseUrl) && Uri.TryCreate(tikaBaseUrl, UriKind.Absolute, out var uri))
                client.BaseAddress = uri;
            else
                client.BaseAddress = new Uri("http://tika:9998");
        });

        return services;
    }
}
