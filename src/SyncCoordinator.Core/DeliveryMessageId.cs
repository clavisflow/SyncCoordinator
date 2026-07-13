using System.Security.Cryptography;
using System.Text;

namespace SyncCoordinator.Core;

public static class DeliveryMessageId
{
    public static Guid Create(Guid sourceMessageId, Guid routeId, string destinationSystem)
    {
        var input = Encoding.UTF8.GetBytes($"{sourceMessageId:N}|{routeId:N}|{destinationSystem}");
        var hash = SHA256.HashData(input);
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }
}
