using System.ComponentModel.DataAnnotations;

namespace SyncCoordinator.Demo.Crm.Models;

public sealed class WorkOrderPayload
{
    public string? WorkOrderNumber { get; set; }
    public string? CaseId { get; set; }
    public string? CaseNumber { get; set; }
    public string? CustomerName { get; set; }

    [Display(Name = "訪問先住所")]
    [Required]
    [StringLength(500)]
    public string? Address { get; set; }

    [Display(Name = "電話番号")]
    [StringLength(40)]
    public string? Phone { get; set; }

    [Display(Name = "製品名")]
    [StringLength(160)]
    public string? ProductName { get; set; }

    [Display(Name = "故障内容")]
    [StringLength(500)]
    public string? ProblemSummary { get; set; }

    [Display(Name = "訪問予定日時")]
    [StringLength(40)]
    public string? ScheduledAt { get; set; }

    [Display(Name = "担当技術者")]
    [StringLength(120)]
    public string? TechnicianName { get; set; }

    [Display(Name = "ステータス")]
    [Required]
    public string? Status { get; set; }

    [Display(Name = "作業結果")]
    [StringLength(4000)]
    public string? WorkResult { get; set; }

    [Display(Name = "完了日時")]
    [StringLength(40)]
    public string? CompletedAt { get; set; }
}
