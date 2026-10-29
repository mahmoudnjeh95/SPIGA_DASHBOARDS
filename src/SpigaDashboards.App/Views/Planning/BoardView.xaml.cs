using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App.Views.Planning;

public partial class BoardView : UserControl
{
    // Click or drag on an empty part of a line picks a slot, as in a
    // calendar; BoardViewModel snaps it and offers what to plan there.
    private Point _slotFrom;
    private bool _slotDragging;

    public BoardView()
    {
        InitializeComponent();
    }

    private BoardViewModel? Board => DataContext as BoardViewModel;

    private void Slot_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MachineRow row, ActualWidth: > 0 } surface || Board is not { } board)
            return;
        _slotFrom = e.GetPosition(surface);
        _slotDragging = false;
        board.BeginSlot(row, _slotFrom.X / surface.ActualWidth);
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void Slot_MouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true, ActualWidth: > 0 } surface || Board is not { } board)
            return;
        var at = e.GetPosition(surface);
        if (!_slotDragging && Math.Abs(at.X - _slotFrom.X) < 4)
            return;
        _slotDragging = true;
        board.DragSlot(at.X / surface.ActualWidth);
    }

    private void Slot_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true } surface)
            return;
        surface.ReleaseMouseCapture();
        Board?.EndSlot();
        e.Handled = true;
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Board is { IsSlotMenuOpen: true } board)
        {
            board.CancelSlotCommand.Execute(null);
            e.Handled = true;
        }
    }
}
