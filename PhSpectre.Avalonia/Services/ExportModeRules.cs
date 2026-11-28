using System.Collections.Generic;
using System.Linq;
using PhSpectre.Models;

namespace PhSpectre.Avalonia.Services;

// Which export modes fit the number of selected photos: Card, InfoOnly and Recipe take one, the collage modes two
// or more.
public static class ExportModeRules
{
    public static IReadOnlyList<ExportMode> AvailableModes(int selectedCount) => selectedCount switch
    {
        // With no photo the single-photo modes are listed, so the box is never empty.
        <= 1 => new[] { ExportMode.Card, ExportMode.InfoOnly, ExportMode.Recipe },
        _ => new[] { ExportMode.Collage, ExportMode.CollageInfoOnly }
    };

    // Crossing the 1/2+ boundary keeps "computes colours or not"; Recipe has no collage twin and falls back to
    // Collage.
    public static ExportMode ClosestValidMode(ExportMode current, int selectedCount)
    {
        var available = AvailableModes(selectedCount);
        if (selectedCount <= 0 || available.Contains(current))
            return current;

        return current switch
        {
            ExportMode.Card => ExportMode.Collage,
            ExportMode.Collage => ExportMode.Card,
            ExportMode.InfoOnly => ExportMode.CollageInfoOnly,
            ExportMode.CollageInfoOnly => ExportMode.InfoOnly,
            ExportMode.Recipe => ExportMode.Collage,
            _ => available[0]
        };
    }
}
