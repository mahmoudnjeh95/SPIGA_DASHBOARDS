using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SpigaDashboards.App.Views;

// Stop pop-up card, dragged with the mouse inside its parent (the slide area
// of the 1600x900 canvas, so the same offset fits the main window and the
// TV). Moved reports where it was left, for the window to save.
public partial class StopAlertPopup : UserControl
{
    private Point _dragStart;
    private Point _offsetAtStart;
    private bool _dragging;

    public event Action<double, double>? Moved;

    public StopAlertPopup()
    {
        InitializeComponent();
    }

    public void SetOffset(double x, double y)
    {
        Offset.X = x;
        Offset.Y = y;
    }

    private UIElement? Area => VisualTreeHelper.GetParent(this) as UIElement;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Handled: the TV window would otherwise move itself (DragMove).
        e.Handled = true;
        if (Area is not { } area)
            return;
        _dragStart = e.GetPosition(area);
        _offsetAtStart = new Point(Offset.X, Offset.Y);
        _dragging = Card.CaptureMouse();
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Area is not FrameworkElement area)
            return;
        var now = e.GetPosition(area);
        // Untransformed place of the card in its parent: keep it all inside.
        var placed = TransformToAncestor(area).Transform(new Point(0, 0));
        var baseX = placed.X - Offset.X;
        var baseY = placed.Y - Offset.Y;
        var x = _offsetAtStart.X + now.X - _dragStart.X;
        var y = _offsetAtStart.Y + now.Y - _dragStart.Y;
        Offset.X = Math.Clamp(x, -baseX, Math.Max(-baseX, area.ActualWidth - ActualWidth - baseX));
        Offset.Y = Math.Clamp(y, -baseY, Math.Max(-baseY, area.ActualHeight - ActualHeight - baseY));
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_dragging)
            Card.ReleaseMouseCapture();
    }

    private void Card_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (Offset.X != _offsetAtStart.X || Offset.Y != _offsetAtStart.Y)
            Moved?.Invoke(Offset.X, Offset.Y);
    }
}
