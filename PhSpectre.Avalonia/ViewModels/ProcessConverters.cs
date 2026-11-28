using global::Avalonia.Data.Converters;
using global::Avalonia.Media;

namespace PhSpectre.Avalonia.ViewModels;

public static class ProcessConverters
{
    // The layer in use is shown semi-bold.
    public static readonly IValueConverter ActiveWeight =
        new FuncValueConverter<bool, FontWeight>(active => active ? FontWeight.SemiBold : FontWeight.Normal);
}
