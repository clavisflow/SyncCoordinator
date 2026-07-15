using Radzen;

namespace SyncCoordinator.Web.Components;

internal static class NotificationServiceExtensions
{
    public static void ShowSuccess(
        this NotificationService service,
        string detail,
        string summary = "完了") =>
        service.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Success,
            Summary = summary,
            Detail = detail,
            Duration = 5000,
            CloseOnClick = true,
            ShowProgress = true
        });

    public static void ShowError(
        this NotificationService service,
        IEnumerable<string> errors,
        string summary = "処理に失敗しました")
    {
        var detail = string.Join(" / ", errors.Where(x => !string.IsNullOrWhiteSpace(x)));
        service.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Error,
            Summary = summary,
            Detail = string.IsNullOrWhiteSpace(detail) ? "エラーが発生しました。" : detail,
            Duration = 10000,
            CloseOnClick = true,
            ShowProgress = true
        });
    }

    public static void ShowWarning(
        this NotificationService service,
        string detail,
        string summary = "確認が必要です") =>
        service.Notify(new NotificationMessage
        {
            Severity = NotificationSeverity.Warning,
            Summary = summary,
            Detail = detail,
            Duration = 10000,
            CloseOnClick = true,
            ShowProgress = true
        });
}
