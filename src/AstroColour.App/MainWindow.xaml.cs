using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using AstroColour.App.Controls;
using AstroColour.Core.Colour;
using AstroColour.Core.Xisf;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AstroColour.App;

public sealed partial class MainWindow : Window
{
    private const string AppTitle = "Astro Colour Editor";

    private readonly ObservableCollection<AdjustmentItem> _adjustments = [];

    // Images longer than this are previewed at reduced size while sliders move.
    private const int ProxyMaxSide = 2400;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private XisfFile? _image;
    private string? _path;
    private PreviewLayer? _full;
    private PreviewLayer? _proxy;

    private readonly DispatcherQueueTimer _settleTimer;
    private CancellationTokenSource? _fullRenderCancellation;
    private bool _rendering;
    private bool _proxyPending;
    private bool _fullPending;
    private bool _fullAllowed = true;
    private bool _suppressEditorEvents;
    private bool _isDirty;
    private bool _overwriteConfirmed;
    private bool _closeConfirmed;
    private bool _dialogOpen;

    private const double MaxImageDips = 9000;
    private double _dipsPerPixel = 1;

    private bool _panning;
    private Point _panStart;
    private double _panStartH, _panStartV;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1500, 950));
        AppWindow.Closing += AppWindow_Closing;
        AdjustmentList.ItemsSource = _adjustments;
        TargetHueSlider.GradientBrush = HueRamp(0, 360);

        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = SettleDelay;
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) =>
        {
            _fullAllowed = true;
            if (_fullPending && !_rendering) _ = RenderLoopAsync();
        };

        UpdateChrome();
    }

    /// <summary>A rendered view of the image: its source planes, an 8-bit buffer and the bitmap shown on screen.</summary>
    private sealed class PreviewLayer(RgbImage source)
    {
        public RgbImage Source { get; } = source;
        public byte[] Pixels { get; } = new byte[source.PixelCount * 4];
        public WriteableBitmap Bitmap { get; } = new(source.Width, source.Height);
    }

    public void OpenOnStartup(string path) => _ = LoadAsync(path);

    private AdjustmentItem? Selected => AdjustmentList.SelectedItem as AdjustmentItem;

    // ------------------------------------------------------------ open / save

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardChangesAsync()) return;

        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".xisf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await LoadAsync(file.Path);
    }

    private async Task LoadAsync(string path)
    {
        SetBusy("Loading image…");
        try
        {
            var (image, rgb, proxy, median) = await Task.Run(() =>
            {
                var img = XisfFile.Load(path);
                var full = RgbImage.FromXisf(img);
                int factor = (int)Math.Ceiling(Math.Max(img.Width, img.Height) / (double)ProxyMaxSide);
                return (img, full, factor > 1 ? full.Downsample(factor) : null, ColourProcessor.EstimateMedian(full));
            });

            _image = image;
            _path = path;
            _isDirty = false;
            _overwriteConfirmed = false;
            _adjustments.Clear();
            ShowMaskToggle.IsOn = false;
            CompareButton.IsChecked = false;

            _full = new PreviewLayer(rgb);
            _proxy = proxy is null ? null : new PreviewLayer(proxy);
            _proxyPending = false;
            PreviewImage.Source = _full.Bitmap;
            // Size the image so zoom 1 shows actual pixels, capped so that fitting a very large
            // image never needs a zoom below the ScrollViewer's minimum of 0.1.
            _dipsPerPixel = Math.Min(1.0 / RasterizationScale, MaxImageDips / Math.Max(image.Width, image.Height));
            PreviewImage.Width = image.Width * _dipsPerPixel;
            PreviewImage.Height = image.Height * _dipsPerPixel;

            LinearInfo.IsOpen = image.IsColour && median < 0.03f;
            MessageInfo.IsOpen = false;
            if (!image.IsColour)
                ShowMessage(InfoBarSeverity.Warning, "Monochrome image", "This image has a single channel, so it has no colours to edit.");

            RequestRender();
            UpdateChrome();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitToWindow);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not open the image", ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: false);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(saveAs: true);

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_image is null) return false;

        string? target = _path;
        if (!saveAs && target is not null && !_overwriteConfirmed)
        {
            var choice = await ShowDialogAsync(new ContentDialog
            {
                Title = "Overwrite the original file?",
                Content = $"{Path.GetFileName(target)} will be replaced with the edited image. Choose “Save as” to keep the original.",
                PrimaryButtonText = "Overwrite",
                SecondaryButtonText = "Save as…",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Secondary,
            });
            if (choice == ContentDialogResult.None) return false;
            if (choice == ContentDialogResult.Secondary) saveAs = true;
            else _overwriteConfirmed = true;
        }

        if (saveAs || target is null)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = Path.GetFileNameWithoutExtension(_path ?? "image") + "_colour",
            };
            picker.FileTypeChoices.Add("XISF image", [".xisf"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;
            target = file.Path;
            _overwriteConfirmed = true;
        }

        SetBusy("Saving…");
        try
        {
            var image = _image;
            var snapshot = _adjustments.Select(a => a.Model.Clone()).ToArray();
            await Task.Run(() => image.Save(target, ColourProcessor.Apply(image, snapshot)));
            _path = target;
            _isDirty = false;
            UpdateChrome();
            ShowMessage(InfoBarSeverity.Success, "Saved", target);
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not save the image", ex.Message);
            return false;
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !_isDirty) return;
        args.Cancel = true;
        if (await ConfirmDiscardChangesAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>Asks to save unsaved edits. Returns false if the user cancelled.</summary>
    private async Task<bool> ConfirmDiscardChangesAsync()
    {
        if (!_isDirty || _path is null) return true;
        var choice = await ShowDialogAsync(new ContentDialog
        {
            Title = "Save your changes?",
            Content = $"The colour adjustments to {Path.GetFileName(_path)} have not been saved.",
            PrimaryButtonText = "Save as…",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        return choice switch
        {
            ContentDialogResult.Primary => await SaveAsync(saveAs: true),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    // ---------------------------------------------------------------- render

    /// <summary>
    /// Queues a preview render. Interactive changes (slider drags) render the reduced-size
    /// preview immediately and defer the full-resolution render until input settles; bursts
    /// of changes coalesce into one render.
    /// </summary>
    private void RequestRender(bool interactive = false)
    {
        _fullPending = true;
        if (interactive && _proxy is not null)
        {
            _proxyPending = true;
            _fullAllowed = false;
            _fullRenderCancellation?.Cancel();
            _settleTimer.Stop();
            _settleTimer.Start();
        }
        else
        {
            _fullAllowed = true;
            _settleTimer.Stop();
        }
        if (!_rendering) _ = RenderLoopAsync();
    }

    private async Task RenderLoopAsync()
    {
        _rendering = true;
        try
        {
            while (_full is { } full)
            {
                PreviewLayer layer;
                if (_proxyPending && _proxy is { } proxy)
                {
                    _proxyPending = false;
                    layer = proxy;
                }
                else if (_fullPending && _fullAllowed)
                {
                    _fullPending = false;
                    layer = full;
                }
                else
                {
                    break;
                }

                var snapshot = _adjustments.Select(a => a.Model.Clone()).ToArray();
                var mask = Selected?.Model.Clone();
                var mode = CompareButton.IsChecked == true ? PreviewMode.Original
                    : ShowMaskToggle.IsOn && mask is not null ? PreviewMode.Mask
                    : PreviewMode.Adjusted;

                var cancellation = new CancellationTokenSource();
                if (layer == full) _fullRenderCancellation = cancellation;
                try
                {
                    await Task.Run(() => ColourProcessor.RenderPreview(layer.Source, snapshot, mode, mask, layer.Pixels, cancellation.Token));
                }
                catch (OperationCanceledException)
                {
                    _fullPending = true; // superseded by a slider move; re-rendered once input settles
                    continue;
                }
                finally
                {
                    if (_fullRenderCancellation == cancellation) _fullRenderCancellation = null;
                    cancellation.Dispose();
                }

                if (full != _full) continue; // a different file was opened meanwhile
                layer.Pixels.CopyTo(layer.Bitmap.PixelBuffer);
                layer.Bitmap.Invalidate();
                if (PreviewImage.Source != layer.Bitmap) PreviewImage.Source = layer.Bitmap;
            }
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Preview failed", ex.Message);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void Compare_Click(object sender, RoutedEventArgs e) => RequestRender();

    private void ShowMaskToggle_Toggled(object sender, RoutedEventArgs e) => RequestRender();

    // ---------------------------------------------------------- colour editor

    private void Picker_Click(object sender, RoutedEventArgs e) => UpdateCursor();

    private void PickColour(Point imagePoint)
    {
        if (_full is null) return;
        var source = _full.Source;
        int x = (int)(imagePoint.X / _dipsPerPixel), y = (int)(imagePoint.Y / _dipsPerPixel);
        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return;

        var (r, g, b) = ColourProcessor.Sample(source, x, y, radius: 3);
        ColourMath.RgbToHsl(r, g, b, out float hue, out _, out _, out float chroma);
        if (chroma < 0.01f)
        {
            ShowMessage(InfoBarSeverity.Informational, "That spot is neutral",
                "The area you clicked is almost grey, so it has no hue to select. Try a more colourful part of the image.");
            return;
        }

        // Re-picking while the selected entry is still unedited retargets it rather than adding another.
        var item = Selected;
        if (item is null || item.Model.HasEdits)
        {
            item = new AdjustmentItem(new ColourAdjustment());
            item.ActiveChanged += (_, _) => { MarkDirty(); RequestRender(); };
            _adjustments.Add(item);
        }
        item.Model.TargetHue = hue;
        item.Model.SampleR = r;
        item.Model.SampleG = g;
        item.Model.SampleB = b;
        item.Refresh();

        AdjustmentList.SelectedItem = item;
        LoadEditor(item);
        PickerButton.IsChecked = false;
        UpdateCursor();
        RequestRender();
    }

    private void AdjustmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadEditor(Selected);
        if (ShowMaskToggle.IsOn) RequestRender();
    }

    private void LoadEditor(AdjustmentItem? item)
    {
        EditorPanel.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (item is null) return;

        _suppressEditorEvents = true;
        var m = item.Model;
        TargetHueSlider.Value = Math.Round(m.TargetHue) % 360;
        RangeSlider.Value = m.Range;
        SmoothnessSlider.Value = m.Smoothness;
        ProtectSlider.Value = m.NeutralProtection;
        HueShiftSlider.Value = m.HueShift;
        SaturationSlider.Value = m.Saturation;
        LightnessSlider.Value = m.Lightness;
        TargetHueSlider.DefaultValue = Math.Round(ColourMath.Hue(m.SampleR, m.SampleG, m.SampleB)) % 360;
        _suppressEditorEvents = false;
        UpdateSliderRamps();
    }

    private void EditorSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || Selected is not { } item) return;

        var m = item.Model;
        m.TargetHue = (float)TargetHueSlider.Value;
        m.Range = (float)RangeSlider.Value;
        m.Smoothness = (float)SmoothnessSlider.Value;
        m.NeutralProtection = (float)ProtectSlider.Value;
        m.HueShift = (float)HueShiftSlider.Value;
        m.Saturation = (float)SaturationSlider.Value;
        m.Lightness = (float)LightnessSlider.Value;
        item.Refresh();

        if (ReferenceEquals(sender, TargetHueSlider)) UpdateSliderRamps();
        MarkDirty();
        RequestRender(interactive: true);
    }

    private void ResetAdjustment_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        item.Model.ResetEdits();
        item.Refresh();
        LoadEditor(item);
        MarkDirty();
        RequestRender();
    }

    private void DeleteAdjustment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AdjustmentItem item) return;
        int index = _adjustments.IndexOf(item);
        _adjustments.Remove(item);
        if (_adjustments.Count > 0)
            AdjustmentList.SelectedIndex = Math.Min(index, _adjustments.Count - 1);
        if (item.Model.HasEdits) MarkDirty();
        RequestRender();
    }

    private void UpdateSliderRamps()
    {
        float h = (float)TargetHueSlider.Value;
        HueShiftSlider.GradientBrush = HueRamp(h - 90, h + 90);
        SaturationSlider.GradientBrush = Ramp(Hsl(h, 0, 0.5f), Hsl(h, 1, 0.5f));
        LightnessSlider.GradientBrush = Ramp(Hsl(h, 0.8f, 0.1f), Hsl(h, 0.8f, 0.5f), Hsl(h, 0.8f, 0.9f));
    }

    private static LinearGradientBrush HueRamp(float from, float to)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        const int steps = 12;
        for (int i = 0; i <= steps; i++)
            brush.GradientStops.Add(new GradientStop { Color = Hsl(from + (to - from) * i / steps, 1, 0.5f), Offset = (double)i / steps });
        return brush;
    }

    private static LinearGradientBrush Ramp(params Color[] colours)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        for (int i = 0; i < colours.Length; i++)
            brush.GradientStops.Add(new GradientStop { Color = colours[i], Offset = (double)i / (colours.Length - 1) });
        return brush;
    }

    private static Color Hsl(float h, float s, float l)
    {
        ColourMath.HslToRgb(h, s, l, out float r, out float g, out float b);
        return Color.FromArgb(255, (byte)(r * 255 + 0.5f), (byte)(g * 255 + 0.5f), (byte)(b * 255 + 0.5f));
    }

    // ------------------------------------------------------------ navigation

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        if (_image is null) return;
        float factor = (float)(1.0 / (_dipsPerPixel * RasterizationScale));
        ZoomAround(new Point(Viewer.ViewportWidth / 2, Viewer.ViewportHeight / 2), factor);
    }

    private void FitToWindow()
    {
        if (_image is null || Viewer.ViewportWidth <= 0) return;
        double factor = FitFactor(Viewer.ViewportWidth, Viewer.ViewportHeight);
        factor = Math.Clamp(factor, Viewer.MinZoomFactor, Viewer.MaxZoomFactor);
        Viewer.ChangeView(0, 0, (float)factor, disableAnimation: true);
    }

    private void ZoomAround(Point viewportPoint, float newFactor)
    {
        newFactor = Math.Clamp(newFactor, Viewer.MinZoomFactor, Viewer.MaxZoomFactor);
        float oldFactor = Viewer.ZoomFactor;
        double contentX = (Viewer.HorizontalOffset + viewportPoint.X) / oldFactor;
        double contentY = (Viewer.VerticalOffset + viewportPoint.Y) / oldFactor;
        Viewer.ChangeView(contentX * newFactor - viewportPoint.X, contentY * newFactor - viewportPoint.Y, newFactor, disableAnimation: true);
    }

    private void ImageHost_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewer);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomAround(point.Position, Viewer.ZoomFactor * (delta > 0 ? 1.25f : 0.8f));
        e.Handled = true;
    }

    private void ImageHost_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_image is null) return;
        var properties = e.GetCurrentPoint(ImageHost).Properties;

        if (PickerButton.IsChecked == true && properties.IsLeftButtonPressed)
        {
            PickColour(e.GetCurrentPoint(PreviewImage).Position);
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetCurrentPoint(Viewer).Position;
        _panStartH = Viewer.HorizontalOffset;
        _panStartV = Viewer.VerticalOffset;
        ImageHost.CapturePointer(e.Pointer);
        UpdateCursor();
        e.Handled = true;
    }

    private void ImageHost_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panning)
        {
            var p = e.GetCurrentPoint(Viewer).Position;
            Viewer.ChangeView(_panStartH - (p.X - _panStart.X), _panStartV - (p.Y - _panStart.Y), null, disableAnimation: true);
        }
        UpdatePixelInfo(e.GetCurrentPoint(PreviewImage).Position);
    }

    private void ImageHost_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ImageHost.ReleasePointerCapture(e.Pointer);
        EndPan();
    }

    private void ImageHost_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndPan();

    private void ImageHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) PixelInfoText.Text = "";
    }

    private void EndPan()
    {
        _panning = false;
        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_panning) ImageHost.SetCursor(InputSystemCursorShape.SizeAll);
        else if (PickerButton.IsChecked == true) ImageHost.SetCursor(RingCursor.Get(RasterizationScale));
        else ImageHost.SetCursor(InputSystemCursorShape.Arrow);
    }

    private void Viewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateZoomText();

    private void Viewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the whole image visible while the window is resized, if it was fitted.
        if (_image is null || e.PreviousSize.Width <= 0) return;
        double previousFit = FitFactor(e.PreviousSize.Width, e.PreviousSize.Height);
        if (Math.Abs(Viewer.ZoomFactor - previousFit) < 0.002) FitToWindow();
    }

    private double FitFactor(double viewportWidth, double viewportHeight) =>
        Math.Min(viewportWidth / PreviewImage.Width, viewportHeight / PreviewImage.Height) * 0.98;

    private double RasterizationScale => Content.XamlRoot?.RasterizationScale ?? 1.0;

    // ------------------------------------------------------------- UI helpers

    private void UpdatePixelInfo(Point p)
    {
        if (_image is null) return;
        int x = (int)(p.X / _dipsPerPixel), y = (int)(p.Y / _dipsPerPixel);
        if (x < 0 || y < 0 || x >= _image.Width || y >= _image.Height)
        {
            PixelInfoText.Text = "";
            return;
        }
        int i = y * _image.Width + x;
        float r = _image.Planes[0][i];
        float g = _image.IsColour ? _image.Planes[1][i] : r;
        float b = _image.IsColour ? _image.Planes[2][i] : r;
        float hue = ColourMath.Hue(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
        PixelInfoText.Text = $"x {x,5}  y {y,5}   R {r:0.000}  G {g:0.000}  B {b:0.000}   H {hue,3:0}°";
    }

    private void UpdateZoomText() => ZoomText.Text = _image is null
        ? ""
        : $"{Viewer.ZoomFactor * _dipsPerPixel * RasterizationScale:0%}";

    private void MarkDirty()
    {
        if (_isDirty) return;
        _isDirty = true;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool loaded = _image is not null;
        EmptyState.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        PanelScroller.IsEnabled = loaded && _image!.IsColour;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = FitButton.IsEnabled =
            ActualSizeButton.IsEnabled = CompareButton.IsEnabled = loaded;

        Title = _path is null ? AppTitle : $"{Path.GetFileName(_path)}{(_isDirty ? " •" : "")} — {AppTitle}";
        FileInfoText.Text = _image is null
            ? ""
            : $"{_path}    {_image.Width} × {_image.Height}    {_image.Channels} ch {_image.ColorSpace}    {_image.SampleFormat}";
        UpdateZoomText();
    }

    private void SetBusy(string message)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;
        Toolbar.IsEnabled = false;
    }

    private void ClearBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        Toolbar.IsEnabled = true;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageInfo.Severity = severity;
        MessageInfo.Title = title;
        MessageInfo.Message = message;
        MessageInfo.IsOpen = true;
    }

    private async Task ShowErrorAsync(string title, string message) =>
        await ShowDialogAsync(new ContentDialog { Title = title, Content = message, CloseButtonText = "OK" });

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = Content.XamlRoot;
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
