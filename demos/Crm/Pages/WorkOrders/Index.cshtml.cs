using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncCoordinator.Demo.Crm.Data;
using SyncCoordinator.Demo.Crm.Models;

namespace SyncCoordinator.Demo.Crm.Pages.WorkOrders;

public sealed class IndexModel(CrmRepository repository) : PageModel
{
    public IReadOnlyList<WorkOrderRecord> WorkOrders { get; private set; } = [];
    public string? DataError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            WorkOrders = await repository.GetWorkOrdersAsync(cancellationToken);
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
        }
    }
}
