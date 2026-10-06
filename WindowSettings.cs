using System.IO;
using System.Text.Json;

namespace ImageViewer;

/// <summary>Window placement and crop preferences persisted to %AppData%\ImageViewer\settings.json.</summary>
internal sealed class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 700;
    public bool Maximized { get; set; }

    public string CropRatio { get; set; } = "Free";
    public bool CropPortrait { get; set; }
    public bool CropGrid { get; set; } = true;
    public string CropCustomRatio { get; set; } = "21:9";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageViewer", "settings.json");

    public static WindowSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (IOException) { }
        catch (JsonException) { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
