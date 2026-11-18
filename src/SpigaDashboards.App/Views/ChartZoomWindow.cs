using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SpigaDashboards.App.Services;

namespace SpigaDashboards.App.Views;

// A chart enlarged (ChartCard's zoom button or double-click): most of the
// screen, mouse wheel to zoom in on part of the X axis, drag to move along
// it, the same toolbar as the tile (kind, series). A snapshot of the chart
// when it was opened; Escape or "Fermer" closes it.
public sealed class ChartZoomWindow : Window
{
    private readonly ChartCard _card;

    public ChartZoomWindow(string title, ChartParts parts, bool showLegend)
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(900, area.Width * 0.82);
        Height = Math.Max(560, area.Height * 0.82);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("fr-FR");
        Title = string.IsNullOrWhiteSpace(title) ? "Graphique" : title;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        if (Application.Current?.MainWindow?.Icon is { } icon)
            Icon = icon;

        _card = new ChartCard { Title = title, ShowLegend = showLegend, IsExpanded = true, Chart = parts };

        var hint = new TextBlock
        {
            Text = "Molette : zoom   ·   Glisser : se déplacer   ·   Clic sur la légende : masquer / afficher   ·   Échap : fermer",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");

        var reset = Button("Réinitialiser le zoom");
        reset.Click += (_, _) => _card.ResetZoom();
        var close = Button("Fermer");
        close.Click += (_, _) => Close();

        var bar = new DockPanel { Margin = new Thickness(12, 10, 12, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(reset);
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(hint);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_card);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        // Opens with a short zoom from the middle.
        var scale = new ScaleTransform(0.94, 0.94);
        root.RenderTransformOrigin = new Point(0.5, 0.5);
        root.RenderTransform = scale;
        root.Opacity = 0;
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(260);
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, time) { EasingFunction = ease });
        };
    }

    private static Button Button(string text)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 6, 14, 6),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0),
        };
        button.SetResourceReference(BackgroundProperty, "SecondaryButtonBrush");
        button.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        return button;
    }
}
