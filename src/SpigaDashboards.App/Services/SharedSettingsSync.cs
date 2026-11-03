using SpigaDashboards.App.Data;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

public enum SharedSaveOutcome { Unchanged, Saved, Conflict, Failed }

public record SharedSaveResult(SharedSaveOutcome Outcome, string Message = "");

// Keeps SharedSettings in step with the other PCs: pulled at startup, at each
// data refresh and before Parametres opens; pushed by Parametres > Enregistrer.
// settings.json keeps the last copy received, for a start without the base.
public class SharedSettingsSync
{
    private const string SharedPart = "les cadences, pauses, seuil d'arrêt, équipes, TRS cibles et diapositives";

    private readonly SharedSettingsRepository _repository;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    // The main deck, the TV deck and Parametres share one AppSettings.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SharedSettingsSync(SharedSettingsRepository repository, AppSettings settings, SettingsService settingsService)
    {
        _repository = repository;
        _settings = settings;
        _settingsService = settingsService;
    }

    // Forget the schema check after a database switch (Parametres).
    public void Reset() => _repository.Reset();

    // Applies the shared settings when another PC saved newer ones. Never
    // throws: a base down is reported by the dashboard's own checks.
    public async Task PullAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var version = await _repository.GetVersionAsync();
            if (version == 0 || version == _settings.SharedSettingsVersion)
                return;
            if (await _repository.GetAsync() is { } row)
                Apply(row);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Paramètres partagés non lus : {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    // After Parametres changed _settings. snapshotAtOpen = SharedSettings.Serialize
    // when the window opened: unchanged values send nothing, and a save that
    // can't reach the base (or comes after another PC's) puts them back, so
    // this PC never computes with values the others don't have.
    public async Task<SharedSaveResult> PushAsync(string snapshotAtOpen)
    {
        var json = SharedSettings.Serialize(_settings);
        if (json == snapshotAtOpen && !await BaseLacksSlidesAsync())
            return new(SharedSaveOutcome.Unchanged);

        await _gate.WaitAsync();
        try
        {
            var newVersion = await _repository.SaveAsync(json, _settings.SharedSettingsVersion, Environment.MachineName);
            if (newVersion is int version)
            {
                _settings.SharedSettingsVersion = version;
                AppLogger.Log($"Paramètres partagés enregistrés (version {version}).");
                return new(SharedSaveOutcome.Saved);
            }

            Restore(snapshotAtOpen);
            var row = await _repository.GetAsync();
            if (row != null)
                Apply(row);
            var who = row == null ? "un autre poste" : $"{row.UpdatedBy} le {row.UpdatedAt:dd/MM à HH:mm}";
            return new(SharedSaveOutcome.Conflict,
                $"Modifiés entre-temps sur {who}, {SharedPart} gardent la version de ce poste-là : vos changements de ces réglages n'ont pas été enregistrés (le reste l'est). "
                + "Annuler puis rouvrir Paramètres pour voir la version à jour, ou Enregistrer à nouveau pour la remplacer par la vôtre.");
        }
        catch (Exception ex)
        {
            Restore(snapshotAtOpen);
            AppLogger.Log($"Paramètres partagés non enregistrés : {ex.Message}");
            return new(SharedSaveOutcome.Failed,
                $"Base inaccessible : {SharedPart}, partagés entre les postes, n'ont pas été enregistrés (le reste l'est). "
                + $"Réessayer une fois la connexion rétablie. ({ex.Message})");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Apply(SharedSettingsRow row)
    {
        if (SharedSettings.Deserialize(row.Json) is not { } shared)
        {
            AppLogger.Log($"Paramètres partagés illisibles (version {row.Version}), ignorés.");
            return;
        }
        shared.ApplyTo(_settings);
        _settings.SharedSettingsVersion = row.Version;
        _settingsService.Save(_settings);
        AppLogger.Log($"Paramètres partagés appliqués (version {row.Version}, enregistrés par {row.UpdatedBy} le {row.UpdatedAt:dd/MM HH:mm}).");
    }

    // The base copy was saved by a version before 1.5.13, without the slide
    // rotation: Enregistrer then sends this PC's even unchanged, so the
    // others get it. Only when this PC is up to date, so it can't overwrite.
    private async Task<bool> BaseLacksSlidesAsync()
    {
        try
        {
            var row = await _repository.GetAsync();
            return row != null && row.Version == _settings.SharedSettingsVersion
                && SharedSettings.Deserialize(row.Json) is { Slides: null };
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Restore(string snapshot) => SharedSettings.Deserialize(snapshot)?.ApplyTo(_settings);
}
