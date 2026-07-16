using Microsoft.Playwright;

namespace SyncCoordinator.E2ETests;

internal static class BrowserAuthenticationSmoke
{
    private const string AdminPassword = "E2E-Admin-Password-2026!";

    public static async Task VerifyAsync(
        Uri webEndpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var playwright = await WaitWithDelayedCleanupAsync(
            Playwright.CreateAsync(),
            static value =>
            {
                value.Dispose();
                return ValueTask.CompletedTask;
            },
            "Playwright driver creation",
            cancellationToken);
        await using var browser = await WaitWithDelayedCleanupAsync(
            playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = 30_000
            }),
            static value => value.DisposeAsync(),
            "Chromium launch",
            cancellationToken);
        using var cancellationRegistration = cancellationToken.Register(
            () => _ = CloseBrowserAfterCancellationAsync(browser));

        await RunWithDiagnosticsAsync(
            browser,
            webEndpoint,
            "admin-setup",
            async page =>
            {
                var pageErrors = CapturePageErrors(page);

                await page.GotoAsync("/");
                await page.WaitForURLAsync("**/account/setup");
                Assert.True(await page.GetByTestId("admin-setup-page").IsVisibleAsync());

                await FillStableAsync(
                    page,
                    (page.Locator("#setup-password"), AdminPassword),
                    (page.Locator("#setup-confirmation"), AdminPassword));
                await page.GetByTestId("admin-setup-submit").ClickAsync();
                await page.WaitForURLAsync("**/");
                Assert.Equal("/", new Uri(page.Url).AbsolutePath);
                Assert.True(await page.GetByTestId("dashboard-page").IsVisibleAsync());
                Assert.Empty(pageErrors);
            });

        await RunWithDiagnosticsAsync(
            browser,
            webEndpoint,
            "admin-login",
            async page =>
            {
                var pageErrors = CapturePageErrors(page);

                await page.GotoAsync("/operations");
                var loginPage = page.GetByTestId("login-page");
                await loginPage.WaitForAsync();
                Assert.True(await loginPage.IsVisibleAsync());
                var loginUri = new Uri(page.Url);
                Assert.Equal("/login", loginUri.AbsolutePath);
                Assert.Contains("returnUrl=", loginUri.Query, StringComparison.OrdinalIgnoreCase);

                await FillStableAsync(
                    page,
                    (page.Locator("#login-user"), "admin"),
                    (page.Locator("#login-password"), AdminPassword));
                await page.GetByTestId("login-submit").ClickAsync();
                await page.WaitForURLAsync("**/operations");
                Assert.Equal("/operations", new Uri(page.Url).AbsolutePath);

                var operationsPage = page.GetByTestId("operations-page");
                Assert.True(await operationsPage.IsVisibleAsync());
                Assert.Equal(
                    "false",
                    (await operationsPage.GetAttributeAsync("aria-busy"))?.ToLowerInvariant());
                Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
                Assert.Empty(pageErrors);
            });
    }

    private static async Task RunWithDiagnosticsAsync(
        IBrowser browser,
        Uri webEndpoint,
        string phase,
        Func<IPage, Task> scenario)
    {
        var context = await CreateContextAsync(browser, webEndpoint);
        IPage? page = null;
        var tracingStarted = false;
        Exception? primaryFailure = null;
        try
        {
            await context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true,
                Title = phase
            });
            tracingStarted = true;
            page = await context.NewPageAsync();
            await scenario(page);
            await context.Tracing.StopAsync();
            tracingStarted = false;
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            var artifactDirectory = TryCreateArtifactDirectory(phase, exception);
            if (artifactDirectory is not null)
            {
                if (page is not null)
                {
                    await TrySecondaryAsync(
                        () => page.ScreenshotAsync(new PageScreenshotOptions
                        {
                            Path = Path.Combine(artifactDirectory, $"{phase}.png"),
                            FullPage = true,
                            Timeout = 5_000
                        }),
                        exception,
                        "screenshot");
                }
                if (tracingStarted)
                {
                    var traceSaved = await TrySecondaryAsync(
                        () => context.Tracing.StopAsync(new TracingStopOptions
                        {
                            Path = Path.Combine(artifactDirectory, $"{phase}-trace.zip")
                        }),
                        exception,
                        "trace");
                    tracingStarted = !traceSaved;
                }

                exception.Data["PlaywrightArtifacts"] = artifactDirectory;
                Console.Error.WriteLine($"Playwright failure artifacts: {artifactDirectory}");
            }
            throw;
        }
        finally
        {
            if (tracingStarted)
            {
                await TrySecondaryAsync(
                    () => context.Tracing.StopAsync(),
                    primaryFailure,
                    "trace cleanup");
            }
            try
            {
                await context.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception cleanupException) when (primaryFailure is not null)
            {
                ReportSecondaryFailure(primaryFailure, "context cleanup", cleanupException);
            }
        }
    }

    private static async Task<IBrowserContext> CreateContextAsync(
        IBrowser browser,
        Uri webEndpoint)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = webEndpoint.AbsoluteUri,
            Locale = "ja-JP",
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });
        context.SetDefaultTimeout(30_000);
        context.SetDefaultNavigationTimeout(30_000);
        return context;
    }

    private static List<string> CapturePageErrors(IPage page)
    {
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        return errors;
    }

    private static async Task FillStableAsync(
        IPage page,
        params (ILocator Locator, string Value)[] fields)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            foreach (var (locator, value) in fields)
            {
                await locator.FillAsync(value);
            }

            await page.WaitForTimeoutAsync(500);
            var stable = true;
            foreach (var (locator, value) in fields)
            {
                stable &= string.Equals(
                    await locator.InputValueAsync(),
                    value,
                    StringComparison.Ordinal);
            }
            if (stable)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "The authentication form was replaced before its input values became stable.");
    }

    private static async Task<T> WaitWithDelayedCleanupAsync<T>(
        Task<T> operation,
        Func<T, ValueTask> cleanup,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAndCleanupAsync(operation, cleanup, operationName);
            throw;
        }
    }

    private static async Task ObserveAndCleanupAsync<T>(
        Task<T> operation,
        Func<T, ValueTask> cleanup,
        string operationName)
    {
        try
        {
            var value = await operation;
            await cleanup(value);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Delayed {operationName} cleanup failed: {exception.Message}");
        }
    }

    private static async Task CloseBrowserAfterCancellationAsync(IBrowser browser)
    {
        try
        {
            await browser.CloseAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Could not close Chromium after E2E cancellation: {exception.Message}");
        }
    }

    private static string? TryCreateArtifactDirectory(
        string phase,
        Exception primaryFailure)
    {
        try
        {
            var configuredRoot = Environment.GetEnvironmentVariable(
                "SYNC_COORDINATOR_E2E_ARTIFACTS");
            var root = string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(Path.GetTempPath(), "SyncCoordinator.E2E", "artifacts")
                : Path.GetFullPath(configuredRoot);
            var directory = Path.Combine(
                root,
                $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{phase}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch (Exception exception)
        {
            ReportSecondaryFailure(primaryFailure, "artifact directory", exception);
            return null;
        }
    }

    private static async Task<bool> TrySecondaryAsync(
        Func<Task> operation,
        Exception? primaryFailure,
        string operationName)
    {
        try
        {
            await operation().WaitAsync(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception exception)
        {
            if (primaryFailure is not null)
            {
                ReportSecondaryFailure(primaryFailure, operationName, exception);
            }
            else
            {
                Console.Error.WriteLine(
                    $"Playwright {operationName} failed: {exception.Message}");
            }
            return false;
        }
    }

    private static void ReportSecondaryFailure(
        Exception primaryFailure,
        string operationName,
        Exception secondaryFailure)
    {
        var key = $"Playwright.{operationName}";
        primaryFailure.Data[key] = secondaryFailure.ToString();
        Console.Error.WriteLine(
            $"Playwright {operationName} failed while handling another failure: " +
            secondaryFailure.Message);
    }
}
