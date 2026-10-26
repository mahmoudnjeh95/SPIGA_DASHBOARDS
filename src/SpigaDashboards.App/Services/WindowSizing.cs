using System.Windows;

namespace SpigaDashboards.App.Services;

public static class WindowSizing
{
    // Dialogs are designed at 800 px tall: on a laptop (768 px, or less at
    // 125 % scaling) they would run off the screen and hide their bottom
    // buttons (Enregistrer). Shrink them to the work area; content scrolls.
    public static void FitToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MaxHeight = area.Height;
        window.MaxWidth = area.Width;
        window.Height = Math.Min(window.Height, area.Height);
        window.Width = Math.Min(window.Width, area.Width);
    }
}
