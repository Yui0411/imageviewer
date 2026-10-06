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
    private Rect _boundsBeforeFullscreen = Rect.Empty;
    private Point? _toolbarPress;

    // Rotation: presses made while a save is running are added up and saved together.
    private bool _rotating;
    private string? _rotatingPath;
    private int _pendingTurns;

    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _animationTimer = new();
    private int _animationFrame;

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        BuildHelp();
        BuildCropBar();
        _animationTimer.Tick += AnimationTimer_Tick;
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
        ExitCrop();
        _nav.Open(path);
        ShowCurrent();
    }

    // ---------------------------------------------------------------- loading

    private async void ShowCurrent()
    {
        var path = _nav.Current;
        if (path == null) return;

        int version = ++_loadVersion;
        if (_undo is { } undo && !SamePath(undo.Path, path)) _undo = null; // undo only lasts while on the cropped image
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
        _animationTimer.Stop();
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
            if (image.Frames is { Count: > 1 })
            {
                _animationFrame = 0;
                _animationTimer.Interval = image.Delays![0];
                _animationTimer.Start();
            }
        }
        UpdateInfo();
        PreloadNeighbors();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_image?.Frames is not { Count: > 1 } frames)
        {
            _animationTimer.Stop();
            return;
        }
        _animationFrame = (_animationFrame + 1) % frames.Count;
        ImageView.Source = frames[_animationFrame];
        _animationTimer.Interval = _image.Delays![_animationFrame];
    }

    /// <summary>Clears the view after the last image in the folder was deleted.</summary>
    private void ShowEmpty()
    {
        _loadVersion++;
        _image = null;
        _undo = null;
        _animationTimer.Stop();
        ImageView.Source = null;
        FileNameText.Text = "";
        Title = "Image Viewer";
        ShowMessage("Drop an image here, or press Ctrl+O");
        UpdateInfo();
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
        if (path == null || _image == null || _cropping) return;
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
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ShowToast(ex is EditException ? ex.Message : $"Couldn't rotate: {ex.Message}");
            return;
        }

        _undo = null; // Ctrl+Z restores the pre-crop file, which would silently drop this rotation
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
                await Task.Run(() =>
                {
                    ImageRotator.Rotate(path, turns);
                    ShellActions.NotifyFileChanged(path); // refresh Explorer's thumbnail right away
                });
            }
        }
        catch (EditException ex)
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
        _animationTimer.Stop();
        var preview = new TransformedBitmap(_image.Bitmap, new RotateTransform(90 * clockwiseTurns));
        preview.Freeze();
        _image = _image with { Bitmap = preview };
        ImageView.Source = preview;
        UpdateImageSize();
        FitToWindow();
    }

    private static bool SamePath(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- file actions

    private void DeleteCurrent()
    {
        var path = _nav.Current;
        if (path == null) return;
        if (_rotating && SamePath(path, _rotatingPath))
        {
            ShowToast("Still saving the rotation — try again in a moment.", warning: false);
            return;
        }
        if (!ShellActions.MoveToRecycleBin(path, new WindowInteropHelper(this).Handle))
        {
            if (File.Exists(path)) ShowToast($"Couldn't move {Path.GetFileName(path)} to the Recycle Bin.");
            return;
        }

        _cache.Remove(path);
        _nav.RemoveCurrent();
        if (_nav.Current == null) ShowEmpty();
        else ShowCurrent();
        ShowToast($"Moved {Path.GetFileName(path)} to the Recycle Bin", warning: false);
    }

    /// <summary>Copies both the picture (for pasting into apps) and the file (for pasting in Explorer).</summary>
    private void CopyCurrent()
    {
        if (_nav.Current is not { } path || _image == null) return;
        try
        {
            var data = new DataObject();
            data.SetImage(_image.Bitmap);
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
            Clipboard.SetDataObject(data, copy: true);
            ShowToast("Copied", warning: false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ShowToast("Couldn't copy — the clipboard is busy. Try again.");
        }
    }

    private void ShowInExplorer()
    {
        if (_nav.Current is { } path) ShellActions.ShowInExplorer(path);
    }

    private void RegisterAsDefault()
    {
        var exe = Environment.ProcessPath ?? "";
        var answer = MessageBox.Show(this,
            "This adds Image Viewer to Windows' list of image apps, for your user account only " +
            "(no admin rights needed, and it can be removed again from this menu).\n\n" +
            $"Files will open with:\n{exe}\n\n" +
            "If you move this program later, run this again from its new location.\n\n" +
            "Windows will then open Settings → Default apps. Under \"Photo viewer\", choose Image Viewer.",
            "Set as default image viewer", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            DefaultAppRegistration.Register();
            DefaultAppRegistration.OpenDefaultAppsSettings();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ShowToast($"Couldn't register: {ex.Message}");
        }
    }

    private void UnregisterAsDefault()
    {
        var answer = MessageBox.Show(this,
            "Remove Image Viewer from Windows' list of image apps? If it's currently your default, Windows will " +
            "switch back to its own photo app.",
            "Remove registration", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            DefaultAppRegistration.Unregister();
            ShowToast("Removed from Windows image apps", warning: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ShowToast($"Couldn't remove registration: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- help

    private static readonly (string Keys, string Action)[] Shortcuts =
    {
        ("← / →", "Previous / next image"),
        ("Home / End", "First / last image"),
        ("R", "Rotate clockwise (saves the file)"),
        ("Shift+R", "Rotate counter-clockwise"),
        ("C", "Crop (Enter applies, Esc cancels)"),
        ("Ctrl+Z", "Undo the last crop"),
        ("Scroll, + / −", "Zoom"),
        ("Drag", "Pan when zoomed in, otherwise move the window"),
        ("Double-click, 0", "Fit to window / actual size"),
        ("F11", "Fullscreen"),
        ("Esc", "Leave fullscreen, or close"),
        ("Ctrl+C", "Copy image"),
        ("Ctrl+E", "Show in Explorer"),
        ("Del", "Move to Recycle Bin"),
        ("Ctrl+O", "Open a file"),
        ("Right-click", "Menu"),
        ("", ""),
        ("While cropping", ""),
        ("Drag", "Move the box, its edges, or draw a new one"),
        ("Arrow keys", "Nudge the box (Shift: further)"),
        ("G / X", "Grid on/off / swap landscape-portrait"),
        ("Middle-drag", "Pan when zoomed in"),
    };

    private void BuildHelp()
    {
        HelpGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        HelpGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < Shortcuts.Length; i++)
        {
            HelpGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            AddHelpCell(Shortcuts[i].Keys, i, 0, "#FFFFFF");
            AddHelpCell(Shortcuts[i].Action, i, 1, "#BBBBBB");
        }
    }

    private void AddHelpCell(string text, int row, int column, string color)
    {
        var cell = new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            Margin = new Thickness(0, 3, column == 0 ? 28 : 0, 3),
        };
        if (column == 0 && Shortcuts[row].Action.Length == 0) cell.FontWeight = FontWeights.SemiBold; // section heading
        System.Windows.Controls.Grid.SetRow(cell, row);
        System.Windows.Controls.Grid.SetColumn(cell, column);
        HelpGrid.Children.Add(cell);
    }

    private void ToggleHelp(bool show) => HelpPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private void HelpPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ToggleHelp(false);
        e.Handled = true;
    }

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
        var area = ViewArea;
        if (_image == null || area.Width <= 0 || area.Height <= 0) return 1;
        return Math.Min(1, Math.Min(area.Width / ImageView.Width, area.Height / ImageView.Height));
    }

    /// <summary>
    /// The part of the viewport the image is fitted into. In crop mode it leaves a margin (so the crop handles at the
    /// image's edges can be grabbed) and keeps clear of the crop bar.
    /// </summary>
    private Rect ViewArea
    {
        get
        {
            double w = Viewport.ActualWidth, h = Viewport.ActualHeight;
            if (!_cropping) return new Rect(0, 0, w, h);
            const double margin = 24;
            double bottom = Math.Max(CropBar.ActualHeight, 40) + CropBar.Margin.Bottom + 16;
            return new Rect(margin, margin, Math.Max(1, w - 2 * margin), Math.Max(1, h - margin - bottom));
        }
    }

    private bool CanPan()
    {
        var area = ViewArea;
        return _image != null && (ImageView.Width * _scale > area.Width + 0.5 || ImageView.Height * _scale > area.Height + 0.5);
    }

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
        var area = ViewArea;
        _offset.X = w <= area.Width ? area.X + (area.Width - w) / 2 : Math.Clamp(_offset.X, area.Right - w, area.X);
        _offset.Y = h <= area.Height ? area.Y + (area.Height - h) / 2 : Math.Clamp(_offset.Y, area.Bottom - h, area.Y);
    }

    private void ApplyTransform()
    {
        ImageTransform.Matrix = new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y);
        if (!_cropping || _panning) Viewport.Cursor = _panning ? Cursors.SizeAll : CanPan() ? Cursors.Hand : null;
        UpdateInfo();
        UpdateCropVisuals();
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_cropping)
        {
            CropMouseDown(e);
            e.Handled = true;
            return;
        }
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
        else if (WindowState == WindowState.Normal && !_fullscreen)
        {
            DragMove();
        }
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            _offset = _panOrigin + (e.GetPosition(Viewport) - _panStart);
            ClampOffset();
            ApplyTransform();
        }
        else if (_cropping)
        {
            CropMouseMove(e);
        }
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropping && !_panning)
        {
            CropMouseUp();
            return;
        }
        EndPan();
    }

    /// <summary>Middle-button drag pans (also in crop mode, where the left button edits the crop box).</summary>
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !CanPan()) return;
        _panning = true;
        _panStart = e.GetPosition(Viewport);
        _panOrigin = _offset;
        Viewport.CaptureMouse();
        ApplyTransform();
        e.Handled = true;
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) EndPan();
    }

    private void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Viewport.ReleaseMouseCapture();
        ApplyTransform();
    }

    private void Viewport_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        if (_cropping) e.Handled = true; // no menu (rotate, delete, ...) in the middle of a crop
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
        bool canNavigate = _nav.Count > 1 && !_cropping;
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
        if (HelpPanel.Visibility == Visibility.Visible)
        {
            ToggleHelp(false);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            // Typing a custom ratio: let keys reach the box; Enter or Esc just leaves it.
            if (e.Key is Key.Enter or Key.Escape)
            {
                Focus();
                e.Handled = true;
            }
            return;
        }
        if (_cropping)
        {
            HandleCropKey(e);
            return;
        }

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
            case Key.C when ctrl: CopyCurrent(); break;
            case Key.E when ctrl: ShowInExplorer(); break;
            case Key.Delete: DeleteCurrent(); break;
            case Key.F1: ToggleHelp(true); break;
            case Key.C when !ctrl: EnterCrop(); break;
            case Key.Z when ctrl: UndoCrop(); break;
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
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
        }
        else if (WindowState == WindowState.Normal && !_fullscreen)
        {
            DragMove();
        }
        else
        {
            // Maximized or fullscreen: wait to see if this becomes a drag, which restores the window (like a title bar).
            _toolbarPress = e.GetPosition(this);
            Toolbar.CaptureMouse();
        }
    }

    private void Toolbar_MouseMove(object sender, MouseEventArgs e)
    {
        if (_toolbarPress is not { } press || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - press.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        EndToolbarPress();
        double relativeX = press.X / ActualWidth;
        var cursor = PointToScreen(current);
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
            cursor = target.TransformFromDevice.Transform(cursor);

        LeaveFullscreen();
        // Keep the cursor over the same relative spot of the toolbar in the restored window.
        Left = cursor.X - relativeX * Width;
        Top = cursor.Y - press.Y;
        DragMove();
    }

    private void Toolbar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndToolbarPress();

    private void EndToolbarPress()
    {
        _toolbarPress = null;
        Toolbar.ReleaseMouseCapture();
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => Navigate(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => Navigate(1);
    private void RotateButton_Click(object sender, RoutedEventArgs e) => Rotate(1);
    private void RotateCcw_Click(object sender, RoutedEventArgs e) => Rotate(3);
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrent();
    private void ShowInExplorer_Click(object sender, RoutedEventArgs e) => ShowInExplorer();
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteCurrent();
    private void RegisterDefault_Click(object sender, RoutedEventArgs e) => RegisterAsDefault();
    private void UnregisterDefault_Click(object sender, RoutedEventArgs e) => UnregisterAsDefault();
    private void Help_Click(object sender, RoutedEventArgs e) => ToggleHelp(true);
    private void OpenButton_Click(object sender, RoutedEventArgs e) => ShowOpenDialog();

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cropping) return;
        var menu = (System.Windows.Controls.ContextMenu)Resources["MainMenu"];
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Greys out actions that need an image, and shows only the relevant registration item.</summary>
    private void MainMenu_Opened(object sender, RoutedEventArgs e)
    {
        bool registered = DefaultAppRegistration.IsRegistered;
        foreach (var item in ((System.Windows.Controls.ContextMenu)sender).Items.OfType<System.Windows.Controls.MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "register": item.Visibility = registered ? Visibility.Collapsed : Visibility.Visible; break;
                case "unregister": item.Visibility = registered ? Visibility.Visible : Visibility.Collapsed; break;
                case "undo": item.IsEnabled = _undo is { } undo && SamePath(undo.Path, _nav.Current); break;
                default:
                    if (!Equals(item.InputGestureText, "Ctrl+O") && !Equals(item.InputGestureText, "F1"))
                        item.IsEnabled = _image != null;
                    break;
            }
        }
    }
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

    /// <summary>
    /// Fullscreen sizes the (normal-state) window to exactly cover its monitor and keeps it above the taskbar while it
    /// is the active window. Relying on Windows to detect a maximized window as fullscreen didn't hide the taskbar.
    /// </summary>
    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        if (on)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (NativeMethods.GetMonitorBounds(hwnd) is not { } monitor) return;

            _maximizedBeforeFullscreen = WindowState == WindowState.Maximized;
            _boundsBeforeFullscreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _fullscreen = true;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;

            var bounds = new Rect(monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top);
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
                bounds.Transform(target.TransformFromDevice); // device pixels → WPF units
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            Topmost = IsActive;
            Window_StateChanged(this, EventArgs.Empty);
        }
        else
        {
            LeaveFullscreen();
            if (_maximizedBeforeFullscreen) WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Back to a normal, resizable window — at its pre-fullscreen size and position if it was fullscreen.</summary>
    private void LeaveFullscreen()
    {
        bool wasFullscreen = _fullscreen;
        _fullscreen = false;
        Topmost = false;
        ResizeMode = ResizeMode.CanResize;
        WindowState = WindowState.Normal;
        if (wasFullscreen && !_boundsBeforeFullscreen.IsEmpty)
        {
            Left = _boundsBeforeFullscreen.Left;
            Top = _boundsBeforeFullscreen.Top;
            Width = _boundsBeforeFullscreen.Width;
            Height = _boundsBeforeFullscreen.Height;
        }
        Window_StateChanged(this, EventArgs.Empty);
    }

    // Only stay above other windows (and the taskbar) while fullscreen and in use, so Alt+Tab still works.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_fullscreen) Topmost = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_fullscreen) Topmost = false;
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
            NativeMethods.ApplyMaximizedBounds(hwnd, lParam);
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
        var bounds = _fullscreen ? _boundsBeforeFullscreen
            : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
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
