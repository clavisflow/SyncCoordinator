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

    public static async Task VerifyMappingAndDatabaseSetupAsync(
        Uri webEndpoint,
        Guid routeId,
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
            "mapping-and-database-setup",
            async page =>
            {
                var pageErrors = CapturePageErrors(page);
                await LoginAndNavigateAsync(page, $"/routes/{routeId:D}/database-setup");

                var databaseSetupPage = page.GetByTestId("database-setup-page");
                await databaseSetupPage.WaitForAsync();
                var targets = page.GetByTestId("deployment-target");
                Assert.Equal(2, await targets.CountAsync());
                for (var index = 0; index < await targets.CountAsync(); index++)
                {
                    Assert.Equal(
                        "反映済み",
                        (await targets.Nth(index)
                            .GetByTestId("deployment-target-status")
                            .InnerTextAsync()).Trim());
                }

                await page.GotoAsync($"/routes/{routeId:D}/mappings");
                var mappingsPage = page.GetByTestId("mappings-page");
                await mappingsPage.WaitForAsync();

                var relatedToggle = page.GetByTestId("related-tables-toggle");
                var relatedPanels = page.GetByTestId("related-table-panel");
                await relatedPanels.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 60_000
                });
                await relatedToggle.ClickAsync();
                await relatedPanels.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Detached
                });
                await relatedToggle.ClickAsync();

                var projectionPanel = page.Locator(
                    "[data-testid='related-table-panel'][data-related-alias='case_info']");
                await projectionPanel.WaitForAsync();

                await projectionPanel.Locator(".related-table-usage .rz-dropdown").ClickAsync();
                await page.GetByRole(AriaRole.Listbox)
                    .GetByText("同期対象の判定のみ", new LocatorGetByTextOptions { Exact = true })
                    .ClickAsync();
                var usageDialog = page.Locator(".rz-dialog");
                await usageDialog.WaitForAsync();
                Assert.Contains("同期項目", await usageDialog.InnerTextAsync(), StringComparison.Ordinal);
                await usageDialog.GetByRole(
                    AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = "キャンセル", Exact = true })
                    .ClickAsync();
                Assert.True(await projectionPanel.IsVisibleAsync());

                await projectionPanel.Locator(".related-table-delete").ClickAsync();
                var deleteDialog = page.Locator(".rz-dialog");
                await deleteDialog.WaitForAsync();
                Assert.Contains("削除", await deleteDialog.InnerTextAsync(), StringComparison.Ordinal);
                await deleteDialog.GetByRole(
                    AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = "キャンセル", Exact = true })
                    .ClickAsync();
                Assert.True(await projectionPanel.IsVisibleAsync());

                const string renamedAlias = "case_info_e2e";
                var aliasInput = projectionPanel.Locator(".related-table-alias input");
                await aliasInput.FillAsync(renamedAlias);
                await aliasInput.PressAsync("Tab");
                projectionPanel = page.Locator(
                    $"[data-testid='related-table-panel'][data-related-alias='{renamedAlias}']");
                await projectionPanel.WaitForAsync();
                Assert.True(await page.Locator(
                    $"[data-testid='column-mapping-item'][data-source-column^='{renamedAlias}.']")
                    .CountAsync() > 0);

                var columnItems = page.GetByTestId("column-mapping-item");
                Assert.True(await columnItems.CountAsync() > 1);
                var originalFirst = await columnItems.Nth(0).GetAttributeAsync("data-source-column");
                var originalSecond = await columnItems.Nth(1).GetAttributeAsync("data-source-column");
                Assert.False(string.IsNullOrWhiteSpace(originalFirst));
                Assert.False(string.IsNullOrWhiteSpace(originalSecond));
                await columnItems.Nth(0).GetByRole(
                        AriaRole.Button,
                        new LocatorGetByRoleOptions { Name = "下へ移動", Exact = true })
                    .ClickAsync();
                await Assertions.Expect(page.GetByTestId("column-mapping-item").Nth(0))
                    .ToHaveAttributeAsync("data-source-column", originalSecond!);

                await page.Locator(".page-actions button").ClickAsync();
                await page.GetByText(
                        "マッピングを安全に保存し、同期を停止しました。必要に応じてDB反映・検証を行い、ルールを有効化してください。",
                        new PageGetByTextOptions { Exact = true })
                    .WaitForAsync();

                await page.ReloadAsync();
                await page.GetByTestId("mappings-page").WaitForAsync();
                await page.Locator(
                        $"[data-testid='related-table-panel'][data-related-alias='{renamedAlias}']")
                    .WaitForAsync();
                await Assertions.Expect(page.GetByTestId("column-mapping-item").Nth(0))
                    .ToHaveAttributeAsync("data-source-column", originalSecond!);
                Assert.Empty(pageErrors);
            });
    }

    private static async Task LoginAndNavigateAsync(IPage page, string path)
    {
        await page.GotoAsync(path);
        if (!string.Equals(new Uri(page.Url).AbsolutePath, "/login", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await FillStableAsync(
            page,
            (page.Locator("#login-user"), "admin"),
            (page.Locator("#login-password"), AdminPassword));
        await page.GetByTestId("login-submit").ClickAsync();
        await page.WaitForURLAsync($"**{path}");
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
