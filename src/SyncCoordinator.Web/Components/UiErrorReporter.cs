using Microsoft.Extensions.Logging;
using SyncCoordinator.Core;

namespace SyncCoordinator.Web.Components;

public sealed partial class UiErrorReporter(
    IOperationalEventRecorder operationalEvents,
    ILogger<UiErrorReporter> logger)
{
    public async Task ReportAsync(Exception exception, string operation)
    {
        OperationFailed(logger, exception, operation);
        await operationalEvents.RecordAsync(new OperationalEventInput(
            OperationalEventSeverity.Error,
            OperationalEventCategories.Application,
            OperationalEventCodes.ApplicationUiOperationFailed,
            "web",
            operation,
            exception.ToString()), CancellationToken.None);
    }

    [LoggerMessage(LogLevel.Error, "Management UI operation failed: {operation}")]
    private static partial void OperationFailed(
        ILogger logger,
        Exception exception,
        string operation);
}
