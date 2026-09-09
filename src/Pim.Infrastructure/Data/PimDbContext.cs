using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Pim.Core.Data;
using Pim.Core.Operations;
using Pim.Infrastructure.Audit;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Endpoints;

namespace Pim.Infrastructure.Data;

public class PimDbContext : DbContext
{
    private static readonly List<Assembly> _moduleAssemblies = new();
    private static readonly object _moduleAssembliesLock = new();

    public static void RegisterModuleAssembly(Assembly assembly)
    {
        lock (_moduleAssembliesLock)
        {
            if (!_moduleAssemblies.Any(a => a.FullName == assembly.FullName))
            {
                _moduleAssemblies.Add(assembly);
            }
        }
    }

    internal static string ModuleAssemblySignature
    {
        get
        {
            lock (_moduleAssembliesLock)
            {
                return string.Join(
                    "|",
                    _moduleAssemblies
                        .Select(a => a.FullName)
                        .OrderBy(n => n, StringComparer.Ordinal));
            }
        }
    }

    private readonly ICurrentUserService? _currentUser;

    /// <summary>
    /// 当前用户 Id。null 表示系统上下文（后台任务、启动引导、集成测试手工构造），
    /// 此时按用户隔离的全局查询过滤器自动不生效。
    /// </summary>
    public Guid? CurrentUserId => _currentUser?.UserId;

    public PimDbContext(DbContextOptions<PimDbContext> options, ICurrentUserService? currentUser = null)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<LoginAttemptEntity> LoginAttempts => Set<LoginAttemptEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();
    public DbSet<AuditVersionEntity> AuditVersions => Set<AuditVersionEntity>();
    public DbSet<OperationConfirmationEntity> OperationConfirmations => Set<OperationConfirmationEntity>();
    public DbSet<DaemonHeartbeatEntity> DaemonHeartbeats => Set<DaemonHeartbeatEntity>();
    public DbSet<EndpointStatusEntity> EndpointStatuses => Set<EndpointStatusEntity>();
    public DbSet<EndpointNotificationActionEntity> EndpointNotificationActions => Set<EndpointNotificationActionEntity>();
    public DbSet<AiProviderSettingEntity> AiProviderSettings => Set<AiProviderSettingEntity>();
    public DbSet<AiRequestLogEntity> AiRequestLogs => Set<AiRequestLogEntity>();

