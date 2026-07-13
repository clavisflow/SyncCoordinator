using Microsoft.EntityFrameworkCore;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class CoordinatorDbContext(DbContextOptions<CoordinatorDbContext> options)
    : DbContext(options)
{
    public DbSet<SystemDefinitionEntity> Systems => Set<SystemDefinitionEntity>();
    public DbSet<SyncRouteEntity> Routes => Set<SyncRouteEntity>();
    public DbSet<RouteFieldPolicyEntity> RouteFieldPolicies => Set<RouteFieldPolicyEntity>();
    public DbSet<RouteTableMappingEntity> RouteTableMappings => Set<RouteTableMappingEntity>();
    public DbSet<RouteColumnMappingEntity> RouteColumnMappings => Set<RouteColumnMappingEntity>();
    public DbSet<InboxMessageEntity> InboxMessages => Set<InboxMessageEntity>();
    public DbSet<QueueCheckpointEntity> QueueCheckpoints => Set<QueueCheckpointEntity>();
    public DbSet<SyncSnapshotEntity> SyncSnapshots => Set<SyncSnapshotEntity>();
    public DbSet<SyncConflictEntity> SyncConflicts => Set<SyncConflictEntity>();
    public DbSet<ConfigurationAuditEntity> ConfigurationAudits => Set<ConfigurationAuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemDefinitionEntity>(entity =>
        {
            entity.ToTable("SystemDefinition");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(64);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.Provider).HasMaxLength(32);
            entity.Property(x => x.ProtectedConnectionString).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<SyncRouteEntity>(entity =>
        {
            entity.ToTable("SyncRoute");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.SourceSystem, x.EntityType, x.Enabled });
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.SourceSystem).HasMaxLength(64);
            entity.Property(x => x.EntityType).HasMaxLength(128);
            entity.Property(x => x.DestinationSystem).HasMaxLength(64);
            entity.Property(x => x.DestinationMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ConflictScope).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.DefaultConflictPolicy).HasConversion<string>().HasMaxLength(40);
        });

        modelBuilder.Entity<RouteFieldPolicyEntity>(entity =>
        {
            entity.ToTable("RouteFieldPolicy");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RouteId, x.FieldName }).IsUnique();
            entity.Property(x => x.FieldName).HasMaxLength(128);
            entity.Property(x => x.Policy).HasConversion<string>().HasMaxLength(40);
            entity.HasOne(x => x.Route).WithMany(x => x.FieldPolicies).HasForeignKey(x => x.RouteId);
        });

        modelBuilder.Entity<RouteTableMappingEntity>(entity =>
        {
            entity.ToTable("RouteTableMapping");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RouteId, x.DestinationSystem }).IsUnique();
            entity.Property(x => x.DestinationSystem).HasMaxLength(64);
            entity.Property(x => x.SourceSchema).HasMaxLength(128);
            entity.Property(x => x.SourceTable).HasMaxLength(128);
            entity.Property(x => x.DestinationSchema).HasMaxLength(128);
            entity.Property(x => x.DestinationTable).HasMaxLength(128);
            entity.HasOne(x => x.Route).WithMany(x => x.TableMappings).HasForeignKey(x => x.RouteId);
        });

        modelBuilder.Entity<RouteColumnMappingEntity>(entity =>
        {
            entity.ToTable("RouteColumnMapping");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TableMappingId, x.SourceColumn }).IsUnique();
            entity.HasIndex(x => new { x.TableMappingId, x.DestinationColumn }).IsUnique();
            entity.Property(x => x.SourceColumn).HasMaxLength(128);
            entity.Property(x => x.DestinationColumn).HasMaxLength(128);
            entity.HasOne(x => x.TableMapping).WithMany(x => x.Columns).HasForeignKey(x => x.TableMappingId);
        });

        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.ToTable("InboxMessage");
            entity.HasKey(x => new { x.SourceMessageId, x.RouteId, x.DestinationSystem });
            entity.HasIndex(x => x.State);
            entity.Property(x => x.DestinationSystem).HasMaxLength(64);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.LastError).HasMaxLength(4000);
        });

        modelBuilder.Entity<QueueCheckpointEntity>(entity =>
        {
            entity.ToTable("QueueCheckpoint");
            entity.HasKey(x => x.SystemCode);
            entity.Property(x => x.SystemCode).HasMaxLength(64);
        });

        modelBuilder.Entity<SyncSnapshotEntity>(entity =>
        {
            entity.ToTable("SyncSnapshot");
            entity.HasKey(x => new { x.RouteId, x.DestinationSystem, x.EntityType, x.EntityId });
            entity.Property(x => x.DestinationSystem).HasMaxLength(64);
            entity.Property(x => x.EntityType).HasMaxLength(128);
            entity.Property(x => x.EntityId).HasMaxLength(256);
            entity.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<SyncConflictEntity>(entity =>
        {
            entity.ToTable("SyncConflict");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ResolvedAtUtc, x.DetectedAtUtc });
            entity.Property(x => x.SourceSystem).HasMaxLength(64);
            entity.Property(x => x.DestinationSystem).HasMaxLength(64);
            entity.Property(x => x.EntityType).HasMaxLength(128);
            entity.Property(x => x.EntityId).HasMaxLength(256);
            entity.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.FieldsJson).HasColumnType("nvarchar(max)");
            entity.HasOne(x => x.Route).WithMany().HasForeignKey(x => x.RouteId);
        });

        modelBuilder.Entity<ConfigurationAuditEntity>(entity =>
        {
            entity.ToTable("ConfigurationAudit");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ChangedAtUtc);
            entity.Property(x => x.ConfigurationType).HasMaxLength(64);
            entity.Property(x => x.ConfigurationId).HasMaxLength(64);
            entity.Property(x => x.ConfigurationName).HasMaxLength(200);
            entity.Property(x => x.Action).HasMaxLength(32);
            entity.Property(x => x.BeforeJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.AfterJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ChangedBy).HasMaxLength(200);
        });
    }
}
