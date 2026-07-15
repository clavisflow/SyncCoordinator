using Microsoft.AspNetCore.Mvc.RazorPages;
using SyncCoordinator.Demo.Crm.Data;
using SyncCoordinator.Demo.Crm.Models;

namespace SyncCoordinator.Demo.Crm.Pages.Cases;

public sealed class IndexModel(CrmRepository repository) : PageModel
{
    public IReadOnlyList<SupportCaseRecord> Cases { get; private set; } = [];
    public string? DataError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            Cases = await repository.GetSupportCasesAsync(cancellationToken);
        }
        catch (CrmDataException exception)
        {
            DataError = exception.Message;
        }
    }
}
