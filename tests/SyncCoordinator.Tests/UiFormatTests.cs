using System.Text.Json.Nodes;
using SyncCoordinator.Web.Components;

namespace SyncCoordinator.Tests;

public sealed class UiFormatTests
{
    [Fact]
    public void JsonValueKeepsJapaneseReadable()
    {
        var value = JsonValue.Create("冷風が出ない（お客様から再連絡）");

        var formatted = UiFormat.JsonValue(value);

        Assert.Equal("\"冷風が出ない（お客様から再連絡）\"", formatted);
        Assert.DoesNotContain("\\u", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonValueFormatsNullAsJsonNull()
    {
        Assert.Equal("null", UiFormat.JsonValue(null));
    }
}
