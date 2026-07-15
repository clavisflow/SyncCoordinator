using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncCoordinator.Demo.Crm.Data;
using SyncCoordinator.Demo.Crm.Models;

namespace SyncCoordinator.Demo.Crm.Pages.Cases;

public sealed class EditModel(CrmRepository repository) : PageModel
{
    [BindProperty]
    public SupportCasePayload Input { get; set; } = new();

    public string EntityId { get; private set; } = string.Empty;
    public string OriginSystem { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public string? DataError { get; private set; }
    public IReadOnlyList<string> Statuses => StatusOptions.SupportCases;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var record = await repository.GetSupportCaseAsync(id, cancellationToken);
            if (record is null)
            {
                return NotFound();
            }

            SetRecord(record);
            return Page();
        }
        catch (CrmDataException exception)
        {
            EntityId = id;
            DataError = exception.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken cancellationToken)
    {
        EntityId = id;
        if (!Statuses.Contains(Input.Status ?? string.Empty, StringComparer.Ordinal))
        {
            ModelState.AddModelError("Input.Status", "ステータスを選択してください。");
        }

        if (!ModelState.IsValid)
        {
            await LoadMetadataAsync(id, cancellationToken);
            return Page();
        }

        try
        {
            await repository.UpdateSupportCaseAsync(id, Input, cancellationToken);
            TempData["SuccessMessage"] = "問い合わせを更新しました。変更は同期キューへ送られます。";
            return RedirectToPage(new { id });
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
            await LoadMetadataAsync(id, cancellationToken);
            return Page();
        }
    }

    private async Task LoadMetadataAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var record = await repository.GetSupportCaseAsync(id, cancellationToken);
            if (record is not null)
            {
                OriginSystem = record.OriginSystem;
                UpdatedAtUtc = record.UpdatedAtUtc;
            }
        }
        catch (CrmDataException exception)
        {
            DataError ??= exception.Message;
        }
    }

    private void SetRecord(SupportCaseRecord record)
    {
        EntityId = record.EntityId;
        OriginSystem = record.OriginSystem;
        UpdatedAtUtc = record.UpdatedAtUtc;
        Input = record.Payload;
    }
}
