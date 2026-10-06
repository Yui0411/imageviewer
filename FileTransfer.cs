using System.IO;

namespace ImageViewer;

internal static class FileTransfer
{
    /// <summary>
    /// Copies or moves files into <paramref name="folder"/> (created if needed). Never overwrites: a name that's
    /// already taken becomes "name (2).ext". Copies keep the file's modified date.
    /// Returns the source files that were transferred, and a message for each one that wasn't.
    /// </summary>
    public static (List<string> Done, List<string> Errors) Run(IEnumerable<string> files, string folder, bool move)
    {
        var done = new List<string>();
        var errors = new List<string>();
        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            try
            {
                if (string.Equals(Path.GetDirectoryName(file), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"{Path.GetFileName(file)} is already in that folder");
                    continue;
                }
                var target = FreeName(folder, Path.GetFileName(file));
                if (move) File.Move(file, target);
                else File.Copy(file, target, overwrite: false);
                done.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return (done, errors);
    }

    private static string FreeName(string folder, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, fileName);
        for (int n = 2; File.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
        return candidate;
    }
}
