using System.IO;
using System.Runtime.InteropServices;

namespace ImageViewer;

/// <summary>
/// Tracks the images in the current file's folder, ordered the way Explorer sorts names.
/// </summary>
internal sealed class FolderNavigator
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif",
        ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif",
    };

    private List<string> _files = new();

    public int Index { get; private set; } = -1;
    public int Count => _files.Count;
    public string? Current => Index >= 0 ? _files[Index] : null;

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public void Open(string path)
    {
        var full = Path.GetFullPath(path);
        _files = Scan(Path.GetDirectoryName(full)!);
        Index = _files.FindIndex(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
        if (Index < 0)
        {
            // Opened a file that the folder scan skipped (e.g. unknown extension) — still show it.
            _files.Add(full);
            _files.Sort(CompareNames);
            Index = _files.IndexOf(full);
        }
    }

    /// <summary>Path at the given offset from the current image, wrapping around; null if there is no other image.</summary>
    public string? Peek(int offset)
    {
        if (Count <= 1) return null;
        return _files[Wrap(Index + offset)];
    }

    public bool Move(int offset)
    {
        if (Count <= 1) return false;
        Index = Wrap(Index + offset);
        return true;
    }

    public void MoveTo(int index)
    {
        if (Count > 0) Index = Math.Clamp(index, 0, Count - 1);
    }

    /// <summary>Drops the current file (e.g. after it was deleted); the next image becomes current, or the last one if it was at the end.</summary>
    public void RemoveCurrent()
    {
        if (Index < 0) return;
        _files.RemoveAt(Index);
        Index = _files.Count == 0 ? -1 : Math.Min(Index, _files.Count - 1);
    }

    private int Wrap(int i) => ((i % Count) + Count) % Count;

    private static List<string> Scan(string dir)
    {
        var files = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (!IsSupported(f)) continue;
                var attrs = File.GetAttributes(f);
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                files.Add(f);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        files.Sort(CompareNames);
        return files;
    }

    private static int CompareNames(string a, string b) => StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));

    // Same "natural" ordering Explorer uses (img2 before img10).
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
