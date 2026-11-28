using PhSpectre.Localization;
using System;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PhSpectre.ColorGrading;
using PhSpectre.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhSpectre.Avalonia.ViewModels;

// The 100% view: the preview is a small copy, which hides noise, so this processes a piece of the full-size photo
// like the export.
public partial class ProcessViewModel
{
    // A bigger piece would make dragging a slider slow.
    private const int DetailMaxWidth = 1280, DetailMaxHeight = 960;

    private sealed class DetailSource(string path)
    {
        public readonly string Path = path;
        public Image<Rgb24>? Photo;
        public NoiseReduction.NoiseProfile? Noise;
        public SkinMask? Skin;
        public bool Released;

        public void Release()
        {
            lock (this)
            {
                Released = true;
                Photo?.Dispose(); Photo = null;
            }
        }
    }

    private DetailSource? _detailSource;
    private (int W, int H) _detailViewport;
    // Middle of the shown piece as a share of the photo's width and height.
    private double _detailX = 0.5, _detailY = 0.5;

    [ObservableProperty] private bool _detailView;
    [ObservableProperty] private Bitmap? _detail;

    /// <summary>The canvas is showing the 100% view (it may still be loading).</summary>
    public bool IsDetailActive => DetailView && (LookIsLight || LookIsColor || LookIsEffects)
        && _settings.EditTab == 0 && _source != null;
    public bool ShowDetail => IsDetailActive && Detail != null;
    public string DetailHint => Detail == null ? Loc.T("Loading the full photo…") : Loc.T("100% · drag to move");

    partial void OnDetailViewChanged(bool value)
    {
        if (!value) ReleaseDetail();
        DetailStateChanged();
    }

    partial void OnDetailChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(DetailHint));
        RaiseDetailVisibility();
    }

    private void RaiseDetailVisibility()
    {
        OnPropertyChanged(nameof(IsDetailActive)); OnPropertyChanged(nameof(ShowDetail)); OnPropertyChanged(nameof(ShowPhotoView));
        RaiseHistogram();
    }

    private void DetailStateChanged()
    {
        if (!IsDetailActive) DropDetailPixels();
        RaiseDetailVisibility();
        ScheduleRender();
    }

    // Frees the full-size pixels.
    private void ReleaseDetail()
    {
        _detailX = _detailY = 0.5;
        DropDetailPixels();
    }

    private void DropDetailPixels()
    {
        var old = _detailSource;
        _detailSource = null;
        Detail = null;
        // A render may be reading the pixels; wait off the UI thread.
        if (old != null) _ = Task.Run(old.Release);
    }

    public void SetDetailViewport(int width, int height)
    {
        if (_detailViewport == (width, height)) return;
        _detailViewport = (width, height);
        if (IsDetailActive) ScheduleDetail();
    }

    /// <summary>Drag on the 100% view, in photo pixels as seen on screen.</summary>
    public void PanDetail(double dx, double dy)
    {
        if (_fullSize.Width <= 0 || _fullSize.Height <= 0) return;
        // The view is turned with the photo; the position is kept on the unturned one.
        (dx, dy) = Quarter switch { 1 => (dy, -dx), 2 => (-dx, -dy), 3 => (-dy, dx), _ => (dx, dy) };
        _detailX = Math.Clamp(_detailX - dx / _fullSize.Width, 0, 1);
        _detailY = Math.Clamp(_detailY - dy / _fullSize.Height, 0, 1);
        ScheduleDetail();
    }

    private void ScheduleDetail()
    {
        var path = _loadedPath;
        var (vw, vh) = _detailViewport;
        if (path == null || vw <= 0 || vh <= 0) return;
        if (_detailSource?.Path != path) _detailSource = new DetailSource(path);
        var source = _detailSource;
        // "Original" shows the piece untouched.
        var adjust = ShowOriginal ? new PhotoAdjust() : CurrentAdjustSettings();
        var look = ShowOriginal ? new GradeStack() : BuildStack();
        int quarter = Quarter;
        double x = _detailX, y = _detailY;
        IsRendering = true;
        int epoch = _renderEpoch;
        EnqueueRender(async () =>
        {
            try
            {
                var shown = await Task.Run(() => RenderDetail(source, adjust, look, quarter, x, y, vw, vh));
                if (shown != null && epoch == _renderEpoch && ReferenceEquals(source, _detailSource)) Detail = shown;
            }
            catch (Exception ex)
            {
                Services.AppLogger.LogError("100% view failed", ex);
                if (ReferenceEquals(source, _detailSource)) DetailView = false;
            }
            finally { if (_pendingRender == null) IsRendering = false; }
        });
    }

    private static WriteableBitmap? RenderDetail(DetailSource source, PhotoAdjust adjust, GradeStack look,
        int quarter, double centerX, double centerY, int viewW, int viewH)
    {
        lock (source)
        {
            if (source.Released) return null;
            var photo = source.Photo ??= Services.FastDecode.LoadUpright(source.Path);

            // A quarter turn swaps the viewport's sides on the photo.
            if (quarter % 2 == 1) (viewW, viewH) = (viewH, viewW);
            int w = Math.Min(photo.Width, Math.Min(viewW, DetailMaxWidth)), h = Math.Min(photo.Height, Math.Min(viewH, DetailMaxHeight));
            int left = Math.Clamp((int)(centerX * photo.Width) - w / 2, 0, photo.Width - w);
            int top = Math.Clamp((int)(centerY * photo.Height) - h / 2, 0, photo.Height - h);

            // Margin around the piece, aligned to the noise reduction grid.
            bool smoothsSkin = adjust.HasSkinSmoothing || look.SmoothsSkin;
            int context = smoothsSkin ? SkinRetouch.Context : NoiseReduction.Context;
            const int align = NoiseReduction.Align;
            int x0 = Math.Max(0, left - context) / align * align, y0 = Math.Max(0, top - context) / align * align;
            var rect = new Rectangle(x0, y0, Math.Min(photo.Width, left + w + context) - x0, Math.Min(photo.Height, top + h + context) - y0);
            using var piece = photo.Clone(c => c.Crop(rect));

            var noise = source.Noise ??= NoiseReduction.Measure(photo);
            // Segmented on the whole photo: a piece alone would be judged differently.
            if (smoothsSkin) source.Skin ??= SkinRetouch.Segmenter?.Segment(photo);
            var skin = smoothsSkin ? source.Skin?.Piece(rect, photo.Width, photo.Height) : null;
            // The export sizes sharpening by the cropped picture.
            var output = adjust.OutputSize(photo.Width, photo.Height);
            int longSide = Math.Max(output.Width, output.Height);
            using var result = adjust.RenderDetail(piece, longSide, noise, skin);
            if (!look.IsEmpty)
                look.Apply(result, wholeFrameEffects: false, photoLongSide: longSide, noise: noise, skin: skin);
            result.Mutate(c =>
            {
                c.Crop(new Rectangle(left - x0, top - y0, w, h));
                if (quarter != 0) c.Rotate(quarter switch { 1 => RotateMode.Rotate90, 2 => RotateMode.Rotate180, _ => RotateMode.Rotate270 });
            });
            return ToWriteable(result);
        }
    }
}
