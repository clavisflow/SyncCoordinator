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
        var mapping = await mappings.FindAsync(SystemCode, change.EntityType, cancellationToken);
        if (mapping is null)
        {
            // 無効化済みルールなどが残した古い通知は適用対象にしない。
            return null;
        }

        await using var connection = CreateConnection();
        var latest = await connection.QuerySingleOrDefaultAsync<QueueRow>(new CommandDefinition(
            Sql.ReadLatestChange,
            new { change.QueueId, change.EntityType, change.EntityId },
            cancellationToken: cancellationToken));
        if (latest is null)
        {
            return null;
        }

        var current = await ReadEntityAsync(connection, mapping, latest.EntityId, null, cancellationToken);
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
                latest.EntityId,
                ChangeOperation.Upsert,
                latest.OccurredAtUtc,
                current);
        }

        if (!string.Equals(latest.Operation, nameof(ChangeOperation.Delete), StringComparison.OrdinalIgnoreCase))
        {
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
                latest.EntityId,
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
        return await ReadEntityAsync(connection, mapping, entityId, null, cancellationToken);
    }

    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        var mapping = await mappings.GetRequiredAsync(SystemCode, request.EntityType, cancellationToken);
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
                await DeleteAsync(connection, transaction, mapping, request.EntityId, behavior, cancellationToken);
                if (behavior.Mode == DeletionMode.Physical)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        Sql.DeleteOrigin,
                        new { request.EntityType, request.EntityId },
                        transaction,
                        cancellationToken: cancellationToken));
                }
            }
            else
            {
                await UpsertAsync(connection, transaction, mapping, request, cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    Sql.UpsertOrigin,
                    new { request.EntityType, request.EntityId, request.OriginSystem },
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
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var parameters = CreateKeyParameters(mapping, entityId);
        var sql = $"SELECT {string.Join(", ", mapping.Columns.Select(column => $"{Quote(column.PhysicalColumn)} AS {Quote(column.PayloadField)}"))} " +
                  $"FROM {QualifiedTable(mapping)} WHERE {KeyPredicate(mapping)};";
        var row = await connection.QuerySingleOrDefaultAsync(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        if (row is not IDictionary<string, object> values)
        {
            return null;
        }

        return new EntityPayload(values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value is null or DBNull ? null : JsonSerializer.SerializeToNode(pair.Value, JsonOptions),
            StringComparer.Ordinal));
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
            var dynamicParameters = CreateKeyParameters(mapping, request.EntityId);
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
        var parameters = CreateKeyParameters(mapping, entityId);
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

    private static Dictionary<string, JsonNode?> CreateWriteValues(
        RelationalEntityMapping mapping,
        ApplyRequest request)
    {
        var keyValues = ParseEntityId(mapping, request.EntityId);
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
                candidate = JsonValue.Create(keyValue);
            }
            else
            {
                candidate = null;
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

    private static DynamicParameters CreateKeyParameters(RelationalEntityMapping mapping, string entityId)
    {
        var values = ParseEntityId(mapping, entityId);
        var parameters = new DynamicParameters();
        for (var index = 0; index < mapping.Keys.Count; index++)
        {
            parameters.Add($"Key{index}", values[mapping.Keys[index].PayloadField]);
        }
        return parameters;
    }

    private static Dictionary<string, string?> ParseEntityId(
        RelationalEntityMapping mapping,
        string entityId)
    {
        if (mapping.Keys.Count == 1)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [mapping.Keys[0].PayloadField] = entityId
            };
        }

        var json = JsonNode.Parse(entityId) as JsonObject ??
            throw new InvalidOperationException($"複合キーのEntityIdがJSON objectではありません: {entityId}");
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in mapping.Keys)
        {
            if (!json.TryGetPropertyValue(key.PayloadField, out var value))
            {
                throw new InvalidOperationException($"EntityIdにキー '{key.PayloadField}' がありません。");
            }
            result[key.PayloadField] = value is null ? null : JsonScalarText(value);
        }
        return result;
    }

    private string KeyPredicate(RelationalEntityMapping mapping) => string.Join(" AND ",
        mapping.Keys.Select((key, index) =>
            $"CAST({Quote(key.PhysicalColumn)} AS {TextType}) = @Key{index}"));

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
