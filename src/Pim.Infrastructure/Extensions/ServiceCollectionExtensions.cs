using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Ai;
using Pim.Core.Operations;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
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
        services.AddScoped<ISystemStatusService, SystemStatusService>();
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.AddScoped<IAiGateway, AiGateway>();
        services.AddScoped<IAiUsageService, AiUsageService>();
        services.AddScoped<IAiProviderHealthService, AiProviderHealthService>();
        services.AddScoped<IAiRequestLogWriter, AiRequestLogWriter>();
        services.AddSingleton<IAiSchemaRegistry, AiSchemaRegistry>();
        services.AddSingleton<IAiChatClientFactory, AiChatClientFactory>();
        services.AddHttpClient("litellm-health");
        services.AddHangfire(config =>
            config.UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(configuration.GetConnectionString("DefaultConnection"))));
        services.AddHangfireServer();
        services.AddScoped<IHangfireMonitoringClient, HangfireMonitoringClient>();
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
        if (!string.IsNullOrWhiteSpace(minioEndpoint))
        {
            services.AddSingleton(sp => new MinioStorage(
                minioEndpoint,
                configuration["Minio:AccessKey"]!,
                configuration["Minio:SecretKey"]!));
        }

        services.AddSingleton(sp => new KopiaService(
            configuration["Kopia:RepositoryPath"]!,
            configuration["Kopia:Password"]!));

        // Tika
        services.AddHttpClient<TikaClient>(client =>
        {
            client.BaseAddress = new Uri(configuration["Tika:BaseUrl"]!);
        });

        return services;
    }
}
