using Microsoft.EntityFrameworkCore;
using UserMgmt.Data.Entities;
using UserMgmt.Data.Interceptors;

namespace UserMgmt.Data;

/// <summary>
/// The single EF Core context for the sidecar SQL store: user attributes,
/// audit log, reconciliation queue, and application log.
/// </summary>
/// <remarks>
/// The audit <see cref="AuditSaveChangesInterceptor"/> is registered via DI
/// in the host. Tests that don't go through DI may pass it through the
/// <see cref="DbContextOptionsBuilder"/> directly.
/// </remarks>
public class UserMgmtDbContext : DbContext
{
    /// <summary>Standard EF Core constructor for DI-built contexts.</summary>
    public UserMgmtDbContext(DbContextOptions<UserMgmtDbContext> options)
        : base(options)
    {
    }

    /// <summary>Sidecar user attribute rows keyed by UPN.</summary>
    public DbSet<UserAttributes> UserAttributes => Set<UserAttributes>();

    /// <summary>Append-only audit log. DENY UPDATE / DENY DELETE is enforced at the DB level.</summary>
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>Queue of partial-state failures awaiting admin resolution.</summary>
    public DbSet<ReconciliationQueue> ReconciliationQueue => Set<ReconciliationQueue>();

    /// <summary>Serilog application log target.</summary>
    public DbSet<AppLog> AppLogs => Set<AppLog>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<UserAttributes>(entity =>
        {
            entity.ToTable("UserAttributes");
            entity.HasKey(e => e.Upn);
            entity.Property(e => e.Upn).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EmployeeId).HasMaxLength(64);
            entity.Property(e => e.CostCenter).HasMaxLength(64);
            entity.Property(e => e.ContractType).HasMaxLength(64);
            entity.Property(e => e.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("AuditEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.ActorUpn).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.TargetUpn).HasMaxLength(256).IsRequired();
            entity.Property(e => e.FieldName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(32);

            // CHECK constraint: Reason is either NULL or one of the four allowed values.
            // Enforced both via this constraint and by client-side validation; the
            // DB layer is the final stop because audit rows can be inserted from
            // multiple processes (Web host, API host, background services).
            entity.ToTable(t => t.HasCheckConstraint(
                "CK_AuditEntries_Reason",
                $"[Reason] IS NULL OR [Reason] IN ('{AuditReason.Stale}', '{AuditReason.Termination}', '{AuditReason.Reorg}', '{AuditReason.Compromise}')"));

            entity.HasIndex(e => e.TargetUpn);
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<ReconciliationQueue>(entity =>
        {
            entity.ToTable("ReconciliationQueue");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Timestamp).IsRequired();
            entity.Property(e => e.TargetUpn).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Operation).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ResolvedBy).HasMaxLength(256);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.TargetUpn);
        });

        modelBuilder.Entity<AppLog>(entity =>
        {
            entity.ToTable("AppLogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Message).IsRequired();
            entity.Property(e => e.Level).HasMaxLength(32).IsRequired();
            entity.Property(e => e.TimeStamp).IsRequired();
        });
    }
}
