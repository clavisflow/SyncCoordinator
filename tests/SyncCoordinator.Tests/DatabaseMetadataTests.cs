using Microsoft.Extensions.Logging.Abstractions;
using SyncCoordinator.Core;
using SyncCoordinator.Infrastructure.Persistence;
using SyncCoordinator.Web.Components;

namespace SyncCoordinator.Tests;

public sealed class DatabaseMetadataTests
{
    [Fact]
    public void MySqlColumnMetadataTypesAreConvertedExplicitly()
    {
        var column = DatabaseMetadataService.MaterializeColumnInfo(
            "CustomerId",
            "int",
            1,
            (uint)3,
            0);

        Assert.True(column.IsNullable);
        Assert.Equal(3, column.Ordinal);
        Assert.False(column.IsPrimaryKey);
    }

    [Fact]
    public async Task UiErrorsAreRecordedAsApplicationEvents()
    {
        var recorder = new RecordingOperationalEventRecorder();
        var reporter = new UiErrorReporter(
            recorder,
            NullLogger<UiErrorReporter>.Instance);

        await reporter.ReportAsync(new InvalidOperationException("broken"), "Mappings.LoadColumns");

        var input = Assert.Single(recorder.Events);
        Assert.Equal(OperationalEventCategories.Application, input.Category);
        Assert.Equal(OperationalEventCodes.ApplicationUiOperationFailed, input.Code);
        Assert.Equal("Mappings.LoadColumns", input.Target);
        Assert.Contains("InvalidOperationException", input.Details, StringComparison.Ordinal);
    }

    private sealed class RecordingOperationalEventRecorder : IOperationalEventRecorder
    {
        public List<OperationalEventInput> Events { get; } = [];

        public Task RecordAsync(OperationalEventInput input, CancellationToken cancellationToken)
        {
            Events.Add(input);
            return Task.CompletedTask;
        }
    }
}
