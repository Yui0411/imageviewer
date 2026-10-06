using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImageViewer;

public partial class MainWindow : Window
{
    private const double MaxZoom = 32;
    private const double ZoomStep = 1.2;
    private const double SideHoverWidth = 100; // distance from the left/right edge that reveals the nav buttons
    private const string MaximizeGlyph = "";
    private const string RestoreGlyph = "";

    private readonly FolderNavigator _nav = new();
    private readonly Dictionary<string, Task<LoadedImage>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<UIElement, bool> _shown = new();
    private readonly WindowSettings _settings = WindowSettings.Load();

    private LoadedImage? _image;
    private int _loadVersion;

    // View state. _scale 1.0 means one image pixel per screen pixel; _offset is the image's top-left in the viewport.
    private double _scale = 1;
    private Point _offset;
    private bool _fitMode = true;
    private bool _panning;
    private Point _panStart, _panOrigin;

    private bool _fullscreen;
    private bool _maximizedBeforeFullscreen;

    // Rotation: presses made while a save is running are added up and saved together.
    private bool _rotating;
    private string? _rotatingPath;
    private int _pendingTurns;

    private readonly DispatcherTimer _toastTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
        };
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            ShowMessage($"File not found:\n{path}");
            return;
        }
        _nav.Open(path);
        ShowCurrent();
    }

    // ---------------------------------------------------------------- loading

    private async void ShowCurrent()
    {
        var path = _nav.Current;
        if (path == null) return;

        int version = ++_loadVersion;
        FileNameText.Text = Path.GetFileName(path);
        Title = $"{Path.GetFileName(path)} - Image Viewer";

        LoadedImage? image = null;
        string? error = null;
        try
        {
            image = await GetImageAsync(path);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        if (version != _loadVersion) return; // the user already moved on to another image

        _image = image;
        if (image == null)
        {
            ImageView.Source = null;
            var hint = Path.GetExtension(path).ToLowerInvariant() is ".heic" or ".heif"
                ? "\n\nHEIC images need the free \"HEIF Image Extensions\" from the Microsoft Store."
                : "";
            ShowMessage($"Can't open {Path.GetFileName(path)}\n{error}{hint}");
        }
        else
        {
            MessageText.Visibility = Visibility.Collapsed;
            ImageView.Source = image.Bitmap;
            UpdateImageSize();
            FitToWindow();
        }
        UpdateInfo();
        PreloadNeighbors();
    }

    private Task<LoadedImage> GetImageAsync(string path)
    {
        if (_cache.TryGetValue(path, out var task))
        {
            bool stale = task.IsFaulted
                || (task.IsCompletedSuccessfully && task.Result.LastWriteUtc != File.GetLastWriteTimeUtc(path));
            if (!stale) return task;
        }
        task = Task.Run(() => ImageLoader.Load(path));
        _cache[path] = task;
        return task;
    }

    /// <summary>Starts decoding the previous and next images so flicking through is instant; drops everything else.</summary>
    private void PreloadNeighbors()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_nav.Current is { } current) keep.Add(current);
        foreach (var path in new[] { _nav.Peek(1), _nav.Peek(-1) })
        {
            if (path == null) continue;
            keep.Add(path);
            _ = GetImageAsync(path);
        }
        foreach (var key in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
            _cache.Remove(key);
    }

    private void Navigate(int offset)
    {
        if (_nav.Move(offset)) ShowCurrent();
    }

    private void GoTo(int index)
    {
        if (_nav.Count == 0 || index == _nav.Index) return;
        _nav.MoveTo(index);
        ShowCurrent();
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
    }

    private void ShowToast(string text, bool warning = true)
    {
        ToastText.Text = text;
        ToastIcon.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        _toastTimer.Interval = TimeSpan.FromSeconds(warning ? 5 : 2.5);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- rotation

    /// <summary>
    /// Rotates the current image and overwrites the file. The view turns immediately; the lossless save runs in the
    /// background, and if it is refused the view reverts and the reason is shown.
    /// </summary>
    private async void Rotate(int clockwiseTurns)
    {
        var path = _nav.Current;
        if (path == null || _image == null) return;
        if (!ImageRotator.CanRotate(path))
        {
            ShowToast($"Rotating {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} files isn't supported.");
            return;
        }
        if (_rotating && !SamePath(path, _rotatingPath))
        {
            ShowToast("Still saving the previous rotation…", warning: false);
            return;
        }

        try
        {
            // Turns already queued haven't been saved yet, so check against the file's current state plus all of them.
            ImageRotator.EnsureCanRotate(path, _pendingTurns + clockwiseTurns);
        }
        catch (Exception ex) when (ex is RotateException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowToast(ex is RotateException ? ex.Message : $"Couldn't rotate: {ex.Message}");
            return;
        }

        ShowRotatedPreview(clockwiseTurns);
        _pendingTurns += clockwiseTurns;
        if (_rotating) return; // the running save loop will pick these turns up

        _rotating = true;
        _rotatingPath = path;
        string? error = null;
        try
        {
            int turns;
            while ((turns = ((_pendingTurns % 4) + 4) % 4) != 0)
            {
                _pendingTurns = 0;
                await Task.Run(() => ImageRotator.Rotate(path, turns));
            }
        }
        catch (RotateException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = $"Couldn't rotate: {ex.Message}";
        }
        finally
        {
            _pendingTurns = 0;
            _rotating = false;
            _rotatingPath = null;
        }

        if (error != null) ShowToast(error);
        _cache.Remove(path);
        if (SamePath(_nav.Current, path)) ShowCurrent(); // show the saved file (or the untouched one, if refused)
    }

    private void ShowRotatedPreview(int clockwiseTurns)
    {
        if (_image == null) return;
        var preview = new TransformedBitmap(_image.Bitmap, new RotateTransform(90 * clockwiseTurns));
        preview.Freeze();
        _image = _image with { Bitmap = preview };
        ImageView.Source = preview;
        UpdateImageSize();
        FitToWindow();
    }

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void UpdateInfo()
    {
        if (_nav.Current == null)
        {
            InfoText.Text = "";
            return;
        }
        var parts = new List<string> { $"{_nav.Index + 1} / {_nav.Count}" };
        if (_image != null)
        {
            parts.Add($"{_image.Bitmap.PixelWidth} × {_image.Bitmap.PixelHeight}");
            parts.Add($"{Math.Round(_scale * 100)}%");
        }
        InfoText.Text = string.Join("     ", parts);
    }

    private void ShowOpenDialog()
    {
        var patterns = string.Join(";", FolderNavigator.SupportedExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog { Filter = $"Images|{patterns}|All files|*.*" };
        if (_nav.Current != null) dialog.InitialDirectory = Path.GetDirectoryName(_nav.Current);
        if (dialog.ShowDialog(this) == true) OpenFile(dialog.FileName);
    }

    // ---------------------------------------------------------------- zoom & pan

    private Point ViewportCenter => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    /// <summary>Sizes the Image element so that a scale of 1 shows the bitmap at its true pixel size on this monitor.</summary>
    private void UpdateImageSize()
    {
        if (_image == null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        ImageView.Width = _image.Bitmap.PixelWidth / dpi.DpiScaleX;
        ImageView.Height = _image.Bitmap.PixelHeight / dpi.DpiScaleY;
    }

    /// <summary>Scale that fits the whole image in the window. Small images are never enlarged.</summary>
    private double FitScale()
    {
        if (_image == null || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return 1;
        return Math.Min(1, Math.Min(Viewport.ActualWidth / ImageView.Width, Viewport.ActualHeight / ImageView.Height));
    }

    private bool CanPan() => _image != null
        && (ImageView.Width * _scale > Viewport.ActualWidth + 0.5 || ImageView.Height * _scale > Viewport.ActualHeight + 0.5);

    private void FitToWindow()
    {
        if (_image == null) return;
        _fitMode = true;
        _scale = FitScale();
        ClampOffset();
        ApplyTransform();
    }

    /// <summary>Zooms to <paramref name="scale"/>, keeping the point under <paramref name="anchor"/> fixed.</summary>
    private void ZoomTo(double scale, Point anchor)
    {
        if (_image == null) return;
        double fit = FitScale();
        scale = Math.Clamp(scale, fit, Math.Max(fit, MaxZoom));
        _offset = new Point(
            anchor.X - (anchor.X - _offset.X) * scale / _scale,
            anchor.Y - (anchor.Y - _offset.Y) * scale / _scale);
        _scale = scale;
        _fitMode = scale <= fit + 1e-9;
        ClampOffset();
        ApplyTransform();
    }

    private void ToggleFitActual(Point anchor)
    {
        if (_image == null) return;
        if (_fitMode && FitScale() < 1) ZoomTo(1, anchor);
        else FitToWindow();
    }

    /// <summary>Centers the image along any axis where it fits, and stops it being dragged out of view otherwise.</summary>
    private void ClampOffset()
    {
        double w = ImageView.Width * _scale, h = ImageView.Height * _scale;
        double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
        _offset.X = w <= vw ? (vw - w) / 2 : Math.Clamp(_offset.X, vw - w, 0);
        _offset.Y = h <= vh ? (vh - h) / 2 : Math.Clamp(_offset.Y, vh - h, 0);
    }

    private void ApplyTransform()
    {
        ImageTransform.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);
        Viewport.Cursor = _panning ? Cursors.SizeAll : CanPan() ? Cursors.Hand : null;
        UpdateInfo();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFitActual(e.GetPosition(Viewport));
        }
        else if (CanPan())
        {
            _panning = true;
            _panStart = e.GetPosition(Viewport);
            _panOrigin = _offset;
            Viewport.CaptureMouse();
            ApplyTransform();
        }
        else if (WindowState == WindowState.Normal)
        {
            DragMove();
        }
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        _offset = _panOrigin + (e.GetPosition(Viewport) - _panStart);
        ClampOffset();
        ApplyTransform();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        ApplyTransform();
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomTo(_scale * Math.Pow(ZoomStep, e.Delta / 120.0), e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_image == null) return;
        if (_fitMode || _scale < FitScale()) FitToWindow();
        else
        {
            ClampOffset();
            ApplyTransform();
        }
    }

    private void Window_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        UpdateImageSize();
        FitToWindow();
    }

    // ---------------------------------------------------------------- hover UI

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning) return;
        var p = e.GetPosition(Root);
        bool nearTop = p.Y <= Toolbar.ActualHeight + 20;
        bool canNavigate = _nav.Count > 1;
        Fade(Toolbar, nearTop);
        Fade(PrevButton, canNavigate && !nearTop && p.X <= SideHoverWidth);
        Fade(NextButton, canNavigate && !nearTop && p.X >= Root.ActualWidth - SideHoverWidth);
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning) return;
        Fade(Toolbar, false);
        Fade(PrevButton, false);
        Fade(NextButton, false);
    }

    private void Fade(UIElement element, bool show)
    {
        if (_shown.TryGetValue(element, out var current) && current == show) return;
        _shown[element] = show;
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 120 : 250)));
    }

    // ---------------------------------------------------------------- input & buttons

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Right or Key.PageDown: Navigate(1); break;
            case Key.Left or Key.PageUp: Navigate(-1); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(_nav.Count - 1); break;
            case Key.F11: SetFullscreen(!_fullscreen); break;
            case Key.Escape:
                if (_fullscreen) SetFullscreen(false);
                else Close();
                break;
            case Key.D0 or Key.NumPad0: ToggleFitActual(ViewportCenter); break;
            case Key.OemPlus or Key.Add: ZoomTo(_scale * ZoomStep, ViewportCenter); break;
            case Key.OemMinus or Key.Subtract: ZoomTo(_scale / ZoomStep, ViewportCenter); break;
            case Key.O when ctrl: ShowOpenDialog(); break;
            case Key.R when !ctrl: Rotate(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 3 : 1); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            OpenFile(files[0]);
    }

    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (WindowState == WindowState.Normal) DragMove();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private void RotateButton_Click(object sender, RoutedEventArgs e) => Rotate(1);
    private void OpenButton_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();
    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------------- window state

    private void ToggleMaximize()
    {
        if (_fullscreen) SetFullscreen(false);
        else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Fullscreen is a maximize that covers the whole monitor (see <see cref="NativeMethods.ApplyMaximizedBounds"/>).</summary>
    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            _maximizedBeforeFullscreen = WindowState == WindowState.Maximized;
            _fullscreen = true;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _fullscreen = false;
            WindowState = WindowState.Normal;
            if (_maximizedBeforeFullscreen) WindowState = WindowState.Maximized;
        }
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized && !_fullscreen;
        MaximizeButton.Content = maximized ? RestoreGlyph : MaximizeGlyph;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
            NativeMethods.ApplyMaximizedBounds(hwnd, lParam, _fullscreen);
        return IntPtr.Zero;
    }

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (screen.IntersectsWith(new Rect(_settings.Left, _settings.Top, Width, Height)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.Left;
                Top = _settings.Top;
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.Left = bounds.Left;
            _settings.Top = bounds.Top;
            _settings.Width = bounds.Width;
            _settings.Height = bounds.Height;
        }
        _settings.Maximized = _fullscreen ? _maximizedBeforeFullscreen : WindowState == WindowState.Maximized;
        _settings.Save();
    }
}
