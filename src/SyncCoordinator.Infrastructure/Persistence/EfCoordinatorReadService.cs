using Microsoft.EntityFrameworkCore;
using SyncCoordinator.Core;

namespace SyncCoordinator.Infrastructure.Persistence;

public sealed class EfCoordinatorReadService(CoordinatorDbContext dbContext) : ICoordinatorReadService
{
    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var systems = await dbContext.Systems.CountAsync(x => x.Enabled, cancellationToken);
        var routes = await dbContext.Routes.CountAsync(x => x.Enabled, cancellationToken);
        var processing = await dbContext.InboxMessages.CountAsync(
            x => x.State == InboxState.Processing,
            cancellationToken);
        var attentionConflicts = await dbContext.SyncConflicts.CountAsync(
            x => x.ResolutionState == ConflictResolutionState.AwaitingDecision ||
                 x.ResolutionState == ConflictResolutionState.Failed,
            cancellationToken);
        var valueTransformationErrors = await dbContext.InboxMessages.CountAsync(
            inbox => inbox.State == InboxState.Held &&
                     !dbContext.SyncConflicts.Any(conflict =>
                         conflict.SourceMessageId == inbox.SourceMessageId &&
                         conflict.RouteId == inbox.RouteId &&
                         conflict.DestinationSystem == inbox.DestinationSystem),
            cancellationToken);
        var failed = await dbContext.InboxMessages.CountAsync(x => x.State == InboxState.Failed, cancellationToken);
        return new DashboardSummary(
            systems, routes, processing, attentionConflicts, valueTransformationErrors, failed);
    }

    public async Task<IReadOnlyList<RouteListItem>> GetRoutesAsync(CancellationToken cancellationToken) =>
        await dbContext.Routes.AsNoTracking().OrderBy(x => x.Name).Select(x => new RouteListItem(
            x.Id,
            x.Name,
            x.SourceSystem.Code,
            x.SourceSystem.DisplayName,
            x.DestinationSystem.Code,
            x.DestinationSystem.DisplayName,
            x.Direction,
            x.DeploymentState,
            x.Enabled,
            x.ConflictScope,
            x.DefaultConflictPolicy)
        {
            OperationallyPaused = x.SourceSystem.PausedAtUtc != null ||
                                  x.DestinationSystem.PausedAtUtc != null ||
                                  x.MappingMaintenanceStartedAtUtc != null,
            MappingMaintenance = x.MappingMaintenanceStartedAtUtc != null
        }).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ConflictListItem>> GetRecentConflictsAsync(
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.SyncConflicts.AsNoTracking()
            .OrderByDescending(x => x.DetectedAtUtc)
            .Take(take)
            .Select(x => new ConflictListItem(
                x.Id,
                x.Route.Name,
                x.SourceSystem,
                dbContext.Systems.Where(system => system.Code == x.SourceSystem)
                    .Select(system => system.DisplayName).FirstOrDefault() ?? x.SourceSystem,
                x.DestinationSystem,
                dbContext.Systems.Where(system => system.Code == x.DestinationSystem)
                    .Select(system => system.DisplayName).FirstOrDefault() ?? x.DestinationSystem,
                x.EntityType,
                x.EntityId,
                x.Operation,
                x.DetectedAtUtc,
                x.ResolutionState,
                x.ResolvedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<ConflictStateCounts> GetConflictStateCountsAsync(CancellationToken cancellationToken)
    {
        var counts = await dbContext.SyncConflicts.AsNoTracking()
            .GroupBy(x => x.ResolutionState)
            .Select(group => new { State = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(x => x.State, x => x.Count, cancellationToken);

        return new ConflictStateCounts(
            counts.GetValueOrDefault(ConflictResolutionState.AwaitingDecision),
            counts.GetValueOrDefault(ConflictResolutionState.WaitingForPrevious),
            counts.GetValueOrDefault(ConflictResolutionState.Pending),
            counts.GetValueOrDefault(ConflictResolutionState.Processing),
            counts.GetValueOrDefault(ConflictResolutionState.Failed),
            counts.GetValueOrDefault(ConflictResolutionState.Resolved),
            counts.GetValueOrDefault(ConflictResolutionState.Superseded));
    }
}
