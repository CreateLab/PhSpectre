using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PhSpectre.Avalonia.ViewModels;

// The histogram over the photo and the Auto button that moves the Light sliders by it.
public partial class ProcessViewModel
{
    [ObservableProperty] private bool _showHistogram;
    // Of the untouched photo and of the rendered result, measured before the border or film edge.
    private ImageHistogram? _originalHistogram, _resultHistogram;

    /// <summary>The histogram of what the canvas shows.</summary>
    public ImageHistogram? Histogram => ShowOriginal || Graded == null ? _originalHistogram : _resultHistogram;
    public bool ShowHistogramPanel => ShowHistogram && HasPhoto && !LookIsCrop && !IsDetailActive && Histogram != null;
    public double HistogramWidth => PhoneStage ? 140 : 220;
    public double HistogramHeight => PhoneStage ? 70 : 110;

    private void RaiseHistogram()
    {
        OnPropertyChanged(nameof(Histogram)); OnPropertyChanged(nameof(ShowHistogramPanel));
    }

    partial void OnShowHistogramChanged(bool value)
    {
        RaiseHistogram();
        if (!value) return;
        _ = MeasureOriginal();
        ScheduleRender();
    }

    private void ResetHistogram()
    {
        _originalHistogram = _resultHistogram = null;
        RaiseHistogram();
        if (ShowHistogram) _ = MeasureOriginal();
    }

    private async Task MeasureOriginal()
    {
        var src = _source;
        if (src == null || _originalHistogram != null) return;
        // A copy: the source is disposed when the photo changes.
        var copy = src.Clone();
        var measured = await Task.Run(() => { using (copy) return ImageHistogram.Compute(copy); });
        if (!ReferenceEquals(src, _source)) return;
        _originalHistogram = measured;
        RaiseHistogram();
    }

    // Reads the cropped photo without tone or Look, so pressing it again gives the same positions.
    [RelayCommand]
    private async Task AutoLight()
    {
        var src = _source;
        if (src == null) return;
        var geometry = CurrentAdjustSettings().WithoutTone().WithoutCleaning();
        var copy = src.Clone();
        var auto = await Task.Run(() =>
        {
            using (copy)
            {
                using var framed = geometry.Render(copy);
                return AutoTone.Suggest(ImageHistogram.Compute(framed));
            }
        });
        if (!ReferenceEquals(src, _source)) return;
        CommitNow(); // a slider moved just before stays its own undo step
        ApplyGroup(() =>
        {
            Exposure = auto.Exposure; Contrast = auto.Contrast; Highlights = auto.Highlights;
            Shadows = auto.Shadows; Whites = auto.Whites; Blacks = auto.Blacks;
        });
    }
}
