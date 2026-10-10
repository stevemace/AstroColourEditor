using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AstroColour.Core;
using AstroColour.Core.Colour;
using AstroColour.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AstroColour.Desktop;

public partial class MainWindow : Window
{
    private const string AppTitle = "Astro Colour Editor";

    private readonly ObservableCollection<AdjustmentItem> _adjustments = [];

    // Images longer than this are previewed at reduced size while sliders move.
    private const int ProxyMaxSide = 2400;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private IAstroImage? _image;
    private string? _path;
    private PreviewLayer? _full;
    private PreviewLayer? _proxy;

    private readonly DispatcherTimer _settleTimer;
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
    private bool _fileActionActive;

    // Zoom limits, in image pixels per screen pixel.
    private const double MinZoom = 0.01, MaxZoom = 32;

    // Displayed size of one image pixel, in DIPs.
    private double _zoom = 1;

    private bool _panning;
    private Point _panStart;
    private Vector _panStartOffset;

    // Last position and size while neither maximized nor minimized, saved on close.
    private PixelPoint _restoredPosition;
    private Size _restoredSize;

    private readonly List<NativeMenuItem> _imageMenuItems = [];

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        PositionChanged += (_, _) => TrackRestoredBounds();
        AdjustmentList.ItemsSource = _adjustments;
        TargetHueSlider.GradientBrush = HueRamp(0, 360);

        SetShortcutTip(OpenButton, "Open an XISF, FITS or TIFF image", "O");
        SetShortcutTip(SaveButton, "Save over the current file", "S");
        SetShortcutTip(SaveAsButton, "Save to a new file", "S", shift: true);
        SetShortcutTip(FitButton, "Fit the image to the window", "0");
        SetShortcutTip(ActualSizeButton, "View at actual pixels", "1");
        ToolTip.SetTip(CompareButton, "Show the original, unedited image (B)");
        ToolTip.SetTip(PickerButton, "Pick a colour from the image (P)");
        if (OperatingSystem.IsMacOS()) BuildMacMenu();

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
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
        public WriteableBitmap Bitmap { get; } =
            new(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    }

    /// <summary>Opens a file passed on the command line or from Finder, after offering to save any edits.</summary>
    public void OpenFromShell(string path) => _ = RunFileActionAsync(async () =>
    {
        if (await ConfirmDiscardChangesAsync()) await LoadAsync(path);
    });

    private AdjustmentItem? Selected => AdjustmentList.SelectedItem as AdjustmentItem;

    // ------------------------------------------------------------ open / save

    /// <summary>Runs an open or save, ignoring requests that arrive while one is already in progress.</summary>
    private async Task RunFileActionAsync(Func<Task> action)
    {
        if (_fileActionActive) return;
        _fileActionActive = true;
        try
        {
            await action();
        }
        finally
        {
            _fileActionActive = false;
        }
    }

    private void Open_Click(object? sender, RoutedEventArgs e) => _ = OpenAsync();

    private Task OpenAsync() => RunFileActionAsync(async () =>
    {
        if (!await ConfirmDiscardChangesAsync()) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open image",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("XISF, FITS or TIFF image") { Patterns = [.. AstroImageFile.AllExtensions.Select(x => "*" + x)] }],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path) await LoadAsync(path);
    });

