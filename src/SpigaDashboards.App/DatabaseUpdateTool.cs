using System.Windows;
using System.Windows.Controls;
using Microsoft.Data.SqlClient;
using Spiga.Data;
using Spiga.UI.Services;
using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App;

// "SpigaDashboards.App.exe --update-database" (start menu « Mettre à jour la
// base SPIGA »), for IT: applies the table changes of this version to the
// application database with an administrator login, so the PCs' own login
// only reads and writes (database\spiga_suivi.sql).
//
// PC-by-PC rollout: while PCs still run 2.0, the planning, note and shared
// settings stay in stats for every PC (PlantDataLocation). Once the last PC
// runs 2.1, IT runs this again and answers yes: writes pause for about
// 90 s, the rows are copied to spiga_suivi in one transaction, and every PC
// follows within 30 s. Nothing is kept of the administrator's credentials.
internal static class DatabaseUpdateTool
{
    private const string Title = "Mettre à jour la base SPIGA Suivi Production";

    public static async Task RunAsync(AppSettings settings, IDbConnectionFactory production)
    {
        AppDbOptions options;
        if (settings.Provider == DatabaseProvider.SqlServer)
        {
            if (Ask(settings) is not { } connectionString)
                return;
            options = new AppDbOptions { Provider = AppDbProvider.SqlServer, ConnectionString = connectionString };
        }
        else
        {
            options = AppDatabase.OptionsFor(settings);
        }

        var appData = AppData.ConnectionFor(options);
        PlantDataMode mode;
        try
        {
            var ran = await Task.Run(() => new Migrator(new AppDb(options)).MigrateAsync());
            await AppData.ImportLegacyAsync(production, options);
            mode = (await PlantDataLocation.ReadAsync(appData))?.Mode ?? PlantDataMode.Application;
            var report = $"{ran} mise(s) à jour appliquée(s) ; base en version {Migrator.CurrentVersion}.\n" +
                         $"Planification, note et réglages partagés : {AppData.Describe(mode)}.";
            AppLogger.Log($"Mise à jour de la base applicative par {Environment.UserName} : {report.Replace('\n', ' ')}");
            MessageBox.Show(report, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLogger.Log("Mise à jour de la base applicative impossible : " + ex);
            MessageBox.Show("La mise à jour a échoué :\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (mode == PlantDataMode.Application)
            return;
        var answer = MessageBox.Show(
            "Des postes étaient encore en version 2.0 : la planification des arrêts, la note des écrans et les réglages partagés " +
            "sont encore dans la base de production, pour tous les postes.\n\n" +
            "Tous les postes, écrans TV compris, sont-ils maintenant en version 2.1 ?\n\n" +
            "Oui : ces données passent dans la base de l'application (environ 2 minutes ; les enregistrements sont " +
            "suspendus pendant ce temps, les écrans continuent d'afficher). Un poste resté en 2.0 ne les verrait plus.\n" +
            "Non : à faire plus tard, quand le dernier poste sera à jour.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

        var progressText = new TextBlock { Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap, Text = "Préparation…" };
        var progress = new Window
        {
            Title = Title,
            Content = progressText,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        progress.Show();
        try
        {
            var copied = await PlantDataLocation.SwitchToApplicationAsync(appData, production, Environment.UserName,
                text => progressText.Text = text);
            progress.Close();
            AppLogger.Log("Données de planification transférées dans la base de l'application : " + string.Join(" | ", copied));
            MessageBox.Show("Transfert terminé : les postes utilisent maintenant la base de l'application.\n\n" + string.Join("\n", copied) +
                            "\n\nLe compte des postes peut maintenant être en lecture seule sur la base de production (fin de stiavelli_compat.sql).",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            progress.Close();
            AppLogger.Log("Transfert des données de planification impossible : " + ex);
            MessageBox.Show("Le transfert a échoué ; rien n'a changé pour les postes (les données restent dans la base de production) :\n" +
                            ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // The application database on the PCs' server, with the administrator's
    // Windows account (default) or a SQL login; null when cancelled.
    private static string? Ask(AppSettings settings)
    {
        var host = new TextBox { Text = settings.SqlServer.Host, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        var database = new TextBox { Text = settings.AppDatabaseName, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
        var windows = new RadioButton { Content = "Mon compte Windows (administrateur SQL)", IsChecked = true, Margin = new Thickness(0, 0, 0, 4) };
        var sqlLogin = new RadioButton { Content = "Login SQL", Margin = new Thickness(0, 0, 0, 6) };
        var user = new TextBox { Margin = new Thickness(0, 4, 0, 6), Padding = new Thickness(6, 4, 6, 4), IsEnabled = false };
        var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 14), Padding = new Thickness(6, 4, 6, 4), IsEnabled = false };
        sqlLogin.Checked += (_, _) => user.IsEnabled = password.IsEnabled = true;
        windows.Checked += (_, _) => user.IsEnabled = password.IsEnabled = false;

        var ok = new Button { Content = "Mettre à jour", IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, Padding = new Thickness(14, 6, 14, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Applique les changements de tables de cette version à la base de l'application. Pour l'informatique : " +
                   "un compte pouvant modifier les tables de cette base est nécessaire ; il n'est pas enregistré.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });
        panel.Children.Add(new TextBlock { Text = "Serveur SQL" });
        panel.Children.Add(host);
        panel.Children.Add(new TextBlock { Text = "Base de l'application" });
        panel.Children.Add(database);
        panel.Children.Add(windows);
        panel.Children.Add(sqlLogin);
        panel.Children.Add(new TextBlock { Text = "Login" });
        panel.Children.Add(user);
        panel.Children.Add(new TextBlock { Text = "Mot de passe" });
        panel.Children.Add(password);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Title,
            Content = panel,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(database.Text))
            return null;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = host.Text.Trim(),
            InitialCatalog = database.Text.Trim(),
            ConnectTimeout = 15,
            CommandTimeout = 600,
            ApplicationName = "SpigaSuivi-MiseAJour",
            TrustServerCertificate = true,
        };
        if (windows.IsChecked == true)
            builder.IntegratedSecurity = true;
        else
        {
            builder.UserID = user.Text.Trim();
            builder.Password = password.Password;
        }
        return builder.ConnectionString;
    }
}
