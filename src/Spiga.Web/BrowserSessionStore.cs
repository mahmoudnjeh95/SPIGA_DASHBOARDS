using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Spiga.UI.Services;

namespace Spiga.Web;

// The tab's sessionStorage, encrypted and signed by ASP.NET Data Protection:
// a reload keeps the sign-in, closing the tab ends it.
public sealed class BrowserSessionStore(ProtectedSessionStorage storage) : ISessionStore
{
    private const string Key = "spiga.session";

    private sealed record Stored(int UserId, DateTime LastActivity);

    public async Task SaveAsync(int userId, DateTime lastActivity)
    {
        try { await storage.SetAsync(Key, new Stored(userId, lastActivity)); }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.JSInterop.JSException or TaskCanceledException) { }
    }

    public async Task<(int UserId, DateTime LastActivity)?> LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<Stored>(Key);
            return result.Success && result.Value != null ? (result.Value.UserId, result.Value.LastActivity) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.JSInterop.JSException or System.Security.Cryptography.CryptographicException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task ClearAsync()
    {
        try { await storage.DeleteAsync(Key); }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.JSInterop.JSException or TaskCanceledException) { }
    }
}
