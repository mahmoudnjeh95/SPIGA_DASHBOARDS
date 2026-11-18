using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Dapper;
using Spiga.Core.Security;
using Spiga.Data;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// Kiosk / TV PCs have no sign-in, but some of their actions change what
// every screen shows: their Paramètres (database connection, rates, lines
// shown) and the note of the screens. They are done only after signing in
// with an account holding the right over the whole plant, checked like a
// sign-in (lockout, audit trail), and written under that account.
//
// When the application database cannot be reached there is no account to
// check. Paramètres are where the connection is repaired: they then open
// only for a Windows administrator (the application started with « Exécuter
// en tant qu'administrateur »). The note cannot be saved anyway.
internal static class KioskSignIn
{
    public static Task<ChangeAuthor?> ForSettingsAsync(Window? owner, AppSettings settings)
        => AskAsync(owner, settings, Perm.ParametresPoste, "Paramètres du poste",
            "Les paramètres du poste modifient l'écran pour tous. Connectez-vous avec un compte SPIGA Suivi Production " +
            "ayant le droit « Modifier les paramètres du poste » sur toute l'usine.",
            "Ouvrir les paramètres", windowsAdministratorFallback: true);

    public static Task<ChangeAuthor?> ForNoteAsync(Window? owner, AppSettings settings)
        => AskAsync(owner, settings, Perm.NoteEcran, "Note de l'écran",
            "La note s'affiche sur tous les écrans du site. Connectez-vous avec un compte SPIGA Suivi Production " +
            "ayant le droit « Modifier la note de l'écran » sur toute l'usine.",
            "Enregistrer la note", windowsAdministratorFallback: false);

    private static async Task<ChangeAuthor?> AskAsync(Window? owner, AppSettings settings, string permission, string title,
        string explanation, string confirm, bool windowsAdministratorFallback)
    {
        var appDb = AppDatabase.OptionsFor(settings);
        if (!await ReachableAsync(appDb))
        {
            if (!windowsAdministratorFallback)
            {
                Say(owner, title, "La base SPIGA Suivi Production est injoignable : rien ne peut être enregistré pour l'instant.",
                    MessageBoxImage.Warning);
                return null;
            }
            if (IsWindowsAdministrator())
            {
                AppLogger.Log($"Paramètres ouverts sans compte par l'administrateur Windows {Environment.UserName} (base applicative injoignable).");
                return new ChangeAuthor(Environment.UserName, "Administrateur Windows (base applicative injoignable)", Environment.MachineName);
            }
            Say(owner, title,
                "La base SPIGA Suivi Production est injoignable : les comptes ne peuvent pas être vérifiés.\n\n" +
                "Pour réparer la connexion, relancez l'application en tant qu'administrateur Windows " +
                "(clic droit › Exécuter en tant qu'administrateur), puis ouvrez Paramètres.",
                MessageBoxImage.Warning);
            return null;
        }

        if (Ask(owner, title, explanation, confirm) is not { } credentials)
            return null;

        var db = new AppDb(appDb);
        var auth = new AuthService(new UserStore(db), new RoleStore(db), new SettingsStore(db), new AuditStore(db),
            TimeProvider.System, new ClientInfo());
        LoginResult result;
        try
        {
            result = await Task.Run(() => auth.LoginAsync(credentials.Login, credentials.Password));
        }
        catch (Exception ex)
        {
            AppLogger.Log("Vérification du compte pour les Paramètres impossible : " + ex);
            Say(owner, title, "Le compte n'a pas pu être vérifié : " + ex.Message, MessageBoxImage.Error);
            return null;
        }

        string? refusal = result.Status switch
        {
            LoginStatus.Success when result.MustChangePassword =>
                "Ce compte a un mot de passe temporaire : changez-le d'abord dans SPIGA Suivi Production.",
            LoginStatus.Success when !result.Actor!.CanEverywhere(permission) =>
                $"Le compte {result.Actor.Login} n'a pas le droit « {PermissionCatalog.Label(permission)} » sur toute l'usine.",
            LoginStatus.Success => null,
            _ => string.IsNullOrEmpty(result.Message) ? "Identifiant ou mot de passe incorrect." : result.Message,
        };
        if (refusal != null)
        {
            Say(owner, title, refusal, MessageBoxImage.Warning);
            return null;
        }
        var actor = result.Actor!;
        AppLogger.Log($"{title} : {actor.Login} connecté sur l'écran.");
        return ChangeAuthor.Account(actor.Login, actor.DisplayName);
    }

    private static void Say(Window? owner, string title, string text, MessageBoxImage icon)
    {
        if (owner != null)
            MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
        else
            MessageBox.Show(text, title, MessageBoxButton.OK, icon);
    }

    private static async Task<bool> ReachableAsync(AppDbOptions options)
    {
        try
        {
            return await Task.Run(async () =>
            {
                using var connection = AppData.ConnectionFor(options).CreateConnection();
                await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM Users");
                return true;
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Base applicative injoignable pour vérifier le compte des Paramètres : " + ex.Message);
            return false;
        }
    }

    private static bool IsWindowsAdministrator()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Identifier and password; null when cancelled.
    private static (string Login, string Password)? Ask(Window? owner, string title, string explanation, string confirm)
    {
        var login = new TextBox { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(6, 4, 6, 4) };
        var ok = new Button { Content = confirm, IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = explanation,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });
        panel.Children.Add(new TextBlock { Text = "Identifiant" });
        panel.Children.Add(login);
        panel.Children.Add(new TextBlock { Text = "Mot de passe" });
        panel.Children.Add(password);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Content = panel,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => login.Focus();
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(login.Text))
            return null;
        return (login.Text.Trim(), password.Password);
    }
}
