using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ImageViewer;

/// <summary>
/// Crop mode. The crop box is kept in the displayed image's pixel coordinates; for JPEGs it snaps (after each drag)
/// so the crop can be done losslessly. Apply overwrites the file (undoable with Ctrl+Z until another image is shown);
/// Save copy writes "name (cropped).ext" next to it.
/// </summary>
public partial class MainWindow
{
    private static readonly (string Key, double Ratio)[] CropRatios =
    {
        ("Free", 0), ("Original", 0), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("5:4", 1.25), ("Custom", 0),
    };

    private enum CropDrag { None, Move, Draw, Handle }

    private const double HandleSize = 10;
    private const double HandleHitRadius = 12;
    private static readonly Thickness ToastMargin = new(20, 0, 20, 28);

    private bool _cropping;
    private bool _cropSaving;
    private string? _cropPath;
    private CropInfo? _cropInfo;
    private Rect _crop; // displayed-image pixels
    private CropDrag _cropDrag;
    private int _cropHx, _cropHy;
    private Point _cropDragStart;
    private Rect _cropDragRect;
    private (string Path, byte[] Original)? _undo;

    private int CropImageWidth => _cropInfo?.DisplayWidth ?? 1;
    private int CropImageHeight => _cropInfo?.DisplayHeight ?? 1;

    private void BuildCropBar()
    {
        if (!CropRatios.Any(r => r.Key == _settings.CropRatio)) _settings.CropRatio = "Free";
        foreach (var (key, _) in CropRatios)
        {
            var chip = new RadioButton
            {
                Content = key,
                Tag = key,
                GroupName = "CropRatio",
                Style = (Style)FindResource("ChipRadio"),
                IsChecked = key == _settings.CropRatio,
            };
            chip.Checked += CropRatio_Checked;
            RatioPanel.Children.Add(chip);
        }
        var custom = _settings.CropCustomRatio.Split(':');
        CustomRatioW.Text = custom[0];
        CustomRatioH.Text = custom.Length > 1 ? custom[1] : "1";
        CustomRatioPanel.Visibility = _settings.CropRatio == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        CropGridToggle.IsChecked = _settings.CropGrid;
    }

    // ---------------------------------------------------------------- entering / leaving

    private void ToggleCrop()
    {
        if (_cropping) ExitCrop();
        else EnterCrop();
    }

    private void EnterCrop()
    {
        var path = _nav.Current;
        if (path == null || _image == null) return;
        if (!ImageCropper.CanCrop(path))
        {
            ShowToast($"Cropping {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} files isn't supported.");
            return;
        }
        if (_rotating)
        {
            ShowToast("Still saving the rotation — try again in a moment.", warning: false);
            return;
        }

        CropInfo info;
        try
        {
            info = ImageCropper.GetInfo(path);
        }
        catch (Exception ex) when (ex is EditException or IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            ShowToast(ex is EditException ? ex.Message : $"Can't crop this file: {ex.Message}");
            return;
        }
        if (info.DisplayWidth != _image.Bitmap.PixelWidth || info.DisplayHeight != _image.Bitmap.PixelHeight)
        {
            ShowToast("The file changed on disk — reloaded it. Try cropping again.", warning: false);
            _cache.Remove(path);
            ShowCurrent();
            return;
        }

        _cropping = true;
        _cropPath = path;
        _cropInfo = info;
        _cropDrag = CropDrag.None;
        FitToWindow();
        var center = new Point(info.DisplayWidth / 2.0, info.DisplayHeight / 2.0);
        _crop = CurrentRatio() is { } ratio
            ? CropMath.Largest(info.DisplayWidth, info.DisplayHeight, ratio, center)
            : new Rect(0, 0, info.DisplayWidth, info.DisplayHeight);
        SnapCrop();

        CropLayer.Visibility = Visibility.Visible;
        CropBar.Visibility = Visibility.Visible;
        PrevButton.Visibility = NextButton.Visibility = Visibility.Collapsed;
        Toast.Margin = new Thickness(20, 0, 20, 96);
        UpdateCropVisuals();
    }