    public override int SaveChanges()
    {
        RefreshAiProviderSettingUpdatedAt();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefreshAiProviderSettingUpdatedAt();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        RefreshAiProviderSettingUpdatedAt();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RefreshAiProviderSettingUpdatedAt();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasQueryFilter(u => u.DeletedAt == null);
        });

        modelBuilder.Entity<RefreshTokenEntity>(e =>
        {
            e.HasIndex(r => r.TokenHash);
            e.HasOne(r => r.User).WithMany().HasForeignKey(r => r.UserId);
        });

        modelBuilder.Entity<LoginAttemptEntity>(e =>
        {
            e.HasIndex(l => new { l.IpAddress, l.AttemptedAt });
        });

        modelBuilder.Entity<AuditLogEntity>(e =>
        {
            e.Property(a => a.MetadataJson).HasDefaultValue("{}");
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.Action);
            e.HasIndex(a => a.ResourceType);
            e.HasIndex(a => a.CorrelationId);
            e.HasIndex(a => a.CreatedAt);
        });

        modelBuilder.Entity<AuditVersionEntity>(e =>
        {
            e.Property(a => a.BeforeJson).HasDefaultValue("{}");
            e.Property(a => a.AfterJson).HasDefaultValue("{}");
            e.Property(a => a.ChangedFieldsJson).HasDefaultValue("[]");
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(a => new { a.ObjectType, a.ObjectId, a.CreatedAt });
            e.HasIndex(a => a.ConfirmationId);
        });

        modelBuilder.Entity<OperationConfirmationEntity>(e =>
        {
            e.Property(o => o.PayloadJson).HasDefaultValue("{}");
            e.Property(o => o.PreviewJson).HasDefaultValue("{}");
            e.Property(o => o.Status).HasDefaultValue(OperationConfirmationStatus.Pending.ToString());
            e.Property(o => o.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(o => o.RequestedByUserId);
            e.HasIndex(o => o.OperationType);
            e.HasIndex(o => o.Status);
            e.HasIndex(o => o.ExpiresAt);
        });

        modelBuilder.Entity<DaemonHeartbeatEntity>(e =>
        {
            e.Property(d => d.DaemonKind).HasDefaultValue("windows");
            e.Property(d => d.ActivityWatchState).HasDefaultValue(DaemonSourceState.Unknown.ToString());
            e.Property(d => d.KeyStatsState).HasDefaultValue(DaemonSourceState.Unknown.ToString());
            e.Property(d => d.StatusJson).HasDefaultValue("{}");
            e.Property(d => d.ReceivedAt).HasDefaultValueSql("now()");
            e.HasIndex(d => new { d.DeviceId, d.DaemonKind }).IsUnique();
            e.HasIndex(d => d.ReceivedAt);
        });

        modelBuilder.Entity<EndpointStatusEntity>(e =>
        {
            e.Property(s => s.Platform).HasDefaultValue("windows");
            e.Property(s => s.UploadStatus).HasDefaultValue("Unknown");
            e.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
            e.Property(s => s.UpdatedAt).HasDefaultValueSql("now()");
            e.HasIndex(s => new { s.UserId, s.DeviceId }).IsUnique();
            e.HasIndex(s => s.LastHeartbeatAt);
        });

        modelBuilder.Entity<EndpointNotificationActionEntity>(e =>
        {
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.DeviceId);
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => a.ConfirmationId);
        });

        modelBuilder.Entity<AiProviderSettingEntity>(e =>
        {
            e.Property(a => a.Provider).HasDefaultValue("litellm");
            e.Property(a => a.Status).HasDefaultValue("disabled");
            e.Property(a => a.CreatedAt).HasDefaultValueSql("now()");
            e.Property(a => a.UpdatedAt).HasDefaultValueSql("now()");
            e.HasIndex(a => a.Provider).IsUnique();
            e.HasIndex(a => a.Status);
        });

        modelBuilder.Entity<AiRequestLogEntity>(e =>
        {
            e.Property(a => a.Provider).HasDefaultValue("litellm");
            e.Property(a => a.RequestMessagesJson).HasDefaultValue("[]");
            e.Property(a => a.RequestPayloadJson).HasDefaultValue("{}");
            e.Property(a => a.ResponseRawJson).HasDefaultValue("{}");
            e.Property(a => a.SchemaValidationErrorsJson).HasDefaultValue("[]");
            e.Property(a => a.MetadataJson).HasDefaultValue("{}");
            e.Property(a => a.EstimatedCost).HasPrecision(18, 8);
            e.HasIndex(a => a.UserId);
            e.HasIndex(a => a.Module);
            e.HasIndex(a => a.Purpose);
            e.HasIndex(a => a.Model);
            e.HasIndex(a => a.Status);
            e.HasIndex(a => a.StartedAt);
            e.HasIndex(a => new { a.SourceObjectType, a.SourceObjectId });
            e.HasIndex(a => a.CorrelationId);
        });

        Assembly[] moduleAssemblies;
        lock (_moduleAssembliesLock)
        {
            moduleAssemblies = _moduleAssemblies.ToArray();
        }

        foreach (var assembly in moduleAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        ApplyUserIsolationFilters(modelBuilder);
    }

    /// <summary>
    /// 为所有实现 <see cref="IUserOwnedEntity"/> 的实体追加按用户隔离的全局查询过滤器：
    /// <c>e =&gt; CurrentUserId == null || e.UserId == CurrentUserId</c>。
    /// 与实体已有的软删等过滤器以 AND 组合，而非覆盖。
    /// </summary>
    private void ApplyUserIsolationFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (!typeof(IUserOwnedEntity).IsAssignableFrom(clrType)) continue;

            var parameter = System.Linq.Expressions.Expression.Parameter(clrType, "e");
            var userId = System.Linq.Expressions.Expression.Property(parameter, nameof(IUserOwnedEntity.UserId));
            var currentUserId = System.Linq.Expressions.Expression.Property(
                System.Linq.Expressions.Expression.Constant(this), nameof(CurrentUserId));
            // e => CurrentUserId == null || (Guid?)e.UserId == CurrentUserId
            // 注意：比较在 Guid? 侧进行，避免 InMemory 提供程序对 (Guid)null 求值抛 "Nullable object must have a value"
            var noUser = System.Linq.Expressions.Expression.Equal(
                currentUserId, System.Linq.Expressions.Expression.Constant(null, typeof(Guid?)));
            var match = System.Linq.Expressions.Expression.Equal(
                System.Linq.Expressions.Expression.Convert(userId, typeof(Guid?)), currentUserId);
            var isolation = (System.Linq.Expressions.LambdaExpression)System.Linq.Expressions.Expression.Lambda(
                System.Linq.Expressions.Expression.OrElse(noUser, match), parameter);

            var existing = entityType.GetQueryFilter();
            entityType.SetQueryFilter(existing is null ? isolation : CombineFilters(existing, isolation));
        }
    }

    private static System.Linq.Expressions.LambdaExpression CombineFilters(
        System.Linq.Expressions.LambdaExpression first,
        System.Linq.Expressions.LambdaExpression second)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(first.Parameters[0].Type, "e");
        var firstBody = new ParameterReplacer(first.Parameters[0], parameter).Visit(first.Body);
        var secondBody = new ParameterReplacer(second.Parameters[0], parameter).Visit(second.Body);
        return System.Linq.Expressions.Expression.Lambda(
            System.Linq.Expressions.Expression.AndAlso(firstBody!, secondBody!), parameter);
    }

    private sealed class ParameterReplacer(
        System.Linq.Expressions.ParameterExpression from,
        System.Linq.Expressions.ParameterExpression to) : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(
            System.Linq.Expressions.ParameterExpression node) => node == from ? to : node;
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IModelCacheKeyFactory, PimDbContextModelCacheKeyFactory>();
    }

    private void RefreshAiProviderSettingUpdatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AiProviderSettingEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
