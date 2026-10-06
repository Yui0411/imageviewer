using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

internal sealed record LoadedImage(string Path, BitmapSource Bitmap, DateTime LastWriteUtc);

internal static class ImageLoader
{
    /// <summary>
    /// Decodes an image fully into memory (so the file is never left locked) and applies its EXIF orientation.
    /// Safe to call from a background thread; the returned bitmap is frozen.
    /// </summary>
    public static LoadedImage Load(string path)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        BitmapSource bitmap = ApplyOrientation(frame, ReadOrientation(frame));
        if (bitmap != frame)
            bitmap = new CachedBitmap(bitmap, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        return new LoadedImage(path, bitmap, lastWrite);
    }

    /// <summary>EXIF orientation (1–8); 1 (upright) when absent or unreadable.</summary>
    public static int ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md
                && md.ContainsQuery("System.Photo.Orientation")
                && md.GetQuery("System.Photo.Orientation") is ushort o)
                return o;
        }
        catch (NotSupportedException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        return 1;
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int orientation)
    {
        // EXIF orientation values: 2/4/5/7 involve a mirror, 3/6/8 are pure rotations.
        (double angle, bool flipX, bool flipY) = orientation switch
        {
            2 => (0d, true, false),
            3 => (180d, false, false),
            4 => (0d, false, true),
            5 => (90d, true, false),
            6 => (90d, false, false),
            7 => (90d, false, true),
            8 => (270d, false, false),
            _ => (0d, false, false),
        };
        if (angle == 0 && !flipX && !flipY) return source;

        var transform = new TransformGroup();
        if (angle != 0) transform.Children.Add(new RotateTransform(angle));
        if (flipX || flipY) transform.Children.Add(new ScaleTransform(flipX ? -1 : 1, flipY ? -1 : 1));
        return new TransformedBitmap(source, transform);
    }
}
