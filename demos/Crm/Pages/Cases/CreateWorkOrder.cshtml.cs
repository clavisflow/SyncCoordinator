using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncCoordinator.Demo.Crm.Data;
using SyncCoordinator.Demo.Crm.Models;

namespace SyncCoordinator.Demo.Crm.Pages.Cases;

public sealed class CreateWorkOrderModel(CrmRepository repository) : PageModel
{
    [BindProperty]
    public WorkOrderPayload Input { get; set; } = new();

    public SupportCaseRecord? SupportCase { get; private set; }
    public string? DataError { get; private set; }
    public IReadOnlyList<string> Statuses => StatusOptions.WorkOrders;

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            SupportCase = await repository.GetSupportCaseAsync(id, cancellationToken);
            if (SupportCase is null)
            {
                return NotFound();
            }

            Input = new WorkOrderPayload
            {
                Address = null,
                ProblemSummary = SupportCase.Payload.Description,
                ScheduledAt = SuggestedVisitTime(SupportCase.Payload.PreferredVisitDate),
                TechnicianName = null,
                StaffNo = null,
                Status = "Draft",
                EstimatedMinutes = 60,
                EstimatedCost = 10000m,
                RequiresParts = null
            };
            return Page();
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnPostAsync(string id, CancellationToken cancellationToken)
    {
        await LoadCaseAsync(id, cancellationToken);
        if (SupportCase is null)
        {
            if (DataError is null)
            {
                return NotFound();
            }

            return Page();
        }

        if (!Statuses.Contains(Input.Status ?? string.Empty, StringComparer.Ordinal))
        {
            ModelState.AddModelError("Input.Status", "ステータスを選択してください。");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var workOrderId = await repository.CreateWorkOrderAsync(SupportCase, Input, cancellationToken);
            TempData["SuccessMessage"] = "作業指示を作成しました。";
            return RedirectToPage("/WorkOrders/Edit", new { id = workOrderId });
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
            return Page();
        }
    }

    private async Task LoadCaseAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            SupportCase = await repository.GetSupportCaseAsync(id, cancellationToken);
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
        }
    }

    private static string? SuggestedVisitTime(string? preferredVisitDate)
    {
        if (DateOnly.TryParse(
            preferredVisitDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var parsed))
        {
            return $"{parsed:yyyy-MM-dd}T10:00";
        }

        return null;
    }
}
