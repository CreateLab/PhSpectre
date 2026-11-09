using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input.Platform;
using global::Avalonia.Interactivity;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class CameraModeView : UserControl
{
    private const double NarrowBelow = 760;
    private bool? _narrow;

    public CameraModeView()
    {
        InitializeComponent();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < NarrowBelow;
        if (_narrow == narrow) return;
        _narrow = narrow;
        ApplyLayout(narrow);
    }

    private RecipeLibraryViewModel? _library;

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_library != null) _library.ScrollToCurrentRequested -= ScrollToCurrent;
        _library = (DataContext as SettingsViewModel)?.Library;
        if (_library != null)
        {
            _library.ScrollToCurrentRequested += ScrollToCurrent;
            if (_narrow is { } n) _library.IsNarrow = n;
        }
    }

    private void ScrollToCurrent()
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("PhotoList");
            if (list?.SelectedItem is { } item) list.ScrollIntoView(item);
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    // Wide: three columns. Narrow (phone): one column, the view model shows one step at a time.
    private void ApplyLayout(bool narrow)
    {
        var cols = this.FindControl<Grid>("Cols")!;
        var left = this.FindControl<Control>("LeftCol")!;
        var center = this.FindControl<Control>("CenterCol")!;
        var right = this.FindControl<Control>("RightCol")!;
        cols.ColumnDefinitions = narrow ? new ColumnDefinitions("*") : new ColumnDefinitions("320,*,340");
        Grid.SetColumn(left, 0);
        Grid.SetColumn(center, narrow ? 0 : 1);
        Grid.SetColumn(right, narrow ? 0 : 2);
        if (DataContext is SettingsViewModel vm) vm.Library.IsNarrow = narrow;
    }

    private async void CopyRecipe_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || vm.Library.CopyText() is not { } text) return;
        await CopyAsync(text);
        vm.Library.CopiedNotice();
    }

    private async void PasteRecipe_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        var text = clipboard == null ? null : await clipboard.TryGetTextAsync();
        vm.Library.PasteText(text);
    }

    private async void CopyReport_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        await CopyAsync(vm.Camera.ReportText);
        var path = vm.Camera.SaveReport();
        vm.SetRecipeStatus(path == null
            ? "Camera dump copied. Send it to the developer"
            : $"Camera dump copied and saved to {path}. Send it to the developer");
    }

    private async void CopyConnectError_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        await CopyAsync(vm.Camera.ConnectErrorDetails);
        vm.SetRecipeStatus("Error copied to the clipboard");
    }

    private async void CopyManual_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) await CopyAsync(vm.Camera.ManualAsText());
    }

    private async System.Threading.Tasks.Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && !string.IsNullOrEmpty(text))
            await clipboard.SetTextAsync(text);
    }
}
