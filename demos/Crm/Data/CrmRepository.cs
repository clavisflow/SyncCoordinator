using System.Data;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SyncCoordinator.Demo.Crm.Models;

namespace SyncCoordinator.Demo.Crm.Data;

public sealed class CrmRepository(CrmConnectionFactory connectionFactory, ILogger<CrmRepository> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false
    };

    private static readonly Action<ILogger, Exception?> LogDatabaseOperationFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1001, "DatabaseOperationFailed"),
            "CRM demo database operation failed.");

    public Task<DashboardSummary> GetDashboardAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM dbo.SupportCase) AS SupportCaseCount,
                (SELECT COUNT(*) FROM dbo.SupportCase WHERE WorkflowState = N'New') AS NewSupportCaseCount,
                (SELECT COUNT(*) FROM dbo.WorkOrder WHERE ISNULL(Status, N'') NOT IN (N'Completed', N'Cancelled')) AS OpenWorkOrderCount,
                (SELECT COUNT(*) FROM dbo.WorkOrder WHERE Status = N'Completed') AS CompletedWorkOrderCount;
            """;
        return ExecuteAsync(async connection =>
        {
            var row = await connection.QuerySingleAsync<DashboardRow>(new CommandDefinition(
                sql,
                cancellationToken: cancellationToken));
            return new DashboardSummary(
                row.SupportCaseCount,
                row.NewSupportCaseCount,
                row.OpenWorkOrderCount,
                row.CompletedWorkOrderCount);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SupportCaseRecord>> GetSupportCasesAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryEntitiesAsync(SupportCaseSelectSql, "s.CaseRef", null, cancellationToken);
        return rows.Select(row => ToSupportCase(row))
            .OrderByDescending(row => row.UpdatedAtUtc)
            .ToList();
    }

    public async Task<SupportCaseRecord?> GetSupportCaseAsync(string entityId, CancellationToken cancellationToken)
    {
        var row = await QueryEntityAsync(SupportCaseSelectSql, "s.CaseRef", entityId, cancellationToken);
        return row is null ? null : ToSupportCase(row);
    }

    public Task UpdateSupportCaseAsync(string entityId, SupportCasePayload payload, CancellationToken cancellationToken)
    {
        EnsureAllowedStatus(payload.Status, StatusOptions.SupportCases, "問い合わせ");
        const string sql = """
            UPDATE dbo.SupportCase
            SET ContactName=@CustomerName, ContactEmail=@Email, ContactPhone=@Phone, ProductLabel=@ProductName,
                DeviceSerial=@SerialNumber, CaseTitle=@Subject, CaseDetails=@Description,
                RequestedVisitOn=@PreferredVisitDate, WorkflowState=@Status, AgentReply=@ResponseMessage,
                ModifiedAtUtc=SYSUTCDATETIME()
            WHERE CaseRef=@EntityId;
            """;
        return UpdateAsync(sql, new
        {
            EntityId = entityId,
            payload.CustomerName,
            payload.Email,
            payload.Phone,
            payload.ProductName,
            payload.SerialNumber,
            payload.Subject,
            payload.Description,
            payload.PreferredVisitDate,
            payload.Status,
            payload.ResponseMessage
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkOrderRecord>> GetWorkOrdersAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryEntitiesAsync(WorkOrderSelectSql, "w.WorkOrderNumber", null, cancellationToken);
        return rows.Select(row => ToWorkOrder(row))
            .OrderByDescending(row => row.UpdatedAtUtc)
            .ToList();
    }

    public async Task<WorkOrderRecord?> GetWorkOrderAsync(string entityId, CancellationToken cancellationToken)
    {
        var row = await QueryEntityAsync(WorkOrderSelectSql, "w.WorkOrderNumber", entityId, cancellationToken);
        return row is null ? null : ToWorkOrder(row);
    }

    public Task UpdateWorkOrderAsync(string entityId, WorkOrderPayload payload, CancellationToken cancellationToken)
    {
        EnsureAllowedStatus(payload.Status, StatusOptions.WorkOrders, "作業指示");
        const string sql = """
            UPDATE dbo.WorkOrder
            SET CaseId=@CaseId, CaseNumber=@CaseNumber, CustomerName=@CustomerName, Address=@Address,
                Phone=@Phone, ProductName=@ProductName, ProblemSummary=@ProblemSummary,
                ScheduledAt=@ScheduledAt, TechnicianName=@TechnicianName, Status=@Status,
                WorkResult=@WorkResult, CompletedAt=@CompletedAt, UpdatedAtUtc=SYSUTCDATETIME()
            WHERE WorkOrderNumber=@EntityId;
            """;
        return UpdateAsync(sql, new
        {
            EntityId = entityId,
            payload.CaseId,
            payload.CaseNumber,
            payload.CustomerName,
            payload.Address,
            payload.Phone,
            payload.ProductName,
            payload.ProblemSummary,
            ScheduledAt = ParseDateTimeOffset(payload.ScheduledAt),
            payload.TechnicianName,
            payload.Status,
            payload.WorkResult,
            CompletedAt = ParseDateTimeOffset(payload.CompletedAt)
        }, cancellationToken);
    }

    public async Task<string> CreateWorkOrderAsync(
        SupportCaseRecord supportCase,
        WorkOrderPayload input,
        CancellationToken cancellationToken)
    {
        EnsureAllowedStatus(input.Status, StatusOptions.WorkOrders, "作業指示");
        var entityId = $"WO-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var payload = new WorkOrderPayload
        {
            WorkOrderNumber = entityId,
            CaseId = supportCase.EntityId,
            CaseNumber = supportCase.Payload.CaseNumber,
            CustomerName = supportCase.Payload.CustomerName,
            Address = input.Address,
            Phone = supportCase.Payload.Phone,
            ProductName = supportCase.Payload.ProductName,
            ProblemSummary = input.ProblemSummary ?? supportCase.Payload.Description,
            ScheduledAt = input.ScheduledAt,
            TechnicianName = input.TechnicianName,
            Status = input.Status,
            WorkResult = null,
            CompletedAt = null
        };

        const string sql = """
            INSERT dbo.WorkOrder
                (WorkOrderNumber, CaseId, CaseNumber, CustomerName, Address, Phone, ProductName,
                 ProblemSummary, ScheduledAt, TechnicianName, Status, WorkResult, CompletedAt,
                 OriginSystem, UpdatedAtUtc)
            VALUES
                (@WorkOrderNumber, @CaseId, @CaseNumber, @CustomerName, @Address, @Phone, @ProductName,
                 @ProblemSummary, @ScheduledAt, @TechnicianName, @Status, @WorkResult, @CompletedAt,
                 N'CRM', SYSUTCDATETIME());
            """;
        await ExecuteAsync(async connection =>
        {
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                payload.WorkOrderNumber,
                payload.CaseId,
                payload.CaseNumber,
                payload.CustomerName,
                payload.Address,
                payload.Phone,
                payload.ProductName,
                payload.ProblemSummary,
                ScheduledAt = ParseDateTimeOffset(payload.ScheduledAt),
                payload.TechnicianName,
                payload.Status,
                payload.WorkResult,
                CompletedAt = ParseDateTimeOffset(payload.CompletedAt)
            }, cancellationToken: cancellationToken));
            return true;
        }, cancellationToken);
        return entityId;
    }

    private Task<IEnumerable<SyncEntity>> QueryEntitiesAsync(
        string selectSql,
        string keyExpression,
        string? entityId,
        CancellationToken cancellationToken) =>
        ExecuteAsync(connection => connection.QueryAsync<SyncEntity>(new CommandDefinition(
            selectSql + (entityId is null ? " ORDER BY UpdatedAtUtc DESC;" : $" WHERE {keyExpression}=@EntityId;"),
            new { EntityId = entityId },
            cancellationToken: cancellationToken)), cancellationToken);

    private async Task<SyncEntity?> QueryEntityAsync(
        string selectSql,
        string keyExpression,
        string entityId,
        CancellationToken cancellationToken) =>
        (await QueryEntitiesAsync(selectSql, keyExpression, entityId, cancellationToken)).SingleOrDefault();

    private async Task UpdateAsync(
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        var affected = await ExecuteAsync(connection => connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken)), cancellationToken);
        if (affected == 0)
        {
            throw new CrmDataException("対象データが見つからないか、すでに削除されています。");
        }
    }

    private async Task<T> ExecuteAsync<T>(
        Func<SqlConnection, Task<T>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            return await action(connection);
        }
        catch (CrmDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            LogDatabaseOperationFailed(logger, exception);
            throw new CrmDataException(
                "CRMデータベースへ接続できないか、必要なテーブルがありません。接続設定とSQLスクリプトの適用状況を確認してください。",
                exception);
        }
    }

    private static SupportCaseRecord ToSupportCase(SyncEntity row) => new(
        row.EntityId,
        row.OriginSystem,
        row.UpdatedAtUtc,
        Deserialize<SupportCasePayload>(row.PayloadJson, row.EntityId));

    private static WorkOrderRecord ToWorkOrder(SyncEntity row) => new(
        row.EntityId,
        row.OriginSystem,
        row.UpdatedAtUtc,
        Deserialize<WorkOrderPayload>(row.PayloadJson, row.EntityId));

    private static T Deserialize<T>(string json, string entityId)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new JsonException("JSON payload was null.");
        }
        catch (JsonException exception)
        {
            throw new CrmDataException($"ID '{entityId}' の業務データを読み取れません。", exception);
        }
    }

    private static void EnsureAllowedStatus(string? value, IReadOnlyList<string> allowed, string entityLabel)
    {
        if (string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new CrmDataException($"{entityLabel}のステータスが正しくありません。");
        }
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var parsed))
        {
            return parsed;
        }

        throw new CrmDataException("日時の形式が正しくありません。");
    }

    private const string SupportCaseSelectSql = """
        SELECT s.CaseRef AS EntityId, s.SourceCode AS OriginSystem, s.ModifiedAtUtc AS UpdatedAtUtc,
               (SELECT s.CaseRef AS CaseNumber, s.ContactName AS CustomerName,
                       s.ContactEmail AS Email, s.ContactPhone AS Phone, s.ProductLabel AS ProductName,
                       s.DeviceSerial AS SerialNumber, s.CaseTitle AS Subject, s.CaseDetails AS Description,
                       s.RequestedVisitOn AS PreferredVisitDate, s.WorkflowState AS Status,
                       s.AgentReply AS ResponseMessage
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS PayloadJson
        FROM dbo.SupportCase AS s
        """;

    private const string WorkOrderSelectSql = """
        SELECT w.WorkOrderNumber AS EntityId, w.OriginSystem, w.UpdatedAtUtc,
               (SELECT w.WorkOrderNumber, w.CaseId, w.CaseNumber, w.CustomerName, w.Address,
                       w.Phone, w.ProductName, w.ProblemSummary, w.ScheduledAt,
                       w.TechnicianName, w.Status, w.WorkResult, w.CompletedAt
                FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS PayloadJson
        FROM dbo.WorkOrder AS w
        """;

    private sealed record DashboardRow(
        int SupportCaseCount,
        int NewSupportCaseCount,
        int OpenWorkOrderCount,
        int CompletedWorkOrderCount);
}
