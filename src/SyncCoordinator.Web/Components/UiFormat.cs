using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SyncCoordinator.Web.Components;

public static class UiFormat
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string LocalDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string LocalDateTime(DateTimeOffset? value, string emptyText) =>
        value is null ? emptyText : LocalDateTime(value.Value);

    public static string JsonValue(JsonNode? value) =>
        value?.ToJsonString(DisplayJsonOptions) ?? "null";
}
