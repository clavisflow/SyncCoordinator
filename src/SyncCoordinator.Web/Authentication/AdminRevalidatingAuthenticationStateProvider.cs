using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace SyncCoordinator.Web.Authentication;

public sealed class AdminRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        var user = authenticationState.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var userName = user.Identity.Name;
        var versionValue = user.FindFirst(AdminAccountService.SessionVersionClaim)?.Value;
        if (string.IsNullOrWhiteSpace(userName) ||
            !int.TryParse(versionValue, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<AdminAccountService>()
            .IsSessionCurrentAsync(userName, version, cancellationToken);
    }
}
