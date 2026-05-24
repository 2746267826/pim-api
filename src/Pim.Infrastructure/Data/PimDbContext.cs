using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Data;
using Pim.Core.Operations;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Data;

public class PimDbContext : DbContext
{
    private static readonly List<Assembly> _moduleAssemblies = new();

    public static void RegisterModuleAssembly(Assembly assembly)
    {
        _moduleAssemblies.Add(assembly);
    }

    public PimDbContext(DbContextOptions<PimDbContext> options) : base(options) { }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<LoginAttemptEntity> LoginAttempts => Set<LoginAttemptEntity>();
    public DbSet<AuditLogEntity> AuditLogs => Set<AuditLogEntity>();
    public DbSet<OperationConfirmationEntity> OperationConfirmations => Set<OperationConfirmationEntity>();
    public DbSet<DaemonHeartbeatEntity> DaemonHeartbeats => Set<DaemonHeartbeatEntity>();

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

        foreach (var assembly in _moduleAssemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }
}
