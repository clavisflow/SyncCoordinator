using System.Net;

namespace SyncCoordinator.Web.Authentication;

public static class LocalRequestGuard
{
    public static bool IsLocal(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress?.IsIPv4MappedToIPv6 == true)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return false;
        }

        var host = context.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress);
    }
}
