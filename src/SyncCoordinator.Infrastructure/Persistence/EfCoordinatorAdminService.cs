using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class EfCoordinatorAdminService(
    CoordinatorDbContext dbContext,
    ProtectedConnectionStringService connectionProtector,
    TimeProvider timeProvider) : ICoordinatorAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SystemListItem>> GetSystemsAsync(CancellationToken cancellationToken) =>
        await dbContext.Systems.AsNoTracking()
            .OrderBy(x => x.Code)
            .Select(x => new SystemListItem(x.Id, x.Code, x.DisplayName, x.Provider, x.Enabled, x.ProtectedConnectionString != null))
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
        CancellationToken cancellationToken)
    {
        Normalize(input);
        ConfigurationValidator.ValidateSystem(input);
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
            entity.DisplayName = input.DisplayName;
            entity.Provider = input.Provider;
            entity.Enabled = input.Enabled;
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

        AddAudit("System", entity.Id.ToString("N"), entity.DisplayName, action, before, SystemSnapshot(entity));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Id;
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
                Port = system.Provider == "MySql" ? 3306 : 1433,
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
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<RouteConfigurationInput?> GetRouteAsync(Guid id, CancellationToken cancellationToken)
    {
        var route = await dbContext.Routes.AsNoTracking()
            .Include(x => x.FieldPolicies)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return route is null ? null : ToInput(route);
    }

    public async Task<Guid> SaveRouteAsync(
        RouteConfigurationInput input,
        CancellationToken cancellationToken)
    {
        Normalize(input);
        var systemCodes = await dbContext.Systems.AsNoTracking()
            .Select(x => x.Code)
            .ToListAsync(cancellationToken);
        ConfigurationValidator.ValidateRoute(input, systemCodes);

        SyncRouteEntity entity;
        object? before = null;
        var action = "Created";
        if (input.Id is { } id)
        {
            entity = await dbContext.Routes.Include(x => x.FieldPolicies)
                         .SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
                     throw new KeyNotFoundException("指定された同期ルートは存在しません。");
            before = RouteSnapshot(entity);
            dbContext.RouteFieldPolicies.RemoveRange(entity.FieldPolicies);
            entity.FieldPolicies = [];
            action = "Updated";
        }
        else
        {
            entity = new SyncRouteEntity
            {
                Id = Guid.NewGuid(),
                Name = input.Name,
                SourceSystem = input.SourceSystem,
                EntityType = input.EntityType
            };
            dbContext.Routes.Add(entity);
        }

        entity.Name = input.Name;
        entity.SourceSystem = input.SourceSystem;
        entity.EntityType = input.EntityType;
        entity.DestinationMode = input.DestinationMode;
        entity.DestinationSystem = input.DestinationMode == DestinationMode.FixedSystem
            ? input.DestinationSystem
            : null;
        entity.ConflictScope = input.ConflictScope;
        entity.DefaultConflictPolicy = input.DefaultConflictPolicy;
        entity.Enabled = input.Enabled;
        entity.FieldPolicies.AddRange(input.FieldPolicies.Select(x => new RouteFieldPolicyEntity
        {
            Id = Guid.NewGuid(),
            RouteId = entity.Id,
            FieldName = x.FieldName,
            Policy = x.Policy
        }));

        AddAudit("Route", entity.Id.ToString("N"), entity.Name, action, before, RouteSnapshot(entity));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<IReadOnlyList<TableMappingListItem>> GetTableMappingsAsync(CancellationToken cancellationToken) =>
        await dbContext.RouteTableMappings.AsNoTracking()
            .OrderBy(x => x.Route.Name).ThenBy(x => x.DestinationSystem)
            .Select(x => new TableMappingListItem(
                x.Id,
                x.RouteId,
                x.Route.Name,
                x.Route.SourceSystem,
                x.DestinationSystem,
                x.SourceSchema + "." + x.SourceTable,
                x.DestinationSchema + "." + x.DestinationTable,
                x.Columns.Count))
            .ToListAsync(cancellationToken);

    public async Task<TableMappingInput?> GetTableMappingAsync(
        Guid routeId,
        string destinationSystem,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.RouteTableMappings.AsNoTracking()
            .Include(x => x.Columns)
            .SingleOrDefaultAsync(
                x => x.RouteId == routeId && x.DestinationSystem == destinationSystem,
                cancellationToken);
        return entity is null ? null : ToInput(entity);
    }

    public async Task<Guid> SaveTableMappingAsync(TableMappingInput input, CancellationToken cancellationToken)
    {
        Normalize(input);
        var route = await dbContext.Routes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.RouteId, cancellationToken) ??
                    throw new KeyNotFoundException("指定された同期ルートは存在しません。");
        var routeInput = ToInput(route);
        ConfigurationValidator.ValidateTableMapping(input, routeInput);
        if (!await dbContext.Systems.AnyAsync(x => x.Code == input.DestinationSystem, cancellationToken))
        {
            throw new ConfigurationValidationException(["宛先システムが存在しません。"]);
        }

        var entity = await dbContext.RouteTableMappings.Include(x => x.Columns)
            .SingleOrDefaultAsync(
                x => x.RouteId == input.RouteId && x.DestinationSystem == input.DestinationSystem,
                cancellationToken);
        object? before = null;
        var action = "Created";
        if (entity is null)
        {
            entity = new RouteTableMappingEntity
            {
                Id = Guid.NewGuid(),
                RouteId = input.RouteId,
                DestinationSystem = input.DestinationSystem,
                SourceSchema = input.SourceSchema,
                SourceTable = input.SourceTable,
                DestinationSchema = input.DestinationSchema,
                DestinationTable = input.DestinationTable
            };
            dbContext.RouteTableMappings.Add(entity);
        }
        else
        {
            before = TableMappingSnapshot(entity);
            dbContext.RouteColumnMappings.RemoveRange(entity.Columns);
            entity.Columns = [];
            action = "Updated";
        }
        entity.SourceSchema = input.SourceSchema;
        entity.SourceTable = input.SourceTable;
        entity.DestinationSchema = input.DestinationSchema;
        entity.DestinationTable = input.DestinationTable;
        entity.Columns.AddRange(input.Columns.Select(x => new RouteColumnMappingEntity
        {
            Id = Guid.NewGuid(),
            TableMappingId = entity.Id,
            SourceColumn = x.SourceColumn,
            DestinationColumn = x.DestinationColumn,
            IsKey = x.IsKey
        }));
        AddAudit("TableMapping", entity.Id.ToString("N"), route.Name, action, before, TableMappingSnapshot(entity));
        await dbContext.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    public async Task<IReadOnlyList<InboxListItem>> GetRecentInboxAsync(
        int take,
        CancellationToken cancellationToken) =>
        await (from inbox in dbContext.InboxMessages.AsNoTracking()
               join route in dbContext.Routes.AsNoTracking() on inbox.RouteId equals route.Id
               orderby inbox.UpdatedAtUtc descending
               select new InboxListItem(
                   inbox.SourceMessageId,
                   route.Name,
                   inbox.DestinationSystem,
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
                RouteName = x.Route.Name
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
            conflict.Conflict.DestinationSystem,
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
        entity.SourceSystem,
        entity.EntityType,
        entity.DestinationMode,
        entity.DestinationSystem,
        entity.ConflictScope,
        entity.DefaultConflictPolicy,
        entity.Enabled,
        FieldPolicies = entity.FieldPolicies
            .OrderBy(x => x.FieldName)
            .Select(x => new { x.FieldName, x.Policy })
            .ToArray()
    };

    private static RouteConfigurationInput ToInput(SyncRouteEntity route) => new()
    {
        Id = route.Id,
        Name = route.Name,
        SourceSystem = route.SourceSystem,
        EntityType = route.EntityType,
        DestinationMode = route.DestinationMode,
        DestinationSystem = route.DestinationSystem,
        ConflictScope = route.ConflictScope,
        DefaultConflictPolicy = route.DefaultConflictPolicy,
        Enabled = route.Enabled,
        FieldPolicies = route.FieldPolicies.OrderBy(x => x.FieldName).Select(x => new FieldPolicyInput
        {
            FieldName = x.FieldName,
            Policy = x.Policy
        }).ToList()
    };

    private static TableMappingInput ToInput(RouteTableMappingEntity entity) => new()
    {
        Id = entity.Id,
        RouteId = entity.RouteId,
        DestinationSystem = entity.DestinationSystem,
        SourceSchema = entity.SourceSchema,
        SourceTable = entity.SourceTable,
        DestinationSchema = entity.DestinationSchema,
        DestinationTable = entity.DestinationTable,
        Columns = entity.Columns.OrderBy(x => x.SourceColumn).Select(x => new ColumnMappingInput
        {
            SourceColumn = x.SourceColumn,
            DestinationColumn = x.DestinationColumn,
            IsKey = x.IsKey
        }).ToList()
    };

    private static object TableMappingSnapshot(RouteTableMappingEntity entity) => new
    {
        entity.Id,
        entity.RouteId,
        entity.DestinationSystem,
        Source = entity.SourceSchema + "." + entity.SourceTable,
        Destination = entity.DestinationSchema + "." + entity.DestinationTable,
        Columns = entity.Columns.OrderBy(x => x.SourceColumn)
            .Select(x => new { x.SourceColumn, x.DestinationColumn, x.IsKey }).ToArray()
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
        input.EntityType = input.EntityType.Trim();
        input.DestinationSystem = input.DestinationSystem?.Trim();
        foreach (var field in input.FieldPolicies)
        {
            field.FieldName = field.FieldName.Trim();
        }
    }

    private static void Normalize(TableMappingInput input)
    {
        input.DestinationSystem = input.DestinationSystem.Trim();
        input.SourceSchema = input.SourceSchema.Trim();
        input.SourceTable = input.SourceTable.Trim();
        input.DestinationSchema = input.DestinationSchema.Trim();
        input.DestinationTable = input.DestinationTable.Trim();
        foreach (var column in input.Columns)
        {
            column.SourceColumn = column.SourceColumn.Trim();
            column.DestinationColumn = column.DestinationColumn.Trim();
        }
    }
}
