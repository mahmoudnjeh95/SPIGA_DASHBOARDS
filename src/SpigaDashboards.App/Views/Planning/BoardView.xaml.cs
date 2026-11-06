using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpigaDashboards.App.ViewModels.Planning;

namespace SpigaDashboards.App.Views.Planning;

public partial class BoardView : UserControl
{
    // Click or drag on an empty part of a line picks a slot, as in a
    // calendar; BoardViewModel snaps it and offers what to plan there.
    private Point _slotFrom;
    private bool _slotDragging;

    // Drag on a planned slot: the middle moves it, an edge (EdgePixels wide)
    // moves its start / end. Below DragPixels of travel it stays a click,
    // which selects the slot as before.
    private const double EdgePixels = 7;
    private const double DragPixels = 4;
    private BoardBlock? _pressedBlock;
    private FrameworkElement? _pressedLine;
    private Point _pressedAt;
    private BlockDrag _pressedMode;
    private bool _blockDragging;
    private BoardViewModel? _board;

    public BoardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private BoardViewModel? Board => DataContext as BoardViewModel;

    // A new selection or form starts at the top of the side panel.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_board != null)
            _board.PropertyChanged -= OnBoardPropertyChanged;
        _board = Board;
        if (_board != null)
            _board.PropertyChanged += OnBoardPropertyChanged;
    }

    private void OnBoardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoardViewModel.SelectedBlock) or nameof(BoardViewModel.Editor))
            SidePanel.ScrollToTop();
    }

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

    // ---- Dragging a planned slot ----

    private void Root_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Board is not { } board || BlockButtonAt(e.OriginalSource as DependencyObject) is not { DataContext: BoardBlock block } button
            || !board.CanDrag(block) || Ancestor<TimelinePanel>(button) is not { ActualWidth: > 0 } line)
            return;
        _pressedBlock = block;
        _pressedLine = line;
        _pressedAt = e.GetPosition(line);
        _pressedMode = EdgeAt(button, e.GetPosition(button));
        _blockDragging = false;
    }

    private void Root_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedBlock == null || _pressedLine == null)
        {
            ShowDragCursor(e);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndPress();
            return;
        }
        var at = e.GetPosition(_pressedLine);
        if (!_blockDragging)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragPixels)
                return;
            // Taking the mouse from the block's button: no click on release.
            _blockDragging = true;
            Root.CaptureMouse();
            Root.Cursor = _pressedMode == BlockDrag.Move ? Cursors.SizeAll : Cursors.SizeWE;
            if (_pressedBlock == null || _pressedLine == null)
                return;
        }
        Board?.DragBlock(_pressedBlock, _pressedMode, (at.X - _pressedAt.X) / _pressedLine.ActualWidth);
        e.Handled = true;
    }

    private async void Root_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedBlock == null)
            return;
        var dragged = _blockDragging;
        EndPress();
        // A plain click: the block's button selects it.
        if (!dragged || Board is not { } board)
            return;
        e.Handled = true;
        await board.EndDragAsync();
    }

    // Capture lost mid-drag (another window, Escape): nothing is moved. The
    // event bubbles: the block's button losing the mouse to Root is not it.
    private void Root_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_blockDragging || e.OriginalSource != Root)
            return;
        EndPress();
        Board?.CancelDrag();
    }

    private void EndPress()
    {
        var wasDragging = _blockDragging;
        _pressedBlock = null;
        _pressedLine = null;
        _blockDragging = false;
        Root.Cursor = null;
        if (wasDragging)
            Root.ReleaseMouseCapture();
    }

    private static BlockDrag EdgeAt(FrameworkElement button, Point at)
    {
        // Too narrow to aim at an edge: it can only be moved.
        if (button.ActualWidth < EdgePixels * 3)
            return BlockDrag.Move;
        return at.X <= EdgePixels ? BlockDrag.Start
            : at.X >= button.ActualWidth - EdgePixels ? BlockDrag.End
            : BlockDrag.Move;
    }

    // Over a slot that can be dragged: the cursor says how (move / stretch).
    private void ShowDragCursor(MouseEventArgs e)
    {
        if (Board is not { } board || BlockButtonAt(e.OriginalSource as DependencyObject) is not { DataContext: BoardBlock block } button)
            return;
        button.Cursor = !board.CanDrag(block) ? Cursors.Hand
            : EdgeAt(button, e.GetPosition(button)) == BlockDrag.Move ? Cursors.SizeAll
            : Cursors.SizeWE;
    }

    private static Button? BlockButtonAt(DependencyObject? source)
    {
        for (var button = Ancestor<Button>(source); button != null; button = Ancestor<Button>(VisualTreeHelper.GetParent(button)))
        {
            if (button.DataContext is BoardBlock)
                return button;
        }
        return null;
    }

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (_blockDragging)
        {
            EndPress();
            Board?.CancelDrag();
            e.Handled = true;
        }
        else if (Board is { IsConfirmOpen: true } confirm)
        {
            confirm.ConfirmNoCommand.Execute(null);
            e.Handled = true;
        }
        else if (Board is { IsUnavailableOpen: true } open)
        {
            open.IsUnavailableOpen = false;
            e.Handled = true;
        }
        else if (Board is { IsSlotMenuOpen: true } board)
        {
            board.CancelSlotCommand.Execute(null);
            e.Handled = true;
        }
    }

    // A click beside the "not available" message closes it.
    private void Unavailable_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == sender && Board is { } board)
            board.IsUnavailableOpen = false;
    }
}
