using System.Text.Json.Nodes;
using SyncCoordinator.Core;

namespace SyncCoordinator.Tests;

public sealed class ValueTransformEngineTests
{
    [Fact]
    public void RejectsStringOverflowByDefault()
    {
        var exception = Assert.Throws<ValueTransformationException>(() => ValueTransformEngine.Transform(
            JsonValue.Create("123456"), new ValueTransformInput(),
            new ColumnValueContract("varchar", true, 5, null, null), "Name", "short_name"));

        Assert.Equal("string-overflow", exception.ReasonCode);
    }

    [Fact]
    public void TruncatesWithoutSplittingSurrogatePairWhenExplicitlyConfigured()
    {
        var result = ValueTransformEngine.Transform(
            JsonValue.Create("1234😀"),
            new ValueTransformInput { StringOverflow = StringOverflowBehavior.Truncate },
            new ColumnValueContract("nvarchar", true, 5, null, null), "Name", "short_name");

        Assert.Equal("1234", result!.GetValue<string>());
    }

    [Fact]
    public void AppliesExactCodeMapAndRejectsUnknownCodes()
    {
        var transform = new ValueTransformInput
        {
            RejectUnmappedValues = true,
            ValueMap = [new ValueMapEntryInput { SourceValue = "Completed", TargetValue = "done" }]
        };

        var mapped = ValueTransformEngine.Transform(
            JsonValue.Create("Completed"), transform,
            new ColumnValueContract("varchar", false, 20, null, null), "Status", "job_status");
        Assert.Equal("done", mapped!.GetValue<string>());
        Assert.Throws<ValueTransformationException>(() => ValueTransformEngine.Transform(
            JsonValue.Create("Unknown"), transform,
            new ColumnValueContract("varchar", false, 20, null, null), "Status", "job_status"));
    }

    [Fact]
    public void RoundsNumericScaleOnlyWhenExplicitlyConfigured()
    {
        var contract = new ColumnValueContract("decimal", false, null, 6, 2);
        Assert.Throws<ValueTransformationException>(() => ValueTransformEngine.Transform(
            JsonValue.Create(12.345m), new ValueTransformInput(), contract, "Price", "price"));

        var rounded = ValueTransformEngine.Transform(
            JsonValue.Create(12.345m),
            new ValueTransformInput { NumericScale = NumericScaleBehavior.Round },
            contract, "Price", "price");
        Assert.Equal(12.35m, rounded!.GetValue<decimal>());
    }

    [Fact]
    public void DecimalWithScaleEqualToPrecisionAcceptsValuesBelowOne()
    {
        var result = ValueTransformEngine.Transform(
            JsonValue.Create(0.12m), new ValueTransformInput(),
            new ColumnValueContract("decimal", false, null, 2, 2), "Rate", "rate");

        Assert.Equal(0.12m, result!.GetValue<decimal>());
    }
}
