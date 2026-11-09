using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Input;
using PhSpectre.Avalonia.Services;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireViewModel();

        // The app bar doubles as the title bar (extended client area): keep room for the system
        // buttons (right on Windows/Linux, left traffic lights on macOS) and let the empty bar drag the window.
        TopBar.Padding = OperatingSystem.IsMacOS() ? new Thickness(84, 10, 16, 10) : new Thickness(16, 10, 150, 10);
        WireFilmstrip();
        EditExportButton.Click += async (_, _) =>
        {
            if (DataContext is not MainWindowViewModel m) return;
            // Look export in Edit; the card layout saves the card through the same button.
            if (m.Settings.IsProcessMode) await EditActions.ExportAsync(this, m.Settings.Process);
            else if (m.SavePngAsync2Command.CanExecute(null)) m.SavePngAsync2Command.Execute(null);
        };
        // Dev hook: PHSPECTRE_OPEN=<folder> loads that folder at startup (used for automated UI checks).
        Opened += (_, _) =>
        {
            var dev = Environment.GetEnvironmentVariable("PHSPECTRE_OPEN");
            if (!string.IsNullOrEmpty(dev) && DataContext is MainWindowViewModel m) m.LoadFolder(dev);
        };
        KeyDown += OnEditKeyDown;
        KeyUp += OnEditKeyUp;

        TopBar.PointerPressed += (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) == null
                && v.FindAncestorOfType<ListBox>(true) == null
                && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };
    }


    // ── Edit shortcuts: Ctrl+Z / Ctrl+Y, Ctrl+S exports, 1–3 switch tabs, hold \ to see the original ──
    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel m || !m.Settings.IsProcessMode || e.Source is TextBox) return;
        var p = m.Settings.Process;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) { if (p.UndoCommand.CanExecute(null)) p.UndoCommand.Execute(null); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Y) { if (p.RedoCommand.CanExecute(null)) p.RedoCommand.Execute(null); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) { _ = EditActions.ExportAsync(this, p); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && (e.Key == Key.OemBackslash || e.Key == Key.OemPipe)) { p.ShowOriginal = true; e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.D1 or Key.D2 or Key.D3)
        { m.Settings.EditTab = e.Key - Key.D1; e.Handled = true; }
    }

    private void OnEditKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel m && (e.Key == Key.OemBackslash || e.Key == Key.OemPipe))
            m.Settings.Process.ShowOriginal = false;
    }
    private void WireViewModel()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this)!;
        vm.PickFolderAsync      = () => FileDialogService.PickFolderAsync(topLevel);
        vm.SavePngAsync         = (name, dir) => FileDialogService.SavePngAsync(topLevel, name, dir);
        vm.PickBatchFolderAsync = defaultDir => FileDialogService.PickExportFolderAsync(topLevel, defaultDir);
        vm.OpenUrlAsync         = url => topLevel.Launcher.LaunchUriAsync(new Uri(url));
        vm.Settings.OpenUrlAsync = url => topLevel.Launcher.LaunchUriAsync(new Uri(url));
        vm.Settings.ChooseSaveFolderAsync = () => FileDialogService.PickExportFolderAsync(topLevel,
            string.IsNullOrEmpty(vm.Settings.SaveFolder) ? SuggestedFileName.DefaultOutputFolder() : vm.Settings.SaveFolder);
    }

    // ── Filmstrip: wheel scrolls sideways, mouse drag pans, selection stays in view ───────────
    private const double WheelStep = 70, DragThreshold = 5;
    private Point _dragStart;
    private double _dragStartOffset;
    private bool _dragArmed, _dragging;
    private ListBoxItem? _pressedItem;

    private ScrollViewer? FilmScroller => FilmstripBox.FindDescendantOfType<ScrollViewer>();

    private void WireFilmstrip()
    {
        FilmstripBox.AddHandler(PointerWheelChangedEvent, OnFilmWheel, RoutingStrategies.Tunnel);
        FilmstripBox.AddHandler(PointerPressedEvent, OnFilmPressed, RoutingStrategies.Tunnel);
        FilmstripBox.AddHandler(PointerMovedEvent, OnFilmMoved, RoutingStrategies.Tunnel);
        FilmstripBox.AddHandler(PointerReleasedEvent, OnFilmReleased, RoutingStrategies.Tunnel);
        FilmstripBox.AddHandler(PointerCaptureLostEvent, (_, _) => EndFilmDrag(), RoutingStrategies.Tunnel);
        FilmstripBox.SelectionChanged += (_, _) =>
        {
            if (FilmstripBox.SelectedItem is { } item) FilmstripBox.ScrollIntoView(item);
        };
    }

    private void OnFilmWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || FilmScroller is not { } sv) return;
        // Vertical wheel has nothing to scroll here, so it pans sideways; Delta.X (tilt wheel,
        // touchpad) already works natively.
        if (Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X))
        {
            sv.Offset = new Vector(Math.Max(0, sv.Offset.X - e.Delta.Y * WheelStep), 0);
            e.Handled = true;
        }
    }

    private void OnFilmPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || !e.GetCurrentPoint(FilmstripBox).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.FindAncestorOfType<CheckBox>(true) != null) return;
        if (FilmScroller is not { } sv) return;

        _dragStart = e.GetPosition(FilmstripBox);
        _dragStartOffset = sv.Offset.X;
        _dragArmed = true;
        _dragging = false;
        _pressedItem = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true);
        // Selection is applied on release (so a drag never selects the tile under the cursor).
        e.Handled = true;
    }

    private void OnFilmMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || FilmScroller is not { } sv) return;
        double dx = e.GetPosition(FilmstripBox).X - _dragStart.X;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(FilmstripBox);
        }
        sv.Offset = new Vector(Math.Max(0, _dragStartOffset - dx), 0);
        e.Handled = true;
    }

    private void OnFilmReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragArmed) return;
        bool wasDrag = _dragging;
        var item = _pressedItem;
        EndFilmDrag();
        if (!wasDrag && item?.DataContext is { } data) FilmstripBox.SelectedItem = data;
        e.Handled = true;
    }

    private void EndFilmDrag()
    {
        _dragArmed = false;
        _dragging = false;
        _pressedItem = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not MainWindowViewModel vm) return;

        if (e.Key == Key.Escape && vm.HasError)
        {
            vm.DismissErrorCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            if (vm.SavePngAsync2Command.CanExecute(null))
                vm.SavePngAsync2Command.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Source is not TextBox)
        {
            if (e.Key == Key.Left) { vm.SelectPreviousFile(); e.Handled = true; return; }
            if (e.Key == Key.Right) { vm.SelectNextFile(); e.Handled = true; return; }
            if (e.Key == Key.Home) { vm.SelectFirstFile(); e.Handled = true; return; }
            if (e.Key == Key.End) { vm.SelectLastFile(); e.Handled = true; return; }
        }

        if (e.Key == Key.Up)
        {
            vm.SelectPreviousFile();
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            vm.SelectNextFile();
            e.Handled = true;
        }
    }
}
