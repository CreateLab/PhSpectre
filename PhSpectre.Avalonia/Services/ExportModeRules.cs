using System.Collections.Generic;
using System.Linq;
using PhSpectre.Models;

namespace PhSpectre.Avalonia.Services;

// Pure rules for which ExportMode values make sense given how many photos are currently
// selected, and what to fall back to when the selection count crosses the 1<->2+ boundary
// and the current mode stops being valid. See the plan's Export Mode table: Card/InfoOnly/
// Recipe apply to exactly 1 photo, Collage/CollageInfoOnly to 2+.
public static class ExportModeRules
{
    public static IReadOnlyList<ExportMode> AvailableModes(int selectedCount) => selectedCount switch
    {
        1 => new[] { ExportMode.Card, ExportMode.InfoOnly, ExportMode.Recipe },
        >= 2 => new[] { ExportMode.Collage, ExportMode.CollageInfoOnly },
        _ => System.Array.Empty<ExportMode>()
    };

    // Card<->Collage and InfoOnly<->CollageInfoOnly preserve "computes colors or not" across
    // the boundary; Recipe has no collage equivalent so it falls back to Collage (closest
    // "still fast-ish, still meaningful for many photos" choice) rather than losing the
    // colors-off intent by landing on plain Card-analog Collage... actually Recipe->Collage
    // is the ТЗ's own explicit call, kept as specified.
    public static ExportMode ClosestValidMode(ExportMode current, int selectedCount)
    {
        var available = AvailableModes(selectedCount);
        if (available.Count == 0 || available.Contains(current))
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
