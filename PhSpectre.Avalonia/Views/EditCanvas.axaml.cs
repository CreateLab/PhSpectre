using System;
using System.ComponentModel;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Media;
using PhSpectre.Avalonia.ViewModels;

namespace PhSpectre.Avalonia.Views;

public partial class EditCanvas : UserControl
{
    private readonly Grid? _host;
    private readonly Border? _window;
    private readonly Canvas? _canvas;
    private readonly Image? _image;
    private ProcessViewModel? _vm;
    private Point? _last;
    private double _dispW, _dispH;

    public EditCanvas()
    {
        InitializeComponent();
        _host = this.FindControl<Grid>("CropHost");
        _window = this.FindControl<Border>("CropWindow");
        _canvas = this.FindControl<Canvas>("CropCanvas");
        _image = this.FindControl<Image>("CropImage");
        if (_host == null) return;
        _host.SizeChanged += (_, _) => Relayout();
        _host.PointerPressed += OnPressed;
        _host.PointerMoved += OnMoved;
        _host.PointerReleased += OnReleased;
        _host.PointerCaptureLost += (_, _) => _last = null;
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as ProcessViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            Relayout();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProcessViewModel.Quarter) or nameof(ProcessViewModel.Straighten) or nameof(ProcessViewModel.Zoom)
            or nameof(ProcessViewModel.CropIndex) or nameof(ProcessViewModel.PanX) or nameof(ProcessViewModel.PanY)
            or nameof(ProcessViewModel.CropBase) or nameof(ProcessViewModel.ShowCropView))
            Relayout();
    }

    // Window = the crop's shape fitted into the stage; photo = the uncropped picture at the same scale,
    // rotated by the quarter turns plus the straighten angle and shifted by the pan. Pure transforms: no pixels are rebuilt.
    private void Relayout()
    {
        if (_vm is not { ShowCropView: true, CropBase: { } bmp } || _host == null || _window == null || _canvas == null || _image == null) return;
        var (cw, ch) = _vm.CropOutputSize();
        double aw = _host.Bounds.Width, ah = _host.Bounds.Height;
        if (cw <= 0 || ch <= 0 || aw <= 0 || ah <= 0) return;
        double k = Math.Min(aw / cw, ah / ch);
        _dispW = cw * k; _dispH = ch * k;
        _window.Width = _canvas.Width = _dispW;
        _window.Height = _canvas.Height = _dispH;
        double iw = bmp.PixelSize.Width * k, ih = bmp.PixelSize.Height * k;
        _image.Width = iw; _image.Height = ih;
        Canvas.SetLeft(_image, (_dispW - iw) / 2);
        Canvas.SetTop(_image, (_dispH - ih) / 2);
        _image.RenderTransform = new TransformGroup
        {
            Children =
            {
                new RotateTransform(_vm.Quarter * 90 + _vm.Straighten),
                new TranslateTransform(-_vm.PanX * _dispW, -_vm.PanY * _dispH),
            }
        };
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_host == null || !e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) return;
        _last = e.GetPosition(_host);
        e.Pointer.Capture(_host);
        _host.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_host == null || _last is not { } last || _vm == null || _dispW <= 0 || _dispH <= 0) return;
        var p = e.GetPosition(_host);
        _vm.PanBy((p.X - last.X) / _dispW, (p.Y - last.Y) / _dispH);
        _last = p;
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_host == null) return;
        _last = null;
        e.Pointer.Capture(null);
        _host.Cursor = Cursor.Default;
    }
}