    /// <summary>The current file's folder, or the Pictures folder if no file is open.</summary>
    private async Task<IStorageFolder?> StartFolderAsync()
    {
        try
        {
            if (Path.GetDirectoryName(_path) is { } folder && await StorageProvider.TryGetFolderFromPathAsync(folder) is { } current)
                return current;
            return await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Pictures);
        }
        catch
        {
            return null; // Let the picker choose.
        }
    }

    private async Task LoadAsync(string path)
    {
        SetBusy("Loading image…");
        try
        {
            var (image, rgb, proxy, median) = await Task.Run(() =>
            {
                var img = AstroImageFile.Load(path);
                var full = RgbImage.From(img);
                int factor = (int)Math.Ceiling(Math.Max(img.Width, img.Height) / (double)ProxyMaxSide);
                return (img, full, factor > 1 ? full.Downsample(factor) : null, ColourProcessor.EstimateMedian(full));
            });

            _image = image;
            _path = path;
            _isDirty = false;
            _overwriteConfirmed = false;
            _adjustments.Clear();
            ShowMaskToggle.IsChecked = false;
            CompareButton.IsChecked = false;

            _full = new PreviewLayer(rgb);
            _proxy = proxy is null ? null : new PreviewLayer(proxy);
            _proxyPending = false;
            PreviewImage.Source = _full.Bitmap;
            SetZoom(1 / RenderScaling);

            LinearInfo.IsOpen = image.IsColour && median < 0.03f;
            MessageInfo.IsOpen = false;
            if (!image.IsColour)
                ShowMessage(InfoBarSeverity.Warning, "Monochrome image", "This image has a single channel, so it has no colours to edit.");

            RequestRender();
            UpdateChrome();
            Dispatcher.UIThread.Post(FitToWindow, DispatcherPriority.Background);
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

    private void Save_Click(object? sender, RoutedEventArgs e) => _ = RunFileActionAsync(() => SaveAsync(saveAs: false));

    private void SaveAs_Click(object? sender, RoutedEventArgs e) => _ = RunFileActionAsync(() => SaveAsync(saveAs: true));

    private async Task<bool> SaveAsync(bool saveAs)
    {
        if (_image is null || _path is null) return false;

        string target = _path;
        if (!saveAs && !_overwriteConfirmed)
        {
            var choice = await ShowDialogAsync("Overwrite the original file?",
                $"{Path.GetFileName(target)} will be replaced with the edited image. Choose “Save as” to keep the original.",
                "Overwrite", "Save as…", "Cancel", DialogResult.Secondary);
            if (choice == DialogResult.None) return false;
            if (choice == DialogResult.Secondary) saveAs = true;
            else _overwriteConfirmed = true;
        }

        if (saveAs)
        {
            // Edits are saved in the format they were loaded from.
            var (formatName, extensions) = AstroImageFile.SaveFormat(_path);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save as",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + "_colour" + extensions[0],
                DefaultExtension = extensions[0].TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(formatName) { Patterns = [.. extensions.Select(x => "*" + x)] }],
                SuggestedStartLocation = await StartFolderAsync(),
                ShowOverwritePrompt = true,
            });
            if (file?.TryGetLocalPath() is not { } picked) return false;
            target = picked;
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

    private void RestorePlacement()
    {
        if (WindowPlacement.Load(Screens.All) is { } placement)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(placement.X, placement.Y);
            Width = placement.Width;
            Height = placement.Height;
            if (placement.Maximized) WindowState = WindowState.Maximized;
        }
        else
        {
            // Default size, shrunk to fit small displays such as a 13" laptop.
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (Screens.Primary is { } screen)
            {
                Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling * 0.92);
                Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling * 0.92);
            }
        }
        _restoredSize = new Size(Width, Height);
        _restoredPosition = Position;
    }

    private void TrackRestoredBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        _restoredPosition = Position;
        _restoredSize = ClientSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty) TrackRestoredBounds();
    }

    private void SavePlacement() =>
        new WindowPlacement(_restoredPosition.X, _restoredPosition.Y, _restoredSize.Width, _restoredSize.Height,
            WindowState == WindowState.Maximized).Save();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (_closeConfirmed || !_isDirty)
        {
            SavePlacement();
            return;
        }
        e.Cancel = true;
        if (_fileActionActive) return;
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
        var choice = await ShowDialogAsync("Save your changes?",
            $"The colour adjustments to {Path.GetFileName(_path)} have not been saved.",
            "Save as…", "Don't save", "Cancel", DialogResult.Primary);
        return choice switch
        {
            DialogResult.Primary => await SaveAsync(saveAs: true),
            DialogResult.Secondary => true,
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
                    : ShowMaskToggle.IsChecked == true && mask is not null ? PreviewMode.Mask
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
                CopyToBitmap(layer);
                if (PreviewImage.Source != layer.Bitmap) PreviewImage.Source = layer.Bitmap;
                PreviewImage.InvalidateVisual();
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

    private static void CopyToBitmap(PreviewLayer layer)
    {
        using var buffer = layer.Bitmap.Lock();
        int rowBytes = layer.Source.Width * 4;
        if (buffer.RowBytes == rowBytes)
        {
            Marshal.Copy(layer.Pixels, 0, buffer.Address, layer.Pixels.Length);
            return;
        }
        for (int y = 0; y < layer.Source.Height; y++)
            Marshal.Copy(layer.Pixels, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
    }

    private void Compare_Click(object? sender, RoutedEventArgs e) => RequestRender();

    private void ShowMaskToggle_Toggled(object? sender, RoutedEventArgs e) => RequestRender();

    // ---------------------------------------------------------- colour editor

    private void Picker_Click(object? sender, RoutedEventArgs e) => UpdateCursor();

    private void PickColour(Point imagePoint)
    {
        if (_full is null) return;
        var source = _full.Source;
        int x = (int)(imagePoint.X / _zoom), y = (int)(imagePoint.Y / _zoom);
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

    private void AdjustmentList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadEditor(Selected);
        if (ShowMaskToggle.IsChecked == true) RequestRender();
    }

    private void LoadEditor(AdjustmentItem? item)
    {
        EditorPanel.IsVisible = item is not null;
        if (item is null) return;

        _suppressEditorEvents = true;
        var m = item.Model;
        TargetHueSlider.Value = Math.Round(m.TargetHue) % 360;
        RangeSlider.Value = m.Range;
        SmoothnessSlider.Value = m.Smoothness;
        ProtectSlider.Value = m.NeutralProtection;
        LightnessMinSlider.Value = m.LightnessMin;
        LightnessMaxSlider.Value = m.LightnessMax;
        LightnessSmoothnessSlider.Value = m.LightnessSmoothness;
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

        // Keep the lightness limits in order by pushing the other one along.
        _suppressEditorEvents = true;
        if (ReferenceEquals(sender, LightnessMinSlider) && LightnessMaxSlider.Value < LightnessMinSlider.Value)
            LightnessMaxSlider.Value = LightnessMinSlider.Value;
        else if (ReferenceEquals(sender, LightnessMaxSlider) && LightnessMinSlider.Value > LightnessMaxSlider.Value)
            LightnessMinSlider.Value = LightnessMaxSlider.Value;
        _suppressEditorEvents = false;

        var m = item.Model;
        m.TargetHue = (float)TargetHueSlider.Value;
        m.Range = (float)RangeSlider.Value;
        m.Smoothness = (float)SmoothnessSlider.Value;
        m.NeutralProtection = (float)ProtectSlider.Value;
        m.LightnessMin = (float)LightnessMinSlider.Value;
        m.LightnessMax = (float)LightnessMaxSlider.Value;
        m.LightnessSmoothness = (float)LightnessSmoothnessSlider.Value;
        m.HueShift = (float)HueShiftSlider.Value;
        m.Saturation = (float)SaturationSlider.Value;
        m.Lightness = (float)LightnessSlider.Value;
        item.Refresh();

        if (ReferenceEquals(sender, TargetHueSlider)) UpdateSliderRamps();
        MarkDirty();
        RequestRender(interactive: true);
    }

    private void ResetAdjustment_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        item.Model.ResetEdits();
        item.Refresh();
        LoadEditor(item);
        MarkDirty();
        RequestRender();
    }

    private void DeleteAdjustment_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not AdjustmentItem item) return;
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
        LightnessMinSlider.GradientBrush = LightnessMaxSlider.GradientBrush =
            Ramp(Hsl(h, 0.8f, 0f), Hsl(h, 0.8f, 0.5f), Hsl(h, 0.8f, 1f));
    }

    private static LinearGradientBrush HueRamp(float from, float to)
    {
        var brush = HorizontalGradient();
        const int steps = 12;
        for (int i = 0; i <= steps; i++)
            brush.GradientStops.Add(new GradientStop(Hsl(from + (to - from) * i / steps, 1, 0.5f), (double)i / steps));
        return brush;
    }

    private static LinearGradientBrush Ramp(params Color[] colours)
    {
        var brush = HorizontalGradient();
        for (int i = 0; i < colours.Length; i++)
            brush.GradientStops.Add(new GradientStop(colours[i], (double)i / (colours.Length - 1)));
        return brush;
    }

    private static LinearGradientBrush HorizontalGradient() => new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
    };

    private static Color Hsl(float h, float s, float l)
    {
        ColourMath.HslToRgb(h, s, l, out float r, out float g, out float b);
        return Color.FromArgb(255, (byte)(r * 255 + 0.5f), (byte)(g * 255 + 0.5f), (byte)(b * 255 + 0.5f));
    }

    // ------------------------------------------------------------ navigation

    private void Fit_Click(object? sender, RoutedEventArgs e) => FitToWindow();

    private void ActualSize_Click(object? sender, RoutedEventArgs e) => ShowActualSize();

    private void ShowActualSize()
    {
        if (_image is null) return;
        ZoomAround(new Point(Viewer.Viewport.Width / 2, Viewer.Viewport.Height / 2), 1 / RenderScaling);
    }

    private void FitToWindow()
    {
        if (_image is null || Viewer.Bounds.Width <= 0) return;
        SetZoom(FitZoom(Viewer.Bounds.Size));
        Viewer.UpdateLayout();
        Viewer.Offset = default;
    }

    /// <summary>The zoom that shows the whole image in a viewport of the given size.</summary>
    private double FitZoom(Size viewport) =>
        _image is null ? 1 : Math.Min(viewport.Width / _image.Width, viewport.Height / _image.Height) * 0.98;

    private void SetZoom(double zoom)
    {
        if (_image is null) return;
        _zoom = Math.Clamp(zoom, MinZoom / RenderScaling, MaxZoom / RenderScaling);
        PreviewImage.Width = _image.Width * _zoom;
        PreviewImage.Height = _image.Height * _zoom;
        // Smooth when zoomed out; sharp pixels when zoomed well in.
        RenderOptions.SetBitmapInterpolationMode(PreviewImage,
            _zoom * RenderScaling >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        UpdateZoomText();
    }

    /// <summary>Zooms so the image point under <paramref name="viewportPoint"/> stays put.</summary>
    private void ZoomAround(Point viewportPoint, double zoom)
    {
        if (_image is null || Viewer.TranslatePoint(viewportPoint, PreviewImage) is not { } anchor) return;
        double oldZoom = _zoom;
        SetZoom(zoom);
        Viewer.UpdateLayout();
        var scaled = new Point(anchor.X / oldZoom * _zoom, anchor.Y / oldZoom * _zoom);
        if (PreviewImage.TranslatePoint(scaled, Viewer) is { } moved)
            Viewer.Offset += moved - viewportPoint;
    }

    private void ImageHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_image is null || e.Delta.Y == 0) return;
        // A mouse wheel notch is 1; trackpads send smaller steps, giving smooth zoom.
        ZoomAround(e.GetPosition(Viewer), _zoom * Math.Pow(1.25, Math.Clamp(e.Delta.Y, -4, 4)));
        e.Handled = true;
    }

    private void ImageHost_Magnify(object? sender, PointerDeltaEventArgs e)
    {
        // Trackpad pinch (macOS).
        if (_image is null) return;
        ZoomAround(e.GetPosition(Viewer), _zoom * (1 + e.Delta.X));
        e.Handled = true;
    }

    private void ImageHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_image is null) return;
        var properties = e.GetCurrentPoint(ImageHost).Properties;

        if (PickerButton.IsChecked == true && properties.IsLeftButtonPressed)
        {
            PickColour(e.GetPosition(PreviewImage));
            e.Handled = true;
            return;
        }

        _panning = true;
        _panStart = e.GetPosition(Viewer);
        _panStartOffset = Viewer.Offset;
        e.Pointer.Capture(ImageHost);
        UpdateCursor();
        e.Handled = true;
    }

    private void ImageHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_panning) Viewer.Offset = _panStartOffset - (e.GetPosition(Viewer) - _panStart);
        UpdatePixelInfo(e.GetPosition(PreviewImage));
    }

    private void ImageHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        EndPan();
    }

    private void ImageHost_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndPan();

    private void ImageHost_PointerExited(object? sender, PointerEventArgs e)
    {
        if (!_panning) PixelInfoText.Text = "";
    }

    private void EndPan()
    {
        _panning = false;
        UpdateCursor();
    }

    private void UpdateCursor() =>
        ImageHost.Cursor = _panning ? new Cursor(StandardCursorType.SizeAll)
            : PickerButton.IsChecked == true ? RingCursor.Get(RenderScaling)
            : Cursor.Default;

    private void Viewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Keep the whole image visible while the window is resized, if it was fitted.
        if (_image is null || e.PreviousSize.Width <= 0) return;
        if (Math.Abs(_zoom / FitZoom(e.PreviousSize) - 1) < 0.002) FitToWindow();
    }

    // -------------------------------------------------------------- keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !Toolbar.IsEnabled) return;

        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.B when CompareButton.IsEnabled:
                    ToggleCompare();
                    break;
                case Key.P when PanelScroller.IsEnabled:
                    TogglePicker();
                    break;
                default:
                    return;
            }
            e.Handled = true;
            return;
        }

        // On macOS the menu bar handles the Command shortcuts.
        if (OperatingSystem.IsMacOS()) return;
        bool shift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);
        if (e.KeyModifiers != KeyModifiers.Control && !shift) return;
        switch (e.Key)
        {
            case Key.O when !shift:
                _ = OpenAsync();
                break;
            case Key.S when SaveButton.IsEnabled:
                _ = RunFileActionAsync(() => SaveAsync(saveAs: shift));
                break;
            case Key.D0 or Key.NumPad0 when !shift:
                FitToWindow();
                break;
            case Key.D1 or Key.NumPad1 when !shift:
                ShowActualSize();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ToggleCompare()
    {
        CompareButton.IsChecked = CompareButton.IsChecked != true;
        RequestRender();
    }

    private void TogglePicker()
    {
        PickerButton.IsChecked = PickerButton.IsChecked != true;
        UpdateCursor();
    }

    private static void SetShortcutTip(Control control, string text, string key, bool shift = false)
    {
        string gesture = OperatingSystem.IsMacOS()
            ? (shift ? "⇧⌘" : "⌘") + key
            : (shift ? "Ctrl+Shift+" : "Ctrl+") + key;
        ToolTip.SetTip(control, $"{text} ({gesture})");
    }

    /// <summary>File and View menus for the macOS menu bar, which also provide the Command-key shortcuts.</summary>
    private void BuildMacMenu()
    {
        NativeMenuItem Item(string header, string? gesture, Action action, bool needsImage = true)
        {
            var item = new NativeMenuItem(header);
            if (gesture is not null) item.Gesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => { if (Toolbar.IsEnabled) action(); };
            if (needsImage) _imageMenuItems.Add(item);
            return item;
        }

        var menu = new NativeMenu
        {
            new NativeMenuItem("File")
            {
                Menu = new NativeMenu
                {
                    Item("Open…", "Meta+O", () => _ = OpenAsync(), needsImage: false),
                    new NativeMenuItemSeparator(),
                    Item("Save", "Meta+S", () => _ = RunFileActionAsync(() => SaveAsync(saveAs: false))),
                    Item("Save As…", "Meta+Shift+S", () => _ = RunFileActionAsync(() => SaveAsync(saveAs: true))),
                },
            },
            new NativeMenuItem("View")
            {
                Menu = new NativeMenu
                {
                    Item("Fit to Window", "Meta+D0", FitToWindow),
                    Item("Actual Size", "Meta+D1", ShowActualSize),
                    new NativeMenuItemSeparator(),
                    Item("Show Original (B)", null, ToggleCompare),
                    Item("Pick Colour (P)", null, () => { if (PanelScroller.IsEnabled) TogglePicker(); }),
                },
            },
        };
        NativeMenu.SetMenu(this, menu);
    }

    // ------------------------------------------------------------- UI helpers

    private void UpdatePixelInfo(Point p)
    {
        if (_image is null) return;
        int x = (int)(p.X / _zoom), y = (int)(p.Y / _zoom);
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

    private void UpdateZoomText() => ZoomText.Text = _image is null ? "" : $"{_zoom * RenderScaling:0%}";

    private void MarkDirty()
    {
        if (_isDirty) return;
        _isDirty = true;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool loaded = _image is not null;
        EmptyState.IsVisible = !loaded;
        PanelScroller.IsEnabled = loaded && _image!.IsColour;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = FitButton.IsEnabled =
            ActualSizeButton.IsEnabled = CompareButton.IsEnabled = loaded;
        foreach (var item in _imageMenuItems) item.IsEnabled = loaded;

        Title = _path is null ? AppTitle : $"{Path.GetFileName(_path)}{(_isDirty ? " •" : "")} — {AppTitle}";
        FileInfoText.Text = _image is null
            ? ""
            : $"{_path}    {_image.Width} × {_image.Height}    {_image.Channels} ch {_image.ColorSpace}    {_image.SampleFormatName}";
        UpdateZoomText();
    }

    private void SetBusy(string message)
    {
        BusyText.Text = message;
        BusyOverlay.IsVisible = true;
        Toolbar.IsEnabled = false;
    }

    private void ClearBusy()
    {
        BusyOverlay.IsVisible = false;
        Toolbar.IsEnabled = true;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        MessageInfo.Severity = severity;
        MessageInfo.Title = title;
        MessageInfo.Message = message;
        MessageInfo.IsOpen = true;
    }

    private async Task ShowErrorAsync(string title, string message) => await ShowDialogAsync(title, message);

    private async Task<DialogResult> ShowDialogAsync(string title, string message,
        string? primary = null, string? secondary = null, string close = "OK", DialogResult defaultButton = DialogResult.None)
    {
        if (_dialogOpen) return DialogResult.None;
        _dialogOpen = true;
        try
        {
            return await MessageDialog.ShowAsync(this, title, message, primary, secondary, close, defaultButton);
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
