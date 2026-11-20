using Dapper;
using Spiga.Data;

namespace Spiga.UI.Services;

public enum SourceState
{
    Connected,
    Manual,
    NotConfigured,
    Error,
}

public sealed record SourceStatus(string Name, string Feeds, SourceState State, string Detail, DateTime? LastData);

// State of every data source of cahier des charges §4.10. The sources the
// client has not opened yet are listed as such, never shown as zero.
public sealed class SourceStatusService(PlantData plant, AppDb appDb, TimeProvider clock)
{
    private const string AwaitingAccess = "Accès en lecture seule à fournir par le client (cahier des charges §7).";

    public async Task<IReadOnlyList<SourceStatus>> CheckAsync()
    {
        var list = new List<SourceStatus> { await ProductionAsync(), await AppDatabaseAsync() };
        list.Add(new SourceStatus("Banderoleuse", "Quantité de produit fini", SourceState.NotConfigured, AwaitingAccess, null));
        list.Add(new SourceStatus("Base Qualité", "Humidité par produit et ligne", SourceState.NotConfigured, AwaitingAccess, null));
        list.Add(new SourceStatus("GMAO / maintenance", "Pièces, consommables, OT, préventif", SourceState.NotConfigured, AwaitingAccess, null));
        list.Add(new SourceStatus("Compteurs énergie et vapeur", "kWh / t, vapeur / t", SourceState.NotConfigured,
            "Compteurs à identifier ; à défaut, relevés manuels (module Saisie).", null));
        return list;
    }

    private async Task<SourceStatus> ProductionAsync()
    {
        const string name = "Contrôleuses de poids (Stiavelli)";
        const string feeds = "TRS, poids, production, état des machines";
        try
        {
            plant.ForgetMachines();
            var machines = await plant.GetMachinesAsync();
            DateTime? last = null;
            foreach (var machine in machines)
            {
                var state = await plant.StateAsync(machine.Id);
                if (state?.LastData is { } t && (last == null || t > last))
                    last = t;
            }
            var stale = last == null || clock.GetLocalNow().DateTime - last > TimeSpan.FromHours(2);
            return new SourceStatus(name, feeds, SourceState.Connected,
                $"{machines.Count} machines, lecture seule{(stale ? " · aucune donnée récente" : "")}.", last);
        }
        catch (Exception ex)
        {
            return new SourceStatus(name, feeds, SourceState.Error, ex.Message, null);
        }
    }

    private async Task<SourceStatus> AppDatabaseAsync()
    {
        const string name = "Base applicative";
        const string feeds = "Comptes, saisies, journal d'audit";
        try
        {
            using var c = await appDb.OpenAsync();
            var version = await c.ExecuteScalarAsync<long>("SELECT MAX(Version) FROM SchemaVersion");
            return new SourceStatus(name, feeds, SourceState.Connected,
                $"{(appDb.Provider == AppDbProvider.SqlServer ? "SQL Server" : "SQLite")}, schéma v{version}.", null);
        }
        catch (Exception ex)
        {
            return new SourceStatus(name, feeds, SourceState.Error, ex.Message, null);
        }
    }

    public static string Label(SourceState state) => state switch
    {
        SourceState.Connected => "Connectée",
        SourceState.Manual => "Saisie manuelle",
        SourceState.NotConfigured => "Non configurée",
        _ => "Erreur",
    };

    public static string Css(SourceState state) => state switch
    {
        SourceState.Connected => "ok",
        SourceState.Manual => "warn",
        SourceState.NotConfigured => "off",
        _ => "bad",
    };
}
