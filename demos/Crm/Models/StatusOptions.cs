namespace SyncCoordinator.Demo.Crm.Models;

public static class StatusOptions
{
    public static readonly IReadOnlyList<string> SupportCases =
        ["New", "Received", "Scheduled", "InProgress", "Completed", "Cancelled"];

    public static readonly IReadOnlyList<string> WorkOrders =
        ["Draft", "Assigned", "Scheduled", "InProgress", "Completed", "Cancelled"];

    public static string Label(string? status) => status switch
    {
        "New" => "新規",
        "Received" => "受付済み",
        "Draft" => "下書き",
        "Assigned" => "担当割当済み",
        "Scheduled" => "訪問予定",
        "InProgress" => "対応中",
        "Completed" => "完了",
        "Cancelled" => "キャンセル",
        _ => status ?? "未設定"
    };
}
