using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class EfCoordinatorAdminService(
    CoordinatorDbContext dbContext,
    ProtectedConnectionStringService connectionProtector,
    WebhookOutboxWriter webhookOutbox,
    TimeProvider timeProvider) : ICoordinatorAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SystemListItem>> GetSystemsAsync(CancellationToken cancellationToken) =>
        await dbContext.Systems.AsNoTracking()
            .OrderBy(x => x.Code)
            .Select(x => new SystemListItem(
                x.Id,
                x.Code,
                x.DisplayName,
                x.Provider,
                x.Enabled,
                x.ProtectedConnectionString != null,
                x.PausedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<SystemConfigurationInput?> GetSystemAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.Systems.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new SystemConfigurationInput
            {
                Id = x.Id,
                Code = x.Code,
                DisplayName = x.DisplayName,
                Provider = x.Provider,
                Enabled = x.Enabled
            })
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<Guid> SaveSystemAsync(
        SystemConfigurationInput input,
        DatabaseConnectionInput? connection,
        CancellationToken cancellationToken)
    {
        Normalize(input);
        ConfigurationValidator.ValidateSystem(input);
        if (connection is not null)
        {
            ConfigurationValidator.ValidateConnection(connection, input.Provider);
        }

        SystemDefinitionEntity entity;
        object? before = null;
        var action = "Created";

        if (input.Id is { } id)
        {
            entity = await dbContext.Systems.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
                     throw new KeyNotFoundException("指定されたシステムは存在しません。");
            if (!string.Equals(entity.Code, input.Code, StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigurationValidationException(["使用開始後のシステムコードは変更できません。"]);
            }
            before = SystemSnapshot(entity);
            if (!string.Equals(entity.Provider, input.Provider, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entity.ProtectedConnectionString))
            {
                throw new ConfigurationValidationException(["接続情報の登録後はProviderを変更できません。接続情報を再設定する運用が必要です。"]);
            }
            if (entity.Enabled && !input.Enabled && await dbContext.Routes.AnyAsync(
                    x => x.Enabled &&
                         (x.SourceSystemId == entity.Id || x.DestinationSystemId == entity.Id),
                    cancellationToken))
            {
                throw new ConfigurationValidationException([
                    "有効な同期ルールで使用中のシステムは無効化できません。構成を外す場合は先に関連ルールを無効化し、保守目的なら一時停止を使用してください。"
                ]);
            }
            entity.DisplayName = input.DisplayName;
            entity.Provider = input.Provider;
            entity.Enabled = input.Enabled;
            if (!input.Enabled)
            {
                entity.PausedAtUtc = null;
            }
            action = "Updated";
        }
        else
        {
            var duplicate = await dbContext.Systems.AnyAsync(
                x => x.Code == input.Code,
                cancellationToken);
            if (duplicate)
            {
                throw new ConfigurationValidationException(["同じシステムコードが既に存在します。"]);
            }

            entity = new SystemDefinitionEntity
            {
                Id = Guid.NewGuid(),
                Code = input.Code,
                DisplayName = input.DisplayName,
                Provider = input.Provider,
                Enabled = input.Enabled
            };
            dbContext.Systems.Add(entity);
        }

        if (connection is not null)
        {
            connection.SystemId = entity.Id;
            ApplyDatabaseConnection(entity, connection);
        }

        AddAudit("System", entity.Id.ToString("N"), entity.DisplayName, action, before, SystemSnapshot(entity));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task SetSystemPausedAsync(
        Guid systemId,
        bool paused,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.Systems.SingleOrDefaultAsync(x => x.Id == systemId, cancellationToken) ??
                     throw new KeyNotFoundException("指定されたシステムは存在しません。");
        if (paused && !entity.Enabled)
        {
            throw new ConfigurationValidationException(["無効なシステムは一時停止できません。"]);
        }
        if ((entity.PausedAtUtc is not null) == paused)
        {
            return;
        }

        var before = SystemSnapshot(entity);
        entity.PausedAtUtc = paused ? timeProvider.GetUtcNow() : null;
        AddAudit(
            "System",
            entity.Id.ToString("N"),
            entity.DisplayName,
            paused ? "Paused" : "Resumed",
            before,
            SystemSnapshot(entity));
        var eventType = paused ? WebhookEventTypes.SystemPaused : WebhookEventTypes.SystemResumed;
        await webhookOutbox.AddAsync(new WebhookEventNotification(
            WebhookEventId.Create(eventType, entity.Id, entity.PausedAtUtc ?? timeProvider.GetUtcNow()),
            eventType,
            timeProvider.GetUtcNow(),
            SystemCode: entity.Code,
            SystemName: entity.DisplayName), cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<DatabaseConnectionInput?> GetDatabaseConnectionAsync(
        Guid systemId,
        CancellationToken cancellationToken)
    {
        var system = await dbContext.Systems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == systemId, cancellationToken);
        if (system is null) return null;
        if (string.IsNullOrWhiteSpace(system.ProtectedConnectionString))
        {
            return new DatabaseConnectionInput
            {
                SystemId = systemId,
                Port = DefaultPort(system.Provider),
                Encrypt = true
            };
        }
        return ManagedConnectionStringFactory.Parse(
            systemId,
            system.Provider,
            connectionProtector.Unprotect(system.ProtectedConnectionString));
    }

    public async Task SaveDatabaseConnectionAsync(
        DatabaseConnectionInput input,
        CancellationToken cancellationToken)
    {
        var system = await dbContext.Systems.SingleOrDefaultAsync(x => x.Id == input.SystemId, cancellationToken) ??
                     throw new KeyNotFoundException("指定されたシステムは存在しません。");
        ConfigurationValidator.ValidateConnection(input, system.Provider);
        ApplyDatabaseConnection(system, input);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void ApplyDatabaseConnection(SystemDefinitionEntity system, DatabaseConnectionInput input)
    {
        var password = input.Password;
        if (string.IsNullOrEmpty(password) && input.HasStoredPassword && !string.IsNullOrWhiteSpace(system.ProtectedConnectionString))
        {
            password = ManagedConnectionStringFactory.GetPassword(
                system.Provider,
                connectionProtector.Unprotect(system.ProtectedConnectionString));
        }
        var connectionString = ManagedConnectionStringFactory.Build(system.Provider, input, password);
        var before = new { Configured = !string.IsNullOrWhiteSpace(system.ProtectedConnectionString) };
        system.ProtectedConnectionString = connectionProtector.Protect(connectionString);
        system.ConnectionUpdatedAtUtc = timeProvider.GetUtcNow();
        input.Password = string.Empty;
        input.HasStoredPassword = !input.IntegratedSecurity;
        AddAudit("DatabaseConnection", system.Id.ToString("N"), system.DisplayName, "Updated", before, ConnectionSnapshot(input));
    }

    public async Task<RouteConfigurationInput?> GetRouteAsync(Guid id, CancellationToken cancellationToken)
    {
        var route = await dbContext.Routes.AsNoTracking()
            .Include(x => x.SourceSystem)
            .Include(x => x.DestinationSystem)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return route is null ? null : ToInput(route);
    }

    public async Task<Guid> SaveRouteAsync(
        RouteConfigurationInput input,
        CancellationToken cancellationToken)
    {
        Normalize(input);
        var systems = await dbContext.Systems
            .Where(x => x.Enabled)
            .ToListAsync(cancellationToken);
        ConfigurationValidator.ValidateRoute(input, systems.Select(x => x.Code).ToArray());
        var sourceSystem = systems.Single(x => string.Equals(x.Code, input.SourceSystem, StringComparison.OrdinalIgnoreCase));
        var destinationSystem = systems.Single(x => string.Equals(x.Code, input.DestinationSystem, StringComparison.OrdinalIgnoreCase));

        SyncRouteEntity entity;
        object? before = null;
        var action = "Created";
        var resetDeployment = false;
        if (input.Id is { } id)
        {
            entity = await dbContext.Routes
                         .Include(x => x.SourceSystem)
                         .Include(x => x.DestinationSystem)
                         .SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
                     throw new KeyNotFoundException("指定された同期ルールは存在しません。");
            before = RouteSnapshot(entity);
            resetDeployment = entity.SourceSystemId != sourceSystem.Id ||
                              entity.DestinationSystemId != destinationSystem.Id ||
                              entity.Direction != input.Direction;
            if (resetDeployment && entity.DeploymentState == DatabaseDeploymentState.Prepared)
            {
                throw new ConfigurationValidationException([
                    "DB反映済みルールの送信元・送信先・同期方向は変更できません。既存Triggerの廃止フローを実施してから変更してください。"
                ]);
            }
            action = "Updated";
        }
        else
        {
            entity = new SyncRouteEntity
            {
                Id = Guid.NewGuid(),
                Name = input.Name,
                SourceSystemId = sourceSystem.Id,
                DestinationSystemId = destinationSystem.Id,
                SourceSystem = sourceSystem,
                DestinationSystem = destinationSystem,
                EntityType = string.Empty,
                DeploymentState = DatabaseDeploymentState.Draft,
                Enabled = false
            };
            entity.EntityType = CreateInternalEntityType(entity.Id);
            dbContext.Routes.Add(entity);
        }

        entity.Name = input.Name;
        entity.SourceSystemId = sourceSystem.Id;
        entity.DestinationSystemId = destinationSystem.Id;
        entity.SourceSystem = sourceSystem;
        entity.DestinationSystem = destinationSystem;
        entity.Direction = input.Direction;
        entity.ConflictScope = input.ConflictScope;
        entity.DefaultConflictPolicy = input.DefaultConflictPolicy;
        if (resetDeployment)
        {
            entity.DeploymentState = DatabaseDeploymentState.Draft;
            entity.Enabled = false;
        }
        AddAudit("Route", entity.Id.ToString("N"), entity.Name, action, before, RouteSnapshot(entity));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<IReadOnlyList<TableMappingListItem>> GetTableMappingsAsync(CancellationToken cancellationToken) =>
        await dbContext.RouteTableMappings.AsNoTracking()
            .OrderBy(x => x.Route.Name)
            .Select(x => new TableMappingListItem(
                x.RouteId,
                x.Route.Name,
                x.Route.SourceSystem.Code,
                x.Route.SourceSystem.DisplayName,
                x.Route.DestinationSystem.Code,
                x.Route.DestinationSystem.DisplayName,
                x.SourceSchema + "." + x.SourceTable,
                x.DestinationSchema + "." + x.DestinationTable,
                x.Columns.Count))
            .ToListAsync(cancellationToken);

    public async Task<TableMappingInput?> GetTableMappingAsync(
        Guid routeId,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.RouteTableMappings.AsNoTracking()
            .Include(x => x.Columns)
            .Include(x => x.FixedValues)
            .Include(x => x.Route)
            .SingleOrDefaultAsync(x => x.RouteId == routeId, cancellationToken);
        return entity is null ? null : ToInput(entity);
    }

    public async Task<Guid> SaveTableMappingAsync(TableMappingInput input, CancellationToken cancellationToken)
    {
        Normalize(input);
        var route = await dbContext.Routes
                        .Include(x => x.SourceSystem)
                        .Include(x => x.DestinationSystem)
                        .SingleOrDefaultAsync(x => x.Id == input.RouteId, cancellationToken) ??
                    throw new KeyNotFoundException("指定された同期ルールは存在しません。");
        var routeInput = ToInput(route);
        ConfigurationValidator.ValidateTableMapping(input, routeInput);
        var entity = await dbContext.RouteTableMappings
            .Include(x => x.Columns)
            .Include(x => x.FixedValues)
            .SingleOrDefaultAsync(x => x.RouteId == input.RouteId, cancellationToken);
        object? before = null;
        var action = "Created";
        var requiresDeployment = entity is null;
        if (entity is null)
        {
            entity = new RouteTableMappingEntity
            {
                RouteId = input.RouteId,
                SourceSchema = input.SourceSchema,
                SourceTable = input.SourceTable,
                DestinationSchema = input.DestinationSchema,
                DestinationTable = input.DestinationTable
            };
            dbContext.RouteTableMappings.Add(entity);
        }
        else
        {
            var tableChanged =
                !string.Equals(entity.SourceSchema, input.SourceSchema, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entity.SourceTable, input.SourceTable, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entity.DestinationSchema, input.DestinationSchema, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entity.DestinationTable, input.DestinationTable, StringComparison.OrdinalIgnoreCase);
            if (tableChanged && route.DeploymentState == DatabaseDeploymentState.Prepared)
            {
                throw new ConfigurationValidationException([
                    "DB反映済みルールの対象テーブルは変更できません。既存Triggerの廃止フローを実施してから変更してください。"
                ]);
            }
            var currentKeys = entity.Columns.Where(x => x.IsKey)
                .Select(x => $"{x.SourceColumn}\u001f{x.DestinationColumn}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var incomingKeys = input.Columns.Where(x => x.IsKey)
                .Select(x => $"{x.SourceColumn}\u001f{x.DestinationColumn}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            requiresDeployment = tableChanged || !currentKeys.SetEquals(incomingKeys);
            var deleteConfigurationChanged =
                entity.SyncDeletes != input.SyncDeletes ||
                entity.SourceDeletionMode != input.SourceDeletionMode ||
                !string.Equals(entity.SourceLogicalDeleteColumn ?? string.Empty, input.SourceLogicalDeleteColumn, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entity.SourceLogicalDeleteValue ?? string.Empty, input.SourceLogicalDeleteValue, StringComparison.Ordinal) ||
                entity.DestinationDeletionMode != input.DestinationDeletionMode ||
                !string.Equals(entity.DestinationLogicalDeleteColumn ?? string.Empty, input.DestinationLogicalDeleteColumn, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entity.DestinationLogicalDeleteValue ?? string.Empty, input.DestinationLogicalDeleteValue, StringComparison.Ordinal);
            requiresDeployment |= deleteConfigurationChanged;
            before = TableMappingSnapshot(entity);
            dbContext.RouteColumnMappings.RemoveRange(entity.Columns);
            dbContext.RouteFixedValueMappings.RemoveRange(entity.FixedValues);
            entity.Columns = [];
            entity.FixedValues = [];
            action = "Updated";
        }
        entity.SourceSchema = input.SourceSchema;
        entity.SourceTable = input.SourceTable;
        entity.DestinationSchema = input.DestinationSchema;
        entity.DestinationTable = input.DestinationTable;
        entity.SyncDeletes = input.SyncDeletes;
        entity.SourceDeletionMode = input.SourceDeletionMode;
        entity.SourceLogicalDeleteColumn = NullIfEmpty(input.SourceLogicalDeleteColumn);
        entity.SourceLogicalDeleteValue = NullIfEmpty(input.SourceLogicalDeleteValue);
        entity.DestinationDeletionMode = input.DestinationDeletionMode;
        entity.DestinationLogicalDeleteColumn = NullIfEmpty(input.DestinationLogicalDeleteColumn);
        entity.DestinationLogicalDeleteValue = NullIfEmpty(input.DestinationLogicalDeleteValue);
        entity.Columns.AddRange(input.Columns.Select(x => new RouteColumnMappingEntity
        {
            Id = Guid.NewGuid(),
            TableMappingId = entity.RouteId,
            SourceColumn = x.SourceColumn,
            DestinationColumn = x.DestinationColumn,
            IsKey = x.IsKey,
            ConflictPolicy = x.ConflictPolicy
        }));
        entity.FixedValues.AddRange(input.FixedValues.Select(x => new RouteFixedValueMappingEntity
        {
            Id = Guid.NewGuid(),
            TableMappingId = entity.RouteId,
            Direction = x.Direction,
            TargetColumn = x.TargetColumn,
            Value = x.Value
        }));

        route.Enabled = false;
        if (requiresDeployment)
        {
            route.DeploymentState = DatabaseDeploymentState.Draft;
        }

        AddAudit("TableMapping", entity.RouteId.ToString("N"), route.Name, action, before, TableMappingSnapshot(entity));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.RouteId;
    }

    public async Task<IReadOnlyList<InboxListItem>> GetRecentInboxAsync(
        int take,
        CancellationToken cancellationToken) =>
        await (from inbox in dbContext.InboxMessages.AsNoTracking()
               join route in dbContext.Routes.AsNoTracking() on inbox.RouteId equals route.Id
               join system in dbContext.Systems.AsNoTracking() on inbox.DestinationSystem equals system.Code
               orderby inbox.UpdatedAtUtc descending
               select new InboxListItem(
                   inbox.SourceMessageId,
                   route.Name,
                   inbox.DestinationSystem,
                   system.DisplayName,
                   inbox.State,
                   inbox.AttemptCount,
                   inbox.FirstSeenAtUtc,
                   inbox.UpdatedAtUtc,
                   inbox.LastError))
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<CheckpointListItem>> GetCheckpointsAsync(
        CancellationToken cancellationToken) =>
        await (from system in dbContext.Systems.AsNoTracking()
               join checkpoint in dbContext.QueueCheckpoints.AsNoTracking()
                   on system.Code equals checkpoint.SystemCode into checkpoints
               from checkpoint in checkpoints.DefaultIfEmpty()
               orderby system.Code
               select new CheckpointListItem(
                   system.Code,
                   system.DisplayName,
                   checkpoint == null ? 0 : checkpoint.LastQueueId,
                   checkpoint == null ? null : checkpoint.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<ConflictDetails?> GetConflictAsync(Guid id, CancellationToken cancellationToken)
    {
        var conflict = await dbContext.SyncConflicts.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                Conflict = x,
                RouteName = x.Route.Name,
                SourceSystemName = dbContext.Systems.Where(system => system.Code == x.SourceSystem)
                    .Select(system => system.DisplayName).FirstOrDefault() ?? x.SourceSystem,
                DestinationSystemName = dbContext.Systems.Where(system => system.Code == x.DestinationSystem)
                    .Select(system => system.DisplayName).FirstOrDefault() ?? x.DestinationSystem
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (conflict is null)
        {
            return null;
        }

        var fields = JsonSerializer.Deserialize<List<FieldConflict>>(
                         conflict.Conflict.FieldsJson,
                         JsonOptions) ?? [];
        return new ConflictDetails(
            conflict.Conflict.Id,
            conflict.RouteName,
            conflict.Conflict.SourceMessageId,
            conflict.Conflict.DeliveryMessageId,
            conflict.Conflict.SourceSystem,
            conflict.SourceSystemName,
            conflict.Conflict.DestinationSystem,
            conflict.DestinationSystemName,
            conflict.Conflict.EntityType,
            conflict.Conflict.EntityId,
            conflict.Conflict.Scope,
            fields,
            conflict.Conflict.DetectedAtUtc,
            conflict.Conflict.ResolvedAtUtc);
    }

    public async Task<IReadOnlyList<ConfigurationAuditListItem>> GetRecentConfigurationAuditsAsync(
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.ConfigurationAudits.AsNoTracking()
            .OrderByDescending(x => x.ChangedAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new ConfigurationAuditListItem(
                x.Id,
                x.ConfigurationType,
                x.ConfigurationName,
                x.Action,
                x.ChangedBy,
                x.ChangedAtUtc))
            .ToListAsync(cancellationToken);

    private void AddAudit(
        string type,
        string id,
        string name,
        string action,
        object? before,
        object after) =>
        dbContext.ConfigurationAudits.Add(new ConfigurationAuditEntity
        {
            Id = Guid.NewGuid(),
            ConfigurationType = type,
            ConfigurationId = id,
            ConfigurationName = name,
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, JsonOptions),
            AfterJson = JsonSerializer.Serialize(after, JsonOptions),
            ChangedBy = "management-ui",
            ChangedAtUtc = timeProvider.GetUtcNow()
        });

    private static object SystemSnapshot(SystemDefinitionEntity entity) => new
    {
        entity.Id,
        entity.Code,
        entity.DisplayName,
        entity.Provider,
        entity.Enabled,
        entity.PausedAtUtc,
        ConnectionConfigured = !string.IsNullOrWhiteSpace(entity.ProtectedConnectionString)
    };

    private static object ConnectionSnapshot(DatabaseConnectionInput input) => new
    {
        input.SystemId,
        input.Server,
        input.Port,
        input.Database,
        input.UserName,
        input.IntegratedSecurity,
        input.Encrypt,
        input.TrustServerCertificate,
        Password = "***"
    };

    private static object RouteSnapshot(SyncRouteEntity entity) => new
    {
        entity.Id,
        entity.Name,
        SourceSystem = entity.SourceSystem.Code,
        entity.EntityType,
        DestinationSystem = entity.DestinationSystem.Code,
        entity.Direction,
        entity.ConflictScope,
        entity.DefaultConflictPolicy,
        entity.DeploymentState,
        entity.Enabled
    };

    private static RouteConfigurationInput ToInput(SyncRouteEntity route) => new()
    {
        Id = route.Id,
        Name = route.Name,
        SourceSystem = route.SourceSystem.Code,
        DestinationSystem = route.DestinationSystem.Code,
        Direction = route.Direction,
        ConflictScope = route.ConflictScope,
        DefaultConflictPolicy = route.DefaultConflictPolicy,
        DeploymentState = route.DeploymentState,
        Enabled = route.Enabled
    };

    private static TableMappingInput ToInput(RouteTableMappingEntity entity) => new()
        {
            RouteId = entity.RouteId,
            SourceSchema = entity.SourceSchema,
            SourceTable = entity.SourceTable,
            DestinationSchema = entity.DestinationSchema,
            DestinationTable = entity.DestinationTable,
            SyncDeletes = entity.SyncDeletes,
            SourceDeletionMode = entity.SourceDeletionMode,
            SourceLogicalDeleteColumn = entity.SourceLogicalDeleteColumn ?? string.Empty,
            SourceLogicalDeleteValue = entity.SourceLogicalDeleteValue ?? string.Empty,
            DestinationDeletionMode = entity.DestinationDeletionMode,
            DestinationLogicalDeleteColumn = entity.DestinationLogicalDeleteColumn ?? string.Empty,
            DestinationLogicalDeleteValue = entity.DestinationLogicalDeleteValue ?? string.Empty,
            Columns = entity.Columns.OrderBy(x => x.SourceColumn).Select(x => new ColumnMappingInput
            {
                SourceColumn = x.SourceColumn,
                DestinationColumn = x.DestinationColumn,
                IsKey = x.IsKey,
                ConflictPolicy = x.ConflictPolicy
            }).ToList(),
            FixedValues = entity.FixedValues
                .OrderBy(x => x.Direction)
                .ThenBy(x => x.TargetColumn)
                .Select(x => new FixedValueMappingInput
                {
                    Direction = x.Direction,
                    TargetColumn = x.TargetColumn,
                    Value = x.Value
                }).ToList()
        };

    private static object TableMappingSnapshot(RouteTableMappingEntity entity) => new
    {
        entity.RouteId,
        Source = entity.SourceSchema + "." + entity.SourceTable,
        Destination = entity.DestinationSchema + "." + entity.DestinationTable,
        DeleteSynchronization = new
        {
            entity.SyncDeletes,
            entity.SourceDeletionMode,
            entity.SourceLogicalDeleteColumn,
            entity.SourceLogicalDeleteValue,
            entity.DestinationDeletionMode,
            entity.DestinationLogicalDeleteColumn,
            entity.DestinationLogicalDeleteValue
        },
        Columns = entity.Columns.OrderBy(x => x.SourceColumn)
            .Select(x => new
            {
                x.SourceColumn,
                x.DestinationColumn,
                x.IsKey,
                x.ConflictPolicy
            }).ToArray(),
        FixedValues = entity.FixedValues
            .OrderBy(x => x.Direction)
            .ThenBy(x => x.TargetColumn)
            .Select(x => new { x.Direction, x.TargetColumn, x.Value })
            .ToArray()
    };

    private static void Normalize(SystemConfigurationInput input)
    {
        input.Code = input.Code.Trim();
        input.DisplayName = input.DisplayName.Trim();
        input.Provider = input.Provider.Trim();
    }

    private static void Normalize(RouteConfigurationInput input)
    {
        input.Name = input.Name.Trim();
        input.SourceSystem = input.SourceSystem.Trim();
        input.DestinationSystem = input.DestinationSystem.Trim();
    }

    private static void Normalize(TableMappingInput input)
    {
        input.SourceSchema = input.SourceSchema.Trim();
        input.SourceTable = input.SourceTable.Trim();
        input.DestinationSchema = input.DestinationSchema.Trim();
        input.DestinationTable = input.DestinationTable.Trim();
        input.SourceLogicalDeleteColumn = input.SourceLogicalDeleteColumn.Trim();
        input.SourceLogicalDeleteValue = input.SourceLogicalDeleteValue.Trim();
        input.DestinationLogicalDeleteColumn = input.DestinationLogicalDeleteColumn.Trim();
        input.DestinationLogicalDeleteValue = input.DestinationLogicalDeleteValue.Trim();
        if (!input.SyncDeletes || input.SourceDeletionMode == DeletionMode.Physical)
        {
            input.SourceLogicalDeleteColumn = string.Empty;
            input.SourceLogicalDeleteValue = string.Empty;
        }
        if (!input.SyncDeletes || input.DestinationDeletionMode == DeletionMode.Physical)
        {
            input.DestinationLogicalDeleteColumn = string.Empty;
            input.DestinationLogicalDeleteValue = string.Empty;
        }
        foreach (var column in input.Columns)
        {
            column.SourceColumn = column.SourceColumn.Trim();
            column.DestinationColumn = column.DestinationColumn.Trim();
        }
        foreach (var fixedValue in input.FixedValues)
        {
            fixedValue.TargetColumn = fixedValue.TargetColumn.Trim();
        }
    }

    private static string CreateInternalEntityType(Guid routeId) => $"rule:{routeId:N}";

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static int DefaultPort(string provider) => provider.ToUpperInvariant() switch
    {
        "MYSQL" => 3306,
        "POSTGRESQL" => 5432,
        _ => 1433
    };
}
