using Microsoft.EntityFrameworkCore;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class CoordinatorDbContext(DbContextOptions<CoordinatorDbContext> options)
    : DbContext(options)
{
    public DbSet<AdminAccountEntity> AdminAccounts => Set<AdminAccountEntity>();
    public DbSet<ManagementSettingsEntity> ManagementSettings => Set<ManagementSettingsEntity>();
    public DbSet<SystemDefinitionEntity> Systems => Set<SystemDefinitionEntity>();
    public DbSet<SyncRouteEntity> Routes => Set<SyncRouteEntity>();
    public DbSet<RouteTableMappingEntity> RouteTableMappings => Set<RouteTableMappingEntity>();
    public DbSet<RouteColumnMappingEntity> RouteColumnMappings => Set<RouteColumnMappingEntity>();
    public DbSet<RouteFixedValueMappingEntity> RouteFixedValueMappings => Set<RouteFixedValueMappingEntity>();
    public DbSet<InboxMessageEntity> InboxMessages => Set<InboxMessageEntity>();
    public DbSet<QueueCheckpointEntity> QueueCheckpoints => Set<QueueCheckpointEntity>();
    public DbSet<SyncSnapshotEntity> SyncSnapshots => Set<SyncSnapshotEntity>();
    public DbSet<SyncConflictEntity> SyncConflicts => Set<SyncConflictEntity>();
    public DbSet<ConfigurationAuditEntity> ConfigurationAudits => Set<ConfigurationAuditEntity>();
    public DbSet<OperationalEventEntity> OperationalEvents => Set<OperationalEventEntity>();
    public DbSet<WebhookEndpointEntity> WebhookEndpoints => Set<WebhookEndpointEntity>();
    public DbSet<WebhookEventEntity> WebhookEvents => Set<WebhookEventEntity>();
    public DbSet<WebhookDeliveryEntity> WebhookDeliveries => Set<WebhookDeliveryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AdminAccountEntity>(entity =>
        {
            entity.ToTable("AdminAccount");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserName).IsUnique();
            entity.Property(x => x.UserName).HasMaxLength(64);
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
        });

        modelBuilder.Entity<ManagementSettingsEntity>(entity =>
        {
            entity.ToTable("ManagementSettings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
        });

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
            entity.HasIndex(x => new { x.SourceSystemId, x.EntityType, x.Enabled });
            entity.HasIndex(x => new { x.DestinationSystemId, x.EntityType, x.Direction, x.Enabled });
            entity.HasIndex(x => new { x.MappingMaintenanceStartedAtUtc, x.EntityType });
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.EntityType).HasMaxLength(128);
            entity.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.DeploymentState).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ConflictScope).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.DefaultConflictPolicy).HasConversion<string>().HasMaxLength(40);
            entity.HasOne(x => x.SourceSystem).WithMany().HasForeignKey(x => x.SourceSystemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.DestinationSystem).WithMany().HasForeignKey(x => x.DestinationSystemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RouteTableMappingEntity>(entity =>
        {
            entity.ToTable("RouteTableMapping");
            entity.HasKey(x => x.RouteId);
            entity.Property(x => x.SourceSchema).HasMaxLength(128);
            entity.Property(x => x.SourceTable).HasMaxLength(128);
            entity.Property(x => x.DestinationSchema).HasMaxLength(128);
            entity.Property(x => x.DestinationTable).HasMaxLength(128);
            entity.Property(x => x.SourceDeletionMode).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.SourceLogicalDeleteColumn).HasMaxLength(128);
            entity.Property(x => x.SourceLogicalDeleteValue).HasMaxLength(4000);
            entity.Property(x => x.DestinationDeletionMode).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.DestinationLogicalDeleteColumn).HasMaxLength(128);
            entity.Property(x => x.DestinationLogicalDeleteValue).HasMaxLength(4000);
            entity.HasOne(x => x.Route).WithOne(x => x.TableMapping).HasForeignKey<RouteTableMappingEntity>(x => x.RouteId);
        });

        modelBuilder.Entity<RouteColumnMappingEntity>(entity =>
        {
            entity.ToTable("RouteColumnMapping");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TableMappingId, x.SourceColumn }).IsUnique();
            entity.HasIndex(x => new { x.TableMappingId, x.DestinationColumn }).IsUnique();
            entity.Property(x => x.SourceColumn).HasMaxLength(128);
            entity.Property(x => x.DestinationColumn).HasMaxLength(128);
            entity.Property(x => x.ConflictPolicy).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.SourceDataType).HasMaxLength(64);
            entity.Property(x => x.DestinationDataType).HasMaxLength(64);
            entity.Property(x => x.ForwardTransformJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.ReverseTransformJson).HasColumnType("nvarchar(max)");
            entity.HasOne(x => x.TableMapping).WithMany(x => x.Columns).HasForeignKey(x => x.TableMappingId);
        });

        modelBuilder.Entity<RouteFixedValueMappingEntity>(entity =>
        {
            entity.ToTable("RouteFixedValueMapping");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.TableMappingId, x.Direction, x.TargetColumn }).IsUnique();
            entity.Property(x => x.Direction).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.TargetColumn).HasMaxLength(128);
            entity.Property(x => x.Value).HasMaxLength(4000);
            entity.Property(x => x.TargetDataType).HasMaxLength(64);
            entity.HasOne(x => x.TableMapping).WithMany(x => x.FixedValues).HasForeignKey(x => x.TableMappingId);
        });

        modelBuilder.Entity<InboxMessageEntity>(entity =>
        {
            entity.ToTable("InboxMessage");
            entity.HasKey(x => new { x.SourceMessageId, x.RouteId, x.DestinationSystem });
            entity.HasIndex(x => new { x.State, x.UpdatedAtUtc });
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
            entity.Property(x => x.SourcePayloadJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.DestinationPayloadJson).HasColumnType("nvarchar(max)");
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

        modelBuilder.Entity<OperationalEventEntity>(entity =>
        {
            entity.ToTable("OperationalEvent");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AcknowledgedAtUtc, x.LastOccurredAtUtc });
            entity.HasIndex(x => new { x.Category, x.Code, x.Source });
            entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Category).HasMaxLength(64);
            entity.Property(x => x.Code).HasMaxLength(100);
            entity.Property(x => x.Source).HasMaxLength(64);
            entity.Property(x => x.Target).HasMaxLength(200);
            entity.Property(x => x.Details).HasMaxLength(4000);
            entity.Property(x => x.CorrelationId).HasMaxLength(64);
            entity.Property(x => x.AcknowledgedBy).HasMaxLength(200);
        });

        modelBuilder.Entity<WebhookEndpointEntity>(entity =>
        {
            entity.ToTable("WebhookEndpoint");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Url).HasMaxLength(2048);
            entity.Property(x => x.ProtectedSecret).HasColumnType("nvarchar(max)");
            entity.Property(x => x.EventTypesJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<WebhookEventEntity>(entity =>
        {
            entity.ToTable("WebhookEvent");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OccurredAtUtc);
            entity.Property(x => x.EventType).HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<WebhookDeliveryEntity>(entity =>
        {
            entity.ToTable("WebhookDelivery");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.State, x.NextAttemptAtUtc });
            entity.HasIndex(x => new { x.State, x.DeliveredAtUtc });
            entity.HasIndex(x => new { x.State, x.LastAttemptAtUtc });
            entity.HasIndex(x => new { x.EventId, x.EndpointId }).IsUnique();
            entity.Property(x => x.State).HasMaxLength(16);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasOne(x => x.Event).WithMany(x => x.Deliveries).HasForeignKey(x => x.EventId);
            entity.HasOne(x => x.Endpoint).WithMany(x => x.Deliveries).HasForeignKey(x => x.EndpointId);
        });
    }
}
