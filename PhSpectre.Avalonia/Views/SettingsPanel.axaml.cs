using Avalonia.Controls;

namespace PhSpectre.Avalonia.Views;

public partial class SettingsPanel : UserControl
{
    public SettingsPanel()
    {
        InitializeComponent();

        SamplingModeBox.Items.Add("Vivid — saturated colors stand out");
        SamplingModeBox.Items.Add("Standard — most frequent colors by area");
        SamplingModeBox.Items.Add("Contrast — vivid mid-lightness colors");

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

        WorkingQualityBox.Items.Add("Fast — 2000px, quickest");
        WorkingQualityBox.Items.Add("Balanced — 3400px");
        WorkingQualityBox.Items.Add("Best — 4800px, closer to desktop detail");

        OutputFormatBox.Items.Add("PNG — lossless, larger file");
        OutputFormatBox.Items.Add("JPEG — smaller file");

        ExportPresetBox.Items.Add("Original size");
        ExportPresetBox.Items.Add("Square (1080×1080)");
        ExportPresetBox.Items.Add("Instagram post (1080×1350)");
        ExportPresetBox.Items.Add("Story (1080×1920) — IG/TG");
        ExportPresetBox.Items.Add("Telegram landscape (1920×1080)");
    }
}
