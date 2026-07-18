using System.Text.Json.Nodes;
using SyncCoordinator.Web.Components;

namespace SyncCoordinator.Tests;

public sealed class UiFormatTests
{
    [Fact]
    public void JsonValueFormatsStringAsHumanReadableText()
    {
        var value = JsonValue.Create("冷風が出ない（お客様から再連絡）");

        var formatted = UiFormat.JsonValue(value);

        Assert.Equal("冷風が出ない（お客様から再連絡）", formatted);
        Assert.DoesNotContain("\\u", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonValueDecodesHtmlEntitiesInStringValues()
    {
        var value = JsonValue.Create("冷房 &amp; 暖房 &lt;確認済み&gt;");

        Assert.Equal("冷房 & 暖房 <確認済み>", UiFormat.JsonValue(value));
    }

    [Fact]
    public void JsonValueKeepsStructuredValuesAsJson()
    {
        var value = JsonNode.Parse("{\"status\":\"確認済み\"}");

        Assert.Equal("{\"status\":\"確認済み\"}", UiFormat.JsonValue(value));
    }

    [Fact]
    public void JsonValueFormatsDatabaseDateWithoutMidnightTime()
    {
        var value = JsonValue.Create(new DateTime(2026, 7, 23));

        Assert.Equal("2026-07-23", UiFormat.JsonValue(value));
    }

    [Fact]
    public void JsonValueFormatsNullAsJsonNull()
    {
        Assert.Equal("null", UiFormat.JsonValue(null));
    }
}
