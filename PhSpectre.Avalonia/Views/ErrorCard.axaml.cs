using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace PhSpectre.Avalonia.Views;

public partial class ErrorCard : UserControl
{
    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<ErrorCard, string?>(nameof(Message));

    public static readonly StyledProperty<ICommand?> TryAgainCommandProperty =
        AvaloniaProperty.Register<ErrorCard, ICommand?>(nameof(TryAgainCommand));

    public static readonly StyledProperty<ICommand?> DismissCommandProperty =
        AvaloniaProperty.Register<ErrorCard, ICommand?>(nameof(DismissCommand));

    public ICommand? DismissCommand
    {
        get => GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public ICommand? TryAgainCommand
    {
        get => GetValue(TryAgainCommandProperty);
        set => SetValue(TryAgainCommandProperty, value);
    }

    public ErrorCard()
    {
        InitializeComponent();
    }

    private async void CopyDetails_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null && !string.IsNullOrEmpty(Message))
            await clipboard.SetTextAsync(Message);
    }
}
