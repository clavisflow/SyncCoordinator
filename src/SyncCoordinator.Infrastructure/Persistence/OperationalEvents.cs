using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed partial class OperationalEventRecorder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OperationalEventRecorder> logger) : IOperationalEventRecorder
{
    private static readonly TimeSpan AggregationWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(90);

    public async Task RecordAsync(OperationalEventInput input, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CoordinatorDbContext>();
            var now = timeProvider.GetUtcNow();
            var category = Truncate(input.Category, 64) ?? "General";
            var code = Truncate(input.Code, 100) ?? "unknown";
            var source = Truncate(input.Source, 64) ?? "unknown";
            var target = Truncate(input.Target, 200);
            var details = Truncate(input.Details, 4000);
            var correlationId = Truncate(
                input.CorrelationId ?? Activity.Current?.TraceId.ToString(),
                64);
            var aggregateAfter = now.Subtract(AggregationWindow);

            var existing = await dbContext.OperationalEvents
                .Where(x => x.AcknowledgedAtUtc == null &&
                            x.LastOccurredAtUtc >= aggregateAfter &&
                            x.Category == category &&
                            x.Code == code &&
                            x.Source == source &&
                            x.Target == target &&
                            x.Details == details)
                .OrderByDescending(x => x.LastOccurredAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is null)
            {
                dbContext.OperationalEvents.Add(new OperationalEventEntity
                {
                    Id = Guid.NewGuid(),
                    Severity = input.Severity,
                    Category = category,
                    Code = code,
                    Source = source,
                    Target = target,
                    Details = details,
                    CorrelationId = correlationId,
                    FirstOccurredAtUtc = now,
                    LastOccurredAtUtc = now,
                    OccurrenceCount = 1
                });
            }
            else
            {
                existing.LastOccurredAtUtc = now;
                existing.OccurrenceCount++;
                existing.Severity = input.Severity > existing.Severity
                    ? input.Severity
                    : existing.Severity;
                existing.CorrelationId = correlationId ?? existing.CorrelationId;
            }

            await dbContext.OperationalEvents
                .Where(x => x.LastOccurredAtUtc < now.Subtract(RetentionPeriod))
                .ExecuteDeleteAsync(cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            FailedToPersist(logger, exception, input.Code);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    [LoggerMessage(LogLevel.Warning, "Failed to persist operational event {eventCode}")]
    private static partial void FailedToPersist(ILogger logger, Exception exception, string eventCode);
}

public sealed class OperationalEventAdminService(
    CoordinatorDbContext dbContext,
    TimeProvider timeProvider) : IOperationalEventAdminService
{
    public async Task<IReadOnlyList<OperationalEventListItem>> GetRecentAsync(
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.OperationalEvents.AsNoTracking()
            .OrderBy(x => x.AcknowledgedAtUtc != null)
            .ThenByDescending(x => x.LastOccurredAtUtc)
            .Take(Math.Clamp(take, 1, 500))
            .Select(x => new OperationalEventListItem(
                x.Id,
                x.Severity,
                x.Category,
                x.Code,
                x.Source,
                x.Target,
                x.Details,
                x.CorrelationId,
                x.FirstOccurredAtUtc,
                x.LastOccurredAtUtc,
                x.OccurrenceCount,
                x.AcknowledgedAtUtc,
                x.AcknowledgedBy))
            .ToListAsync(cancellationToken);

    public async Task AcknowledgeAsync(
        Guid id,
        string acknowledgedBy,
        CancellationToken cancellationToken)
    {
        var operationalEvent = await dbContext.OperationalEvents
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ??
            throw new KeyNotFoundException("指定されたシステムイベントは存在しません。");
        if (operationalEvent.AcknowledgedAtUtc is not null)
        {
            return;
        }

        operationalEvent.AcknowledgedAtUtc = timeProvider.GetUtcNow();
        operationalEvent.AcknowledgedBy = string.IsNullOrWhiteSpace(acknowledgedBy)
            ? "management-ui"
            : acknowledgedBy[..Math.Min(acknowledgedBy.Length, 200)];
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
