using System.IO;
using System.Text.Json;

namespace ImageViewer;

/// <summary>
/// Favourite images, kept as full paths in %AppData%\ImageViewer\favourites.json and saved on every change.
/// The image files themselves are never touched.
/// </summary>
internal sealed class FavouritesStore
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _file;

    public FavouritesStore(string? file = null)
    {
        _file = file ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageViewer", "favourites.json");
        try
        {
            if (File.Exists(_file))
                foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_file)) ?? Array.Empty<string>())
                    _paths.Add(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public bool Contains(string? path) => path != null && _paths.Contains(path);

    /// <summary>Marks or unmarks a file; returns whether it is now a favourite.</summary>
    public bool Toggle(string path)
    {
        bool added = _paths.Add(path);
        if (!added) _paths.Remove(path);
        Save();
        return added;
    }

    public void Remove(IEnumerable<string> paths)
    {
        bool changed = false;
        foreach (var path in paths.ToList()) changed |= _paths.Remove(path);
        if (changed) Save();
    }

    /// <summary>Favourites in <paramref name="folder"/> (not its subfolders) that still exist; forgets ones that don't.</summary>
    public List<string> InFolder(string folder)
    {
        var inFolder = _paths.Where(p => SameFolder(p, folder)).ToList();
        var missing = inFolder.Where(p => !File.Exists(p)).ToList();
        Remove(missing);
        return inFolder.Except(missing, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void ClearFolder(string folder) => Remove(_paths.Where(p => SameFolder(p, folder)));

    private static bool SameFolder(string path, string folder) =>
        string.Equals(Path.GetDirectoryName(path), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_paths.Order(StringComparer.OrdinalIgnoreCase)));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
