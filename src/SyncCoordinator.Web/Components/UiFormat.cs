using System.Globalization;

namespace SyncCoordinator.Web.Components;

public static class UiFormat
{
    public static string LocalDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public static string LocalDateTime(DateTimeOffset? value, string emptyText) =>
        value is null ? emptyText : LocalDateTime(value.Value);
}
