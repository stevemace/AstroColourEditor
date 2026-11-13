using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using AstroColour.Core;
using AstroColour.Core.Colour;
using AstroColour.Core.Curves;
using AstroColour.Core.Stars;
using AstroColour.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private readonly CurveSet _curves = new();
    private readonly HueCurveSet _hueCurves = new();
    private bool _preferColourCurves; // edit newly picked colours with curves rather than HSL
    private Rotation _rotation;
    private CropRect? _crop;      // in the image's own pixel coordinates, before rotation; null keeps the whole image
    private bool _cropEditing;

    // Undo and redo. The image has unsaved edits unless the current state is the one last opened or saved.
    private readonly EditHistory _history = new();
    private long _savedStateId;
    private bool _restoring; // applying an undo or redo, so control changes are not edits

    private readonly AppSettings _settings = AppSettings.Load();

    // Split before/after view: where the divider is, as a fraction of the viewport's width.
    private double _splitFraction = 0.5;
    private bool _splitDragging;
    private readonly DispatcherTimer _hueHistogramTimer;

    // Images longer than this are previewed at reduced size while sliders move.
    private const int ProxyMaxSide = 2400;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private IAstroImage? _image;
    private string? _path;
    private PreviewLayer? _full;
    private PreviewLayer? _proxy;
    private int _proxyFactor = 1;

    /// <summary>Blurred selection masks kept between renders, and for saving.</summary>
    private readonly MaskCache _maskCache = new();

    // Statistics of the full-resolution image's channels for narrowband normalisation (colour images only).
    private ChannelStatistics? _channelStatistics;

    // Stars-only image added back over the edited image, at full and preview resolution.
    private StarsState? _stars;

    private readonly DispatcherTimer _settleTimer;
    private CancellationTokenSource? _fullRenderCancellation;
    private bool _rendering;
    private bool _proxyPending;
    private bool _fullPending;
    private bool _fullAllowed = true;
    private bool _suppressEditorEvents;
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
    private NativeMenuItem? _undoMenuItem, _redoMenuItem, _recentMenuItem; // macOS menu bar only

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        PositionChanged += (_, _) => TrackRestoredBounds();
        Deactivated += (_, _) => SetMaskKeyHeld(false); // the key-up would go to another window
        AdjustmentList.ItemsSource = _adjustments;
        HueRangeBand.Ramp = HueRamp(0, 360);
        HueRangeBand.HistogramColour = h => Hsl((float)h, 0.8f, 0.5f);
        BrightnessRangeBand.Ramp = Ramp(Colors.Black, Colors.White);
        PickerHint.Text = "Turn on the picker, then click a colour in the image to add it to the list. " +
            $"Shift-click more colours to add them to the selected one, or {(OperatingSystem.IsMacOS() ? "Option" : "Alt")}-click to leave them out. " +
            "Drag to pan, scroll to zoom.";
        GlobalSaturationSlider.GradientBrush = Ramp(Hsl(0, 0, 0.5f), Hsl(0, 1, 0.5f));
        ImageCurves.Curves = _curves;
        HueCurves.Curves = _hueCurves;

        SetShortcutTip(OpenButton, "Open an XISF, FITS, TIFF or camera raw image", "O");
        SetShortcutTip(SaveButton, "Save over the current file", "S");
        SetShortcutTip(SaveAsButton, "Save to a new file", "S", shift: true);
        SetShortcutTip(UndoButton, "Undo", "Z");
        SetShortcutTip(RedoButton, "Redo", "Z", shift: true);
        SetShortcutTip(RotateLeftButton, "Rotate 90° anticlockwise", "[");
        SetShortcutTip(RotateRightButton, "Rotate 90° clockwise", "]");
        ToolTip.SetTip(CropButton, "Crop the image; the astrometric solution is kept (C)");
        SetShortcutTip(FitButton, "Fit the image to the window", "0");
        SetShortcutTip(ActualSizeButton, "View at actual pixels", "1");
        ToolTip.SetTip(CompareButton, "Show the original, unedited image (B)");
        ToolTip.SetTip(ClippingButton, "Highlight pixels the edits push to black (blue) or white (red) (J)");
        ToolTip.SetTip(SplitButton, "Show the original and the edited image side by side, split by a draggable divider (Y)");
        ToolTip.SetTip(PickerButton, "Pick a colour from the image (P)");
        if (OperatingSystem.IsMacOS()) BuildMacMenu();

        _settleTimer = new DispatcherTimer { Interval = SettleDelay };
        _settleTimer.Tick += (_, _) =>
        {
            _settleTimer.Stop();
            _fullAllowed = true;
            if (_fullPending && !_rendering) _ = RenderLoopAsync();
        };

        // The hue histogram follows the edits before the hue curves, at most a few times a second.
        _hueHistogramTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hueHistogramTimer.Tick += (_, _) =>
        {
            _hueHistogramTimer.Stop();
            UpdateHueHistogram();
        };

        Viewer.ScrollChanged += (_, _) => UpdateSplit();
        Viewer.LayoutUpdated += (_, _) => UpdateSplit();

        UpdateRecentFiles();
        UpdateChrome();
    }

    private bool IsDirty => _image is not null && _history.Current is { } current && current.ContentId != _savedStateId;

    /// <summary>A rendered view of the image: its source planes, an 8-bit buffer and the bitmap shown on screen.</summary>
    private sealed class PreviewLayer(RgbImage source)
    {
        public RgbImage Source { get; } = source;
        /// <summary>The stars image at this layer's size, if one is loaded.</summary>
        public RgbImage? Stars { get; set; }
        public byte[] Pixels { get; } = new byte[source.PixelCount * 4];
        public WriteableBitmap Bitmap { get; } = NewBitmap(source);
        /// <summary>The unedited image, for the split view; made when first needed.</summary>
        public WriteableBitmap? Original { get; set; }

        public static WriteableBitmap NewBitmap(RgbImage source) =>
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
            FileTypeFilter = [new FilePickerFileType("XISF, FITS, TIFF or camera raw image") { Patterns = [.. AstroImageFile.AllExtensions.Select(x => "*" + x)] }],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        if (files is [var file] && file.TryGetLocalPath() is { } path) await LoadAsync(path);
    });

    /// <summary>The current file's folder, else the folder last opened from or saved to, else the Pictures folder.</summary>
    private async Task<IStorageFolder?> StartFolderAsync()
    {
        try
        {
            foreach (var folder in (string?[])[Path.GetDirectoryName(_path), _settings.LastFolder])
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                if (await StorageProvider.TryGetFolderFromPathAsync(folder) is { } found) return found;
            }
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
            var (image, rgb, proxy, factor, median, histogram, clipping, statistics, hues) = await Task.Run(() =>
            {
                var img = AstroImageFile.Load(path);
                var full = RgbImage.From(img);
                int factor = (int)Math.Ceiling(Math.Max(img.Width, img.Height) / (double)ProxyMaxSide);
                var small = factor > 1 ? full.Downsample(factor) : null;
                return (img, full, small, factor, ColourProcessor.EstimateMedian(full), CurveSet.Histogram(small ?? full),
                    CurveClipping.Sample(small ?? full), img.IsColour ? ChannelStatistics.Measure(full) : null,
                    img.IsColour ? HueCurveSet.Histogram(small ?? full) : null);
            });

            _image = image;
            _path = path;
            _overwriteConfirmed = false;
            _adjustments.Clear();
            _curves.Reset();
            _hueCurves.Reset();
            ImageCurves.Histogram = histogram;
            ImageCurves.Clipping = clipping;
            ImageCurves.ShowChannel(CurveChannel.Rgb);
            HueCurves.Refresh();
            HueCurves.Histogram = hues;
            HueRangeBand.Histogram = hues;
            _channelStatistics = statistics;
            SetNarrowband(new NarrowbandSettings());
            SetGlobalSaturation(0);
            SetRemovePurple(0);
            SetRotation(Rotation.None);
            _crop = null;
            _cropEditing = false;
            CropButton.IsChecked = false;
            CompareButton.IsChecked = false;
            if (!image.IsColour) EditorTabs.SelectedItem = GlobalTab;

            _maskCache.Clear();
            _full = new PreviewLayer(rgb);
            _proxy = proxy is null ? null : new PreviewLayer(proxy);
            _proxyFactor = factor;
            _proxyPending = false;
            SetStars(null);
            SetStarBrightness(1);
            SetShowStars(true);
            PreviewImage.Source = _full.Bitmap;
            BeforeImage.Source = null;
            SetZoom(1 / RenderScaling);
            _savedStateId = _history.Reset(CaptureState()).ContentId;
            _settings.AddRecent(path);
            _settings.Save();
            UpdateRecentFiles();
            if (SplitButton.IsChecked == true) _ = ShowSplitAsync();

            LinearInfo.IsOpen = image.IsColour && median < 0.03f;
            MessageInfo.IsOpen = false;
            if (!image.IsColour)
                ShowMessage(InfoBarSeverity.Warning, "Monochrome image", "This image has a single channel, so it has no colours to edit, but you can adjust its curves.");

            RequestRender();
            UpdateChrome();
            Dispatcher.UIThread.Post(FitToWindow, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            if (!File.Exists(path))
            {
                _settings.RemoveRecent(path);
                _settings.Save();
                UpdateRecentFiles();
            }
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
        if (AstroImageFile.IsRaw(target)) saveAs = true; // raw files cannot be written
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
            // The current file's format is offered first; the file's extension picks the format it is saved in.
            var types = AstroImageFile.SaveFileTypes(_image, _path);
            var choices = types.Select(t => new FilePickerFileType(t.Name) { Patterns = [.. t.Extensions.Select(x => "*" + x)] }).ToList();
            var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
            {
                Title = "Save as",
                SuggestedFileName = Path.GetFileNameWithoutExtension(_path) + "_colour" + types[0].Extensions[0],
                DefaultExtension = types[0].Extensions[0].TrimStart('.'),
                FileTypeChoices = choices,
                SuggestedStartLocation = await StartFolderAsync(),
                ShowOverwritePrompt = true,
            });
            if (result.File?.TryGetLocalPath() is not { } picked) return false;
            target = picked;
            if (AstroImageFile.FormatOf(target) is null)
            {
                // No recognised extension was typed, so add the chosen file type's.
                int chosen = Math.Max(0, choices.IndexOf(result.SelectedFileType!));
                target += types[chosen].Extensions[0];
            }
            _overwriteConfirmed = true;
        }

        SetBusy("Saving…");
        try
        {
            var image = _image;
            var snapshot = _adjustments.Select(a => a.Model.Clone()).ToArray();
            var curves = _curves.Clone();
            var hueCurves = _hueCurves.Clone();
            var saturation = GlobalSaturation;
            var removePurple = RemovePurple;
            var stars = _stars is null || !ShowStars ? null : new StarLayer(_stars.Full, StarBrightness);
            var rotation = _rotation;
            var crop = _crop;
            var narrowband = Narrowband;
            var savedState = _history.Current;
            await Task.Run(() => AstroImageFile.Save(image, target,
                ColourProcessor.Apply(image, snapshot, curves, stars, saturation, narrowband, removePurple, hueCurves, _maskCache),
                rotation, crop));
            _path = target;
            if (savedState is not null) _savedStateId = savedState.ContentId;
            // Saved PNG and JPEG files can't be opened again, so only the folder is remembered for those.
            if (AstroImageFile.AllExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase))
                _settings.AddRecent(target);
            else
                _settings.RememberFolder(target);
            _settings.Save();
            UpdateRecentFiles();
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
        if (_closeConfirmed || !IsDirty)
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
        if (!IsDirty || _path is null) return true;
        var choice = await ShowDialogAsync("Save your changes?",
            $"The edits to {Path.GetFileName(_path)} have not been saved.",
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
                var curves = _curves.Clone();
                var hueCurves = _hueCurves.Clone();
                var saturation = GlobalSaturation;
                var removePurple = RemovePurple;
                var narrowband = Narrowband;
                var mask = Selected?.Model.Clone();
                var stars = layer.Stars is null || !ShowStars ? null : new StarLayer(layer.Stars, StarBrightness);
                var mode = CompareButton.IsChecked == true ? PreviewMode.Original
                    : ShowingMask && mask is not null ? PreviewMode.Mask
                    : PreviewMode.Adjusted;

                bool showClipping = ClippingButton.IsChecked == true;
                float pixelScale = layer == full ? 1 : _proxyFactor;

                var cancellation = new CancellationTokenSource();
                if (layer == full) _fullRenderCancellation = cancellation;
                ClippingStats clipped;
                try
                {
                    clipped = await Task.Run(() => ColourProcessor.RenderPreview(layer.Source, snapshot, curves, mode, mask, layer.Pixels,
                        cancellation.Token, stars, saturation, narrowband, removePurple, hueCurves, showClipping,
                        pixelScale, _maskCache));
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
                ShowClippingInfo(showClipping && mode == PreviewMode.Adjusted ? clipped : null);
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

    private static void CopyToBitmap(PreviewLayer layer) => CopyToBitmap(layer.Pixels, layer.Bitmap, layer.Source);

    private static void CopyToBitmap(byte[] pixels, WriteableBitmap bitmap, RgbImage source)
    {
        using var buffer = bitmap.Lock();
        int rowBytes = source.Width * 4;
        if (buffer.RowBytes == rowBytes)
        {
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
            return;
        }
        for (int y = 0; y < source.Height; y++)
            Marshal.Copy(pixels, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
    }

    private void Compare_Click(object? sender, RoutedEventArgs e)
    {
        // Showing the whole original and the split view are alternatives.
        if (CompareButton.IsChecked == true && SplitButton.IsChecked == true)
        {
            SplitButton.IsChecked = false;
            HideSplit();
        }
        RequestRender();
    }

    // -------------------------------------------------------------- clipping

    private void Clipping_Click(object? sender, RoutedEventArgs e) => RequestRender();

    private void ToggleClipping()
    {
        ClippingButton.IsChecked = ClippingButton.IsChecked != true;
        RequestRender();
    }

    /// <summary>Shows the shares of pixels clipped by the edits in the status bar, or hides them if null.</summary>
    private void ShowClippingInfo(ClippingStats? clipped)
    {
        ClippingInfoText.IsVisible = clipped is not null;
        if (clipped is not { } c) return;
        ClippingInfoText.Text = $"Clipped:  shadows {Percent(c.Shadows)}   highlights {Percent(c.Highlights)}";

        static string Percent(double fraction) =>
            fraction <= 0 ? "0%" : fraction < 0.0001 ? "<0.01%" : $"{fraction * 100:0.##}%";
    }

    // ------------------------------------------------------------------ crop

    private void Crop_Click(object? sender, RoutedEventArgs e) => SetCropEditing(CropButton.IsChecked == true);

    private void CropDone_Click(object? sender, RoutedEventArgs e) => SetCropEditing(false);

    /// <summary>Starts or finishes cropping, fitting the whole image (to crop) or the cropped part (when done) to the window.</summary>
    private void SetCropEditing(bool editing)
    {
        if (_image is null) editing = false;
        CropButton.IsChecked = editing;
        if (editing == _cropEditing) return;
        _cropEditing = editing;
        if (editing && PickerButton.IsChecked == true)
        {
            PickerButton.IsChecked = false;
            UpdateCursor();
        }
        UpdateCropOverlay();
        FitToWindow();
    }

    /// <summary>Shows the crop over the image: editable while cropping, otherwise hiding what is cropped away.</summary>
    private void UpdateCropOverlay()
    {
        if (_image is null)
        {
            CropOverlay.IsVisible = CropBar.IsVisible = false;
            return;
        }
        var (width, height) = _rotation.Size(_image.Width, _image.Height);
        var shown = _crop?.Rotate(_rotation, _image.Width, _image.Height) ?? new CropRect(0, 0, width, height);
        CropOverlay.ImageSize = new Size(width, height);
        CropOverlay.Zoom = _zoom;
        CropOverlay.Selection = new Rect(shown.X, shown.Y, shown.Width, shown.Height);
        CropOverlay.IsEditing = _cropEditing;
        CropOverlay.IsVisible = _cropEditing || _crop is not null;
        CropBar.IsVisible = _cropEditing;
        CropSizeText.Text = $"{shown.Width} × {shown.Height} px";
    }

    private void CropOverlay_SelectionChanged(object? sender, bool interactive)
    {
        if (_image is null) return;
        var (width, height) = _rotation.Size(_image.Width, _image.Height);
        var r = CropOverlay.Selection;
        int left = Math.Clamp((int)Math.Round(r.Left), 0, width - 1), top = Math.Clamp((int)Math.Round(r.Top), 0, height - 1);
        int right = Math.Clamp((int)Math.Round(r.Right), left + 1, width), bottom = Math.Clamp((int)Math.Round(r.Bottom), top + 1, height);
        var shown = new CropRect(left, top, right - left, bottom - top);
        CropSizeText.Text = $"{shown.Width} × {shown.Height} px";

        var crop = shown.IsWhole(width, height) ? (CropRect?)null : shown.Unrotate(_rotation, _image.Width, _image.Height);
        if (crop == _crop && interactive) return;
        _crop = crop;
        RecordEdit(CropOverlay, sealStep: !interactive);
        if (!interactive) UpdateCropOverlay(); // snap the rectangle to whole pixels
    }

    private void CropAspect_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_image is null) return;
        var (width, height) = _rotation.Size(_image.Width, _image.Height);
        CropOverlay.AspectRatio = CropAspectBox.SelectedIndex switch
        {
            1 => width / (double)height,
            2 => 1,
            3 => 5 / 4.0,
            4 => 4 / 5.0,
            5 => 4 / 3.0,
            6 => 3 / 4.0,
            7 => 3 / 2.0,
            8 => 2 / 3.0,
            9 => 16 / 9.0,
            10 => 9 / 16.0,
            _ => null,
        };
        if (_cropEditing) CropOverlay.ApplyAspectRatio();
    }

    private void CropReset_Click(object? sender, RoutedEventArgs e)
    {
        if (_crop is null) return;
        _crop = null;
        RecordEdit();
        UpdateCropOverlay();
    }

    // ------------------------------------------------------------ split view

    private void Split_Click(object? sender, RoutedEventArgs e)
    {
        if (SplitButton.IsChecked == true)
        {
            if (CompareButton.IsChecked == true)
            {
                CompareButton.IsChecked = false;
                RequestRender();
            }
            _ = ShowSplitAsync();
        }
        else
        {
            HideSplit();
        }
    }

    private void ToggleSplit()
    {
        SplitButton.IsChecked = SplitButton.IsChecked != true;
        Split_Click(SplitButton, new RoutedEventArgs());
    }

    /// <summary>Shows the unedited image left of the divider, rendering it at full resolution the first time.</summary>
    private async Task ShowSplitAsync()
    {
        if (_full is not { } full) return;
        if (full.Original is null)
        {
            var pixels = new byte[full.Source.PixelCount * 4];
            await Task.Run(() => ColourProcessor.RenderPreview(full.Source, [], null, PreviewMode.Original, null, pixels));
            if (full != _full) return; // a different file was opened meanwhile
            var bitmap = PreviewLayer.NewBitmap(full.Source);
            CopyToBitmap(pixels, bitmap, full.Source);
            full.Original = bitmap;
        }
        if (SplitButton.IsChecked != true) return;
        BeforeImage.Source = full.Original;
        BeforeRotator.IsVisible = SplitOverlay.IsVisible = true;
        UpdateSplit();
    }

    private void HideSplit()
    {
        BeforeRotator.IsVisible = SplitOverlay.IsVisible = false;
        _splitDragging = false;
    }

    /// <summary>Places the divider and clips the unedited image to its left, after a scroll, zoom, resize or drag.</summary>
    private void UpdateSplit()
    {
        if (!SplitOverlay.IsVisible) return;
        var viewport = Viewer.Viewport;
        double x = Math.Round(viewport.Width * _splitFraction);
        double left = x - SplitDivider.Width / 2;
        if (Canvas.GetLeft(SplitDivider) != left) Canvas.SetLeft(SplitDivider, left);
        if (SplitDivider.Height != viewport.Height) SplitDivider.Height = viewport.Height;

        // The divider is fixed in the viewport, so find where it falls on the (possibly rotated) image.
        if (Viewer.TranslatePoint(new Point(x, 0), BeforeRotator) is not { } local) return;
        var clip = new Rect(0, 0, Math.Clamp(local.X, 0, BeforeRotator.Bounds.Width), BeforeRotator.Bounds.Height);
        if (BeforeRotator.Clip is RectangleGeometry { Rect: var current } && current == clip) return;
        BeforeRotator.Clip = new RectangleGeometry(clip);
    }

    private void SplitHandle_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SplitHandle).Properties.IsLeftButtonPressed) return;
        _splitDragging = true;
        e.Pointer.Capture(SplitHandle);
        e.Handled = true;
    }

    private void SplitHandle_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_splitDragging || Viewer.Viewport.Width <= 0) return;
        _splitFraction = Math.Clamp(e.GetPosition(Viewer).X / Viewer.Viewport.Width, 0, 1);
        UpdateSplit();
        e.Handled = true;
    }

    private void SplitHandle_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _splitDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void EditorTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Selection changes in the adjustment list bubble up to here too.
        if (!ReferenceEquals(e.Source, EditorTabs)) return;
        if (!ColourTab.IsSelected && PickerButton.IsChecked == true)
        {
            PickerButton.IsChecked = false;
            UpdateCursor();
        }
        // The selected colour range is only shown while its tab is open.
        if (_maskKeyHeld || _selectionDragging) RequestRender();
    }

    // ---------------------------------------------------------- colour editor

    private void Picker_Click(object? sender, RoutedEventArgs e)
    {
        if (PickerButton.IsChecked == true) SetCropEditing(false);
        UpdateCursor();
    }

    private void PickColour(Point imagePoint)
    {
        if (_full is null) return;
        var source = _full.Source;
        int x = (int)(imagePoint.X / _zoom), y = (int)(imagePoint.Y / _zoom);
        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return;

        var (r, g, b) = ColourProcessor.Sample(source, x, y, radius: 3);
        BaseAdjustments?.Apply(ref r, ref g, ref b); // pick the colour as the colour editor sees it
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
            item = CreateItem(new ColourAdjustment(), _preferColourCurves);
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
        RecordEdit(changesImage: false); // an unadjusted colour changes nothing yet
        RequestRender();
    }

    private AdjustmentItem CreateItem(ColourAdjustment model, bool editWithCurves)
    {
        var item = new AdjustmentItem(model) { EditWithCurves = editWithCurves };
        item.ActiveChanged += (_, _) =>
        {
            if (_restoring) return;
            RecordEdit(changesImage: item.Model.HasEdits);
            RequestRender();
        };
        return item;
    }

    private void AdjustmentList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadEditor(Selected);
        if (ShowingMask) RequestRender();
    }

    private void LoadEditor(AdjustmentItem? item)
    {
        EditorPanel.IsVisible = item is not null;
        if (item is null) return;

        _suppressEditorEvents = true;
        var m = item.Model;
        ProtectSlider.Value = m.NeutralProtection;
        MaskBlurSlider.Value = m.MaskBlur;
        HueShiftSlider.Value = m.HueShift;
        SaturationSlider.Value = m.Saturation;
        LightnessSlider.Value = m.Lightness;
        _suppressEditorEvents = false;
        // Keep the refinements out of the way unless this colour uses them.
        RefineExpander.IsExpanded = HasRefinements(m);
        ShowSelectionRange(m);
        UpdateSliderRamps();

        ColourCurves.Curves = m.Curves;
        ShowEditMode(item.EditWithCurves);
    }

    private void ColourEditMode_Click(object? sender, RoutedEventArgs e)
    {
        bool curves = sender == CurvesModeButton;
        _preferColourCurves = curves;
        if (Selected is { } item) item.EditWithCurves = curves;
        ShowEditMode(curves);
    }

    /// <summary>Shows the HSL sliders or the curves for the selected colour.</summary>
    private void ShowEditMode(bool curves)
    {
        HslModeButton.IsChecked = !curves;
        CurvesModeButton.IsChecked = curves;
        HslEditor.IsVisible = !curves;
        ColourCurves.IsVisible = curves;
        UpdateSelectionHistogram();
    }

    /// <summary>Shows the histogram of just the selected colours behind their curves.</summary>
    private void UpdateSelectionHistogram()
    {
        if (!ColourCurves.IsVisible || Selected is not { } item || (_proxy ?? _full) is not { } layer) return;
        ColourCurves.Histogram = CurveSet.Histogram(layer.Source, selection: item.Model, baseAdjustments: BaseAdjustments);
    }

    private void ColourCurves_CurvesChanged(object? sender, bool interactive)
    {
        Selected?.Refresh();
        RecordEdit((ColourCurves, Selected), sealStep: !interactive);
        RequestRender(interactive);
    }

    private void EditorSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || Selected is not { } item) return;

        var m = item.Model;
        bool hadEdits = m.HasEdits;
        m.NeutralProtection = (float)ProtectSlider.Value;
        m.MaskBlur = (float)MaskBlurSlider.Value;
        m.HueShift = (float)HueShiftSlider.Value;
        m.Saturation = (float)SaturationSlider.Value;
        m.Lightness = (float)LightnessSlider.Value;
        item.Refresh();

        if (ReferenceEquals(sender, ProtectSlider)) UpdateBrightnessHistogram();
        if (!HslEditor.IsVisible) UpdateSelectionHistogram();
        RecordEdit((sender, item), changesImage: (hadEdits || m.HasEdits) && m.Enabled);
        RequestRender(interactive: true);
    }

    // ------------------------------------------------------ selected colours

    /// <summary>Whether the selected colours are shown in place of the image: while M is held or they are being changed.</summary>
    private bool ShowingMask => (_maskKeyHeld || _selectionDragging) && ColourTab.IsSelected;

    private bool _maskKeyHeld;
    private bool _selectionDragging;

    private void SelectionDrag_Started(object? sender, EventArgs e)
    {
        _selectionDragging = true;
        RequestRender(interactive: true);
    }

    private void SelectionDrag_Completed(object? sender, EventArgs e)
    {
        if (!_selectionDragging) return;
        _selectionDragging = false;
        RequestRender();
    }

    private void SetMaskKeyHeld(bool held)
    {
        if (_maskKeyHeld == held) return;
        _maskKeyHeld = held;
        if (Selected is not null) RequestRender();
    }

    private static bool HasRefinements(ColourAdjustment m) =>
        m.LightnessMin > 0 || m.LightnessMax < 100 || m.NeutralProtection != new ColourAdjustment().NeutralProtection;

    /// <summary>Shows a colour's hue and brightness range on the two bands, with their readouts.</summary>
    private void ShowSelectionRange(ColourAdjustment m)
    {
        HueRangeBand.SetRange(m.TargetHue - m.Range, m.TargetHue + m.Range, m.Smoothness);
        BrightnessRangeBand.SetRange(m.LightnessMin, m.LightnessMax, m.LightnessSmoothness);
        UpdateRangeTexts(m);
        UpdateBrightnessHistogram();
    }

    private void UpdateRangeTexts(ColourAdjustment m)
    {
        HueRangeText.Text = $"{ColourMath.HueName(m.TargetHue)} {Math.Round(m.TargetHue) % 360:0}°    " +
            $"Width ±{m.Range:0}°    Feather {m.Smoothness:0}°";
        BrightnessRangeText.Text = $"Darkest {m.LightnessMin:0}%    Brightest {m.LightnessMax:0}%    Feather {m.LightnessSmoothness:0}%";
    }

    private void HueRangeBand_RangeChanged(object? sender, bool interactive)
    {
        if (Selected is not { } item) return;
        var m = item.Model;
        bool hadEdits = m.HasEdits;
        m.TargetHue = ColourAdjustment.WrapHue((float)((HueRangeBand.Low + HueRangeBand.High) / 2));
        m.Range = (float)((HueRangeBand.High - HueRangeBand.Low) / 2);
        m.Smoothness = (float)HueRangeBand.Feather;
        UpdateSliderRamps();
        if (!interactive) UpdateBrightnessHistogram(); // too slow to follow a drag
        SelectionRangeChanged(item, hadEdits, interactive);
    }

    private void BrightnessRangeBand_RangeChanged(object? sender, bool interactive)
    {
        if (Selected is not { } item) return;
        var m = item.Model;
        bool hadEdits = m.HasEdits;
        m.LightnessMin = (float)Math.Round(BrightnessRangeBand.Low, 1);
        m.LightnessMax = (float)Math.Round(BrightnessRangeBand.High, 1);
        m.LightnessSmoothness = (float)Math.Round(BrightnessRangeBand.Feather, 1);
        SelectionRangeChanged(item, hadEdits, interactive);
    }

    private void SelectionRangeChanged(AdjustmentItem item, bool hadEdits, bool interactive)
    {
        var m = item.Model;
        item.Refresh();
        UpdateRangeTexts(m);
        if (!HslEditor.IsVisible) UpdateSelectionHistogram();
        RecordEdit((HueRangeBand, item), changesImage: (hadEdits || m.HasEdits) && m.Enabled, sealStep: !interactive);
        RequestRender(interactive);
    }

    private void HueRangeBand_ResetRequested(object? sender, EventArgs e)
    {
        if (Selected is not { } item) return;
        var m = item.Model;
        var defaults = new ColourAdjustment();
        bool hadEdits = m.HasEdits;
        m.TargetHue = ColourMath.Hue(m.SampleR, m.SampleG, m.SampleB);
        m.Range = defaults.Range;
        m.Smoothness = defaults.Smoothness;
        ShowSelectionRange(m);
        UpdateSliderRamps();
        SelectionRangeChanged(item, hadEdits, interactive: false);
    }

    private void BrightnessRangeBand_ResetRequested(object? sender, EventArgs e)
    {
        if (Selected is not { } item) return;
        var m = item.Model;
        var defaults = new ColourAdjustment();
        bool hadEdits = m.HasEdits;
        m.LightnessMin = defaults.LightnessMin;
        m.LightnessMax = defaults.LightnessMax;
        m.LightnessSmoothness = defaults.LightnessSmoothness;
        ShowSelectionRange(m);
        SelectionRangeChanged(item, hadEdits, interactive: false);
    }

    private void RefineExpander_Expanded(object? sender, RoutedEventArgs e) => UpdateBrightnessHistogram();

    /// <summary>
    /// Shows how bright the colours of the selected hue are behind the brightness band, ignoring the brightness
    /// limits themselves so that what they leave out still shows.
    /// </summary>
    private void UpdateBrightnessHistogram()
    {
        if (!RefineExpander.IsExpanded || Selected is not { } item || (_proxy ?? _full) is not { } layer) return;
        var hues = item.Model.Clone();
        hues.LightnessMin = 0;
        hues.LightnessMax = 100;
        BrightnessRangeBand.Histogram = CurveSet.Histogram(layer.Source, bins: 100, selection: hues, baseAdjustments: BaseAdjustments)[0];
    }

    /// <summary>
    /// Shift-click widens the selected colour to take in the colour clicked; Alt-click (Option on macOS) narrows
    /// it to leave that colour out.
    /// </summary>
    private void RefineSelection(Point imagePoint, bool include)
    {
        if (_full is null || Selected is not { } item) return;
        var source = _full.Source;
        int x = (int)(imagePoint.X / _zoom), y = (int)(imagePoint.Y / _zoom);
        if (x < 0 || y < 0 || x >= source.Width || y >= source.Height) return;

        var (r, g, b) = ColourProcessor.Sample(source, x, y, radius: 3);
        BaseAdjustments?.Apply(ref r, ref g, ref b); // judge the colour as the colour editor sees it
        r = Math.Clamp(r, 0f, 1f);
        g = Math.Clamp(g, 0f, 1f);
        b = Math.Clamp(b, 0f, 1f);
        ColourMath.RgbToHsl(r, g, b, out float hue, out _, out _, out float chroma);
        if (chroma < 0.01f)
        {
            ShowMessage(InfoBarSeverity.Informational, "That spot is neutral",
                "The area you clicked is almost grey, so it has no hue to add or leave out.");
            return;
        }

        var m = item.Model;
        bool hadEdits = m.HasEdits;
        if (include ? !m.Include(hue, chroma, ColourMath.Luminance(r, g, b)) : !m.Exclude(hue))
        {
            if (include)
                ShowMessage(InfoBarSeverity.Informational, "Already selected", "That colour is already fully selected.");
            else if (ColourAdjustment.HueDistance(hue, m.TargetHue) >= m.Range + m.Smoothness)
                ShowMessage(InfoBarSeverity.Informational, "Already left out", "That colour is not selected.");
            else
                ShowMessage(InfoBarSeverity.Informational, "Too near the middle",
                    "That colour is at the heart of the selection, so leaving it out would leave nothing. Pick it as a new colour instead.");
            return;
        }

        if (HasRefinements(m)) RefineExpander.IsExpanded = true;
        ShowSelectionRange(m);
        UpdateSliderRamps();
        item.Refresh();
        if (!HslEditor.IsVisible) UpdateSelectionHistogram();
        RecordEdit(changesImage: (hadEdits || m.HasEdits) && m.Enabled);
        RequestRender();
    }

    private void ResetAdjustment_Click(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || !item.Model.HasHslEdits) return;
        item.Model.ResetHsl();
        item.Refresh();
        LoadEditor(item);
        RecordEdit();
        RequestRender();
    }

    private void DeleteAdjustment_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not AdjustmentItem item) return;
        int index = _adjustments.IndexOf(item);
        _adjustments.Remove(item);
        if (_adjustments.Count > 0)
            AdjustmentList.SelectedIndex = Math.Min(index, _adjustments.Count - 1);
        RecordEdit(changesImage: item.Model.HasEdits && item.Model.Enabled);
        RequestRender();
    }

    private void UpdateSliderRamps()
    {
        if (Selected is not { } item) return;
        float h = item.Model.TargetHue;
        HueShiftSlider.GradientBrush = HueRamp(h - 90, h + 90);
        SaturationSlider.GradientBrush = Ramp(Hsl(h, 0, 0.5f), Hsl(h, 1, 0.5f));
        LightnessSlider.GradientBrush = Ramp(Hsl(h, 0.8f, 0.1f), Hsl(h, 0.8f, 0.5f), Hsl(h, 0.8f, 0.9f));
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

    // ---------------------------------------------------------------- global

    private void ImageCurves_CurvesChanged(object? sender, bool interactive)
    {
        RecordEdit(ImageCurves, sealStep: !interactive);
        UpdateSelectionHistogram();
        QueueHueHistogram();
        RequestRender(interactive);
    }

    private void HueCurves_CurvesChanged(object? sender, bool interactive)
    {
        RecordEdit(HueCurves, sealStep: !interactive);
        UpdateSelectionHistogram();
        QueueHueHistogram();
        RequestRender(interactive);
    }

    private void QueueHueHistogram()
    {
        _hueHistogramTimer.Stop();
        _hueHistogramTimer.Start();
    }

    /// <summary>Shows the hues of the image as the hue curves see it, behind them.</summary>
    private void UpdateHueHistogram()
    {
        if ((_proxy ?? _full) is not { } layer || _image is not { IsColour: true }) return;
        HueCurves.Histogram = HueCurveSet.Histogram(layer.Source, baseAdjustments:
            Core.Colour.BaseAdjustments.Create(Narrowband, new CurveLookup(_curves), GlobalSaturation, RemovePurple));
        // The colour editor sees the image after the hue curves too.
        HueRangeBand.Histogram = HueCurveSet.Histogram(layer.Source, baseAdjustments: BaseAdjustments);
    }

    private float GlobalSaturation => (float)GlobalSaturationSlider.Value;

    private void SetGlobalSaturation(double value)
    {
        _suppressEditorEvents = true;
        GlobalSaturationSlider.Value = value;
        _suppressEditorEvents = false;
    }

    private void GlobalSaturationSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _restoring) return;
        RecordEdit(sender);
        UpdateSelectionHistogram();
        QueueHueHistogram();
        RequestRender(interactive: true);
    }

    private float RemovePurple => (float)RemovePurpleSlider.Value;

    private void SetRemovePurple(double value)
    {
        _suppressEditorEvents = true;
        RemovePurpleSlider.Value = value;
        _suppressEditorEvents = false;
    }

    /// <summary>The changes made before the colour editor, or null when there are none.</summary>
    private BaseAdjustments? BaseAdjustments =>
        Core.Colour.BaseAdjustments.Create(Narrowband, new CurveLookup(_curves), GlobalSaturation, RemovePurple,
            new HueCurveLookup(_hueCurves));

    // ------------------------------------------------------------ narrowband

    private NarrowbandSettings _narrowband = new();

    /// <summary>The normalisation for the current settings, or null when it is off.</summary>
    private NarrowbandNormalization? Narrowband => NarrowbandNormalization.Create(_narrowband, _channelStatistics);

    /// <summary>Shows <paramref name="settings"/> in the controls without treating it as an edit.</summary>
    private void SetNarrowband(NarrowbandSettings settings)
    {
        _suppressEditorEvents = true;
        NarrowbandOffButton.IsChecked = settings.Palette == NarrowbandPalette.Off;
        NarrowbandShoButton.IsChecked = settings.Palette == NarrowbandPalette.Sho;
        NarrowbandHooButton.IsChecked = settings.Palette == NarrowbandPalette.Hoo;
        SiiBoostSlider.Value = settings.SiiBoost;
        OiiiBoostSlider.Value = settings.OiiiBoost;
        GreenReductionSlider.Value = settings.GreenReduction * 100;
        LightnessNormalisedButton.IsChecked = settings.Lightness == NarrowbandLightness.Normalised;
        LightnessOriginalButton.IsChecked = settings.Lightness == NarrowbandLightness.Original;
        LightnessHAlphaButton.IsChecked = settings.Lightness == NarrowbandLightness.HAlpha;
        NarrowbandEditor.IsVisible = !settings.IsEmpty;
        SiiBoostSlider.IsVisible = settings.Palette != NarrowbandPalette.Hoo; // HOO has no SII
        _suppressEditorEvents = false;
        _narrowband = settings;
    }

    private void ChangeNarrowband(NarrowbandSettings settings, bool interactive, object? mergeKey = null)
    {
        bool changed = settings != _narrowband;
        SetNarrowband(settings);
        if (!changed) return;
        RecordEdit(mergeKey);
        UpdateNarrowbandHistograms();
        RequestRender(interactive);
    }

    /// <summary>The Global curves act on the normalised image, and everything after them sees it too.</summary>
    private void UpdateNarrowbandHistograms()
    {
        if ((_proxy ?? _full) is { } layer)
        {
            var narrowband = Core.Colour.BaseAdjustments.Create(Narrowband);
            ImageCurves.Histogram = CurveSet.Histogram(layer.Source, baseAdjustments: narrowband);
            ImageCurves.Clipping = CurveClipping.Sample(layer.Source, narrowband);
        }
        UpdateSelectionHistogram();
        QueueHueHistogram();
    }

    private void NarrowbandPalette_Click(object? sender, RoutedEventArgs e)
    {
        var palette = sender == NarrowbandShoButton ? NarrowbandPalette.Sho
            : sender == NarrowbandHooButton ? NarrowbandPalette.Hoo
            : NarrowbandPalette.Off;
        ChangeNarrowband(_narrowband with { Palette = palette }, interactive: false);
    }

    private void NarrowbandLightness_Click(object? sender, RoutedEventArgs e)
    {
        var lightness = sender == LightnessOriginalButton ? NarrowbandLightness.Original
            : sender == LightnessHAlphaButton ? NarrowbandLightness.HAlpha
            : NarrowbandLightness.Normalised;
        ChangeNarrowband(_narrowband with { Lightness = lightness }, interactive: false);
    }

    private void NarrowbandSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _restoring) return;
        ChangeNarrowband(_narrowband with
        {
            SiiBoost = (float)SiiBoostSlider.Value,
            OiiiBoost = (float)OiiiBoostSlider.Value,
            GreenReduction = (float)GreenReductionSlider.Value / 100f,
        }, interactive: true, mergeKey: sender);
    }

    // ----------------------------------------------------------------- stars

    private float StarBrightness => (float)StarBrightnessSlider.Value;

    private bool ShowStars => ShowStarsCheckBox.IsChecked == true;

    private void LoadStars_Click(object? sender, RoutedEventArgs e) => _ = RunFileActionAsync(async () =>
    {
        if (_image is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add stars image",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("XISF, FITS, TIFF or camera raw image") { Patterns = [.. AstroImageFile.AllExtensions.Select(x => "*" + x)] }],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path) return;

        SetBusy("Loading stars…");
        try
        {
            var image = _image;
            int factor = _proxyFactor;
            var (full, proxy) = await Task.Run(() =>
            {
                var stars = AstroImageFile.Load(path);
                if (stars.Width != image.Width || stars.Height != image.Height)
                    throw new InvalidDataException(
                        $"The stars image is {stars.Width} × {stars.Height}, but this image is {image.Width} × {image.Height}. They must be the same size.");
                var rgb = RgbImage.From(stars);
                return (rgb, factor > 1 ? rgb.Downsample(factor) : null);
            });
            if (image != _image) return; // a different file was opened meanwhile
            SetStars(new StarsState(full, proxy, path));
            _settings.RememberFolder(path);
            _settings.Save();
            RecordEdit();
            RequestRender();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not add the stars", ex.Message);
        }
        finally
        {
            ClearBusy();
        }
    });

    private void RemoveStars_Click(object? sender, RoutedEventArgs e)
    {
        if (_stars is null) return;
        SetStars(null);
        RecordEdit();
        RequestRender();
    }

    private void SetStars(StarsState? stars)
    {
        _stars = stars;
        if (_full is not null) _full.Stars = stars?.Full;
        if (_proxy is not null) _proxy.Stars = stars?.Proxy;
        StarsEditor.IsVisible = stars is not null;
        StarsFileText.Text = stars is null ? "" : Path.GetFileName(stars.Path);
        ToolTip.SetTip(StarsFileText, stars?.Path);
        LoadStarsText.Text = stars is null ? "Add stars image…" : "Replace stars image…";
    }

    private void SetStarBrightness(double value)
    {
        _suppressEditorEvents = true;
        StarBrightnessSlider.Value = value;
        _suppressEditorEvents = false;
    }

    private void SetShowStars(bool value)
    {
        _suppressEditorEvents = true;
        ShowStarsCheckBox.IsChecked = value;
        _suppressEditorEvents = false;
    }

    private void ShowStarsCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressEditorEvents || _restoring) return;
        RecordEdit(changesImage: _stars is not null);
        RequestRender();
    }

    private void StarBrightnessSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents || _restoring) return;
        // Without stars the brightness changes nothing yet, but it is kept for when they are added.
        RecordEdit(sender, changesImage: _stars is not null);
        RequestRender(interactive: true);
    }

    // -------------------------------------------------------------- rotation

    private void RotateLeft_Click(object? sender, RoutedEventArgs e) => Rotate(-1);

    private void RotateRight_Click(object? sender, RoutedEventArgs e) => Rotate(1);

    /// <summary>Turns the view by quarter turns, keeping a fitted image fitted and otherwise the centre in place.</summary>
    private void Rotate(int quarterTurns)
    {
        if (_image is null) return;
        bool fitted = Math.Abs(_zoom / FitZoom(Viewer.Bounds.Size) - 1) < 0.002;
        var centre = new Point(Viewer.Viewport.Width / 2, Viewer.Viewport.Height / 2);
        var anchor = Viewer.TranslatePoint(centre, PreviewImage);

        SetRotation(_rotation.Turn(quarterTurns));
        RecordEdit();

        if (fitted)
        {
            FitToWindow();
            return;
        }
        Viewer.UpdateLayout();
        if (anchor is { } a && PreviewImage.TranslatePoint(a, Viewer) is { } moved)
            Viewer.Offset += moved - centre;
    }

    private void SetRotation(Rotation rotation)
    {
        _rotation = rotation;
        ImageRotator.LayoutTransform = rotation == Rotation.None ? null : new RotateTransform(rotation.Degrees());
        BeforeRotator.LayoutTransform = rotation == Rotation.None ? null : new RotateTransform(rotation.Degrees());
        UpdateCropOverlay();
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
        if (VisibleArea is { } area)
        {
            // Centre the cropped area; the image around it is hidden.
            var viewport = Viewer.Viewport;
            Viewer.Offset = new Vector((area.X + area.Width / 2.0) * _zoom - viewport.Width / 2,
                (area.Y + area.Height / 2.0) * _zoom - viewport.Height / 2);
        }
        else
        {
            Viewer.Offset = default;
        }
    }

    /// <summary>The crop as displayed, unless the whole image is shown (no crop, or while cropping).</summary>
    private CropRect? VisibleArea =>
        _image is not null && !_cropEditing && _crop is { } c ? c.Rotate(_rotation, _image.Width, _image.Height) : null;

    /// <summary>The zoom that shows the whole image, or the cropped part of it, in a viewport of the given size.</summary>
    private double FitZoom(Size viewport)
    {
        if (_image is null) return 1;
        var (width, height) = VisibleArea is { } area ? (area.Width, area.Height) : _rotation.Size(_image.Width, _image.Height);
        return Math.Min(viewport.Width / width, viewport.Height / height) * 0.98;
    }

    private void SetZoom(double zoom)
    {
        if (_image is null) return;
        _zoom = Math.Clamp(zoom, MinZoom / RenderScaling, MaxZoom / RenderScaling);
        PreviewImage.Width = BeforeImage.Width = _image.Width * _zoom;
        PreviewImage.Height = BeforeImage.Height = _image.Height * _zoom;
        // Smooth when zoomed out; sharp pixels when zoomed well in.
        var interpolation = _zoom * RenderScaling >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        RenderOptions.SetBitmapInterpolationMode(PreviewImage, interpolation);
        RenderOptions.SetBitmapInterpolationMode(BeforeImage, interpolation);
        CropOverlay.Zoom = _zoom;
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

        if (properties.IsLeftButtonPressed && e.KeyModifiers is KeyModifiers.Shift or KeyModifiers.Alt
            && ColourTab.IsSelected && ColourPanel.IsEnabled && Selected is not null && !_cropEditing)
        {
            RefineSelection(e.GetPosition(PreviewImage), include: e.KeyModifiers == KeyModifiers.Shift);
            e.Handled = true;
            return;
        }

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
                case Key.Y when SplitButton.IsEnabled:
                    ToggleSplit();
                    break;
                case Key.J when ClippingButton.IsEnabled:
                    ToggleClipping();
                    break;
                case Key.C when CropButton.IsEnabled:
                    SetCropEditing(!_cropEditing);
                    break;
                case Key.Enter or Key.Escape when _cropEditing:
                    SetCropEditing(false);
                    break;
                case Key.P when ColourPanel.IsEnabled:
                    TogglePicker();
                    break;
                case Key.M when ColourPanel.IsEnabled:
                    SetMaskKeyHeld(true);
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
            case Key.OemOpenBrackets when !shift:
                Rotate(-1);
                break;
            case Key.OemCloseBrackets when !shift:
                Rotate(1);
                break;
            case Key.Z:
                if (shift) Redo();
                else Undo();
                break;
            case Key.Y when !shift:
                Redo();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key == Key.M) SetMaskKeyHeld(false);
    }

    private void ToggleCompare()
    {
        CompareButton.IsChecked = CompareButton.IsChecked != true;
        Compare_Click(CompareButton, new RoutedEventArgs());
    }

    private void TogglePicker()
    {
        EditorTabs.SelectedItem = ColourTab;
        PickerButton.IsChecked = PickerButton.IsChecked != true;
        if (PickerButton.IsChecked == true) SetCropEditing(false);
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

        _undoMenuItem = Item("Undo", "Meta+Z", Undo);
        _redoMenuItem = Item("Redo", "Meta+Shift+Z", Redo);
        _recentMenuItem = new NativeMenuItem("Open Recent") { Menu = new NativeMenu() };
        var menu = new NativeMenu
        {
            new NativeMenuItem("File")
            {
                Menu = new NativeMenu
                {
                    Item("Open…", "Meta+O", () => _ = OpenAsync(), needsImage: false),
                    _recentMenuItem,
                    new NativeMenuItemSeparator(),
                    Item("Save", "Meta+S", () => _ = RunFileActionAsync(() => SaveAsync(saveAs: false))),
                    Item("Save As…", "Meta+Shift+S", () => _ = RunFileActionAsync(() => SaveAsync(saveAs: true))),
                },
            },
            new NativeMenuItem("Edit")
            {
                Menu = new NativeMenu { _undoMenuItem, _redoMenuItem },
            },
            new NativeMenuItem("View")
            {
                Menu = new NativeMenu
                {
                    Item("Fit to Window", "Meta+D0", FitToWindow),
                    Item("Actual Size", "Meta+D1", ShowActualSize),
                    new NativeMenuItemSeparator(),
                    Item("Rotate Left", "Meta+OemOpenBrackets", () => Rotate(-1)),
                    Item("Rotate Right", "Meta+OemCloseBrackets", () => Rotate(1)),
                    new NativeMenuItemSeparator(),
                    Item("Show Original (B)", null, ToggleCompare),
                    Item("Split Before and After (Y)", null, ToggleSplit),
                    Item("Show Clipping (J)", null, ToggleClipping),
                    Item("Crop (C)", null, () => SetCropEditing(!_cropEditing)),
                    Item("Pick Colour (P)", null, () => { if (ColourPanel.IsEnabled) TogglePicker(); }),
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

    // ------------------------------------------------------------ undo/redo

    /// <summary>
    /// Records the edits as they now are as one undo step. Changes with the same <paramref name="mergeKey"/> in quick
    /// succession (a slider or curve point being dragged) make one step; <paramref name="sealStep"/> ends such a run.
    /// </summary>
    /// <param name="changesImage">False for an edit that leaves the image as it was, so it doesn't count as unsaved.</param>
    private void RecordEdit(object? mergeKey = null, bool changesImage = true, bool sealStep = false)
    {
        if (_restoring || _image is null) return;
        _history.Record(CaptureState(), mergeKey, changesImage);
        if (sealStep) _history.Seal();
        UpdateChrome();
    }

    private EditState CaptureState() => new(
        [.. _adjustments.Select(a => new AdjustmentState(a.Model.Clone(), a.EditWithCurves))],
        AdjustmentList.SelectedIndex,
        _curves.Clone(),
        _hueCurves.Clone(),
        GlobalSaturation,
        RemovePurple,
        _narrowband,
        _stars,
        StarBrightness,
        ShowStars,
        _rotation,
        _crop);

    private void Undo_Click(object? sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object? sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_image is not null && _history.Undo() is { } state) Restore(state);
    }

    private void Redo()
    {
        if (_image is not null && _history.Redo() is { } state) Restore(state);
    }

    /// <summary>Puts every edit and its controls back as they were in <paramref name="state"/>.</summary>
    private void Restore(EditState state)
    {
        _restoring = true;
        try
        {
            bool narrowbandChanged = state.Narrowband != _narrowband;
            bool rotationChanged = state.Rotation != _rotation || state.Crop != _crop;
            bool fitted = Math.Abs(_zoom / FitZoom(Viewer.Bounds.Size) - 1) < 0.002;

            _adjustments.Clear();
            foreach (var a in state.Adjustments) _adjustments.Add(CreateItem(a.Model.Clone(), a.EditWithCurves));
            _curves.CopyFrom(state.Curves);
            ImageCurves.Curves = _curves;
            _hueCurves.CopyFrom(state.HueCurves);
            HueCurves.Refresh();
            SetGlobalSaturation(state.Saturation);
            SetRemovePurple(state.RemovePurple);
            SetNarrowband(state.Narrowband);
            SetStars(state.Stars);
            SetStarBrightness(state.StarBrightness);
            SetShowStars(state.ShowStars);
            _crop = state.Crop;
            SetRotation(state.Rotation);

            int index = Math.Min(state.SelectedIndex, _adjustments.Count - 1);
            AdjustmentList.SelectedIndex = index;
            LoadEditor(Selected);

            if (narrowbandChanged) UpdateNarrowbandHistograms();
            else
            {
                UpdateSelectionHistogram();
                QueueHueHistogram();
            }
            if (rotationChanged && fitted) Dispatcher.UIThread.Post(FitToWindow, DispatcherPriority.Background);
        }
        finally
        {
            _restoring = false;
        }
        RequestRender();
        UpdateChrome();
    }

    // ---------------------------------------------------------- recent files

    /// <summary>Rebuilds the recent files menu under the arrow beside Open.</summary>
    private void BuildRecentFlyout()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var path in _settings.RecentFiles)
        {
            var item = new MenuItem { Header = Path.GetFileName(path) };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => OpenRecent(path);
            flyout.Items.Add(item);
        }
        if (_settings.RecentFiles.Count == 0)
            flyout.Items.Add(new MenuItem { Header = "No recent files", IsEnabled = false });
        else
        {
            flyout.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Clear recent files" };
            clear.Click += (_, _) => ClearRecentFiles();
            flyout.Items.Add(clear);
        }
        RecentButton.Flyout = flyout;
    }

    private void OpenRecent(string path) => _ = RunFileActionAsync(async () =>
    {
        if (await ConfirmDiscardChangesAsync()) await LoadAsync(path);
    });

    private void ClearRecentFiles()
    {
        _settings.RecentFiles.Clear();
        _settings.Save();
        UpdateRecentFiles();
    }

    /// <summary>Lists the recent files on the start screen and in the macOS File menu.</summary>
    private void UpdateRecentFiles()
    {
        var recent = _settings.RecentFiles;
        BuildRecentFlyout();
        EmptyRecent.IsVisible = recent.Count > 0;
        EmptyRecentList.Children.Clear();
        foreach (var path in recent.Take(5))
        {
            var link = new HyperlinkButton
            {
                Content = new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Padding = new Thickness(6, 2),
            };
            ToolTip.SetTip(link, path);
            link.Click += (_, _) => OpenRecent(path);
            EmptyRecentList.Children.Add(link);
        }

        if (_recentMenuItem?.Menu is { } menu)
        {
            menu.Items.Clear();
            foreach (var path in recent)
            {
                var item = new NativeMenuItem(Path.GetFileName(path)) { ToolTip = path };
                item.Click += (_, _) => { if (Toolbar.IsEnabled) OpenRecent(path); };
                menu.Items.Add(item);
            }
            if (recent.Count > 0)
            {
                menu.Items.Add(new NativeMenuItemSeparator());
                var clear = new NativeMenuItem("Clear Menu");
                clear.Click += (_, _) => ClearRecentFiles();
                menu.Items.Add(clear);
            }
            _recentMenuItem.IsEnabled = recent.Count > 0;
        }
    }

    private void UpdateChrome()
    {
        bool loaded = _image is not null;
        EmptyState.IsVisible = !loaded;
        ColourPanel.IsEnabled = loaded && _image!.IsColour;
        GlobalPanel.IsEnabled = loaded;
        StarsPanel.IsEnabled = loaded;
        // A single-channel image only has the composite curve, and no saturation to change.
        ImageCurves.ChannelsEnabled = GlobalSaturationSlider.IsEnabled = RemovePurpleSlider.IsEnabled =
            NarrowbandSection.IsEnabled = HueCurvesSection.IsEnabled = loaded && _image!.IsColour;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = FitButton.IsEnabled =
            RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled =
            ActualSizeButton.IsEnabled = CompareButton.IsEnabled = SplitButton.IsEnabled = ClippingButton.IsEnabled = CropButton.IsEnabled = loaded;
        foreach (var item in _imageMenuItems) item.IsEnabled = loaded;
        UndoButton.IsEnabled = loaded && _history.CanUndo;
        RedoButton.IsEnabled = loaded && _history.CanRedo;
        if (_undoMenuItem is not null) _undoMenuItem.IsEnabled = UndoButton.IsEnabled;
        if (_redoMenuItem is not null) _redoMenuItem.IsEnabled = RedoButton.IsEnabled;

        Title = _path is null ? AppTitle : $"{Path.GetFileName(_path)}{(IsDirty ? " •" : "")} — {AppTitle}";
        FileInfoText.Text = _image is null
            ? ""
            : $"{_path}{ModifiedText(_path)}    {_image.Width} × {_image.Height}{(_crop is { } c ? $" (cropped to {c.Width} × {c.Height})" : "")}    {_image.Channels} ch {_image.ColorSpace}    {_image.SampleFormatName}";
        UpdateZoomText();
    }

    /// <summary>The file's last-modified date and time for the status bar, read live so it follows saves.</summary>
    private static string ModifiedText(string? path) =>
        path is not null && File.Exists(path) ? $"    Modified {File.GetLastWriteTime(path):g}" : "";

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
