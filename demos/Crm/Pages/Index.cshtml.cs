using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncCoordinator.Demo.Crm.Data;
using SyncCoordinator.Demo.Crm.Models;

namespace SyncCoordinator.Demo.Crm.Pages;

public sealed class IndexModel(CrmRepository repository) : PageModel
{
    public DashboardSummary Summary { get; private set; } = new(0, 0, 0, 0);
    public string? DataError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Summary = await repository.GetDashboardAsync(cancellationToken);
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
        }
    }
}
