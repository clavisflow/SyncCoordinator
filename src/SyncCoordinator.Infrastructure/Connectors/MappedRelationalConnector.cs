using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using SyncCoordinator.Contracts;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Connectors;

/// <summary>
/// 管理DBで検証・有効化されたテーブル／列マッピングを使って、RDBの業務テーブルを同期する。
/// キュー、適用済みメッセージ、Origin、TombstoneはDatabaseDeploymentServiceが配備する。
/// </summary>
internal sealed class MappedRelationalConnector(
    string systemCode,
    RelationalProvider provider,
    string connectionString,
    RelationalMappingProvider mappings) : ISyncConnector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SystemCode => systemCode;

    public async Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(
        long afterQueueId,
        int take,
        CancellationToken cancellationToken)
    {
        // 画面でDB配備、検証、有効化が完了するまでは、対象DBに共通テーブルが
        // 存在する前提を置かない。
        if (!await mappings.HasActiveMappingAsync(SystemCode, cancellationToken))
        {
            return [];
        }

        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<QueueRow>(new CommandDefinition(
            Sql.ReadChanges,
            new { AfterQueueId = afterQueueId, Take = take },
            cancellationToken: cancellationToken));
        return rows.Select(row => new ChangeQueueItem(
            row.QueueId,
            row.MessageId,
            row.EntityType,
            row.EntityId,
            Enum.Parse<ChangeOperation>(row.Operation, true),
            row.OccurredAtUtc)).ToArray();
    }

    public async Task<bool> WasAppliedMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        var count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            Sql.WasApplied,
            new { MessageId = messageId },
            cancellationToken: cancellationToken));
        return count != 0;
    }

    public async Task<SyncMessage?> ReadLatestMessageAsync(
        ChangeQueueItem change,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        var latest = await connection.QuerySingleOrDefaultAsync<QueueRow>(new CommandDefinition(
            Sql.ReadLatestChange,
            new { change.QueueId, change.EntityType, change.EntityId },
            cancellationToken: cancellationToken));
        if (latest is null)
        {
            return null;
        }
        RelationalEntityMapping mapping;
        try
        {
            mapping = await mappings.ResolveRequiredAsync(
                SystemCode,
                latest.EntityType,
                latest.EntityId,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // 無効化済みルールなどが残した古い通知は適用対象にしない。
            return null;
        }
        var canonicalEntityId = mapping.ToCanonicalEntityId(latest.EntityId);

        EntityPayload? current;
        try
        {
            current = await ReadEntityAsync(
                connection,
                mapping,
                latest.EntityId,
                entityIdIsPhysical: true,
                null,
                cancellationToken);
        }
        catch (ValueTransformationException exception)
        {
            var keyValues = CanonicalKeyPayload(mapping, latest.EntityId);
            return new SyncMessage(
                latest.MessageId,
                SystemCode,
                SystemCode,
                latest.EntityType,
                canonicalEntityId,
                ChangeOperation.Upsert,
                latest.OccurredAtUtc,
                new EntityPayload(keyValues))
            {
                ValidationFailure = new SyncValidationFailure(
                    exception.FieldName,
                    exception.TargetColumn,
                    exception.ReasonCode,
                    exception.Message)
            };
        }
        if (current is not null)
        {
            var origin = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
                Sql.ReadOrigin,
                new { latest.EntityType, latest.EntityId },
                cancellationToken: cancellationToken));
            return new SyncMessage(
                latest.MessageId,
                SystemCode,
                string.IsNullOrWhiteSpace(origin) ? SystemCode : origin,
                latest.EntityType,
                canonicalEntityId,
                ChangeOperation.Upsert,
                latest.OccurredAtUtc,
                current);
        }

        if (!string.Equals(latest.Operation, nameof(ChangeOperation.Delete), StringComparison.OrdinalIgnoreCase))
        {
            if (mapping.RelatedTables.Any(x => x.Usage == RelatedTableUsage.Eligibility))
            {
                var keyValues = CanonicalKeyPayload(mapping, latest.EntityId);
                return new SyncMessage(
                    latest.MessageId,
                    SystemCode,
                    SystemCode,
                    latest.EntityType,
                    canonicalEntityId,
                    ChangeOperation.Delete,
                    latest.OccurredAtUtc,
                    new EntityPayload(keyValues))
                {
                    IsEligibilityRemoval = true
                };
            }
            return null;
        }

        var tombstone = await connection.QuerySingleOrDefaultAsync<TombstoneRow>(new CommandDefinition(
            Sql.ReadTombstone,
            new { latest.MessageId, latest.EntityType, latest.EntityId },
            cancellationToken: cancellationToken));
        return tombstone is null
            ? null
            : new SyncMessage(
                latest.MessageId,
                SystemCode,
                string.IsNullOrWhiteSpace(tombstone.OriginSystem) ? SystemCode : tombstone.OriginSystem,
                latest.EntityType,
                canonicalEntityId,
                ChangeOperation.Delete,
                latest.OccurredAtUtc,
                DeserializePayload(tombstone.PayloadJson));
    }

    public async Task<EntityPayload?> ReadCurrentAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var mapping = await mappings.GetRequiredAsync(SystemCode, entityType, cancellationToken);
        await using var connection = CreateConnection();
        return await ReadEntityAsync(
            connection,
            mapping,
            entityId,
            entityIdIsPhysical: false,
            null,
            cancellationToken);
    }

    public async Task<EntityPayload?> ReadCurrentForRouteAsync(
        Guid routeId,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var mapping = await mappings.GetRequiredAsync(routeId, SystemCode, cancellationToken);
        if (!string.Equals(mapping.EntityType, entityType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"同期ルール '{routeId}' のEntityTypeは '{entityType}' ではありません。");
        }
        await using var connection = CreateConnection();
        return await ReadEntityAsync(
            connection,
            mapping,
            entityId,
            entityIdIsPhysical: false,
            null,
            cancellationToken);
    }

    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        var mapping = request.RouteId is { } routeId
            ? await mappings.GetRequiredAsync(routeId, SystemCode, cancellationToken)
            : await mappings.GetRequiredAsync(SystemCode, request.EntityType, cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var contextWasSet = false;

        try
        {
            var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                Sql.WasApplied,
                new { MessageId = request.DeliveryMessageId },
                transaction,
                cancellationToken: cancellationToken));
            if (exists != 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ApplyResult(ApplyStatus.AlreadyApplied);
            }

            await connection.ExecuteAsync(new CommandDefinition(
                Sql.InsertApplied,
                new { MessageId = request.DeliveryMessageId, AppliedAtUtc = DateTimeOffset.UtcNow },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                Sql.SetMessageContext,
                new { MessageId = request.DeliveryMessageId },
                transaction,
                cancellationToken: cancellationToken));
            contextWasSet = true;

            if (request.Operation == ChangeOperation.Delete)
            {
                var behavior = request.DeletionBehavior ??
                    throw new InvalidOperationException("削除方式が指定されていません。");
                var physicalEntityId = behavior.Mode == DeletionMode.Physical
                    ? await ReadPhysicalEntityIdAsync(
                        connection,
                        transaction,
                        mapping,
                        request.EntityId,
                        cancellationToken)
                    : null;
                await DeleteAsync(connection, transaction, mapping, request.EntityId, behavior, cancellationToken);
                if (physicalEntityId is not null)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        Sql.DeleteOrigin,
                        new { request.EntityType, EntityId = physicalEntityId },
                        transaction,
                        cancellationToken: cancellationToken));
                }
            }
            else
            {
                await UpsertAsync(connection, transaction, mapping, request, cancellationToken);
                var physicalEntityId = await ReadPhysicalEntityIdAsync(
                    connection,
                    transaction,
                    mapping,
                    request.EntityId,
                    cancellationToken) ??
                    throw new InvalidOperationException(
                        $"同期先へ適用したレコード '{request.EntityType}/{request.EntityId}' を読み戻せません。");
                await connection.ExecuteAsync(new CommandDefinition(
                    Sql.UpsertOrigin,
                    new { request.EntityType, EntityId = physicalEntityId, request.OriginSystem },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                Sql.ClearMessageContext,
                transaction: transaction,
                cancellationToken: cancellationToken));
            contextWasSet = false;
            await transaction.CommitAsync(cancellationToken);
            return new ApplyResult(ApplyStatus.Applied);
        }
        catch (Exception exception)
        {
            await TryRollbackAsync(transaction, cancellationToken);
            if (contextWasSet)
            {
                await TryClearMessageContextAsync(connection, cancellationToken);
            }

            if (IsUniqueViolation(exception))
            {
                return new ApplyResult(ApplyStatus.AlreadyApplied);
            }

            throw;
        }
    }

    private async Task<EntityPayload?> ReadEntityAsync(
        DbConnection connection,
        RelationalEntityMapping mapping,
        string entityId,
        bool entityIdIsPhysical,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var parameters = CreateKeyParameters(mapping, entityId, entityIdIsPhysical);
        var sql = BuildReadEntitySql(mapping, parameters);
        var rows = (await connection.QueryAsync(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken))).Take(2).ToArray();
        if (rows.Length > 1)
        {
            var aliases = string.Join(", ", mapping.RelatedTables
                .Where(x => x.Usage == RelatedTableUsage.Projection)
                .Select(x => x.Alias));
            var hasProjection = aliases.Length > 0;
            throw new ValueTransformationException(
                hasProjection ? aliases : string.Join(", ", mapping.Keys.Select(x => x.EntityIdField)),
                mapping.Table,
                hasProjection ? "related-projection-multiple-rows" : "entity-key-multiple-rows",
                hasProjection
                    ? $"関連テーブルの項目取得が同期単位 '{mapping.EntityType}/{entityId}' に対して複数行を返しました。項目取得の結合は最大1行になるよう設定してください。"
                    : $"同期単位キー '{mapping.EntityType}/{entityId}' が複数行に一致しました。キー列は1行を一意に識別するよう設定してください。");
        }
        var row = rows.SingleOrDefault();
        if (row is not IDictionary<string, object> values)
        {
            return null;
        }

        return new EntityPayload(values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is null or DBNull ? null : JsonSerializer.SerializeToNode(pair.Value, JsonOptions),
            StringComparer.Ordinal));
    }

    internal string BuildReadEntitySql(RelationalEntityMapping mapping, DynamicParameters parameters)
    {
        const string baseAlias = "sc_base";
        var projectionTables = mapping.RelatedTables.Where(x => x.Usage == RelatedTableUsage.Projection).ToArray();
        var select = string.Join(", ", mapping.Columns.Select(column =>
            $"{Quote(string.IsNullOrWhiteSpace(column.TableAlias) ? baseAlias : column.TableAlias)}.{Quote(column.PhysicalColumn)} AS {Quote(column.PayloadField)}"));
        var joins = string.Join(" ", projectionTables.Select(related =>
            $"LEFT JOIN {QualifiedTable(related.Schema, related.Table)} AS {Quote(related.Alias)} " +
            $"ON ({RelatedExpressionSql(related.JoinExpression, related.Alias, baseAlias)})" +
            RelatedConditionSql(related, baseAlias, includeAnd: true)));
        var eligibility = mapping.RelatedTables.Where(x => x.Usage == RelatedTableUsage.Eligibility)
            .Select(related =>
                $"EXISTS (SELECT 1 FROM {QualifiedTable(related.Schema, related.Table)} AS {Quote(related.Alias)} " +
                $"WHERE ({RelatedExpressionSql(related.JoinExpression, related.Alias, baseAlias)})" +
                RelatedConditionSql(related, baseAlias, includeAnd: true) + ")")
            .ToArray();
        var where = KeyPredicate(mapping, baseAlias);
        if (eligibility.Length > 0)
        {
            where += " AND " + string.Join(" AND ", eligibility);
        }
        var selectPrefix = provider == RelationalProvider.SqlServer ? "SELECT TOP (2)" : "SELECT";
        var rowLimit = provider == RelationalProvider.SqlServer ? string.Empty : " LIMIT 2";
        return $"{selectPrefix} {select} FROM {QualifiedTable(mapping)} AS {Quote(baseAlias)} {joins} WHERE {where}{rowLimit};";
    }

    private async Task UpsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalEntityMapping mapping,
        ApplyRequest request,
        CancellationToken cancellationToken)
    {
        var writeValues = CreateWriteValues(mapping, request);
        string sql;
        object parameters;

        if (provider == RelationalProvider.PostgreSql)
        {
            sql = BuildPostgreSqlUpsert(mapping, writeValues.Keys.ToArray());
            parameters = new { PayloadJson = JsonSerializer.Serialize(writeValues, JsonOptions) };
        }
        else
        {
            var dynamicParameters = CreateKeyParameters(mapping, request.EntityId, entityIdIsPhysical: false);
            var columns = writeValues.Keys.ToArray();
            for (var index = 0; index < columns.Length; index++)
            {
                dynamicParameters.Add($"Value{index}", ToScalarParameter(writeValues[columns[index]]));
            }

            sql = provider == RelationalProvider.SqlServer
                ? BuildSqlServerUpsert(mapping, columns)
                : BuildMySqlUpsert(mapping, columns);
            parameters = dynamicParameters;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private async Task DeleteAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalEntityMapping mapping,
        string entityId,
        DeletionBehavior behavior,
        CancellationToken cancellationToken)
    {
        var parameters = CreateKeyParameters(mapping, entityId, entityIdIsPhysical: false);
        string sql;
        if (behavior.Mode == DeletionMode.Physical)
        {
            sql = $"DELETE FROM {QualifiedTable(mapping)} WHERE {KeyPredicate(mapping)};";
        }
        else
        {
            var logicalColumn = behavior.LogicalDeleteColumn ??
                throw new InvalidOperationException("論理削除列が指定されていません。");
            parameters.Add("DeleteValue", behavior.LogicalDeleteValue);
            var valueExpression = provider == RelationalProvider.PostgreSql
                ? $"(jsonb_populate_record(NULL::{QualifiedTable(mapping)}, jsonb_build_object('{SqlLiteral(logicalColumn)}', @DeleteValue))).{Quote(logicalColumn)}"
                : "@DeleteValue";
            sql = $"UPDATE {QualifiedTable(mapping)} SET {Quote(logicalColumn)}={valueExpression} WHERE {KeyPredicate(mapping)};";
        }

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private async Task<string?> ReadPhysicalEntityIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        RelationalEntityMapping mapping,
        string canonicalEntityId,
        CancellationToken cancellationToken)
    {
        const string alias = "sc_identity";
        var parameters = CreateKeyParameters(mapping, canonicalEntityId, entityIdIsPhysical: false);
        var selectPrefix = provider == RelationalProvider.SqlServer ? "SELECT TOP (1)" : "SELECT";
        var rowLimit = provider == RelationalProvider.SqlServer ? string.Empty : " LIMIT 1";
        var sql =
            $"{selectPrefix} {PhysicalEntityIdExpression(mapping, alias)} " +
            $"FROM {QualifiedTable(mapping)} AS {Quote(alias)} " +
            $"WHERE {KeyPredicate(mapping, alias)}{rowLimit};";
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static Dictionary<string, JsonNode?> CreateWriteValues(
        RelationalEntityMapping mapping,
        ApplyRequest request)
    {
        var keyValues = ResolveKeyValues(mapping, request.EntityId, entityIdIsPhysical: false);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var column in mapping.Columns)
        {
            JsonNode? candidate;
            if (request.Payload.Fields.TryGetValue(column.PayloadField, out var value))
            {
                candidate = value;
            }
            else if (column.IsKey && keyValues.TryGetValue(column.PayloadField, out var keyValue))
            {
                candidate = keyValue;
            }
            else
            {
                // 項目ごとの同期方向で除外された列や、DB既定値を使う列は書き込まない。
                // null値を明示的に同期する場合はPayloadにキーが存在し、上の分岐に入る。
                continue;
            }
            values[column.PhysicalColumn] = ValueTransformEngine.Transform(
                candidate,
                new ValueTransformInput(),
                column.Contract,
                column.PayloadField,
                column.PhysicalColumn);
        }

        foreach (var fixedValue in mapping.FixedValues)
        {
            values[fixedValue.PhysicalColumn] = ValueTransformEngine.Transform(
                JsonValue.Create(fixedValue.Value),
                new ValueTransformInput(),
                fixedValue.Contract,
                fixedValue.PhysicalColumn,
                fixedValue.PhysicalColumn);
        }

        return values;
    }

    private static DynamicParameters CreateKeyParameters(
        RelationalEntityMapping mapping,
        string entityId,
        bool entityIdIsPhysical)
    {
        var values = ResolveKeyValues(mapping, entityId, entityIdIsPhysical);
        var parameters = new DynamicParameters();
        for (var index = 0; index < mapping.Keys.Count; index++)
        {
            var value = values[mapping.Keys[index].EntityIdField];
            parameters.Add($"Key{index}", value is null ? null : JsonScalarText(value));
        }
        return parameters;
    }

    private static Dictionary<string, JsonNode?> ResolveKeyValues(
        RelationalEntityMapping mapping,
        string entityId,
        bool entityIdIsPhysical)
    {
        if (entityIdIsPhysical)
        {
            var physical = RelationalEntityMapping.ParseEntityId(mapping.Keys, entityId);
            return mapping.Keys.ToDictionary(
                key => key.EntityIdField,
                key => physical[key.EntityIdField]?.DeepClone(),
                StringComparer.Ordinal);
        }

        JsonObject canonical;
        if (mapping.ColumnKeys.Count == 1)
        {
            canonical = new JsonObject
            {
                [mapping.ColumnKeys[0].PayloadField] = JsonValue.Create(entityId)
            };
        }
        else
        {
            canonical = JsonNode.Parse(entityId) as JsonObject ??
                        throw new InvalidOperationException($"複合キーのEntityIdがJSON objectではありません: {entityId}");
        }

        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var key in mapping.Keys)
        {
            if (key.IsFixed)
            {
                result[key.EntityIdField] = key.FixedValue?.DeepClone();
                continue;
            }

            if (!canonical.TryGetPropertyValue(key.PayloadField!, out var value))
            {
                throw new InvalidOperationException($"EntityIdにキー '{key.PayloadField}' がありません。");
            }
            result[key.EntityIdField] = ValueTransformEngine.Transform(
                value,
                new ValueTransformInput(),
                key.Contract,
                key.PayloadField!,
                key.PhysicalColumn);
        }
        return result;
    }

    internal static string CreatePhysicalEntityId(RelationalEntityMapping mapping, string canonicalEntityId)
    {
        var values = ResolveKeyValues(mapping, canonicalEntityId, entityIdIsPhysical: false);
        if (mapping.Keys.Count == 1)
        {
            var value = values[mapping.Keys[0].EntityIdField];
            return value is null ? string.Empty : JsonScalarText(value);
        }

        var json = new JsonObject();
        foreach (var key in mapping.Keys)
        {
            json[key.EntityIdField] = values[key.EntityIdField]?.DeepClone();
        }
        return json.ToJsonString();
    }

    private string PhysicalEntityIdExpression(RelationalEntityMapping mapping, string alias) =>
        mapping.Keys.Count == 1
            ? $"CAST({Quote(alias)}.{Quote(mapping.Keys[0].PhysicalColumn)} AS {TextType})"
            : provider switch
            {
                RelationalProvider.SqlServer =>
                    $"CONVERT(nvarchar(256), (SELECT {string.Join(", ", mapping.Keys.Select(key => $"{Quote(alias)}.{Quote(key.PhysicalColumn)} AS {Quote(key.EntityIdField)}"))} FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES))",
                RelationalProvider.MySql =>
                    $"CAST(JSON_OBJECT({string.Join(", ", mapping.Keys.Select(key => $"'{SqlLiteral(key.EntityIdField)}', {Quote(alias)}.{Quote(key.PhysicalColumn)}"))}) AS CHAR(256))",
                RelationalProvider.PostgreSql =>
                    $"CAST(jsonb_build_object({string.Join(", ", mapping.Keys.Select(key => $"'{SqlLiteral(key.EntityIdField)}', {Quote(alias)}.{Quote(key.PhysicalColumn)}"))}) AS varchar(256))",
                _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
            };

    private static Dictionary<string, JsonNode?> CanonicalKeyPayload(
        RelationalEntityMapping mapping,
        string physicalEntityId)
    {
        var physical = RelationalEntityMapping.ParseEntityId(mapping.Keys, physicalEntityId);
        return mapping.ColumnKeys.ToDictionary(
            key => key.PayloadField,
            key => physical[key.PayloadField]?.DeepClone(),
            StringComparer.Ordinal);
    }

    private string KeyPredicate(RelationalEntityMapping mapping, string? tableAlias = null) => string.Join(" AND ",
        mapping.Keys.Select((key, index) =>
            $"CAST({(tableAlias is null ? string.Empty : Quote(tableAlias) + ".")}{Quote(key.PhysicalColumn)} AS {TextType}) = @Key{index}"));

    private string BuildSqlServerUpsert(RelationalEntityMapping mapping, IReadOnlyList<string> columns)
    {
        var nonKeys = columns.Where(column => !mapping.Keys.Any(key =>
            string.Equals(key.PhysicalColumn, column, StringComparison.OrdinalIgnoreCase))).ToArray();
        var insert = $"INSERT INTO {QualifiedTable(mapping)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select((_, index) => $"@Value{index}"))});";
        if (nonKeys.Length == 0)
        {
            return $"IF NOT EXISTS (SELECT 1 FROM {QualifiedTable(mapping)} WHERE {KeyPredicate(mapping)}) {insert}";
        }

        var assignments = string.Join(", ", nonKeys.Select(column =>
            $"{Quote(column)}=@Value{Array.FindIndex(columns.ToArray(), candidate => string.Equals(candidate, column, StringComparison.Ordinal))}"));
        return $"UPDATE {QualifiedTable(mapping)} SET {assignments} WHERE {KeyPredicate(mapping)}; IF @@ROWCOUNT = 0 {insert}";
    }

    private string BuildMySqlUpsert(RelationalEntityMapping mapping, IReadOnlyList<string> columns)
    {
        var nonKeys = columns.Where(column => !mapping.Keys.Any(key =>
            string.Equals(key.PhysicalColumn, column, StringComparison.OrdinalIgnoreCase))).ToArray();
        var assignments = nonKeys.Length == 0
            ? $"{Quote(mapping.Keys[0].PhysicalColumn)}={Quote(mapping.Keys[0].PhysicalColumn)}"
            : string.Join(", ", nonKeys.Select(column => $"{Quote(column)}=VALUES({Quote(column)})"));
        return $"INSERT INTO {QualifiedTable(mapping)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select((_, index) => $"@Value{index}"))}) ON DUPLICATE KEY UPDATE {assignments};";
    }

    private string BuildPostgreSqlUpsert(RelationalEntityMapping mapping, IReadOnlyList<string> columns)
    {
        var nonKeys = columns.Where(column => !mapping.Keys.Any(key =>
            string.Equals(key.PhysicalColumn, column, StringComparison.OrdinalIgnoreCase))).ToArray();
        var conflict = nonKeys.Length == 0
            ? "DO NOTHING"
            : $"DO UPDATE SET {string.Join(", ", nonKeys.Select(column => $"{Quote(column)}=EXCLUDED.{Quote(column)}"))}";
        return $"WITH input AS (SELECT (jsonb_populate_record(NULL::{QualifiedTable(mapping)}, CAST(@PayloadJson AS jsonb))).*) " +
               $"INSERT INTO {QualifiedTable(mapping)} ({string.Join(", ", columns.Select(Quote))}) " +
               $"SELECT {string.Join(", ", columns.Select(Quote))} FROM input " +
               $"ON CONFLICT ({string.Join(", ", mapping.Keys.Select(key => Quote(key.PhysicalColumn)))}) {conflict};";
    }

    private string QualifiedTable(RelationalEntityMapping mapping) =>
        $"{Quote(mapping.Schema)}.{Quote(mapping.Table)}";

    private string QualifiedTable(string schema, string table) => $"{Quote(schema)}.{Quote(table)}";

    private string RelatedConditionSql(
        RelationalRelatedTable related,
        string baseAlias,
        bool includeAnd)
    {
        if (string.IsNullOrWhiteSpace(related.ConditionExpression))
        {
            return string.Empty;
        }

        var prefix = includeAnd ? " AND " : string.Empty;
        var expression = RelatedExpressionSql(related.ConditionExpression, related.Alias, baseAlias);
        return $"{prefix}({expression})";
    }

    private string RelatedExpressionSql(string expression, string relatedAlias, string baseAlias) => expression
        .Replace("{related}", Quote(relatedAlias), StringComparison.OrdinalIgnoreCase)
        .Replace("{source}", Quote(baseAlias), StringComparison.OrdinalIgnoreCase);

    private string Quote(string identifier) => provider switch
    {
        RelationalProvider.SqlServer => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
        RelationalProvider.MySql => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
        RelationalProvider.PostgreSql => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
    };

    private string TextType => provider switch
    {
        RelationalProvider.SqlServer => "nvarchar(4000)",
        RelationalProvider.MySql => "CHAR",
        RelationalProvider.PostgreSql => "text",
        _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
    };

    private DbConnection CreateConnection() => provider switch
    {
        RelationalProvider.SqlServer => new SqlConnection(connectionString),
        RelationalProvider.MySql => new MySqlConnection(connectionString),
        RelationalProvider.PostgreSql => new NpgsqlConnection(connectionString),
        _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
    };

    private SupportSql Sql => provider switch
    {
        RelationalProvider.SqlServer => SupportSql.SqlServer,
        RelationalProvider.MySql => SupportSql.MySql,
        RelationalProvider.PostgreSql => SupportSql.PostgreSql,
        _ => throw new InvalidOperationException($"未対応のProviderです: {provider}")
    };

    private static string? ToScalarParameter(JsonNode? value) =>
        value is null ? null : JsonScalarText(value);

    private static string JsonScalarText(JsonNode value) =>
        value is JsonValue jsonValue
            ? JsonValueText(jsonValue)
            : value.ToJsonString();

    private static string JsonValueText(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<DateTimeOffset>(out var offset)) return offset.ToString("O", CultureInfo.InvariantCulture);
        if (value.TryGetValue<DateTime>(out var dateTime)) return dateTime.ToString("O", CultureInfo.InvariantCulture);
        if (value.TryGetValue<bool>(out var boolean)) return boolean ? "true" : "false";
        if (value.TryGetValue<long>(out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<decimal>(out var decimalValue)) return decimalValue.ToString(CultureInfo.InvariantCulture);
        if (value.TryGetValue<double>(out var doubleValue)) return doubleValue.ToString("R", CultureInfo.InvariantCulture);
        return value.ToJsonString();
    }

    private static EntityPayload DeserializePayload(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);

    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsUniqueViolation(Exception exception) =>
        exception is SqlException { Number: 2601 or 2627 } or
        MySqlException { Number: 1062 } or
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static async Task TryRollbackAsync(DbTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch
        {
            // 元の例外を優先する。
        }
    }

    private async Task TryClearMessageContextAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                Sql.ClearMessageContext,
                cancellationToken: cancellationToken));
        }
        catch
        {
            // 接続を破棄するため、元の例外を優先する。
        }
    }

    private sealed class QueueRow
    {
        public long QueueId { get; init; }
        public Guid MessageId { get; init; }
        public required string EntityType { get; init; }
        public required string EntityId { get; init; }
        public required string Operation { get; init; }
        public DateTimeOffset OccurredAtUtc { get; init; }
    }

    private sealed class TombstoneRow
    {
        public required string OriginSystem { get; init; }
        public required string PayloadJson { get; init; }
    }

    private sealed record SupportSql(
        string ReadChanges,
        string ReadLatestChange,
        string WasApplied,
        string ReadOrigin,
        string ReadTombstone,
        string InsertApplied,
        string SetMessageContext,
        string ClearMessageContext,
        string UpsertOrigin,
        string DeleteOrigin)
    {
        public static SupportSql SqlServer { get; } = new(
            "SELECT TOP (@Take) QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc FROM dbo.SyncChangeQueue WHERE QueueId > @AfterQueueId ORDER BY QueueId;",
            "SELECT TOP (1) QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc FROM dbo.SyncChangeQueue WHERE QueueId >= @QueueId AND EntityType=@EntityType AND EntityId=@EntityId ORDER BY QueueId DESC;",
            "SELECT COUNT(1) FROM dbo.SyncAppliedMessage WHERE MessageId=@MessageId;",
            "SELECT OriginSystem FROM dbo.SyncEntityOrigin WHERE EntityType=@EntityType AND EntityId=@EntityId;",
            "SELECT OriginSystem, PayloadJson FROM dbo.SyncDeleteTombstone WHERE MessageId=@MessageId AND EntityType=@EntityType AND EntityId=@EntityId;",
            "INSERT INTO dbo.SyncAppliedMessage(MessageId, AppliedAtUtc) VALUES (@MessageId, @AppliedAtUtc);",
            "EXEC sys.sp_set_session_context @key=N'SyncMessageId', @value=@MessageId;",
            "EXEC sys.sp_set_session_context @key=N'SyncMessageId', @value=NULL;",
            "UPDATE dbo.SyncEntityOrigin SET OriginSystem=@OriginSystem WHERE EntityType=@EntityType AND EntityId=@EntityId; IF @@ROWCOUNT=0 INSERT dbo.SyncEntityOrigin(EntityType, EntityId, OriginSystem) VALUES (@EntityType, @EntityId, @OriginSystem);",
            "DELETE FROM dbo.SyncEntityOrigin WHERE EntityType=@EntityType AND EntityId=@EntityId;");

        public static SupportSql MySql { get; } = new(
            "SELECT QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc FROM SyncChangeQueue WHERE QueueId > @AfterQueueId ORDER BY QueueId LIMIT @Take;",
            "SELECT QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc FROM SyncChangeQueue WHERE QueueId >= @QueueId AND EntityType=@EntityType AND EntityId=@EntityId ORDER BY QueueId DESC LIMIT 1;",
            "SELECT COUNT(1) FROM SyncAppliedMessage WHERE MessageId=@MessageId;",
            "SELECT OriginSystem FROM SyncEntityOrigin WHERE EntityType=@EntityType AND EntityId=@EntityId;",
            "SELECT OriginSystem, CAST(PayloadJson AS CHAR CHARACTER SET utf8mb4) AS PayloadJson FROM SyncDeleteTombstone WHERE MessageId=@MessageId AND EntityType=@EntityType AND EntityId=@EntityId;",
            "INSERT INTO SyncAppliedMessage(MessageId, AppliedAtUtc) VALUES (@MessageId, @AppliedAtUtc);",
            "SET @sync_message_id=@MessageId;",
            "SET @sync_message_id=NULL;",
            "INSERT INTO SyncEntityOrigin(EntityType, EntityId, OriginSystem) VALUES (@EntityType, @EntityId, @OriginSystem) ON DUPLICATE KEY UPDATE OriginSystem=VALUES(OriginSystem);",
            "DELETE FROM SyncEntityOrigin WHERE EntityType=@EntityType AND EntityId=@EntityId;");

        public static SupportSql PostgreSql { get; } = new(
            "SELECT \"QueueId\", \"MessageId\", \"EntityType\", \"EntityId\", \"Operation\", \"OccurredAtUtc\" FROM public.\"SyncChangeQueue\" WHERE \"QueueId\" > @AfterQueueId ORDER BY \"QueueId\" LIMIT @Take;",
            "SELECT \"QueueId\", \"MessageId\", \"EntityType\", \"EntityId\", \"Operation\", \"OccurredAtUtc\" FROM public.\"SyncChangeQueue\" WHERE \"QueueId\" >= @QueueId AND \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId ORDER BY \"QueueId\" DESC LIMIT 1;",
            "SELECT COUNT(1) FROM public.\"SyncAppliedMessage\" WHERE \"MessageId\"=@MessageId;",
            "SELECT \"OriginSystem\" FROM public.\"SyncEntityOrigin\" WHERE \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId;",
            "SELECT \"OriginSystem\", \"PayloadJson\"::text AS \"PayloadJson\" FROM public.\"SyncDeleteTombstone\" WHERE \"MessageId\"=@MessageId AND \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId;",
            "INSERT INTO public.\"SyncAppliedMessage\"(\"MessageId\", \"AppliedAtUtc\") VALUES (@MessageId, @AppliedAtUtc);",
            "SELECT set_config('synccoordinator.message_id', CAST(@MessageId AS text), true);",
            "SELECT set_config('synccoordinator.message_id', '', true);",
            "INSERT INTO public.\"SyncEntityOrigin\"(\"EntityType\", \"EntityId\", \"OriginSystem\") VALUES (@EntityType, @EntityId, @OriginSystem) ON CONFLICT (\"EntityType\", \"EntityId\") DO UPDATE SET \"OriginSystem\"=EXCLUDED.\"OriginSystem\";",
            "DELETE FROM public.\"SyncEntityOrigin\" WHERE \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId;");
    }
}