    /// <summary>The bar can wrap onto two rows in a narrow window; keep the image clear of it.</summary>
    private void CropBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_cropping) return;
        if (_fitMode) FitToWindow();
        else
        {
            ClampOffset();
            ApplyTransform();
        }
    }

    private void ExitCrop()
    {
        if (!_cropping) return;
        _cropping = false;
        _cropDrag = CropDrag.None;
        _cropInfo = null;
        _cropPath = null;
        Viewport.ReleaseMouseCapture();
        CropLayer.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        PrevButton.Visibility = NextButton.Visibility = Visibility.Visible;
        Toast.Margin = ToastMargin;
        FitToWindow(); // back to the full window, and restores the normal cursor
    }

    // ---------------------------------------------------------------- ratio, grid, orientation

    /// <summary>Width ÷ height of the locked ratio, or null when free.</summary>
    private double? CurrentRatio()
    {
        if (_cropInfo is not { } info) return null;
        double? ratio = _settings.CropRatio switch
        {
            "Free" => null,
            "Original" => (double)info.DisplayWidth / info.DisplayHeight,
            "Custom" => ParseCustomRatio(),
            var key => CropRatios.First(r => r.Key == key).Ratio,
        };
        return ratio is { } r && _settings.CropPortrait ? 1 / r : ratio;
    }

    private double? ParseCustomRatio() =>
        int.TryParse(CustomRatioW.Text, out int w) && int.TryParse(CustomRatioH.Text, out int h) && w > 0 && h > 0
            ? (double)w / h
            : null;

    /// <summary>Re-fits the box to the locked ratio, as large as possible around its current center.</summary>
    private void RefitCrop()
    {
        if (!_cropping) return;
        if (CurrentRatio() is { } ratio)
        {
            var center = new Point(_crop.X + _crop.Width / 2, _crop.Y + _crop.Height / 2);
            _crop = CropMath.Largest(CropImageWidth, CropImageHeight, ratio, center);
        }
        SnapCrop();
        UpdateCropVisuals();
    }

    private void CropRatio_Checked(object sender, RoutedEventArgs e)
    {
        foreach (var chip in RatioPanel.Children.OfType<RadioButton>())
            if (chip != sender) chip.IsChecked = false; // don't rely on GroupName alone
        _settings.CropRatio = (string)((FrameworkElement)sender).Tag;
        CustomRatioPanel.Visibility = _settings.CropRatio == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        RefitCrop();
    }

    private void CustomRatio_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CustomRatioW == null || CustomRatioH == null || ParseCustomRatio() == null) return;
        _settings.CropCustomRatio = $"{CustomRatioW.Text}:{CustomRatioH.Text}";
        if (_settings.CropRatio == "Custom") RefitCrop();
    }

    private void SwapCropOrientation()
    {
        _settings.CropPortrait = !_settings.CropPortrait;
        if (!_cropping) return;
        if (CurrentRatio() == null)
        {
            // Free: turn the box itself, around its center, shrinking it if it no longer fits.
            double w = Math.Min(_crop.Height, CropImageWidth), h = Math.Min(_crop.Width, CropImageHeight);
            var center = new Point(_crop.X + _crop.Width / 2, _crop.Y + _crop.Height / 2);
            _crop = CropMath.Move(new Rect(center.X - w / 2, center.Y - h / 2, w, h), new Vector(), CropImageWidth, CropImageHeight);
        }
        RefitCrop();
    }

    private void CropSwap_Click(object sender, RoutedEventArgs e) => SwapCropOrientation();

    private void CropGridToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.CropGrid = CropGridToggle.IsChecked == true;
        UpdateCropVisuals();
    }

    /// <summary>Rounds the box to whole pixels and, for JPEGs, lines it up with the block grid (keeping the locked ratio).</summary>
    private void SnapCrop()
    {
        if (_cropInfo is not { } info) return;
        var snapped = info.Snap(CropMath.ToPixels(_crop, info.DisplayWidth, info.DisplayHeight), CurrentRatio());
        _crop = new Rect(snapped.X, snapped.Y, snapped.Width, snapped.Height);
    }

    // ---------------------------------------------------------------- drawing

    private Point ImageToView(Point p)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(_offset.X + p.X / dpi.DpiScaleX * _scale, _offset.Y + p.Y / dpi.DpiScaleY * _scale);
    }

    private Point ViewToImage(Point v)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point((v.X - _offset.X) / _scale * dpi.DpiScaleX, (v.Y - _offset.Y) / _scale * dpi.DpiScaleY);
    }

    private Rect CropViewRect => new(ImageToView(_crop.TopLeft), ImageToView(_crop.BottomRight));

    private static IEnumerable<(int hx, int hy, Point at)> Handles(Rect r)
    {
        double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        yield return (-1, -1, r.TopLeft);
        yield return (0, -1, new Point(cx, r.Top));
        yield return (1, -1, r.TopRight);
        yield return (1, 0, new Point(r.Right, cy));
        yield return (1, 1, r.BottomRight);
        yield return (0, 1, new Point(cx, r.Bottom));
        yield return (-1, 1, r.BottomLeft);
        yield return (-1, 0, new Point(r.Left, cy));
    }

    private void UpdateCropVisuals()
    {
        if (!_cropping) return;
        var r = CropViewRect;

        CropShade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(new Rect(0, 0, Viewport.ActualWidth, Viewport.ActualHeight)), new RectangleGeometry(r) },
        };
        Canvas.SetLeft(CropBorder, r.X);
        Canvas.SetTop(CropBorder, r.Y);
        CropBorder.Width = r.Width;
        CropBorder.Height = r.Height;

        var grid = new GeometryGroup();
        if (_settings.CropGrid)
        {
            for (int i = 1; i <= 2; i++)
            {
                grid.Children.Add(new LineGeometry(new Point(r.X + r.Width * i / 3, r.Top), new Point(r.X + r.Width * i / 3, r.Bottom)));
                grid.Children.Add(new LineGeometry(new Point(r.Left, r.Y + r.Height * i / 3), new Point(r.Right, r.Y + r.Height * i / 3)));
            }
        }
        CropGridLines.Data = grid;

        var handles = new GeometryGroup();
        foreach (var (_, _, at) in Handles(r))
            handles.Children.Add(new RectangleGeometry(new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize)));
        CropHandles.Data = handles;

        CropSizeText.Text = $"{(int)Math.Round(_crop.Width)} × {(int)Math.Round(_crop.Height)}";
    }

    // ---------------------------------------------------------------- mouse

    private (int hx, int hy)? HitHandle(Point v)
    {
        foreach (var (hx, hy, at) in Handles(CropViewRect))
            if (Math.Abs(v.X - at.X) <= HandleHitRadius && Math.Abs(v.Y - at.Y) <= HandleHitRadius)
                return (hx, hy);
        return null;
    }

    private Point ClampToImage(Point p) => new(Math.Clamp(p.X, 0, CropImageWidth), Math.Clamp(p.Y, 0, CropImageHeight));

    private void CropMouseDown(MouseButtonEventArgs e)
    {
        if (_cropSaving) return;
        var v = e.GetPosition(Viewport);
        _cropDragRect = _crop;
        _cropDragStart = ViewToImage(v);
        if (HitHandle(v) is { } handle)
        {
            _cropDrag = CropDrag.Handle;
            (_cropHx, _cropHy) = handle;
        }
        else if (CropViewRect.Contains(v))
        {
            _cropDrag = CropDrag.Move;
        }
        else
        {
            _cropDrag = CropDrag.Draw; // drag outside the box to draw a new one
            _cropDragStart = ClampToImage(_cropDragStart);
        }
        Viewport.CaptureMouse();
    }

    private void CropMouseMove(MouseEventArgs e)
    {
        var v = e.GetPosition(Viewport);
        if (_cropDrag == CropDrag.None)
        {
            Viewport.Cursor = CropCursor(v);
            return;
        }
        var p = ViewToImage(v);
        double w = CropImageWidth, h = CropImageHeight;
        var ratio = CurrentRatio();
        _crop = _cropDrag switch
        {
            CropDrag.Move => CropMath.Move(_cropDragRect, p - _cropDragStart, w, h),
            CropDrag.Draw => CropMath.Draw(_cropDragStart, ClampToImage(p), w, h, ratio),
            _ => CropMath.Resize(_cropDragRect, _cropHx, _cropHy, ClampToImage(p), w, h, ratio),
        };
        UpdateCropVisuals();
    }

    private void CropMouseUp()
    {
        if (_cropDrag == CropDrag.Draw && (_crop.Width < CropMath.MinSize || _crop.Height < CropMath.MinSize))
            _crop = _cropDragRect; // a click outside the box, not a drag: keep the old box
        _cropDrag = CropDrag.None;
        Viewport.ReleaseMouseCapture();
        SnapCrop();
        UpdateCropVisuals();
    }

    private Cursor CropCursor(Point v)
    {
        if (HitHandle(v) is { } h)
        {
            return h switch
            {
                (-1, -1) or (1, 1) => Cursors.SizeNWSE,
                (1, -1) or (-1, 1) => Cursors.SizeNESW,
                (_, 0) => Cursors.SizeWE,
                _ => Cursors.SizeNS,
            };
        }
        return CropViewRect.Contains(v) ? Cursors.SizeAll : Cursors.Cross;
    }

    // ---------------------------------------------------------------- keyboard

    private void HandleCropKey(KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape: ExitCrop(); break;
            case Key.Enter: ApplyCrop(saveCopy: false); break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.None: ExitCrop(); break;
            case Key.Left: NudgeCrop(-1, 0, shift); break;
            case Key.Right: NudgeCrop(1, 0, shift); break;
            case Key.Up: NudgeCrop(0, -1, shift); break;
            case Key.Down: NudgeCrop(0, 1, shift); break;
            case Key.G:
                CropGridToggle.IsChecked = !(CropGridToggle.IsChecked == true);
                CropGridToggle_Click(CropGridToggle, e);
                break;
            case Key.X: SwapCropOrientation(); break;
            case Key.F11: SetFullscreen(!_fullscreen); break;
            case Key.D0 or Key.NumPad0: ToggleFitActual(ViewportCenter); break;
            case Key.OemPlus or Key.Add: ZoomTo(_scale * ZoomStep, ViewportCenter); break;
            case Key.OemMinus or Key.Subtract: ZoomTo(_scale / ZoomStep, ViewportCenter); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Moves the box by 1 px (10 with Shift); for JPEGs by whole blocks so it stays lossless.</summary>
    private void NudgeCrop(int dx, int dy, bool large)
    {
        if (_cropInfo is not { } info || _cropSaving) return;
        bool sideways = info.Turns % 2 == 1;
        int blockX = sideways ? info.BlockHeight : info.BlockWidth, blockY = sideways ? info.BlockWidth : info.BlockHeight;
        int StepFor(int block) => block > 1 ? block * (large ? 4 : 1) : large ? 10 : 1;
        _crop = CropMath.Move(_crop, new Vector(dx * StepFor(blockX), dy * StepFor(blockY)), info.DisplayWidth, info.DisplayHeight);
        SnapCrop();
        UpdateCropVisuals();
    }

    // ---------------------------------------------------------------- saving

    private void CropButton_Click(object sender, RoutedEventArgs e) => ToggleCrop();
    private void CropCancel_Click(object sender, RoutedEventArgs e) => ExitCrop();
    private void CropApply_Click(object sender, RoutedEventArgs e) => ApplyCrop(saveCopy: false);
    private void CropSaveCopy_Click(object sender, RoutedEventArgs e) => ApplyCrop(saveCopy: true);
    private void UndoCrop_Click(object sender, RoutedEventArgs e) => UndoCrop();

    private async void ApplyCrop(bool saveCopy)
    {
        if (!_cropping || _cropSaving || _cropPath is not { } path || _cropInfo is not { } info) return;
        SnapCrop();
        var rect = CropMath.ToPixels(_crop, info.DisplayWidth, info.DisplayHeight);
        if (rect.Width == info.DisplayWidth && rect.Height == info.DisplayHeight)
        {
            ShowToast("Drag the edges of the crop box to choose what to keep.", warning: false);
            return;
        }
        if (!saveCopy && !Confirm(
                $"Crop {Path.GetFileName(path)} to {rect.Width} × {rect.Height} and overwrite it?\n\n" +
                "Everything outside the box will be removed from the file. You can undo with Ctrl+Z " +
                "until you move to another image."))
            return; // stay in crop mode so the box can be adjusted

        SetCropSaving(true);
        try
        {
            if (saveCopy)
            {
                var copy = await Task.Run(() =>
                {
                    var bytes = ImageCropper.Crop(path, rect);
                    return WriteNewFile(path, bytes);
                });
                ExitCrop();
                _nav.Open(path); // rescan so the copy is part of the folder, staying on the original
                UpdateInfo();
                PreloadNeighbors();
                ShowToast($"Saved {Path.GetFileName(copy)}", warning: false);
            }
            else
            {
                var original = await Task.Run(() =>
                {
                    var before = File.ReadAllBytes(path);
                    var bytes = ImageCropper.Crop(path, rect);
                    ImageRotator.Replace(path, bytes);
                    ShellActions.NotifyFileChanged(path);
                    return before;
                });
                _undo = (path, original);
                ExitCrop();
                _cache.Remove(path);
                ShowCurrent();
                ShowToast("Cropped. Press Ctrl+Z to undo.", warning: false);
            }
        }
        catch (EditException ex)
        {
            ShowToast(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            ShowToast($"Couldn't save the crop: {ex.Message}");
        }
        finally
        {
            SetCropSaving(false);
        }
    }

    private void SetCropSaving(bool saving)
    {
        _cropSaving = saving;
        CropApplyButton.IsEnabled = CropSaveCopyButton.IsEnabled = !saving;
        CropApplyButton.Content = saving ? "Saving…" : "Apply";
    }

    /// <summary>Writes "name (cropped).ext", or "name (cropped 2).ext" and so on — never over an existing file.</summary>
    private static string WriteNewFile(string original, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(original)!;
        var name = Path.GetFileNameWithoutExtension(original);
        var ext = Path.GetExtension(original);
        for (int n = 1; ; n++)
        {
            var target = Path.Combine(dir, n == 1 ? $"{name} (cropped){ext}" : $"{name} (cropped {n}){ext}");
            if (File.Exists(target)) continue;
            try
            {
                using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                stream.Write(bytes);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // created by someone else in the meantime; try the next name
            }
        }
    }

    private async void UndoCrop()
    {
        if (_cropping) return;
        if (_undo is not { } undo || !SamePath(undo.Path, _nav.Current))
        {
            ShowToast("Nothing to undo.", warning: false);
            return;
        }
        _undo = null;
        try
        {
            await Task.Run(() =>
            {
                ImageRotator.Replace(undo.Path, undo.Original);
                ShellActions.NotifyFileChanged(undo.Path);
            });
            _cache.Remove(undo.Path);
            if (SamePath(_nav.Current, undo.Path)) ShowCurrent();
            ShowToast("Crop undone", warning: false);
        }
        catch (EditException ex)
        {
            _undo = undo;
            ShowToast(ex.Message);
        }
    }
}
