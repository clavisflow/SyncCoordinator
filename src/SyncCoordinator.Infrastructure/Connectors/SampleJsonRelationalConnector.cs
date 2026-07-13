using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using MySqlConnector;
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

    public async Task<SyncMessage> ReadMessageAsync(
        ChangeQueueItem change,
        CancellationToken cancellationToken)
    {
        if (change.Operation == ChangeOperation.Delete)
        {
            throw new NotSupportedException(
                "Sample Connector は物理削除後の payload を推測しません。soft-delete または tombstone 対応 Connector に差し替えてください。");
        }

        await using var connection = CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<EntityRow>(new CommandDefinition(
            Sql.ReadEntity,
            new { change.EntityType, change.EntityId },
            cancellationToken: cancellationToken)) ??
                  throw new InvalidOperationException(
                      $"Queue item {change.QueueId} の entity {change.EntityType}/{change.EntityId} が見つかりません。");
        return new SyncMessage(
            change.MessageId,
            SystemCode,
            string.IsNullOrWhiteSpace(row.OriginSystem) ? SystemCode : row.OriginSystem,
            change.EntityType,
            change.EntityId,
            change.Operation,
            change.OccurredAtUtc,
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
                await connection.ExecuteAsync(new CommandDefinition(
                    Sql.DeleteEntity,
                    new { request.EntityType, request.EntityId },
                    transaction,
                    cancellationToken: cancellationToken));
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
        _ => throw new ArgumentOutOfRangeException(nameof(options.Provider))
    };

    private SqlSet Sql => options.Provider == RelationalProvider.SqlServer
        ? SqlSet.SqlServer
        : SqlSet.MySql;

    private static EntityPayload DeserializePayload(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, JsonNode?>>(json, JsonOptions) ?? []);

    private static bool IsUniqueViolation(Exception exception) =>
        exception is SqlException { Number: 2601 or 2627 } or
        MySqlException { Number: 1062 };

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
        string WasApplied,
        string ReadEntity,
        string ReadPayload,
        string InsertApplied,
        string SetMessageContext,
        string ClearMessageContext,
        string UpsertEntity,
        string DeleteEntity)
    {
        public static SqlSet SqlServer { get; } = new(
            """
            SELECT TOP (@Take) QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc
            FROM dbo.SyncChangeQueue
            WHERE QueueId > @AfterQueueId
            ORDER BY QueueId;
            """,
            "SELECT COUNT(1) FROM dbo.SyncAppliedMessage WHERE MessageId = @MessageId;",
            """
            SELECT OriginSystem, PayloadJson FROM dbo.SampleSyncEntity
            WHERE EntityType = @EntityType AND EntityId = @EntityId;
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
            "DELETE FROM dbo.SampleSyncEntity WHERE EntityType=@EntityType AND EntityId=@EntityId;");

        public static SqlSet MySql { get; } = new(
            """
            SELECT QueueId, MessageId, EntityType, EntityId, Operation, OccurredAtUtc
            FROM SyncChangeQueue
            WHERE QueueId > @AfterQueueId
            ORDER BY QueueId
            LIMIT @Take;
            """,
            "SELECT COUNT(1) FROM SyncAppliedMessage WHERE MessageId = @MessageId;",
            """
            SELECT OriginSystem, PayloadJson FROM SampleSyncEntity
            WHERE EntityType = @EntityType AND EntityId = @EntityId;
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
            "DELETE FROM SampleSyncEntity WHERE EntityType=@EntityType AND EntityId=@EntityId;");
    }
}
