using Avalonia.Controls;

namespace PhSpectre.Avalonia.Views;

public partial class SettingsPanel : UserControl
{
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
    }

    private static void AddItem(ComboBox box, string shortText, string fullText)
    {
        var item = new ComboBoxItem { Content = shortText };
        ToolTip.SetTip(item, fullText);
        box.Items.Add(item);
    }
}
