using System;
using PhSpectre.Localization;

namespace PhSpectre.Avalonia.Localization;

/// <summary><c>{l:T 'English text'}</c> in XAML. Resolved once, so a language change needs a restart.</summary>
public sealed class TExtension(string key)
{
    public string ProvideValue(IServiceProvider _) => Loc.T(key);
}
