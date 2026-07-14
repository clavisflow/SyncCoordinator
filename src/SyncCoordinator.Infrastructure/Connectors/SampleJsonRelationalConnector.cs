using System.Data.Common;
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
/// 未確定の業務テーブルを代用する SampleSyncEntity 用 Connector。
/// 実導入時は ISyncConnector を実装し、ここをシステム別の変換に差し替える。
/// </summary>
public sealed class SampleJsonRelationalConnector(
    RelationalSystemOptions options,
    string connectionString) : ISyncConnector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SystemCode => options.SystemCode;

    public async Task<IReadOnlyList<ChangeQueueItem>> ReadChangesAsync(
        long afterQueueId,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        var rows = await connection.QueryAsync<QueueRow>(new CommandDefinition(
            Sql.ReadChanges,
            new { AfterQueueId = afterQueueId, Take = take },
            cancellationToken: cancellationToken));
        return rows.Select(x => new ChangeQueueItem(
            x.QueueId,
            x.MessageId,
            x.EntityType,
            x.EntityId,
            Enum.Parse<ChangeOperation>(x.Operation, true),
            x.OccurredAtUtc)).ToArray();
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

        var current = await connection.QuerySingleOrDefaultAsync<EntityRow>(new CommandDefinition(
            Sql.ReadEntity,
            new { latest.EntityType, latest.EntityId },
            cancellationToken: cancellationToken));
        if (current is not null)
        {
            return new SyncMessage(
                latest.MessageId,
                SystemCode,
                string.IsNullOrWhiteSpace(current.OriginSystem) ? SystemCode : current.OriginSystem,
                latest.EntityType,
                latest.EntityId,
                ChangeOperation.Upsert,
                latest.OccurredAtUtc,
                DeserializePayload(current.PayloadJson));
        }

        if (!string.Equals(latest.Operation, nameof(ChangeOperation.Delete), StringComparison.OrdinalIgnoreCase))
        {
            // Delete同期が無効なテーブルなど、現在行もTombstoneもない通知は
            // 適用対象がないためCheckpointだけ進める。
            return null;
        }

        var row = await connection.QuerySingleOrDefaultAsync<EntityRow>(new CommandDefinition(
            Sql.ReadTombstone,
            new { latest.MessageId, latest.EntityType, latest.EntityId },
            cancellationToken: cancellationToken));
        if (row is null)
        {
            return null;
        }
        return new SyncMessage(
            latest.MessageId,
            SystemCode,
            string.IsNullOrWhiteSpace(row.OriginSystem) ? SystemCode : row.OriginSystem,
            latest.EntityType,
            latest.EntityId,
            ChangeOperation.Delete,
            latest.OccurredAtUtc,
            DeserializePayload(row.PayloadJson));
    }

    public async Task<EntityPayload?> ReadCurrentAsync(
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        var json = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            Sql.ReadPayload,
            new { EntityType = entityType, EntityId = entityId },
            cancellationToken: cancellationToken));
        return json is null ? null : DeserializePayload(json);
    }

    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

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
                new
                {
                    MessageId = request.DeliveryMessageId,
                    AppliedAtUtc = DateTimeOffset.UtcNow
                },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                Sql.SetMessageContext,
                new { MessageId = request.DeliveryMessageId },
                transaction,
                cancellationToken: cancellationToken));

            if (request.Operation == ChangeOperation.Delete)
            {
                var behavior = request.DeletionBehavior ??
                               throw new InvalidOperationException("削除方式が指定されていません。");
                var sql = behavior.Mode == DeletionMode.Physical
                    ? Sql.DeleteEntity
                    : LogicalDeleteSql(behavior.LogicalDeleteColumn ??
                                       throw new InvalidOperationException("論理削除列が指定されていません。"));
                await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    new
                    {
                        request.EntityType,
                        request.EntityId,
                        DeleteValue = behavior.LogicalDeleteValue,
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    transaction,
                    cancellationToken: cancellationToken));
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
                await connection.ExecuteAsync(new CommandDefinition(
                    Sql.UpsertEntity,
                    new
                    {
                        request.EntityType,
                        request.EntityId,
                        request.OriginSystem,
                        PayloadJson = JsonSerializer.Serialize(request.Payload.Fields, JsonOptions),
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    },
                    transaction,
                    cancellationToken: cancellationToken));
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
            await transaction.CommitAsync(cancellationToken);
            return new ApplyResult(ApplyStatus.Applied);
        }
        catch (Exception exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ApplyResult(ApplyStatus.AlreadyApplied);
        }
    }

    private DbConnection CreateConnection() => options.Provider switch
    {
        RelationalProvider.SqlServer => new SqlConnection(connectionString),
        RelationalProvider.MySql => new MySqlConnection(connectionString),
        RelationalProvider.PostgreSql => new NpgsqlConnection(connectionString),
        _ => throw new InvalidOperationException($"未対応のProviderです: {options.Provider}")
    };

    private SqlSet Sql => options.Provider switch
    {
        RelationalProvider.SqlServer => SqlSet.SqlServer,
        RelationalProvider.MySql => SqlSet.MySql,
        RelationalProvider.PostgreSql => SqlSet.PostgreSql,
        _ => throw new InvalidOperationException($"未対応のProviderです: {options.Provider}")
    };

    private string LogicalDeleteSql(string column) => options.Provider switch
    {
        RelationalProvider.SqlServer => $"UPDATE dbo.SampleSyncEntity SET [{column.Replace("]", "]]", StringComparison.Ordinal)}]=@DeleteValue, UpdatedAtUtc=@UpdatedAtUtc WHERE EntityType=@EntityType AND EntityId=@EntityId;",
        RelationalProvider.MySql => $"UPDATE SampleSyncEntity SET `{column.Replace("`", "``", StringComparison.Ordinal)}`=@DeleteValue, UpdatedAtUtc=@UpdatedAtUtc WHERE EntityType=@EntityType AND EntityId=@EntityId;",
        RelationalProvider.PostgreSql => $"UPDATE public.\"SampleSyncEntity\" SET \"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\"=(jsonb_populate_record(NULL::public.\"SampleSyncEntity\", jsonb_build_object('{column.Replace("'", "''", StringComparison.Ordinal)}', @DeleteValue))).\"{column.Replace("\"", "\"\"", StringComparison.Ordinal)}\", \"UpdatedAtUtc\"=@UpdatedAtUtc WHERE \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId;",
        _ => throw new InvalidOperationException($"未対応のProviderです: {options.Provider}")
    };

    private static EntityPayload DeserializePayload(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);

    private static bool IsUniqueViolation(Exception exception) =>
        exception is SqlException { Number: 2601 or 2627 } or
        MySqlException { Number: 1062 } or
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed class QueueRow
    {
        public long QueueId { get; init; }
        public Guid MessageId { get; init; }
        public required string EntityType { get; init; }
        public required string EntityId { get; init; }
        public required string Operation { get; init; }
        public DateTimeOffset OccurredAtUtc { get; init; }
    }

    private sealed class EntityRow
    {
        public required string OriginSystem { get; init; }
        public required string PayloadJson { get; init; }
    }

    private sealed record SqlSet(
        string ReadChanges,
        string ReadLatestChange,
        string WasApplied,
        string ReadEntity,
        string ReadTombstone,
        string ReadPayload,
        string InsertApplied,
        string SetMessageContext,
        string ClearMessageContext,
        string UpsertEntity,
        string DeleteEntity,
        string UpsertOrigin,
        string DeleteOrigin)
    {
        public static SqlSet SqlServer { get; } = new(
            """
            SELECT TOP (@Take) QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc
            FROM dbo.SyncChangeQueue
            WHERE QueueId > @AfterQueueId
            ORDER BY QueueId;
            """,
            """
            SELECT TOP (1) QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc
            FROM dbo.SyncChangeQueue
            WHERE QueueId >= @QueueId
              AND EntityType = @EntityType
              AND EntityId = @EntityId
            ORDER BY QueueId DESC;
            """,
            "SELECT COUNT(1) FROM dbo.SyncAppliedMessage WHERE MessageId = @MessageId;",
            """
            SELECT OriginSystem, PayloadJson FROM dbo.SampleSyncEntity
            WHERE EntityType = @EntityType AND EntityId = @EntityId;
            """,
            """
            SELECT OriginSystem, PayloadJson FROM dbo.SyncDeleteTombstone
            WHERE MessageId = @MessageId AND EntityType = @EntityType AND EntityId = @EntityId;
            """,
            """
            SELECT PayloadJson FROM dbo.SampleSyncEntity
            WHERE EntityType = @EntityType AND EntityId = @EntityId;
            """,
            "INSERT INTO dbo.SyncAppliedMessage (MessageId, AppliedAtUtc) VALUES (@MessageId, @AppliedAtUtc);",
            "EXEC sys.sp_set_session_context @key=N'SyncMessageId', @value=@MessageId;",
            "EXEC sys.sp_set_session_context @key=N'SyncMessageId', @value=NULL;",
            """
            UPDATE dbo.SampleSyncEntity
            SET OriginSystem=@OriginSystem, PayloadJson=@PayloadJson, UpdatedAtUtc=@UpdatedAtUtc
            WHERE EntityType=@EntityType AND EntityId=@EntityId;
            IF @@ROWCOUNT = 0
                INSERT INTO dbo.SampleSyncEntity
                    (EntityType, EntityId, OriginSystem, PayloadJson, UpdatedAtUtc)
                VALUES
                    (@EntityType, @EntityId, @OriginSystem, @PayloadJson, @UpdatedAtUtc);
            """,
            "DELETE FROM dbo.SampleSyncEntity WHERE EntityType=@EntityType AND EntityId=@EntityId;",
            """
            UPDATE dbo.SyncEntityOrigin SET OriginSystem=@OriginSystem
            WHERE EntityType=@EntityType AND EntityId=@EntityId;
            IF @@ROWCOUNT = 0
                INSERT dbo.SyncEntityOrigin(EntityType, EntityId, OriginSystem)
                VALUES (@EntityType, @EntityId, @OriginSystem);
            """,
            "DELETE FROM dbo.SyncEntityOrigin WHERE EntityType=@EntityType AND EntityId=@EntityId;");

        public static SqlSet MySql { get; } = new(
            """
            SELECT QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc
            FROM SyncChangeQueue
            WHERE QueueId > @AfterQueueId
            ORDER BY QueueId
            LIMIT @Take;
            """,
            """
            SELECT QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc
            FROM SyncChangeQueue
            WHERE QueueId >= @QueueId
              AND EntityType = @EntityType
              AND EntityId = @EntityId
            ORDER BY QueueId DESC
            LIMIT 1;
            """,
            "SELECT COUNT(1) FROM SyncAppliedMessage WHERE MessageId = @MessageId;",
            """
            SELECT OriginSystem, PayloadJson FROM SampleSyncEntity
            WHERE EntityType = @EntityType AND EntityId = @EntityId;
            """,
            """
            SELECT OriginSystem, PayloadJson FROM SyncDeleteTombstone
            WHERE MessageId = @MessageId AND EntityType = @EntityType AND EntityId = @EntityId;
            """,
            """
            SELECT PayloadJson FROM SampleSyncEntity
            WHERE EntityType = @EntityType AND EntityId = @EntityId;
            """,
            "INSERT INTO SyncAppliedMessage (MessageId, AppliedAtUtc) VALUES (@MessageId, @AppliedAtUtc);",
            "SET @sync_message_id = @MessageId;",
            "SET @sync_message_id = NULL;",
            """
            INSERT INTO SampleSyncEntity
                (EntityType, EntityId, OriginSystem, PayloadJson, UpdatedAtUtc)
            VALUES
                (@EntityType, @EntityId, @OriginSystem, @PayloadJson, @UpdatedAtUtc)
            ON DUPLICATE KEY UPDATE
                OriginSystem=VALUES(OriginSystem),
                PayloadJson=VALUES(PayloadJson),
                UpdatedAtUtc=VALUES(UpdatedAtUtc);
            """,
            "DELETE FROM SampleSyncEntity WHERE EntityType=@EntityType AND EntityId=@EntityId;",
            """
            INSERT INTO SyncEntityOrigin(EntityType, EntityId, OriginSystem)
            VALUES (@EntityType, @EntityId, @OriginSystem)
            ON DUPLICATE KEY UPDATE OriginSystem=VALUES(OriginSystem);
            """,
            "DELETE FROM SyncEntityOrigin WHERE EntityType=@EntityType AND EntityId=@EntityId;");

        public static SqlSet PostgreSql { get; } = new(
            """
            SELECT "QueueId", "MessageId", "EntityType", "EntityId", "Operation", "OccurredAtUtc"
            FROM public."SyncChangeQueue"
            WHERE "QueueId" > @AfterQueueId
            ORDER BY "QueueId"
            LIMIT @Take;
            """,
            """
            SELECT "QueueId", "MessageId", "EntityType", "EntityId", "Operation", "OccurredAtUtc"
            FROM public."SyncChangeQueue"
            WHERE "QueueId" >= @QueueId
              AND "EntityType" = @EntityType
              AND "EntityId" = @EntityId
            ORDER BY "QueueId" DESC
            LIMIT 1;
            """,
            "SELECT COUNT(1) FROM public.\"SyncAppliedMessage\" WHERE \"MessageId\" = @MessageId;",
            """
            SELECT "OriginSystem", "PayloadJson"::text AS "PayloadJson" FROM public."SampleSyncEntity"
            WHERE "EntityType" = @EntityType AND "EntityId" = @EntityId;
            """,
            """
            SELECT "OriginSystem", "PayloadJson"::text AS "PayloadJson" FROM public."SyncDeleteTombstone"
            WHERE "MessageId" = @MessageId AND "EntityType" = @EntityType AND "EntityId" = @EntityId;
            """,
            """
            SELECT "PayloadJson"::text FROM public."SampleSyncEntity"
            WHERE "EntityType" = @EntityType AND "EntityId" = @EntityId;
            """,
            "INSERT INTO public.\"SyncAppliedMessage\" (\"MessageId\", \"AppliedAtUtc\") VALUES (@MessageId, @AppliedAtUtc);",
            "SELECT set_config('synccoordinator.message_id', CAST(@MessageId AS text), true);",
            "SELECT set_config('synccoordinator.message_id', '', true);",
            """
            INSERT INTO public."SampleSyncEntity"
                ("EntityType", "EntityId", "OriginSystem", "PayloadJson", "UpdatedAtUtc")
            VALUES
                (@EntityType, @EntityId, @OriginSystem, CAST(@PayloadJson AS jsonb), @UpdatedAtUtc)
            ON CONFLICT ("EntityType", "EntityId") DO UPDATE SET
                "OriginSystem" = EXCLUDED."OriginSystem",
                "PayloadJson" = EXCLUDED."PayloadJson",
                "UpdatedAtUtc" = EXCLUDED."UpdatedAtUtc";
            """,
            "DELETE FROM public.\"SampleSyncEntity\" WHERE \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId;",
            """
            INSERT INTO public."SyncEntityOrigin"("EntityType", "EntityId", "OriginSystem")
            VALUES (@EntityType, @EntityId, @OriginSystem)
            ON CONFLICT ("EntityType", "EntityId") DO UPDATE SET
                "OriginSystem" = EXCLUDED."OriginSystem";
            """,
            "DELETE FROM public.\"SyncEntityOrigin\" WHERE \"EntityType\"=@EntityType AND \"EntityId\"=@EntityId;");
    }
}
