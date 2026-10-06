using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>A decoded image. Animated GIFs also carry every composited frame and how long to show each.</summary>
internal sealed record LoadedImage(
    string Path, BitmapSource Bitmap, DateTime LastWriteUtc,
    IReadOnlyList<BitmapSource>? Frames = null, IReadOnlyList<TimeSpan>? Delays = null);

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
        if (decoder is GifBitmapDecoder && GifComposer.Compose(decoder) is { } animation)
            return new LoadedImage(path, animation.Frames[0], lastWrite, animation.Frames, animation.Delays);

        var frame = decoder.Frames[0];
        var bitmap = Detach(ApplyOrientation(frame, ReadOrientation(frame)));
        return new LoadedImage(path, bitmap, lastWrite);
    }

    /// <summary>
    /// Copies the pixels into a standalone frozen bitmap. A decoded frame stays tied to its decoder, which belongs to
    /// the background thread that created it; using it from the UI thread (rotating, copying) would throw.
    /// </summary>
    private static BitmapSource Detach(BitmapSource source)
    {
        int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, source.Format, source.Palette, pixels, stride);
        copy.Freeze();
        return copy;
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
