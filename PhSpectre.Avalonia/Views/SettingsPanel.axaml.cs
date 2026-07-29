using Avalonia.Controls;
using Avalonia.Input;
using PhSpectre.Avalonia.ViewModels;

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

        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    private void ToggleMetadataEditor_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ToggleMetadataEditorCommand.Execute(null);
    }

    private static void AddItem(ComboBox box, string shortText, string fullText)
    {
        var item = new ComboBoxItem { Content = shortText };
        ToolTip.SetTip(item, fullText);
        box.Items.Add(item);
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
                     SamplingRow, ColorCountRow, ExportThemeRow, VerbosityRow,
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
