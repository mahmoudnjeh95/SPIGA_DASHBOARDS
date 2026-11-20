using System.Windows.Media;

namespace SpigaDashboards.App.Services;

// Tier 2 = the graphics card draws effects and animations. Below (no GPU
// driver, remote desktop, some hall PCs), WPF draws in software on the CPU:
// a blur or a glow redrawn 60 times a second then takes the processor the
// dashboard needs. Read each time: a remote session can change it.
public static class RenderBudget
{
    public static bool IsSoftware => (RenderCapability.Tier >> 16) < 2;
}
