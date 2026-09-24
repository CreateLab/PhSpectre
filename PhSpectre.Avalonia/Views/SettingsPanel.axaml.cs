using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using PhSpectre.Avalonia.ViewModels;
using PhSpectre.Models;

namespace PhSpectre.Avalonia.Views;

public partial class SettingsPanel : UserControl
{
    private bool? _isNarrow;

    public SettingsPanel()
    {
        InitializeComponent();

        // Short label in the box + full explanation as a ToolTip — desktop mouse users get
        // the explanation on hover, touch users on Android just see the short label (there's
        // no hover to trigger a tooltip there, which is an acceptable trade for not having
        // every ComboBox row overflow a 300px-wide panel or a phone screen).
        AddItem(BackgroundModeBox, "Theme color",   "Flat background matching the card theme above");
        AddItem(BackgroundModeBox, "Custom color",  "Flat background using the custom hex color below");
        AddItem(BackgroundModeBox, "Blurred photo", "Blurred, brightness-adjusted backdrop built from the source photo (not used for Collage)");

        AddItem(SamplingModeBox, "Vivid",    "Saturated colors stand out");
        AddItem(SamplingModeBox, "Standard", "Most frequent colors by area");
        AddItem(SamplingModeBox, "Contrast", "Vivid mid-lightness colors");

        ColorCountBox.Items.Add("Auto");
        for (int i = 3; i <= 8; i++)
            ColorCountBox.Items.Add(i.ToString());

        MetaVerbosityBox.Items.Add("None");
        MetaVerbosityBox.Items.Add("Short");
        MetaVerbosityBox.Items.Add("Default");
        MetaVerbosityBox.Items.Add("Detail");
        MetaVerbosityBox.Items.Add("Full");

        MetaStyleBox.Items.Add("Film strip");
        MetaStyleBox.Items.Add("Overlay");

        AddItem(WorkingQualityBox, "Fast",     "2000px, quickest processing");
        AddItem(WorkingQualityBox, "Balanced", "3400px");
        AddItem(WorkingQualityBox, "Best",     "4800px, closer to desktop detail");

        AddItem(OutputFormatBox, "PNG",  "Lossless, larger file");
        AddItem(OutputFormatBox, "JPEG", "Smaller file, lossy compression");

        AddItem(ExportPresetBox, "Original",     "Original size, no cropping or resizing");
        // Half size only makes sense on desktop — mobile already has its own size-reduction
        // control (Working size) and its render path never reads HalfSize, so showing this
        // here on mobile would be a silent no-op. SettingsViewModel.ExportSizeIndex's index
        // mapping matches this platform split exactly.
        if (!OperatingSystem.IsAndroid())
            AddItem(ExportPresetBox, "Half (2× downscale)", "Compress 2× — roughly 4× smaller file, same crop as Original");
        AddItem(ExportPresetBox, "Square",       "1080×1080 — Instagram/Telegram square post");
        AddItem(ExportPresetBox, "IG post",      "1080×1350 (4:5) — Instagram feed default");
        AddItem(ExportPresetBox, "Story",        "1080×1920 — Instagram & Telegram Stories");
        AddItem(ExportPresetBox, "TG landscape", "1920×1080 — Telegram landscape photo");

        SwatchShapeBox.Items.Add("Rectangle");
        SwatchShapeBox.Items.Add("Rounded");
        SwatchShapeBox.Items.Add("Circle");

        SortOrderBox.Items.Add("None");
        SortOrderBox.Items.Add("Hue");
        SortOrderBox.Items.Add("Luminance");
        SortOrderBox.Items.Add("Percent");

        AddItem(CompositionGuideBox, "None",     "No overlay");
        AddItem(CompositionGuideBox, "Thirds",   "Rule of thirds grid");
        AddItem(CompositionGuideBox, "Golden",   "Golden ratio (phi) grid");
        AddItem(CompositionGuideBox, "Diagonal", "Diagonal method / golden triangles");
        AddItem(CompositionGuideBox, "Cross",    "Center cross");

        // Export Mode's item list is rebuilt dynamically (see RebuildExportModeItems) —
        // inapplicable modes for the current photo selection are removed entirely, not
        // shown disabled, per the bugfix-pass spec ("disabled without explanation reads as
        // a bug"). Wired up once the DataContext (the SettingsViewModel) actually arrives.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            vm.PropertyChanged -= OnSettingsPropertyChangedForExportMode;
            vm.PropertyChanged += OnSettingsPropertyChangedForExportMode;
            RebuildExportModeItems(vm);
        };

        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ToggleMetadataEditor_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ToggleMetadataEditorCommand.Execute(null);
    }

    private static readonly (ExportMode Mode, string Label, string Tip)[] ExportModeOptions =
    [
        (ExportMode.Card,           "Card",           "Photo + full color palette — slower, computes colors"),
        (ExportMode.InfoOnly,       "Info only",      "Photo + camera info, no colors — fast"),
        (ExportMode.Recipe,         "Recipe",         "Film recipe card, no colors — fast"),
        (ExportMode.Collage,        "Collage",        "Combined photos + pooled palette — slower"),
        (ExportMode.CollageInfoOnly,"Collage (fast)", "Combined photos, no colors — fast"),
    ];

    private void OnSettingsPropertyChangedForExportMode(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel vm) return;
        if (e.PropertyName is nameof(SettingsViewModel.IsSingleModeAvailable) or nameof(SettingsViewModel.IsCollageModeAvailable))
            RebuildExportModeItems(vm);
        else if (e.PropertyName == nameof(SettingsViewModel.ExportMode))
            SyncExportModeSelection(vm);
    }

    private void RebuildExportModeItems(SettingsViewModel vm)
    {
        ExportModeBox.SelectionChanged -= ExportModeBox_SelectionChanged;
        ExportModeBox.Items.Clear();
        foreach (var (mode, label, tip) in ExportModeOptions)
        {
            bool applicable = mode is ExportMode.Card or ExportMode.InfoOnly or ExportMode.Recipe
                ? vm.IsSingleModeAvailable
                : vm.IsCollageModeAvailable;
            if (!applicable) continue;
            var item = new ComboBoxItem { Content = label, Tag = mode };
            ToolTip.SetTip(item, tip);
            ExportModeBox.Items.Add(item);
        }
        SyncExportModeSelection(vm);
        ExportModeBox.SelectionChanged += ExportModeBox_SelectionChanged;
    }

    // The VM is the source of truth (it auto-corrects ExportMode via ExportModeRules
    // whenever the selection count changes) — this just reflects that choice onto whichever
    // ComboBoxItem currently represents it, rather than the box driving VM state on rebuild.
    private void SyncExportModeSelection(SettingsViewModel vm)
    {
        foreach (var obj in ExportModeBox.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is ExportMode m && m == vm.ExportMode)
            {
                ExportModeBox.SelectedItem = item;
                return;
            }
        }
    }

    private void ExportModeBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && ExportModeBox.SelectedItem is ComboBoxItem item &&
            item.Tag is ExportMode mode && vm.ExportMode != mode)
            vm.ExportMode = mode;
    }

    private static ComboBoxItem AddItem(ComboBox box, string shortText, string fullText)
    {
        var item = new ComboBoxItem { Content = shortText };
        ToolTip.SetTip(item, fullText);
        box.Items.Add(item);
        return item;
    }

    // Container-query stand-in: Avalonia has no width-based style triggers, so each
    // settings-row Grid's two children are repositioned in code when the panel itself
    // (not the window) gets too narrow to fit "label: control" on one line — this covers
    // both a narrow Desktop sidebar and small Android screens with the same code path.
    private void ApplyResponsiveLayout(double width)
    {
        var narrow = width < 260;
        if (_isNarrow == narrow) return;
        _isNarrow = narrow;

        foreach (var row in new[]
                 {
                     SamplingRow, ColorCountRow, BackgroundThemeRow, BackgroundModeRow, GuideRow, VerbosityRow,
                     StyleRow, FormatRow, ExportSizeRow, WorkingQualityRow,
                     ShapeRow, SortRow,
                     MetaCameraRow, MetaLensRow, MetaFocalRow, MetaApertureRow,
                     MetaShutterRow, MetaIsoRow, MetaDateRow
                 })
        {
            if (row.Children.Count < 2) continue;
            var label = row.Children[0];
            var value = row.Children[1];
            if (narrow)
            {
                Grid.SetRow(label, 0); Grid.SetColumn(label, 0); Grid.SetColumnSpan(label, 2);
                Grid.SetRow(value, 1); Grid.SetColumn(value, 0); Grid.SetColumnSpan(value, 2);
            }
            else
            {
                Grid.SetRow(label, 0); Grid.SetColumn(label, 0); Grid.SetColumnSpan(label, 1);
                Grid.SetRow(value, 0); Grid.SetColumn(value, 1); Grid.SetColumnSpan(value, 1);
            }
        }
    }
}
