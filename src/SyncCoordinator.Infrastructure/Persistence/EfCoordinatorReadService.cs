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
        var held = await dbContext.InboxMessages.CountAsync(x => x.State == InboxState.Held, cancellationToken);
        var failed = await dbContext.InboxMessages.CountAsync(x => x.State == InboxState.Failed, cancellationToken);
        var conflicts = await dbContext.SyncConflicts.CountAsync(x => x.ResolvedAtUtc == null, cancellationToken);
        return new DashboardSummary(systems, routes, processing, held, failed, conflicts);
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
                x.DetectedAtUtc,
                x.ResolvedAtUtc != null))
            .ToListAsync(cancellationToken);
}
