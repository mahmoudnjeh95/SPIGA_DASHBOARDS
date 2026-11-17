using Microsoft.AspNetCore.Components;
using Spiga.Core.Security;
using Spiga.UI.Services;

namespace Spiga.UI.Components;

// Base of every screen: the signed-in actor, the clock, and Run(), which
// turns a refused operation into a message instead of an error page.
public abstract class SpigaPage : ComponentBase
{
    [Inject] protected UserSession Session { get; set; } = default!;
    [Inject] protected TimeProvider Clock { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected Actor Actor => Session.Actor!;
    protected bool CanEditStationSettings => Session.Can(Perm.ParametresPoste);
    protected DateTime Now => Clock.GetLocalNow().DateTime;
    protected string? Error { get; set; }
    protected string? Info { get; set; }

    protected async Task<bool> Run(Func<Task> action, string? success = null)
    {
        Error = null;
        Info = null;
        try
        {
            await action();
            Info = success;
            return true;
        }
        catch (Exception ex) when (ex is RuleViolationException or AccessDeniedException)
        {
            Error = ex.Message;
            return false;
        }
    }
}
