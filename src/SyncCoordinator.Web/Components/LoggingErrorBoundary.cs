using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;

namespace SyncCoordinator.Web.Components;

public sealed class LoggingErrorBoundary : ErrorBoundary, IDisposable
{
    [Inject]
    private UiErrorReporter ErrorReporter { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    protected override void OnInitialized()
    {
        Navigation.LocationChanged += OnLocationChanged;
    }

    protected override Task OnErrorAsync(Exception exception) =>
        ErrorReporter.ReportAsync(
            exception,
            $"UnhandledComponent:{Navigation.ToBaseRelativePath(Navigation.Uri)}");

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args) =>
        _ = InvokeAsync(Recover);

    public void Dispose()
    {
        Navigation.LocationChanged -= OnLocationChanged;
    }
}
