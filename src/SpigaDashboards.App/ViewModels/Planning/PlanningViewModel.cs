using CommunityToolkit.Mvvm.ComponentModel;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.ViewModels.Planning;

// Planification window: Pannes (give each stop a cause), Statistiques (where
// and when the pannes are) and Planification (the week board). A tab loads
// the first time it is shown; "Planifier un préventif" on the first two
// opens the rule form on the board.
public partial class PlanningViewModel : ObservableObject
{
    public const int PannesTab = 0;
    public const int StatsTab = 1;
    public const int BoardTab = 2;

    private readonly PlanningContext _ctx;
    private readonly bool[] _loaded = new bool[3];
    private bool _ready;

    public PannesViewModel Pannes { get; }
    public StatsViewModel Stats { get; }
    public BoardViewModel Board { get; }

    [ObservableProperty] private int selectedTab;
    [ObservableProperty] private string loadError = "";

    public PlanningViewModel(PlanningContext ctx)
    {
        _ctx = ctx;
        Pannes = new PannesViewModel(ctx);
        Stats = new StatsViewModel(ctx);
        Board = new BoardViewModel(ctx);
        Pannes.PreventiveRequested += OpenPreventiveForm;
        Stats.PreventiveRequested += OpenPreventiveForm;
        Stats.QualifyRequested += () => SelectedTab = PannesTab;
        // What is left to qualify, shown over the statistics (they count qualified stops only).
        Pannes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PannesViewModel.OpenSummary))
                Stats.UnqualifiedText = Pannes.OpenSummary;
        };
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _ctx.LoadMachinesAsync();
            await _ctx.Planning.EnsureSchemaAsync();
            _ready = true;
            await LoadTabAsync(SelectedTab);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Planification - ouverture : {ex}");
            LoadError = PlanningErrors.Describe(ex);
        }
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (_ready)
            _ = LoadTabAsync(value);
    }

    private async Task LoadTabAsync(int tab)
    {
        if (tab is < 0 or > 2)
            return;
        var first = !_loaded[tab];
        _loaded[tab] = true;
        switch (tab)
        {
            case PannesTab:
                if (first) await Pannes.InitializeAsync(); else await Pannes.RefreshAsync();
                break;
            case StatsTab:
                await Stats.RefreshAsync();
                break;
            default:
                if (first) await Board.InitializeAsync(); else await Board.RefreshAsync();
                break;
        }
    }

    private async void OpenPreventiveForm(string machineId, string component)
    {
        SelectedTab = BoardTab;
        if (!_loaded[BoardTab])
        {
            _loaded[BoardTab] = true;
            await Board.InitializeAsync();
        }
        Board.StartPlanForm(machineId, component);
    }
}
