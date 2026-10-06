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
    private string? _folder;

    public int Index { get; private set; } = -1;
    public int Count => _files.Count;
    public string? Current => Index >= 0 ? _files[Index] : null;
    public string? Folder => _folder;

    /// <summary>When set, navigation (Move, Peek, MoveToEdge) only stops at files it accepts, e.g. favourites.</summary>
    public Func<string, bool>? Filter { get; set; }

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public void Open(string path)
    {
        var full = Path.GetFullPath(path);
        _folder = Path.GetDirectoryName(full)!;
        _files = Scan(_folder);
        Index = _files.FindIndex(f => string.Equals(f, full, StringComparison.OrdinalIgnoreCase));
        if (Index < 0)
        {
            // Opened a file that the folder scan skipped (e.g. unknown extension) — still show it.
            _files.Add(full);
            _files.Sort(CompareNames);
            Index = _files.IndexOf(full);
        }
    }

    /// <summary>
    /// Path <paramref name="offset"/> images away (skipping any the filter rejects), wrapping around;
    /// null if there is no other such image.
    /// </summary>
    public string? Peek(int offset) => Target(offset) is { } i ? _files[i] : null;

    public bool Move(int offset)
    {
        if (Target(offset) is not { } i) return false;
        Index = i;
        return true;
    }

    /// <summary>Goes to the first or last image the filter accepts.</summary>
    public bool MoveToEdge(bool last)
    {
        for (int k = 0; k < Count; k++)
        {
            int i = last ? Count - 1 - k : k;
            if (!Accepts(i)) continue;
            Index = i;
            return true;
        }
        return false;
    }

    /// <summary>1-based position of the current image among those the filter accepts, and how many it accepts.</summary>
    public (int Position, int Total) Position()
    {
        if (Filter == null) return (Index + 1, Count);
        int total = 0, position = 0;
        for (int i = 0; i < Count; i++)
        {
            if (!Accepts(i)) continue;
            total++;
            if (i <= Index) position = total;
        }
        return (position, total);
    }

    /// <summary>Re-reads the folder (e.g. after files were moved out), staying on the current image if it's still there.</summary>
    public void Rescan()
    {
        if (_folder == null) return;
        var current = Current;
        int previous = Index;
        _files = Scan(_folder);
        Index = current == null ? -1 : _files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
        if (Index < 0) Index = _files.Count == 0 ? -1 : Math.Clamp(previous, 0, _files.Count - 1);
    }

    private int? Target(int offset)
    {
        if (Index < 0 || offset == 0) return null;
        int at = Index, direction = Math.Sign(offset);
        for (int step = 0; step < Math.Abs(offset); step++)
        {
            int? next = null;
            for (int k = 1; k < Count; k++)
            {
                int i = Wrap(at + direction * k);
                if (Accepts(i)) { next = i; break; }
            }
            if (next == null) return null;
            at = next.Value;
        }
        return at == Index ? null : at;
    }

    private bool Accepts(int i) => Filter == null || Filter(_files[i]);

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
