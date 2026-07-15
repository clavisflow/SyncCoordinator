using System.ComponentModel.DataAnnotations;

namespace SyncCoordinator.Demo.Crm.Models;

public sealed class SupportCasePayload
{
    public string? CaseNumber { get; set; }

    [Display(Name = "お客様名")]
    [StringLength(100)]
    public string? CustomerName { get; set; }

    [Display(Name = "メールアドレス")]
    [EmailAddress]
    [StringLength(200)]
    public string? Email { get; set; }

    [Display(Name = "電話番号")]
    [StringLength(30)]
    public string? Phone { get; set; }

    [Display(Name = "製品名")]
    [StringLength(150)]
    public string? ProductName { get; set; }

    [Display(Name = "製造番号")]
    [StringLength(100)]
    public string? SerialNumber { get; set; }

    [Display(Name = "件名")]
    [StringLength(200)]
    public string? Subject { get; set; }

    [Display(Name = "お問い合わせ内容")]
    [StringLength(4000)]
    public string? Description { get; set; }

    [Display(Name = "訪問希望日")]
    [StringLength(40)]
    public string? PreferredVisitDate { get; set; }

    [Display(Name = "ステータス")]
    [Required]
    public string? Status { get; set; }

    [Display(Name = "お客様への回答")]
    [StringLength(4000)]
    public string? ResponseMessage { get; set; }
}
