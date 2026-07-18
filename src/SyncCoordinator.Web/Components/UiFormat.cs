using System.Globalization;
using System.Net;
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

    public static string JsonValue(JsonNode? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<string>(out var text))
            {
                return WebUtility.HtmlDecode(text);
            }
            if (scalar.TryGetValue<DateOnly>(out var date))
            {
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (scalar.TryGetValue<DateTime>(out var dateTime) && dateTime.TimeOfDay == TimeSpan.Zero)
            {
                return dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        return value.ToJsonString(DisplayJsonOptions);
    }
}
