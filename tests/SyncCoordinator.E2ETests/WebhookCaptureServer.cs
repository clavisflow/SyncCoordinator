using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SyncCoordinator.E2ETests;

internal sealed class WebhookCaptureServer : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly object gate = new();
    private readonly List<CapturedWebhook> requests = [];
    private TaskCompletionSource<bool> requestArrived = NewSignal();
    private int failuresRemaining;
    private int disposeStarted;

    private WebhookCaptureServer(WebApplication application, int failuresBeforeSuccess)
    {
        this.application = application;
        failuresRemaining = failuresBeforeSuccess;
    }

    public Uri Endpoint { get; private set; } = null!;

    public static async Task<WebhookCaptureServer> StartAsync(
        CancellationToken cancellationToken,
        int failuresBeforeSuccess = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var application = builder.Build();
        var server = new WebhookCaptureServer(application, failuresBeforeSuccess);

        application.MapPost("/", async context =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            TaskCompletionSource<bool> signal;
            lock (server.gate)
            {
                if (Volatile.Read(ref server.disposeStarted) != 0)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
                server.requests.Add(new CapturedWebhook(
                    body,
                    context.Request.Headers["Webhook-Id"].ToString(),
                    context.Request.Headers["Webhook-Timestamp"].ToString(),
                    context.Request.Headers["Webhook-Signature"].ToString()));
                signal = server.requestArrived;
                server.requestArrived = NewSignal();
            }
            signal.TrySetResult(true);
            context.Response.StatusCode = Interlocked.Decrement(ref server.failuresRemaining) >= 0
                ? StatusCodes.Status500InternalServerError
                : StatusCodes.Status204NoContent;
        });

        try
        {
            await application.StartAsync(cancellationToken);
            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.Single() ??
                throw new InvalidOperationException(
                    "The webhook capture server did not expose an address.");
            server.Endpoint = new Uri(address, UriKind.Absolute);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task<CapturedWebhook> WaitForAsync(
        Func<CapturedWebhook, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        while (true)
        {
            Task signal;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref disposeStarted) != 0,
                    this);
                var index = requests.FindIndex(request => predicate(request));
                if (index >= 0)
                {
                    var request = requests[index];
                    requests.RemoveAt(index);
                    return request;
                }
                signal = requestArrived.Task;
            }

            var remaining = deadline - TimeProvider.System.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"No matching webhook was received within {timeout.TotalSeconds:N0} seconds.");
            }

            try
            {
                await signal.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Recheck the queue once at the deadline boundary.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0)
        {
            return;
        }

        TaskCompletionSource<bool> signal;
        lock (gate)
        {
            signal = requestArrived;
            requestArrived = NewSignal();
        }
        signal.TrySetCanceled();

        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await application.StopAsync(stopTimeout.Token);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Could not stop the temporary webhook receiver cleanly: {exception.Message}");
        }
        finally
        {
            try
            {
                await application.DisposeAsync();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"Could not dispose the temporary webhook receiver cleanly: {exception.Message}");
            }
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed record CapturedWebhook(
    string Body,
    string WebhookId,
    string Timestamp,
    string Signature);
