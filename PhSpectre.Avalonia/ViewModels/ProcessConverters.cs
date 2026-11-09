using global::Avalonia.Data.Converters;
using global::Avalonia.Media;

namespace PhSpectre.Avalonia.ViewModels;

public static class ProcessConverters
{
    // The layer currently in use is shown in semi-bold in the library list.
    public static readonly IValueConverter ActiveWeight =
        new FuncValueConverter<bool, FontWeight>(active => active ? FontWeight.SemiBold : FontWeight.Normal);
}
