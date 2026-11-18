using CommunityToolkit.Mvvm.ComponentModel;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Planning;

// The three pages of the stop follow-up, each in the menu (Maintenance).
public enum PlanningSection
{
    Qualification,   // Pannes: give each stop a cause
    Statistics,      // where and when the pannes are
    Planning,        // the week board, planned stops, preventive maintenance
}

// Shared by the three pages (PlanningSectionView), so they link up:
// "Qualifier →" on the statistics opens the qualification, "Planifier un
// préventif" on the first two opens the rule form on the planning board.
// A page loads the first time it is shown, refreshes the next times.
public partial class PlanningViewModel : ObservableObject
{
    private readonly PlanningContext _ctx;
    private readonly HashSet<PlanningSection> _loaded = new();
    private Task? _init;
    private (string MachineId, string Component)? _pendingPlan;

    public PannesViewModel Pannes { get; }
    public StatsViewModel Stats { get; }
    public BoardViewModel Board { get; }

    [ObservableProperty] private string loadError = "";

    // A page asks for another one (the host shows it).
    public event Action<PlanningSection>? SectionRequested;

    public PlanningViewModel(PlanningContext ctx)
    {
        _ctx = ctx;
        Pannes = new PannesViewModel(ctx);
        Stats = new StatsViewModel(ctx);
        Board = new BoardViewModel(ctx);
        Pannes.PreventiveRequested += OpenPreventiveForm;
        Stats.PreventiveRequested += OpenPreventiveForm;
        Stats.QualifyRequested += () => SectionRequested?.Invoke(PlanningSection.Qualification);
        // What is left to qualify, shown over the statistics (they count qualified stops only).
        Pannes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PannesViewModel.OpenSummary))
                Stats.UnqualifiedText = Pannes.OpenSummary;
        };
    }

    public static string Title(PlanningSection section) => section switch
    {
        PlanningSection.Qualification => "Qualification des arrêts",
        PlanningSection.Statistics => "Statistiques des arrêts",
        _ => "Planification des arrêts",
    };

    // The page of this section comes on screen.
    public async Task ShowAsync(PlanningSection section)
    {
        try
        {
            await (_init ??= InitializeAsync());
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - ouverture : {ex}");
            LoadError = PlanningErrors.Describe(ex);
            return;
        }
        // The statistics say how many stops are still to qualify: that comes
        // from the qualification list, loaded first even if not shown.
        if (section == PlanningSection.Statistics && !_loaded.Contains(PlanningSection.Qualification))
            await LoadAsync(PlanningSection.Qualification);
        await LoadAsync(section);
        if (section == PlanningSection.Planning && _pendingPlan is { } plan)
        {
            _pendingPlan = null;
            Board.StartPlanForm(plan.MachineId, plan.Component);
        }
    }

    private async Task InitializeAsync()
    {
        await _ctx.LoadMachinesAsync();
        await _ctx.Planning.EnsureSchemaAsync();
    }

    private async Task LoadAsync(PlanningSection section)
    {
        var first = _loaded.Add(section);
        switch (section)
        {
            case PlanningSection.Qualification:
                if (first) await Pannes.InitializeAsync(); else await Pannes.RefreshAsync();
                break;
            case PlanningSection.Statistics:
                await Stats.RefreshAsync();
                break;
            default:
                if (first) await Board.InitializeAsync(); else await Board.RefreshAsync();
                break;
        }
    }

    // The rule form opens on the planning board once it has loaded.
    private void OpenPreventiveForm(string machineId, string component)
    {
        _pendingPlan = (machineId, component);
        SectionRequested?.Invoke(PlanningSection.Planning);
    }
}
