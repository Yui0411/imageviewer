using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImageViewer;

/// <summary>
/// Favourites: mark the best shots with F (or the heart), review them with "favourites only" (Shift+F), then copy or
/// move them out. Marks live in the app's own list, so the image files are never modified.
/// </summary>
public partial class MainWindow
{
    private const string HeartOutlineGlyph = "";
    private const string HeartFilledGlyph = "";
    private const string FavouritesSubfolder = "Favourites";
    private static readonly Brush HeartBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x6D)));

    private FavouritesStore _favourites = new();
    private bool _favouritesOnly;
    private bool _transferring;
    private int _folderFavouriteCount;
    private readonly DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private Func<string, bool>? _confirmOverride = null; // set only by tests, to answer confirmation prompts

    private bool IsCurrentFavourite => _favourites.Contains(_nav.Current);

    private void InitFavourites()
    {
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            if (Toolbar.Opacity < 0.5) Fade(FavouriteBadge, false);
        };
    }

    private void ToggleFavourite()
    {
        if (_cropping || _nav.Current is not { } path || _image == null) return;
        _favourites.Toggle(path);
        RefreshFavouriteCount();
        if (_favouritesOnly && _folderFavouriteCount == 0) SetFavouritesOnly(false);
        UpdateFavouriteUi(flash: true);
        UpdateInfo();
        PreloadNeighbors();
    }

    private void ToggleFavouritesOnly() => SetFavouritesOnly(!_favouritesOnly);

    private void SetFavouritesOnly(bool on)
    {
        if (on == _favouritesOnly || _cropping) return;
        if (on && _folderFavouriteCount == 0)
        {
            ShowToast("No favourites in this folder yet — press F to mark one.", warning: false);
            return;
        }
        _favouritesOnly = on;
        _nav.Filter = on ? p => _favourites.Contains(p) : null;
        if (on && !IsCurrentFavourite && _nav.Move(1)) ShowCurrent();
        ShowToast(on ? "Showing favourites only — Shift+F shows all" : "Showing all images", warning: false);
        UpdateInfo();
        PreloadNeighbors();
    }

    /// <summary>Called whenever the current image or its mark changes.</summary>
    private void UpdateFavouriteUi(bool flash)
    {
        bool favourite = IsCurrentFavourite;
        FavouriteButton.Content = favourite ? HeartFilledGlyph : HeartOutlineGlyph;
        if (favourite) FavouriteButton.Foreground = HeartBrush;
        else FavouriteButton.ClearValue(ForegroundProperty); // back to the toolbar style's colour
        FavouriteButton.ToolTip = favourite ? "Remove from favourites (F)" : "Add to favourites (F)";
        if (!favourite)
        {
            _badgeTimer.Stop();
            Fade(FavouriteBadge, false);
        }
        else if (flash || Toolbar.Opacity >= 0.5)
        {
            Fade(FavouriteBadge, true);
            _badgeTimer.Stop();
            _badgeTimer.Start();
        }
    }

    private void RefreshFavouriteCount() =>
        _folderFavouriteCount = _nav.Folder is { } folder ? _favourites.InFolder(folder).Count : 0;

    private bool Confirm(string message) => _confirmOverride?.Invoke(message)
        ?? MessageBox.Show(this, message, "Image Viewer", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>Copies or moves this folder's favourites to a chosen folder, or to a "Favourites" subfolder.</summary>
    private async void TransferFavourites(bool move, bool toSubfolder)
    {
        if (_cropping || _transferring || _nav.Folder is not { } folder) return;
        var files = _favourites.InFolder(folder).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            ShowToast("No favourites in this folder yet — press F to mark one.", warning: false);
            return;
        }
        if (move && _rotating && files.Any(f => SamePath(f, _rotatingPath)))
        {
            ShowToast("Still saving a rotation — try again in a moment.", warning: false);
            return;
        }

        string target;
        if (toSubfolder)
        {
            target = Path.Combine(folder, FavouritesSubfolder);
        }
        else
        {
            var dialog = new OpenFolderDialog
            {
                Title = $"{(move ? "Move" : "Copy")} {Photos(files.Count)} to…",
                InitialDirectory = folder,
            };
            if (dialog.ShowDialog(this) != true) return;
            target = dialog.FolderName;
        }
        if (string.Equals(Path.GetFullPath(target).TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("They're already in that folder — choose a different one.", warning: false);
            return;
        }
        if (move && !Confirm($"Move {Photos(files.Count)} to\n{target}?\n\nThey'll no longer be in this folder."))
            return;

        _transferring = true;
        ShowToast($"{(move ? "Moving" : "Copying")} {Photos(files.Count)}…", warning: false);
        List<string> done, errors;
        try
        {
            (done, errors) = await Task.Run(() => FileTransfer.Run(files, target, move));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _transferring = false;
            ShowToast($"Couldn't {(move ? "move" : "copy")} the favourites: {ex.Message}");
            return;
        }
        _transferring = false;

        if (move)
        {
            _favourites.Remove(done);
            foreach (var file in done) _cache.Remove(file);
            var before = _nav.Current;
            _nav.Rescan(); // stay in this folder
            RefreshFavouriteCount();
            if (_favouritesOnly && _folderFavouriteCount == 0) SetFavouritesOnly(false);
            if (_favouritesOnly && !IsCurrentFavourite) _nav.Move(1);
            if (_nav.Current == null) ShowEmpty();
            else if (!SamePath(before, _nav.Current)) ShowCurrent();
        }
        RefreshFavouriteCount();
        UpdateFavouriteUi(flash: false);
        UpdateInfo();
        PreloadNeighbors();

        var where = toSubfolder ? $"the \"{FavouritesSubfolder}\" subfolder" : target;
        var message = $"{(move ? "Moved" : "Copied")} {Photos(done.Count)} to {where}";
        if (errors.Count > 0)
            message += $". {errors.Count} couldn't be {(move ? "moved" : "copied")} — {errors[0]}";
        ShowToast(message, warning: errors.Count > 0);
    }

    private void ClearFolderFavourites()
    {
        if (_nav.Folder is not { } folder || _folderFavouriteCount == 0) return;
        if (!Confirm($"Clear {_folderFavouriteCount} favourite{(_folderFavouriteCount == 1 ? "" : "s")} in this folder?\n\nThe photos themselves aren't affected."))
            return;
        _favourites.ClearFolder(folder);
        RefreshFavouriteCount();
        if (_favouritesOnly) SetFavouritesOnly(false);
        UpdateFavouriteUi(flash: false);
        UpdateInfo();
        PreloadNeighbors();
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static string Photos(int n) => n == 1 ? "1 photo" : $"{n} photos";

    // ---------------------------------------------------------------- menu & buttons

    private void FavouriteButton_Click(object sender, RoutedEventArgs e) => ToggleFavourite();
    private void FavouriteMenu_Click(object sender, RoutedEventArgs e) => ToggleFavourite();
    private void FavouritesOnlyMenu_Click(object sender, RoutedEventArgs e) => ToggleFavouritesOnly();
    private void CopyFavourites_Click(object sender, RoutedEventArgs e) => TransferFavourites(move: false, toSubfolder: false);
    private void MoveFavourites_Click(object sender, RoutedEventArgs e) => TransferFavourites(move: true, toSubfolder: false);
    private void CopyFavouritesToSubfolder_Click(object sender, RoutedEventArgs e) => TransferFavourites(move: false, toSubfolder: true);
    private void MoveFavouritesToSubfolder_Click(object sender, RoutedEventArgs e) => TransferFavourites(move: true, toSubfolder: true);
    private void ClearFavourites_Click(object sender, RoutedEventArgs e) => ClearFolderFavourites();

    private void FavouritesMenu_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        RefreshFavouriteCount();
        bool any = _folderFavouriteCount > 0 && !_transferring;
        foreach (var item in ((MenuItem)sender).Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "toggle":
                    item.IsChecked = IsCurrentFavourite;
                    item.IsEnabled = _image != null;
                    break;
                case "only":
                    item.IsChecked = _favouritesOnly;
                    item.IsEnabled = _favouritesOnly || _folderFavouriteCount > 0;
                    break;
                default:
                    item.IsEnabled = any;
                    break;
            }
        }
    }
}
