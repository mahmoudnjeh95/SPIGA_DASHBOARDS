using System.Windows.Controls;
using System.Windows.Data;
using SpigaDashboards.App.ViewModels.Planning;
using SpigaDashboards.App.Views.Planning;

namespace SpigaDashboards.App;

public partial class PlanningSectionView : UserControl
{
    public PlanningSectionView(PlanningViewModel viewModel, PlanningSection section)
    {
        InitializeComponent();
        DataContext = viewModel;
        TitleText.Text = PlanningViewModel.Title(section);
        // The line under the title: what the page shows. On Planification,
        // the period on screen ("Semaine 47 · 16 nov. → 22 nov.").
        if (section == PlanningSection.Planning)
            SubtitleText.SetBinding(TextBlock.TextProperty, new Binding("Board.RangeLabel"));
        else
            SubtitleText.Text = section == PlanningSection.Qualification
                ? "Les arrêts sont détectés par le compteur de chaque ligne : la durée est déjà exacte, il ne reste qu'à donner la cause."
                : "Calculé sur les arrêts qualifiés (page Qualification des arrêts).";
        SectionHost.Content = section switch
        {
            PlanningSection.Qualification => new PannesView { DataContext = viewModel.Pannes },
            PlanningSection.Statistics => new StatsView { DataContext = viewModel.Stats },
            _ => new BoardView { DataContext = viewModel.Board },
        };
        // Loaded comes again each time the page is put back on screen: the
        // first time it loads, then it refreshes (as the tabs did).
        Loaded += async (_, _) => await viewModel.ShowAsync(section);
    }
}
