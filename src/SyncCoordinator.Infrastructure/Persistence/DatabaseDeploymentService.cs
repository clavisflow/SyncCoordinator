using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class DatabaseDeploymentOptions
{
    public bool AllowDirectApply { get; set; }
}

public sealed class DatabaseDeploymentService(
    CoordinatorDbContext dbContext,
    ProtectedConnectionStringService protector,
    TimeProvider timeProvider,
    IOptions<DatabaseDeploymentOptions> options,
    IOperationalEventRecorder operationalEvents) : IDatabaseDeploymentService
{
    public async Task<DatabaseDeploymentPlan> GetPlanAsync(Guid routeId, CancellationToken cancellationToken)
    {
        var built = await BuildPlanAsync(routeId, cancellationToken);
        var targets = new List<DatabaseDeploymentTarget>(built.Targets.Count);
        foreach (var target in built.Targets)
        {
            DatabaseDeploymentTargetStatus status;
            try
            {
                status = await VerifyTargetAsync(target, cancellationToken)
                    ? DatabaseDeploymentTargetStatus.Applied
                    : DatabaseDeploymentTargetStatus.NotApplied;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                status = DatabaseDeploymentTargetStatus.Unavailable;
            }
            targets.Add(target.Public with { Status = status });
        }

        var displayedState = built.Plan.State == DatabaseDeploymentState.Prepared &&
                             targets.All(x => x.Status == DatabaseDeploymentTargetStatus.Applied)
            ? DatabaseDeploymentState.Prepared
            : DatabaseDeploymentState.Draft;
        return built.Plan with { State = displayedState, Targets = targets };
    }

    public async Task<DatabaseDeploymentResult> ApplyTargetAsync(
        Guid routeId,
        string systemCode,
        string databaseNameConfirmation,
        CancellationToken cancellationToken)
    {
        if (!options.Value.AllowDirectApply)
        {
            throw new ConfigurationValidationException(["管理画面からのDB直接反映は構成で無効になっています。SQLをダウンロードしてDBAが実行してください。"]);
        }
        var built = await BuildPlanAsync(routeId, cancellationToken);
        var target = built.Targets.SingleOrDefault(x =>
                         string.Equals(x.Public.SystemCode, systemCode, StringComparison.OrdinalIgnoreCase)) ??
                     throw new KeyNotFoundException("指定された反映先は存在しません。");
        if (!string.Equals(target.Public.DatabaseName, databaseNameConfirmation, StringComparison.Ordinal))
        {
            throw new ConfigurationValidationException(["確認用のデータベース名が一致しません。"]);
        }

        await using var connection = CreateConnection(target.System);
        DbTransaction? transaction = null;

        try
        {
            await connection.OpenAsync(cancellationToken);
            if (IsSqlServer(target.System.Provider) || IsPostgreSql(target.System.Provider))
            {
                transaction = await connection.BeginTransactionAsync(cancellationToken);
            }

            foreach (var batch in target.Batches)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    batch,
                    transaction: transaction,
                    cancellationToken: cancellationToken));
            }
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (Exception exception)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await operationalEvents.RecordAsync(new OperationalEventInput(
                    OperationalEventSeverity.Error,
                    OperationalEventCategories.Database,
                    OperationalEventCodes.DatabaseDeploymentFailed,
                    "web",
                    $"{built.Route.Name} / {target.Public.SystemName}",
                    $"{exception.GetType().Name}: {exception.Message}"), CancellationToken.None);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }

        var verified = await VerifyTargetWithOperationalEventAsync(built, target, cancellationToken);
        if (!verified)
        {
            await operationalEvents.RecordAsync(new OperationalEventInput(
                OperationalEventSeverity.Warning,
                OperationalEventCategories.Database,
                OperationalEventCodes.DatabaseVerificationFailed,
                "web",
                $"{built.Route.Name} / {target.Public.SystemName}",
                "Required synchronization objects were not found after deployment."), CancellationToken.None);
        }
        AddAudit(built.Route, "DatabaseApplied", new { target.Public.SystemCode, target.Public.DatabaseName, Verified = verified });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new DatabaseDeploymentResult(
            verified,
            verified
                ? DisplayText.Create("DatabaseSetup_ResultApplyVerified", target.Public.SystemName)
                : DisplayText.Create("DatabaseSetup_ResultApplyUnverified", target.Public.SystemName),
            built.Route.DeploymentState,
            built.Route.Enabled);
    }

    public async Task<DatabaseDeploymentResult> VerifyAsync(Guid routeId, CancellationToken cancellationToken)
    {
        var built = await BuildPlanAsync(routeId, cancellationToken);
        foreach (var target in built.Targets)
        {
            if (!await VerifyTargetWithOperationalEventAsync(built, target, cancellationToken))
            {
                await operationalEvents.RecordAsync(new OperationalEventInput(
                    OperationalEventSeverity.Warning,
                    OperationalEventCategories.Database,
                    OperationalEventCodes.DatabaseVerificationFailed,
                    "web",
                    $"{built.Route.Name} / {target.Public.SystemName}",
                    "Required synchronization objects were not found."), CancellationToken.None);
                built.Route.DeploymentState = DatabaseDeploymentState.Draft;
                built.Route.Enabled = false;
                AddAudit(built.Route, "DatabaseVerificationFailed");
                await dbContext.SaveChangesAsync(cancellationToken);
                return new DatabaseDeploymentResult(
                    false,
                    DisplayText.Create("DatabaseSetup_ResultObjectsMissing", target.Public.SystemName),
                    built.Route.DeploymentState,
                    built.Route.Enabled);
            }
        }

        built.Route.DeploymentState = DatabaseDeploymentState.Prepared;
        AddAudit(built.Route, "DatabasePrepared");
        await dbContext.SaveChangesAsync(cancellationToken);
        return new DatabaseDeploymentResult(
            true,
            DisplayText.Create("DatabaseSetup_ResultAllVerified"),
            built.Route.DeploymentState,
            built.Route.Enabled);
    }

    public async Task<DatabaseDeploymentResult> SetEnabledAsync(
        Guid routeId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var route = await dbContext.Routes.SingleOrDefaultAsync(x => x.Id == routeId, cancellationToken) ??
                    throw new KeyNotFoundException("指定された同期ルールは存在しません。");
        if (enabled && route.DeploymentState != DatabaseDeploymentState.Prepared)
        {
            throw new ConfigurationValidationException(["DB構成の検証が完了するまでルールを有効化できません。"]);
        }
        if (enabled && await dbContext.Systems.AsNoTracking().AnyAsync(
                x => (x.Id == route.SourceSystemId || x.Id == route.DestinationSystemId) && !x.Enabled,
                cancellationToken))
        {
            throw new ConfigurationValidationException(["無効なシステムを含む同期ルールは有効化できません。"]);
        }

        route.Enabled = enabled;
        if (enabled)
        {
            route.MappingMaintenanceStartedAtUtc = null;
        }
        AddAudit(route, enabled ? "Enabled" : "Disabled");
        await dbContext.SaveChangesAsync(cancellationToken);
        return new DatabaseDeploymentResult(
            true,
            DisplayText.Create(enabled ? "DatabaseSetup_ResultRuleEnabled" : "DatabaseSetup_ResultRuleDisabled"),
            route.DeploymentState,
            route.Enabled);
    }

    private async Task<BuiltPlan> BuildPlanAsync(Guid routeId, CancellationToken cancellationToken)
    {
        var route = await dbContext.Routes
            .Include(x => x.SourceSystem)
            .Include(x => x.DestinationSystem)
            .Include(x => x.TableMapping).ThenInclude(x => x!.Columns)
            .Include(x => x.TableMapping).ThenInclude(x => x!.FixedValues)
            .Include(x => x.TableMapping).ThenInclude(x => x!.RelatedTables)
            .SingleOrDefaultAsync(x => x.Id == routeId, cancellationToken) ??
                    throw new KeyNotFoundException("指定された同期ルールは存在しません。");
        var mapping = route.TableMapping ??
                      throw new ConfigurationValidationException(["先にテーブル／列マッピングを保存してください。"]);
        var keys = mapping.Columns.Where(x => x.IsKey).OrderBy(x => x.SourceColumn, StringComparer.Ordinal).ToArray();
        var sourceFixedKeys = mapping.FixedValues
            .Where(x => x.IsKey && x.Direction == MappingWriteDirection.Reverse)
            .OrderBy(x => x.TargetColumn, StringComparer.Ordinal)
            .ToArray();
        var destinationFixedKeys = mapping.FixedValues
            .Where(x => x.IsKey && x.Direction == MappingWriteDirection.Forward)
            .OrderBy(x => x.TargetColumn, StringComparer.Ordinal)
            .ToArray();
        var columns = mapping.Columns.OrderBy(x => x.SourceColumn, StringComparer.Ordinal).ToArray();
        var sourceTableColumns = columns.Where(x => string.IsNullOrWhiteSpace(x.SourceTableAlias)).ToArray();
        if (keys.Length == 0)
        {
            throw new ConfigurationValidationException(["キー列が設定されていません。"]);
        }

        var systems = await dbContext.Systems.AsNoTracking()
            .Where(x => x.Id == route.SourceSystemId || x.Id == route.DestinationSystemId)
            .ToListAsync(cancellationToken);
        var source = GetConfiguredSystem(systems, route.SourceSystemId, route.SourceSystem.Code);
        var destination = GetConfiguredSystem(systems, route.DestinationSystemId, route.DestinationSystem.Code);
        if (mapping.RelatedTables.Any(x => x.DetectChanges) && !IsSqlServer(source.Provider))
        {
            throw new ConfigurationValidationException([
                "関連テーブルの変更検知とキー展開は現在SQL Serverでのみ利用できます。"
            ]);
        }
        var warnings = new List<DisplayText>
        {
            DisplayText.Create("DatabaseSetup_WarningDdl")
        };
        warnings.Add(mapping.SyncDeletes
            ? DisplayText.Create("DatabaseSetup_WarningDeleteEnabled")
            : DisplayText.Create("DatabaseSetup_WarningDeleteDisabled"));
        if (keys.Length + sourceFixedKeys.Length > 1 ||
            keys.Length + destinationFixedKeys.Length > 1)
        {
            warnings.Add(DisplayText.Create("DatabaseSetup_WarningCompositeKey"));
        }

        var targets = new List<TargetDefinition>
        {
            BuildTarget(
                route,
                source,
                mapping.SourceSchema,
                mapping.SourceTable,
                keys.Select(x => new DeploymentColumn(x.SourceColumn, CanonicalFieldName(x)))
                    .Concat(sourceFixedKeys.Select(x => new DeploymentColumn(
                        x.TargetColumn,
                        $"@fixed:{x.TargetColumn}",
                        x.Value)))
                    .ToArray(),
                sourceTableColumns.Select(x => new DeploymentColumn(x.SourceColumn, CanonicalFieldName(x))).ToArray(),
                ToDeletionBehavior(mapping.SyncDeletes, mapping.SourceDeletionMode, mapping.SourceLogicalDeleteColumn, mapping.SourceLogicalDeleteValue),
                MappingWriteDirection.Forward,
                DisplayText.Create("DatabaseSetup_SourceDirection", source.DisplayName, destination.DisplayName),
                createTrigger: true,
                relatedTables: mapping.RelatedTables)
        };
        targets.Add(BuildTarget(
            route,
            destination,
            mapping.DestinationSchema,
            mapping.DestinationTable,
            keys.Select(x => new DeploymentColumn(x.DestinationColumn, CanonicalFieldName(x)))
                .Concat(destinationFixedKeys.Select(x => new DeploymentColumn(
                    x.TargetColumn,
                    $"@fixed:{x.TargetColumn}",
                    x.Value)))
                .ToArray(),
            columns.Select(x => new DeploymentColumn(x.DestinationColumn, CanonicalFieldName(x))).ToArray(),
            ToDeletionBehavior(mapping.SyncDeletes, mapping.DestinationDeletionMode, mapping.DestinationLogicalDeleteColumn, mapping.DestinationLogicalDeleteValue),
            MappingWriteDirection.Reverse,
            route.Direction == SyncDirection.Bidirectional
                ? DisplayText.Create("DatabaseSetup_DestinationReverseDirection", destination.DisplayName, source.DisplayName)
                : DisplayText.Create("DatabaseSetup_DestinationDirection", destination.DisplayName),
            createTrigger: route.Direction == SyncDirection.Bidirectional,
            relatedTables: []));

        return new BuiltPlan(
            route,
            targets,
            new DatabaseDeploymentPlan(
                route.Id,
                route.Name,
                route.DeploymentState,
                route.Enabled,
                options.Value.AllowDirectApply,
                targets.Select(x => x.Public).ToArray(),
                warnings));
    }

    private TargetDefinition BuildTarget(
        SyncRouteEntity route,
        SystemDefinitionEntity system,
        string schema,
        string table,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        DeletionBehavior? deletionBehavior,
        MappingWriteDirection direction,
        DisplayText directionLabel,
        bool createTrigger,
        IReadOnlyList<RouteRelatedTableEntity> relatedTables)
    {
        var connectionString = protector.Unprotect(system.ProtectedConnectionString!);
        var connection = ManagedConnectionStringFactory.Parse(system.Id, system.Provider, connectionString);
        var triggerBase = $"TR_SC_{(direction == MappingWriteDirection.Forward ? "F" : "R")}_{route.Id:N}"[..20];
        var batches = system.Provider.ToUpperInvariant() switch
        {
            "SQLSERVER" => BuildSqlServerBatches(route.EntityType, schema, table, keys, payloadColumns, system.Code, deletionBehavior, triggerBase, createTrigger),
            "MYSQL" => BuildMySqlBatches(route.EntityType, schema, table, keys, payloadColumns, system.Code, deletionBehavior, triggerBase, createTrigger),
            "POSTGRESQL" => BuildPostgreSqlBatches(route.EntityType, schema, table, keys, payloadColumns, system.Code, deletionBehavior, triggerBase, createTrigger),
            _ => throw new InvalidOperationException($"未対応のProviderです: {system.Provider}")
        };
        var relatedTriggerNames = new List<string>();
        if (createTrigger && direction == MappingWriteDirection.Forward && IsSqlServer(system.Provider))
        {
            var relatedIndex = 0;
            foreach (var related in relatedTables.Where(x => x.DetectChanges).OrderBy(x => x.Alias, StringComparer.Ordinal))
            {
                var relatedTriggerName = $"TR_SC_X_{route.Id:N}_{relatedIndex++}";
                batches.Add(BuildSqlServerRelatedTrigger(
                    route.EntityType,
                    schema,
                    table,
                    keys,
                    related,
                    relatedTriggerName));
                relatedTriggerNames.Add(relatedTriggerName);
            }
            // Create/alter the current trigger set first. The final cleanup only removes obsolete
            // names, so a manually executed script cannot lose all working related triggers merely
            // because creation of a replacement failed before reaching this batch.
            batches.Add(BuildSqlServerRelatedTriggerCleanup(route.Id, relatedTriggerNames));
        }
        var deploymentKey = $"{route.Id:N}:{direction}";
        var definitionHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n-- SyncCoordinator batch --\n", batches))));
        AppendDeploymentMarker(system.Provider, batches, deploymentKey, definitionHash);
        var triggerNames = !createTrigger
            ? []
            : string.Equals(system.Provider, "MySql", StringComparison.OrdinalIgnoreCase)
                ? deletionBehavior?.Mode == DeletionMode.Physical
                    ? new[] { triggerBase + "_I", triggerBase + "_U", triggerBase + "_D" }
                    : new[] { triggerBase + "_I", triggerBase + "_U" }
                : new[] { triggerBase };
        triggerNames = triggerNames.Concat(relatedTriggerNames).ToArray();
        var changes = new List<DisplayText>
        {
            DisplayText.Create("DatabaseSetup_ChangeCreateQueue"),
            DisplayText.Create("DatabaseSetup_ChangeCreateAppliedMessage"),
            DisplayText.Create("DatabaseSetup_ChangeCreateEntityOrigin"),
            DisplayText.Create("DatabaseSetup_ChangeCreateDeleteTombstone"),
            DisplayText.Create("DatabaseSetup_ChangeRecordDeployment"),
            DisplayText.Create(system.Provider.ToUpperInvariant() switch
            {
                "SQLSERVER" => "DatabaseSetup_ChangeSqlServerPermissions",
                "MYSQL" => "DatabaseSetup_ChangeMySqlPermissions",
                "POSTGRESQL" => "DatabaseSetup_ChangePostgreSqlPermissions",
                _ => throw new InvalidOperationException($"未対応のProviderです: {system.Provider}")
            })
        };
        if (createTrigger)
        {
            changes.Add(DisplayText.Create("DatabaseSetup_ChangeTrigger", schema, table));
            if (deletionBehavior is not null)
            {
                changes.Add(deletionBehavior.Mode == DeletionMode.Physical
                    ? DisplayText.Create("DatabaseSetup_ChangePhysicalDelete")
                    : DisplayText.Create("DatabaseSetup_ChangeLogicalDelete", deletionBehavior.LogicalDeleteColumn!));
            }
        }
        foreach (var related in relatedTables.Where(x => x.DetectChanges))
        {
            changes.Add(DisplayText.Create("DatabaseSetup_ChangeTrigger", related.Schema, related.Table));
        }

        var script = system.Provider.ToUpperInvariant() switch
        {
            "SQLSERVER" => string.Join($"{Environment.NewLine}GO{Environment.NewLine}{Environment.NewLine}", batches) + Environment.NewLine + "GO",
            "MYSQL" => RenderMySqlScript(batches),
            "POSTGRESQL" => RenderPostgreSqlScript(batches),
            _ => throw new InvalidOperationException($"未対応のProviderです: {system.Provider}")
        };
        return new TargetDefinition(
            system,
            schema,
            table,
            triggerNames,
            deploymentKey,
            definitionHash,
            batches,
            new DatabaseDeploymentTarget(
                system.Code,
                system.DisplayName,
                system.Provider,
                connection.Database,
                directionLabel,
                script,
                changes));
    }

    private async Task<bool> VerifyTargetAsync(TargetDefinition target, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(target.System);
        await connection.OpenAsync(cancellationToken);
        var tableCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            target.System.Provider.ToUpperInvariant() switch
            {
                "SQLSERVER" => "SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo') AND name IN (N'SyncChangeQueue', N'SyncAppliedMessage', N'SyncEntityOrigin', N'SyncDeleteTombstone', N'SyncCoordinatorDeployment')",
                "MYSQL" => "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('SyncChangeQueue', 'SyncAppliedMessage', 'SyncEntityOrigin', 'SyncDeleteTombstone', 'SyncCoordinatorDeployment')",
                "POSTGRESQL" => "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('SyncChangeQueue', 'SyncAppliedMessage', 'SyncEntityOrigin', 'SyncDeleteTombstone', 'SyncCoordinatorDeployment')",
                _ => throw new InvalidOperationException($"未対応のProviderです: {target.System.Provider}")
            },
            cancellationToken: cancellationToken));
        if (tableCount != 5)
        {
            return false;
        }
        var appliedHash = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            target.System.Provider.ToUpperInvariant() switch
            {
                "SQLSERVER" => "SELECT DefinitionHash FROM dbo.SyncCoordinatorDeployment WHERE DeploymentKey = @deploymentKey",
                "MYSQL" => "SELECT DefinitionHash FROM SyncCoordinatorDeployment WHERE DeploymentKey = @deploymentKey",
                "POSTGRESQL" => "SELECT \"DefinitionHash\" FROM public.\"SyncCoordinatorDeployment\" WHERE \"DeploymentKey\" = @deploymentKey",
                _ => throw new InvalidOperationException($"未対応のProviderです: {target.System.Provider}")
            },
            new { deploymentKey = target.DeploymentKey },
            cancellationToken: cancellationToken));
        if (!string.Equals(appliedHash, target.DefinitionHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (target.TriggerNames.Count == 0)
        {
            return true;
        }

        var triggerCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            BuildTriggerVerificationSql(target.System.Provider),
            new { names = target.TriggerNames.ToArray(), schema = target.Schema },
            cancellationToken: cancellationToken));
        return triggerCount == target.TriggerNames.Count;
    }

    internal static string BuildTriggerVerificationSql(string provider) =>
        provider.ToUpperInvariant() switch
        {
            "SQLSERVER" => "SELECT COUNT(*) FROM sys.triggers WHERE name IN @names",
            "MYSQL" => "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() AND TRIGGER_NAME IN @names",
            "POSTGRESQL" => "SELECT COUNT(DISTINCT trigger_name) FROM information_schema.triggers WHERE event_object_schema = @schema AND trigger_name = ANY(@names)",
            _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
        };

    private async Task<bool> VerifyTargetWithOperationalEventAsync(
        BuiltPlan built,
        TargetDefinition target,
        CancellationToken cancellationToken)
    {
        try
        {
            return await VerifyTargetAsync(target, cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await operationalEvents.RecordAsync(new OperationalEventInput(
                OperationalEventSeverity.Error,
                OperationalEventCategories.Database,
                OperationalEventCodes.DatabaseVerificationFailed,
                "web",
                $"{built.Route.Name} / {target.Public.SystemName}",
                $"{exception.GetType().Name}: {exception.Message}"), CancellationToken.None);
            throw;
        }
    }

    private DbConnection CreateConnection(SystemDefinitionEntity system)
    {
        var value = protector.Unprotect(system.ProtectedConnectionString!);
        if (IsSqlServer(system.Provider))
        {
            return new SqlConnection(value);
        }
        if (string.Equals(system.Provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            var builder = new MySqlConnectionStringBuilder(value) { AllowUserVariables = true };
            return new MySqlConnection(builder.ConnectionString);
        }
        if (IsPostgreSql(system.Provider))
        {
            return new NpgsqlConnection(value);
        }
        throw new InvalidOperationException($"未対応のProviderです: {system.Provider}");
    }

    internal static List<string> BuildSqlServerBatches(
        string entityType,
        string schema,
        string table,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        DeletionBehavior? deletionBehavior,
        string triggerName,
        bool createTrigger)
    {
        var batches = new List<string>
        {
            """
            SET XACT_ABORT ON;
            IF OBJECT_ID(N'dbo.SyncChangeQueue', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SyncChangeQueue
                (
                    QueueId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SyncChangeQueue PRIMARY KEY,
                    MessageId uniqueidentifier NOT NULL,
                    EntityType nvarchar(128) NOT NULL,
                    EntityId nvarchar(256) NOT NULL,
                    Operation varchar(16) NOT NULL,
                    OccurredAtUtc datetimeoffset(7) NOT NULL CONSTRAINT DF_SyncChangeQueue_OccurredAtUtc DEFAULT SYSUTCDATETIME()
                );
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.SyncAppliedMessage', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SyncAppliedMessage
                (
                    MessageId uniqueidentifier NOT NULL CONSTRAINT PK_SyncAppliedMessage PRIMARY KEY,
                    AppliedAtUtc datetimeoffset(7) NOT NULL
                );
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.SyncEntityOrigin', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SyncEntityOrigin
                (
                    EntityType nvarchar(128) NOT NULL,
                    EntityId nvarchar(256) NOT NULL,
                    OriginSystem nvarchar(64) NOT NULL,
                    CONSTRAINT PK_SyncEntityOrigin PRIMARY KEY (EntityType, EntityId)
                );
            END;
            """,
            """
            IF OBJECT_ID(N'dbo.SyncDeleteTombstone', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.SyncDeleteTombstone
                (
                    MessageId uniqueidentifier NOT NULL,
                    EntityType nvarchar(128) NOT NULL,
                    EntityId nvarchar(256) NOT NULL,
                    OriginSystem nvarchar(64) NOT NULL,
                    PayloadJson nvarchar(max) NOT NULL,
                    DeletedAtUtc datetimeoffset(7) NOT NULL,
                    CONSTRAINT PK_SyncDeleteTombstone PRIMARY KEY (MessageId, EntityType, EntityId),
                    CONSTRAINT CK_SyncDeleteTombstone_PayloadJson CHECK (ISJSON(PayloadJson) = 1)
                );
            END;
            """
        };
        if (createTrigger)
        {
            var idExpression = SqlServerEntityId("i", keys);
            var triggerEvents = deletionBehavior?.Mode == DeletionMode.Physical
                ? "AFTER INSERT, UPDATE, DELETE"
                : "AFTER INSERT, UPDATE";
            var upsertPredicates = new List<string>();
            if (deletionBehavior?.Mode == DeletionMode.Logical)
            {
                upsertPredicates.Add($"NOT ({SqlServerIsDeleted("i", deletionBehavior)})");
            }
            var fixedFilter = SqlServerFixedKeyFilter("i", keys);
            if (!string.IsNullOrEmpty(fixedFilter))
            {
                upsertPredicates.Add(fixedFilter);
            }
            var upsertWhere = upsertPredicates.Count == 0
                ? string.Empty
                : $"WHERE {string.Join(" AND ", upsertPredicates)}";
            var deleteBlock = BuildSqlServerDeleteBlock(
                entityType,
                systemCode,
                keys,
                payloadColumns,
                deletionBehavior);
            batches.Add($$"""
                CREATE OR ALTER TRIGGER {{SqlServerIdentifier(schema)}}.{{SqlServerIdentifier(triggerName)}}
                ON {{SqlServerIdentifier(schema)}}.{{SqlServerIdentifier(table)}}
                {{triggerEvents}}
                AS
                BEGIN
                    SET NOCOUNT ON;
                    DECLARE @ContextMessageId uniqueidentifier = TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'SyncMessageId'));
                    INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
                    SELECT COALESCE(@ContextMessageId, NEWID()), N'{{SqlLiteral(entityType)}}', {{idExpression}}, 'Upsert', SYSUTCDATETIME()
                    FROM inserted AS i
                    {{upsertWhere}};
                    {{deleteBlock}}
                END;
                """);
        }
        return batches;
    }

    internal static List<string> BuildMySqlBatches(
        string entityType,
        string schema,
        string table,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        DeletionBehavior? deletionBehavior,
        string triggerName,
        bool createTrigger)
    {
        var batches = new List<string>
        {
            "CREATE TABLE IF NOT EXISTS SyncChangeQueue (QueueId BIGINT NOT NULL AUTO_INCREMENT, MessageId CHAR(36) NOT NULL, EntityType VARCHAR(128) NOT NULL, EntityId VARCHAR(256) NOT NULL, Operation VARCHAR(16) NOT NULL, OccurredAtUtc DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)), PRIMARY KEY (QueueId)) ENGINE=InnoDB",
            "CREATE TABLE IF NOT EXISTS SyncAppliedMessage (MessageId CHAR(36) NOT NULL, AppliedAtUtc DATETIME(6) NOT NULL, PRIMARY KEY (MessageId)) ENGINE=InnoDB",
            "CREATE TABLE IF NOT EXISTS SyncEntityOrigin (EntityType VARCHAR(128) NOT NULL, EntityId VARCHAR(256) NOT NULL, OriginSystem VARCHAR(64) NOT NULL, PRIMARY KEY (EntityType, EntityId)) ENGINE=InnoDB",
            "CREATE TABLE IF NOT EXISTS SyncDeleteTombstone (MessageId CHAR(36) NOT NULL, EntityType VARCHAR(128) NOT NULL, EntityId VARCHAR(256) NOT NULL, OriginSystem VARCHAR(64) NOT NULL, PayloadJson JSON NOT NULL, DeletedAtUtc DATETIME(6) NOT NULL, PRIMARY KEY (MessageId, EntityType, EntityId)) ENGINE=InnoDB"
        };
        if (createTrigger)
        {
            foreach (var (suffix, operation) in new[] { ("I", "INSERT"), ("U", "UPDATE") })
            {
                var name = triggerName + "_" + suffix;
                batches.Add($"DROP TRIGGER IF EXISTS {MySqlIdentifier(schema)}.{MySqlIdentifier(name)}");
                batches.Add(BuildMySqlWriteTrigger(
                    entityType,
                    schema,
                    table,
                    name,
                    operation,
                    keys,
                    payloadColumns,
                    systemCode,
                    deletionBehavior));
            }
            var deleteTriggerName = triggerName + "_D";
            batches.Add($"DROP TRIGGER IF EXISTS {MySqlIdentifier(schema)}.{MySqlIdentifier(deleteTriggerName)}");
            if (deletionBehavior?.Mode == DeletionMode.Physical)
            {
                batches.Add(BuildMySqlDeleteTrigger(
                    entityType,
                    schema,
                    table,
                    deleteTriggerName,
                    keys,
                    payloadColumns,
                    systemCode));
            }
        }
        return batches;
    }

    internal static string BuildSqlServerRelatedTriggerCleanup(
        Guid routeId,
        IReadOnlyList<string> retainedTriggerNames)
    {
        var triggerPrefix = SqlLiteral($"TR_SC_X_{routeId:N}_");
        var retainedPredicate = retainedTriggerNames.Count == 0
            ? string.Empty
            : $" AND name NOT IN ({string.Join(", ", retainedTriggerNames.Select(x => $"N'{SqlLiteral(x)}'"))})";
        return $$"""
            DECLARE @syncCoordinatorDropSql nvarchar(max) = N'';
            SELECT @syncCoordinatorDropSql = @syncCoordinatorDropSql
                + N'DROP TRIGGER ' + QUOTENAME(OBJECT_SCHEMA_NAME(object_id)) + N'.' + QUOTENAME(name) + N';'
            FROM sys.triggers
            WHERE LEFT(name, LEN(N'{{triggerPrefix}}')) = N'{{triggerPrefix}}'{{retainedPredicate}};

            IF LEN(@syncCoordinatorDropSql) > 0
                EXEC sys.sp_executesql @syncCoordinatorDropSql;
            """;
    }

    internal static string BuildSqlServerRelatedTrigger(
        string entityType,
        string baseSchema,
        string baseTable,
        IReadOnlyList<DeploymentColumn> keys,
        RouteRelatedTableEntity related,
        string triggerName)
    {
        var entityId = SqlServerEntityId("b", keys);
        var insertedJoin = ExpandSqlServerRelatedExpression(related.JoinExpression, "b", "i");
        var deletedJoin = ExpandSqlServerRelatedExpression(related.JoinExpression, "b", "d");
        return $$"""
            CREATE OR ALTER TRIGGER {{SqlServerIdentifier(related.Schema)}}.{{SqlServerIdentifier(triggerName)}}
            ON {{SqlServerIdentifier(related.Schema)}}.{{SqlServerIdentifier(related.Table)}}
            AFTER INSERT, UPDATE, DELETE
            AS
            BEGIN
                SET NOCOUNT ON;
                ;WITH affected AS
                (
                    SELECT {{entityId}} AS EntityId
                    FROM {{SqlServerIdentifier(baseSchema)}}.{{SqlServerIdentifier(baseTable)}} AS b
                    INNER JOIN inserted AS i ON ({{insertedJoin}})
                    UNION
                    SELECT {{entityId}} AS EntityId
                    FROM {{SqlServerIdentifier(baseSchema)}}.{{SqlServerIdentifier(baseTable)}} AS b
                    INNER JOIN deleted AS d ON ({{deletedJoin}})
                )
                INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
                SELECT NEWID(), N'{{SqlLiteral(entityType)}}', EntityId, 'Upsert', SYSUTCDATETIME()
                FROM affected;
            END;
            """;
    }

    private static string ExpandSqlServerRelatedExpression(
        string expression,
        string sourceAlias,
        string relatedAlias) => expression
        .Replace("{source}", SqlServerIdentifier(sourceAlias), StringComparison.OrdinalIgnoreCase)
        .Replace("{related}", SqlServerIdentifier(relatedAlias), StringComparison.OrdinalIgnoreCase);

    internal static List<string> BuildPostgreSqlBatches(
        string entityType,
        string schema,
        string table,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        DeletionBehavior? deletionBehavior,
        string triggerName,
        bool createTrigger)
    {
        var batches = new List<string>
        {
            """
            CREATE TABLE IF NOT EXISTS public."SyncChangeQueue"
            (
                "QueueId" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "MessageId" uuid NOT NULL,
                "EntityType" varchar(128) NOT NULL,
                "EntityId" varchar(256) NOT NULL,
                "Operation" varchar(16) NOT NULL,
                "OccurredAtUtc" timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS public."SyncAppliedMessage"
            (
                "MessageId" uuid PRIMARY KEY,
                "AppliedAtUtc" timestamptz NOT NULL
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS public."SyncEntityOrigin"
            (
                "EntityType" varchar(128) NOT NULL,
                "EntityId" varchar(256) NOT NULL,
                "OriginSystem" varchar(64) NOT NULL,
                PRIMARY KEY ("EntityType", "EntityId")
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS public."SyncDeleteTombstone"
            (
                "MessageId" uuid NOT NULL,
                "EntityType" varchar(128) NOT NULL,
                "EntityId" varchar(256) NOT NULL,
                "OriginSystem" varchar(64) NOT NULL,
                "PayloadJson" jsonb NOT NULL,
                "DeletedAtUtc" timestamptz NOT NULL,
                PRIMARY KEY ("MessageId", "EntityType", "EntityId")
            )
            """
        };
        if (!createTrigger)
        {
            return batches;
        }

        var functionName = triggerName + "_fn";
        batches.Add(BuildPostgreSqlTriggerFunction(
            entityType,
            schema,
            functionName,
            keys,
            payloadColumns,
            systemCode,
            deletionBehavior));
        batches.Add($"DROP TRIGGER IF EXISTS {PostgreSqlIdentifier(triggerName)} ON {PostgreSqlIdentifier(schema)}.{PostgreSqlIdentifier(table)}");
        var events = deletionBehavior?.Mode == DeletionMode.Physical
            ? "INSERT OR UPDATE OR DELETE"
            : "INSERT OR UPDATE";
        batches.Add($$"""
            CREATE TRIGGER {{PostgreSqlIdentifier(triggerName)}}
            AFTER {{events}} ON {{PostgreSqlIdentifier(schema)}}.{{PostgreSqlIdentifier(table)}}
            FOR EACH ROW EXECUTE FUNCTION {{PostgreSqlIdentifier(schema)}}.{{PostgreSqlIdentifier(functionName)}}()
            """);
        return batches;
    }

    private static string BuildPostgreSqlTriggerFunction(
        string entityType,
        string schema,
        string functionName,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        DeletionBehavior? deletionBehavior)
    {
        var upsertNew = PostgreSqlUpsertEvent(entityType, "NEW", keys);
        var body = deletionBehavior?.Mode switch
        {
            DeletionMode.Physical => $$"""
                IF TG_OP = 'DELETE' THEN
                    {{PostgreSqlDeleteEvent(entityType, "OLD", keys, payloadColumns, systemCode, deleteOrigin: true)}}
                    RETURN OLD;
                END IF;
                {{upsertNew}}
                RETURN NEW;
                """,
            DeletionMode.Logical => $$"""
                IF TG_OP = 'INSERT' THEN
                    IF {{PostgreSqlIsDeleted("NEW", deletionBehavior)}} THEN
                        {{PostgreSqlDeleteEvent(entityType, "NEW", keys, payloadColumns, systemCode, deleteOrigin: false)}}
                    ELSE
                        {{upsertNew}}
                    END IF;
                ELSE
                    IF {{PostgreSqlIsDeleted("NEW", deletionBehavior)}} AND NOT ({{PostgreSqlIsDeleted("OLD", deletionBehavior)}}) THEN
                        {{PostgreSqlDeleteEvent(entityType, "OLD", keys, payloadColumns, systemCode, deleteOrigin: false)}}
                    ELSIF NOT ({{PostgreSqlIsDeleted("NEW", deletionBehavior)}}) THEN
                        {{upsertNew}}
                    END IF;
                END IF;
                RETURN NEW;
                """,
            _ => $$"""
                {{upsertNew}}
                RETURN NEW;
                """
        };

        return $$"""
            CREATE OR REPLACE FUNCTION {{PostgreSqlIdentifier(schema)}}.{{PostgreSqlIdentifier(functionName)}}()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $sync_coordinator$
            DECLARE
                v_message_id uuid;
                v_entity_id varchar(256);
                v_payload jsonb;
            BEGIN
                v_message_id := COALESCE(
                    NULLIF(current_setting('synccoordinator.message_id', true), '')::uuid,
                    gen_random_uuid());
                {{body}}
            END;
            $sync_coordinator$
            """;
    }

    private static string PostgreSqlUpsertEvent(
        string entityType,
        string rowAlias,
        IReadOnlyList<DeploymentColumn> keys) =>
        PostgreSqlConditional(PostgreSqlFixedKeyFilter(rowAlias, keys), $$"""
        INSERT INTO public."SyncChangeQueue"
            ("MessageId", "EntityType", "EntityId", "Operation", "OccurredAtUtc")
        VALUES
            (v_message_id, '{{SqlLiteral(entityType)}}', {{PostgreSqlEntityId(rowAlias, keys)}}, 'Upsert', CURRENT_TIMESTAMP);
        """);

    private static string PostgreSqlDeleteEvent(
        string entityType,
        string rowAlias,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        bool deleteOrigin)
    {
        var removeOrigin = deleteOrigin
            ? $$"""
                DELETE FROM public."SyncEntityOrigin"
                WHERE "EntityType" = '{{SqlLiteral(entityType)}}' AND "EntityId" = v_entity_id;
                """
            : string.Empty;
        var statements = $$"""
            v_entity_id := {{PostgreSqlEntityId(rowAlias, keys)}};
            v_payload := {{PostgreSqlPayloadJson(rowAlias, payloadColumns)}};
            INSERT INTO public."SyncDeleteTombstone"
                ("MessageId", "EntityType", "EntityId", "OriginSystem", "PayloadJson", "DeletedAtUtc")
            VALUES
                (v_message_id, '{{SqlLiteral(entityType)}}', v_entity_id,
                 COALESCE((SELECT "OriginSystem" FROM public."SyncEntityOrigin"
                           WHERE "EntityType" = '{{SqlLiteral(entityType)}}' AND "EntityId" = v_entity_id),
                          '{{SqlLiteral(systemCode)}}'),
                 v_payload, CURRENT_TIMESTAMP)
            ON CONFLICT ("MessageId", "EntityType", "EntityId") DO UPDATE SET
                "OriginSystem" = EXCLUDED."OriginSystem",
                "PayloadJson" = EXCLUDED."PayloadJson",
                "DeletedAtUtc" = EXCLUDED."DeletedAtUtc";
            INSERT INTO public."SyncChangeQueue"
                ("MessageId", "EntityType", "EntityId", "Operation", "OccurredAtUtc")
            VALUES
                (v_message_id, '{{SqlLiteral(entityType)}}', v_entity_id, 'Delete', CURRENT_TIMESTAMP);
            {{removeOrigin}}
            """;
        return PostgreSqlConditional(PostgreSqlFixedKeyFilter(rowAlias, keys), statements);
    }

    private static string BuildSqlServerDeleteBlock(
        string entityType,
        string systemCode,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        DeletionBehavior? deletionBehavior)
    {
        if (deletionBehavior is null)
        {
            return string.Empty;
        }

        var keyMatch = SqlServerKeyMatch("i", "d", keys);
        var deletedFixedFilter = SqlServerFixedKeyFilter("d", keys);
        var insertedFixedFilter = SqlServerFixedKeyFilter("i", keys);
        var deletedWhere = string.IsNullOrEmpty(deletedFixedFilter)
            ? string.Empty
            : deletedFixedFilter + " AND ";
        var insertedWhere = string.IsNullOrEmpty(insertedFixedFilter)
            ? string.Empty
            : insertedFixedFilter + " AND ";
        var eventSource = deletionBehavior.Mode == DeletionMode.Physical
            ? $$"""
                INSERT @DeleteEvents(MessageId, EntityId, PayloadJson)
                SELECT COALESCE(@ContextMessageId, NEWID()), {{SqlServerEntityId("d", keys)}}, {{SqlServerPayloadJson("d", payloadColumns)}}
                FROM deleted AS d
                WHERE {{deletedWhere}}NOT EXISTS (SELECT 1 FROM inserted AS i WHERE {{keyMatch}});
                """
            : $$"""
                INSERT @DeleteEvents(MessageId, EntityId, PayloadJson)
                SELECT COALESCE(@ContextMessageId, NEWID()), {{SqlServerEntityId("d", keys)}}, {{SqlServerPayloadJson("d", payloadColumns)}}
                FROM deleted AS d
                INNER JOIN inserted AS i ON {{keyMatch}}
                WHERE {{deletedWhere}}{{SqlServerIsDeleted("i", deletionBehavior)}}
                  AND NOT ({{SqlServerIsDeleted("d", deletionBehavior)}});

                INSERT @DeleteEvents(MessageId, EntityId, PayloadJson)
                SELECT COALESCE(@ContextMessageId, NEWID()), {{SqlServerEntityId("i", keys)}}, {{SqlServerPayloadJson("i", payloadColumns)}}
                FROM inserted AS i
                WHERE {{insertedWhere}}{{SqlServerIsDeleted("i", deletionBehavior)}}
                  AND NOT EXISTS (SELECT 1 FROM deleted AS d WHERE {{keyMatch}});
                """;
        var deleteOrigin = deletionBehavior.Mode == DeletionMode.Physical
            ? $$"""
                DELETE origin
                FROM dbo.SyncEntityOrigin AS origin
                INNER JOIN @DeleteEvents AS event
                    ON origin.EntityType = N'{{SqlLiteral(entityType)}}' AND origin.EntityId = event.EntityId;
                """
            : string.Empty;

        return $$"""

                    DECLARE @DeleteEvents TABLE
                    (
                        MessageId uniqueidentifier NOT NULL,
                        EntityId nvarchar(256) NOT NULL,
                        PayloadJson nvarchar(max) NOT NULL
                    );
                    {{eventSource}}

                    DELETE tombstone
                    FROM dbo.SyncDeleteTombstone AS tombstone
                    INNER JOIN @DeleteEvents AS event
                        ON tombstone.MessageId = event.MessageId
                       AND tombstone.EntityType = N'{{SqlLiteral(entityType)}}'
                       AND tombstone.EntityId = event.EntityId;

                    INSERT dbo.SyncDeleteTombstone
                        (MessageId, EntityType, EntityId, OriginSystem, PayloadJson, DeletedAtUtc)
                    SELECT event.MessageId, N'{{SqlLiteral(entityType)}}', event.EntityId,
                           COALESCE(origin.OriginSystem, N'{{SqlLiteral(systemCode)}}'),
                           event.PayloadJson, SYSUTCDATETIME()
                    FROM @DeleteEvents AS event
                    LEFT JOIN dbo.SyncEntityOrigin AS origin
                        ON origin.EntityType = N'{{SqlLiteral(entityType)}}' AND origin.EntityId = event.EntityId;

                    INSERT dbo.SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
                    SELECT MessageId, N'{{SqlLiteral(entityType)}}', EntityId, 'Delete', SYSUTCDATETIME()
                    FROM @DeleteEvents;
                    {{deleteOrigin}}
                """;
    }

    private static string BuildMySqlWriteTrigger(
        string entityType,
        string schema,
        string table,
        string triggerName,
        string operation,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        DeletionBehavior? deletionBehavior)
    {
        var upsert = MySqlConditional(MySqlFixedKeyFilter("NEW", keys), $$"""
            INSERT SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
            VALUES (COALESCE(@sync_message_id, UUID()), '{{SqlLiteral(entityType)}}', {{MySqlEntityId("NEW", keys)}}, 'Upsert', UTC_TIMESTAMP(6));
            """);
        if (deletionBehavior?.Mode != DeletionMode.Logical)
        {
            return $$"""
                CREATE TRIGGER {{MySqlIdentifier(schema)}}.{{MySqlIdentifier(triggerName)}}
                AFTER {{operation}} ON {{MySqlIdentifier(schema)}}.{{MySqlIdentifier(table)}}
                FOR EACH ROW
                BEGIN
                    {{upsert}}
                END
                """;
        }

        var deleteFrom = string.Equals(operation, "INSERT", StringComparison.Ordinal) ? "NEW" : "OLD";
        var deleteStatements = BuildMySqlTombstoneStatements(
            entityType,
            deleteFrom,
            keys,
            payloadColumns,
            systemCode,
            deleteOrigin: false);
        var body = string.Equals(operation, "INSERT", StringComparison.Ordinal)
            ? $$"""
                IF {{MySqlIsDeleted("NEW", deletionBehavior)}} THEN
                    {{deleteStatements}}
                ELSE
                    {{upsert}}
                END IF;
                """
            : $$"""
                IF {{MySqlIsDeleted("NEW", deletionBehavior)}} AND NOT ({{MySqlIsDeleted("OLD", deletionBehavior)}}) THEN
                    {{deleteStatements}}
                ELSEIF NOT ({{MySqlIsDeleted("NEW", deletionBehavior)}}) THEN
                    {{upsert}}
                END IF;
                """;
        return $$"""
            CREATE TRIGGER {{MySqlIdentifier(schema)}}.{{MySqlIdentifier(triggerName)}}
            AFTER {{operation}} ON {{MySqlIdentifier(schema)}}.{{MySqlIdentifier(table)}}
            FOR EACH ROW
            BEGIN
                DECLARE v_message_id CHAR(36);
                DECLARE v_entity_id VARCHAR(256);
                {{body}}
            END
            """;
    }

    private static string BuildMySqlDeleteTrigger(
        string entityType,
        string schema,
        string table,
        string triggerName,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode) =>
        $$"""
        CREATE TRIGGER {{MySqlIdentifier(schema)}}.{{MySqlIdentifier(triggerName)}}
        AFTER DELETE ON {{MySqlIdentifier(schema)}}.{{MySqlIdentifier(table)}}
        FOR EACH ROW
        BEGIN
            DECLARE v_message_id CHAR(36);
            DECLARE v_entity_id VARCHAR(256);
            {{BuildMySqlTombstoneStatements(entityType, "OLD", keys, payloadColumns, systemCode, deleteOrigin: true)}}
        END
        """;

    private static string BuildMySqlTombstoneStatements(
        string entityType,
        string rowAlias,
        IReadOnlyList<DeploymentColumn> keys,
        IReadOnlyList<DeploymentColumn> payloadColumns,
        string systemCode,
        bool deleteOrigin)
    {
        var removeOrigin = deleteOrigin
            ? $$"""
                DELETE FROM SyncEntityOrigin
                WHERE EntityType = '{{SqlLiteral(entityType)}}' AND EntityId = v_entity_id;
                """
            : string.Empty;
        var statements = $$"""
            SET v_message_id = COALESCE(@sync_message_id, UUID());
            SET v_entity_id = {{MySqlEntityId(rowAlias, keys)}};
            INSERT INTO SyncDeleteTombstone
                (MessageId, EntityType, EntityId, OriginSystem, PayloadJson, DeletedAtUtc)
            VALUES
                (v_message_id, '{{SqlLiteral(entityType)}}', v_entity_id,
                 COALESCE((SELECT OriginSystem FROM SyncEntityOrigin
                           WHERE EntityType = '{{SqlLiteral(entityType)}}' AND EntityId = v_entity_id LIMIT 1),
                          '{{SqlLiteral(systemCode)}}'),
                 {{MySqlPayloadJson(rowAlias, payloadColumns)}}, UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE
                OriginSystem = VALUES(OriginSystem), PayloadJson = VALUES(PayloadJson), DeletedAtUtc = VALUES(DeletedAtUtc);
            INSERT SyncChangeQueue(MessageId, EntityType, EntityId, Operation, OccurredAtUtc)
            VALUES (v_message_id, '{{SqlLiteral(entityType)}}', v_entity_id, 'Delete', UTC_TIMESTAMP(6));
            {{removeOrigin}}
            """;
        return MySqlConditional(MySqlFixedKeyFilter(rowAlias, keys), statements);
    }

    private static string SqlServerEntityId(string alias, IReadOnlyList<DeploymentColumn> keys) =>
        keys.Count == 1
            ? $"CONVERT(nvarchar(256), {alias}.{SqlServerIdentifier(keys[0].PhysicalColumn)})"
            : $"CONVERT(nvarchar(256), (SELECT {string.Join(", ", keys.Select(x => $"{alias}.{SqlServerIdentifier(x.PhysicalColumn)} AS {SqlServerIdentifier(x.PayloadField)}"))} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES))";

    private static string SqlServerPayloadJson(string alias, IReadOnlyList<DeploymentColumn> columns) =>
        $"(SELECT {string.Join(", ", columns.Select(x => $"{alias}.{SqlServerIdentifier(x.PhysicalColumn)} AS {SqlServerIdentifier(x.PayloadField)}"))} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)";

    private static string SqlServerKeyMatch(
        string leftAlias,
        string rightAlias,
        IReadOnlyList<DeploymentColumn> keys) =>
        string.Join(" AND ", keys.Select(x =>
            $"({leftAlias}.{SqlServerIdentifier(x.PhysicalColumn)} = {rightAlias}.{SqlServerIdentifier(x.PhysicalColumn)} OR ({leftAlias}.{SqlServerIdentifier(x.PhysicalColumn)} IS NULL AND {rightAlias}.{SqlServerIdentifier(x.PhysicalColumn)} IS NULL))"));

    private static string SqlServerFixedKeyFilter(
        string alias,
        IReadOnlyList<DeploymentColumn> keys) =>
        string.Join(" AND ", keys
            .Where(x => x.FixedValue is not null)
            .Select(x =>
                $"CONVERT(nvarchar(4000), {alias}.{SqlServerIdentifier(x.PhysicalColumn)}) = N'{SqlLiteral(x.FixedValue!)}'"));

    private static string SqlServerIsDeleted(string alias, DeletionBehavior behavior) =>
        $"CASE WHEN CONVERT(nvarchar(4000), {alias}.{SqlServerIdentifier(behavior.LogicalDeleteColumn!)}) = N'{SqlLiteral(behavior.LogicalDeleteValue!)}' THEN 1 ELSE 0 END = 1";

    private static string MySqlEntityId(string alias, IReadOnlyList<DeploymentColumn> keys) =>
        keys.Count == 1
            ? $"CAST({alias}.{MySqlIdentifier(keys[0].PhysicalColumn)} AS CHAR(256))"
            : $"CAST(JSON_OBJECT({string.Join(", ", keys.Select(x => $"'{SqlLiteral(x.PayloadField)}', {alias}.{MySqlIdentifier(x.PhysicalColumn)}"))}) AS CHAR(256))";

    private static string MySqlPayloadJson(string alias, IReadOnlyList<DeploymentColumn> columns) =>
        $"JSON_OBJECT({string.Join(", ", columns.Select(x => $"'{SqlLiteral(x.PayloadField)}', {alias}.{MySqlIdentifier(x.PhysicalColumn)}"))})";

    private static string MySqlFixedKeyFilter(
        string alias,
        IReadOnlyList<DeploymentColumn> keys) =>
        string.Join(" AND ", keys
            .Where(x => x.FixedValue is not null)
            .Select(x =>
                $"CAST({alias}.{MySqlIdentifier(x.PhysicalColumn)} AS CHAR) = '{SqlLiteral(x.FixedValue!)}'"));

    private static string MySqlConditional(string condition, string statements) =>
        string.IsNullOrEmpty(condition)
            ? statements
            : $"IF {condition} THEN{Environment.NewLine}{statements}{Environment.NewLine}END IF;";

    private static string MySqlIsDeleted(string alias, DeletionBehavior behavior) =>
        $"COALESCE(CAST({alias}.{MySqlIdentifier(behavior.LogicalDeleteColumn!)} AS CHAR) = '{SqlLiteral(behavior.LogicalDeleteValue!)}', FALSE)";

    private static string PostgreSqlEntityId(string alias, IReadOnlyList<DeploymentColumn> keys) =>
        keys.Count == 1
            ? $"CAST({alias}.{PostgreSqlIdentifier(keys[0].PhysicalColumn)} AS varchar(256))"
            : $"CAST(jsonb_build_object({string.Join(", ", keys.Select(x => $"'{SqlLiteral(x.PayloadField)}', {alias}.{PostgreSqlIdentifier(x.PhysicalColumn)}"))}) AS varchar(256))";

    private static string PostgreSqlPayloadJson(string alias, IReadOnlyList<DeploymentColumn> columns) =>
        $"jsonb_build_object({string.Join(", ", columns.Select(x => $"'{SqlLiteral(x.PayloadField)}', {alias}.{PostgreSqlIdentifier(x.PhysicalColumn)}"))})";

    private static string PostgreSqlFixedKeyFilter(
        string alias,
        IReadOnlyList<DeploymentColumn> keys) =>
        string.Join(" AND ", keys
            .Where(x => x.FixedValue is not null)
            .Select(x =>
                $"CAST({alias}.{PostgreSqlIdentifier(x.PhysicalColumn)} AS text) = '{SqlLiteral(x.FixedValue!)}'"));

    private static string PostgreSqlConditional(string condition, string statements) =>
        string.IsNullOrEmpty(condition)
            ? statements
            : $"IF {condition} THEN{Environment.NewLine}{statements}{Environment.NewLine}END IF;";

    private static string PostgreSqlIsDeleted(string alias, DeletionBehavior behavior) =>
        $"COALESCE(CAST({alias}.{PostgreSqlIdentifier(behavior.LogicalDeleteColumn!)} AS text) = '{SqlLiteral(behavior.LogicalDeleteValue!)}', FALSE)";

    internal static string RenderMySqlScript(IReadOnlyList<string> batches)
    {
        var builder = new StringBuilder("SET NAMES utf8mb4;")
            .AppendLine()
            .AppendLine();
        foreach (var batch in batches)
        {
            if (batch.TrimStart().StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine("DELIMITER $$").Append(batch).AppendLine("$$").AppendLine("DELIMITER ;");
            }
            else
            {
                builder.Append(batch).AppendLine(";").AppendLine();
            }
        }
        return builder.ToString();
    }

    internal static string RenderPostgreSqlScript(IReadOnlyList<string> batches) =>
        $"SET client_encoding = 'UTF8';{Environment.NewLine}{Environment.NewLine}" +
        string.Join($";{Environment.NewLine}{Environment.NewLine}", batches) + ";" + Environment.NewLine;

    internal static void AppendDeploymentMarker(
        string provider,
        ICollection<string> batches,
        string deploymentKey,
        string definitionHash)
    {
        if (IsSqlServer(provider))
        {
            batches.Add($$"""
                IF OBJECT_ID(N'dbo.SyncCoordinatorDeployment', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SyncCoordinatorDeployment
                    (
                        DeploymentKey nvarchar(128) NOT NULL CONSTRAINT PK_SyncCoordinatorDeployment PRIMARY KEY,
                        DefinitionHash char(64) NOT NULL,
                        AppliedAtUtc datetimeoffset(7) NOT NULL
                    );
                END;

                UPDATE dbo.SyncCoordinatorDeployment
                SET DefinitionHash = '{{SqlLiteral(definitionHash)}}', AppliedAtUtc = SYSUTCDATETIME()
                WHERE DeploymentKey = N'{{SqlLiteral(deploymentKey)}}';
                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT dbo.SyncCoordinatorDeployment(DeploymentKey, DefinitionHash, AppliedAtUtc)
                    VALUES (N'{{SqlLiteral(deploymentKey)}}', '{{SqlLiteral(definitionHash)}}', SYSUTCDATETIME());
                END;
                """);
            return;
        }

        if (IsPostgreSql(provider))
        {
            batches.Add("""
                CREATE TABLE IF NOT EXISTS public."SyncCoordinatorDeployment"
                (
                    "DeploymentKey" varchar(128) PRIMARY KEY,
                    "DefinitionHash" char(64) NOT NULL,
                    "AppliedAtUtc" timestamptz NOT NULL
                )
                """);
            batches.Add($$"""
                INSERT INTO public."SyncCoordinatorDeployment"
                    ("DeploymentKey", "DefinitionHash", "AppliedAtUtc")
                VALUES
                    ('{{SqlLiteral(deploymentKey)}}', '{{SqlLiteral(definitionHash)}}', CURRENT_TIMESTAMP)
                ON CONFLICT ("DeploymentKey") DO UPDATE SET
                    "DefinitionHash" = EXCLUDED."DefinitionHash",
                    "AppliedAtUtc" = EXCLUDED."AppliedAtUtc"
                """);
            return;
        }

        if (!string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"未対応のProviderです: {provider}");
        }

        batches.Add("CREATE TABLE IF NOT EXISTS SyncCoordinatorDeployment (DeploymentKey VARCHAR(128) NOT NULL, DefinitionHash CHAR(64) NOT NULL, AppliedAtUtc DATETIME(6) NOT NULL, PRIMARY KEY (DeploymentKey)) ENGINE=InnoDB");
        batches.Add($$"""
            INSERT INTO SyncCoordinatorDeployment(DeploymentKey, DefinitionHash, AppliedAtUtc)
            VALUES ('{{SqlLiteral(deploymentKey)}}', '{{SqlLiteral(definitionHash)}}', UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE DefinitionHash = VALUES(DefinitionHash), AppliedAtUtc = VALUES(AppliedAtUtc)
            """);
    }

    private static SystemDefinitionEntity GetConfiguredSystem(
        IReadOnlyCollection<SystemDefinitionEntity> systems,
        Guid id,
        string code)
    {
        var system = systems.SingleOrDefault(x => x.Id == id) ??
                     throw new ConfigurationValidationException([$"システム '{code}' が存在しません。"]);
        if (string.IsNullOrWhiteSpace(system.ProtectedConnectionString))
        {
            throw new ConfigurationValidationException([$"システム '{code}' のDB接続情報が未設定です。"]);
        }
        if (!system.Enabled)
        {
            throw new ConfigurationValidationException([$"システム '{code}' は無効です。"]);
        }
        if (system.PausedAtUtc is not null)
        {
            throw new ConfigurationValidationException([$"システム '{code}' は一時停止中です。再開後に業務DBへ反映してください。"]);
        }
        return system;
    }

    private void AddAudit(SyncRouteEntity route, string action, object? details = null) =>
        dbContext.ConfigurationAudits.Add(new ConfigurationAuditEntity
        {
            Id = Guid.NewGuid(),
            ConfigurationType = "DatabaseDeployment",
            ConfigurationId = route.Id.ToString("N"),
            ConfigurationName = route.Name,
            Action = action,
            ChangedBy = "ManagementUI",
            ChangedAtUtc = timeProvider.GetUtcNow(),
            AfterJson = JsonSerializer.Serialize(new { route.DeploymentState, route.Enabled, Details = details })
        });

    private static bool IsSqlServer(string provider) =>
        string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase);

    private static bool IsPostgreSql(string provider) =>
        string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase);

    private static DeletionBehavior? ToDeletionBehavior(
        bool enabled,
        DeletionMode mode,
        string? logicalDeleteColumn,
        string? logicalDeleteValue) =>
        enabled ? new DeletionBehavior(mode, logicalDeleteColumn, logicalDeleteValue) : null;

    private static string SqlServerIdentifier(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    private static string MySqlIdentifier(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string PostgreSqlIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string CanonicalFieldName(RouteColumnMappingEntity column) =>
        string.IsNullOrWhiteSpace(column.SourceTableAlias)
            ? column.SourceColumn
            : $"{column.SourceTableAlias}.{column.SourceColumn}";

    private sealed record BuiltPlan(
        SyncRouteEntity Route,
        IReadOnlyList<TargetDefinition> Targets,
        DatabaseDeploymentPlan Plan);

    private sealed record TargetDefinition(
        SystemDefinitionEntity System,
        string Schema,
        string Table,
        IReadOnlyList<string> TriggerNames,
        string DeploymentKey,
        string DefinitionHash,
        IReadOnlyList<string> Batches,
        DatabaseDeploymentTarget Public);

    internal sealed record DeploymentColumn(
        string PhysicalColumn,
        string PayloadField,
        string? FixedValue = null);
}
