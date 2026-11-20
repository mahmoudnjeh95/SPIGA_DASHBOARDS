using System.Windows.Controls;

namespace SpigaDashboards.App.Views;

public partial class RunStateImage : UserControl
{
    public RunStateImage()
    {
        InitializeComponent();
        // Software rendering: the halo pulses without its blur, which the
        // CPU redrew every frame for each machine of the slide.
        if (Services.RenderBudget.IsSoftware)
            Halo.Effect = null;
    }
}
