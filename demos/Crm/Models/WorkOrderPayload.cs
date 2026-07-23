using System.ComponentModel.DataAnnotations;

namespace SyncCoordinator.Demo.Crm.Models;

public sealed class WorkOrderPayload
{
    public string? WorkOrderNumber { get; set; }

    [Display(Name = "受付番号")]
    public string? CaseRef { get; set; }

    public string? CustomerName { get; set; }
    public string? Phone { get; set; }
    public string? ProductName { get; set; }

    [Display(Name = "訪問先住所")]
    [Required]
    [StringLength(500)]
    public string? Address { get; set; }

    [Display(Name = "故障内容")]
    [StringLength(500)]
    public string? ProblemSummary { get; set; }

    [Display(Name = "訪問予定日時")]
    [StringLength(40)]
    public string? ScheduledAt { get; set; }

    [Display(Name = "担当技術者")]
    [StringLength(120)]
    public string? TechnicianName { get; set; }

    [Display(Name = "スタッフNo（同期条件）")]
    [StringLength(32)]
    public string? StaffNo { get; set; }

    public string? AssignedStaffNumbers { get; set; }

    [Display(Name = "ステータス")]
    [Required]
    public string? Status { get; set; }

    [Display(Name = "作業結果")]
    [StringLength(4000)]
    public string? WorkResult { get; set; }

    [Display(Name = "完了日時")]
    [StringLength(40)]
    public string? CompletedAt { get; set; }

    [Display(Name = "見積作業時間（分）")]
    [Range(0, int.MaxValue)]
    public int? EstimatedMinutes { get; set; }

    [Display(Name = "見積金額")]
    [Range(typeof(decimal), "0", "99999999.9999")]
    public decimal? EstimatedCost { get; set; }

    [Display(Name = "部品が必要")]
    public bool? RequiresParts { get; set; }

    [Display(Name = "作業メモ")]
    [StringLength(1000)]
    public string? WorkNote { get; set; }

    [Display(Name = "外部追跡ID")]
    public Guid? ExternalTrackingId { get; set; }
}
