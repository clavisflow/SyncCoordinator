using System.Net;
using Microsoft.AspNetCore.Http;
using SyncCoordinator.Web.Authentication;

namespace SyncCoordinator.Tests;

public sealed class AuthenticationTests
{
    [Theory]
    [InlineData("short", "short", "password_too_short")]
    [InlineData("abcdefghijkl", "abcdefghijkm", "password_mismatch")]
    public void NewPasswordValidationRejectsInvalidInput(
        string password,
        string confirmation,
        string expectedError)
    {
        var result = AdminAccountService.ValidateNewPassword(password, confirmation);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedError, result.ErrorCode);
    }

    [Fact]
    public void NewPasswordValidationAcceptsTwelveCharacters()
    {
        var result = AdminAccountService.ValidateNewPassword("abcdefghijkl", "abcdefghijkl");

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", false)]
    public void LocalRecoveryOnlyAcceptsLoopbackAddress(string address, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Request.Host = new HostString("localhost");

        Assert.Equal(expected, LocalRequestGuard.IsLocal(context));
    }

    [Fact]
    public void LocalRecoveryRejectsLoopbackProxyForNonLocalHost()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString("sync.internal.example");

        Assert.False(LocalRequestGuard.IsLocal(context));
    }
}
