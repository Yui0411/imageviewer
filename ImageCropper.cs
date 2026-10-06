using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// Crop geometry for one file. The crop is chosen on the image as displayed (after its EXIF orientation is applied),
/// but performed on the pixels as stored, where a JPEG's top-left corner must sit on the block grid.
/// </summary>
internal sealed record CropInfo(int StoredWidth, int StoredHeight, int Turns, bool Mirror, int BlockWidth, int BlockHeight)
{
    public int DisplayWidth => Turns % 2 == 0 ? StoredWidth : StoredHeight;
    public int DisplayHeight => Turns % 2 == 0 ? StoredHeight : StoredWidth;

    /// <summary>
    /// Adjusts a crop so it can be done losslessly: the stored top-left moves out to the block grid (so nothing the
    /// user selected is lost), then, if a ratio is locked, the free edges are adjusted to restore it exactly.
    /// </summary>
    public Int32Rect Snap(Int32Rect display, double? ratio)
    {
        var s = ToStored(display);
        int x0 = s.X / BlockWidth * BlockWidth, y0 = s.Y / BlockHeight * BlockHeight;
        int x1 = s.X + s.Width, y1 = s.Y + s.Height;
        if (ratio is { } r)
        {
            double storedRatio = Turns % 2 == 0 ? r : 1 / r;
            int h = Math.Max(1, (int)Math.Round((x1 - x0) / storedRatio));
            if (y0 + h <= StoredHeight)
            {
                y1 = y0 + h;
            }
            else
            {
                y1 = StoredHeight;
                x1 = Math.Min(StoredWidth, x0 + Math.Max(1, (int)Math.Round((y1 - y0) * storedRatio)));
            }
        }
        return ToDisplay(new Int32Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)));
    }

    public bool IsAligned(Int32Rect display)
    {
        var s = ToStored(display);
        return s.X % BlockWidth == 0 && s.Y % BlockHeight == 0;
    }

    public Int32Rect ToStored(Int32Rect display)
    {
        var a = DisplayToStored(display.X, display.Y);
        var b = DisplayToStored(display.X + display.Width, display.Y + display.Height);
        return FromCorners(a, b);
    }

    public Int32Rect ToDisplay(Int32Rect stored)
    {
        var a = StoredToDisplay(stored.X, stored.Y);
        var b = StoredToDisplay(stored.X + stored.Width, stored.Y + stored.Height);
        return FromCorners(a, b);
    }

    // Stored → displayed is "mirror horizontally, then rotate clockwise Turns times" (see ImageRotator.Combine).
    private (int x, int y) StoredToDisplay(int x, int y)
    {
        int w = StoredWidth, h = StoredHeight;
        if (Mirror) x = w - x;
        for (int i = 0; i < Turns; i++)
        {
            (x, y) = (h - y, x);
            (w, h) = (h, w);
        }
        return (x, y);
    }

    private (int x, int y) DisplayToStored(int x, int y)
    {
        int w = DisplayWidth, h = DisplayHeight;
        for (int i = 0; i < Turns; i++)
        {
            (x, y) = (y, w - x);
            (w, h) = (h, w);
        }
        if (Mirror) x = StoredWidth - x;
        return (x, y);
    }

    private static Int32Rect FromCorners((int x, int y) a, (int x, int y) b) =>
        new(Math.Min(a.x, b.x), Math.Min(a.y, b.y), Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));
}

/// <summary>Crops JPEG (losslessly, via <see cref="JpegLosslessCrop"/>), PNG and BMP files.</summary>
internal static class ImageCropper
{
    public static bool CanCrop(string path) => ImageRotator.CanRotate(path);

    public static CropInfo GetInfo(string path) => GetInfo(File.ReadAllBytes(path), path);

    /// <summary>Returns the new file contents, already verified against the original pixels. Doesn't write anything.</summary>
    public static byte[] Crop(string path, Int32Rect displayRect)
    {
        var original = File.ReadAllBytes(path);
        var info = GetInfo(original, path);
        var stored = info.ToStored(displayRect);
        if (stored.Width < 1 || stored.Height < 1 || stored.X < 0 || stored.Y < 0
            || stored.X + stored.Width > info.StoredWidth || stored.Y + stored.Height > info.StoredHeight)
            throw new EditException("The crop is outside the image.");
        if (stored.Width == info.StoredWidth && stored.Height == info.StoredHeight)
            throw new EditException("The crop covers the whole image, so there's nothing to crop.");

        var source = ImageRotator.Decode(original); // pixels as stored, orientation not applied
        if (!IsJpeg(path))
            return CropLossless(source, stored, Path.GetExtension(path));

        if (!info.IsAligned(displayRect))
            throw new EditException("The crop doesn't line up with the JPEG block grid.");
        var cropped = JpegLosslessCrop.Crop(original, stored.X, stored.Y, stored.Width, stored.Height);
        var croppedPixels = ImageRotator.Decode(cropped);
        Verify(source, stored, croppedPixels, edgeBand: 4);

        // Refresh the EXIF size and embedded thumbnail. Windows does this as a lossless transcode; keep the result
        // only if it decodes to exactly the same pixels.
        try
        {
            var withMetadata = UpdateJpegMetadata(cropped, stored.Width, stored.Height);
            Verify(croppedPixels, new Int32Rect(0, 0, stored.Width, stored.Height), ImageRotator.Decode(withMetadata), edgeBand: 0);
            return withMetadata;
        }
        catch (Exception ex) when (ex is EditException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            return cropped; // still a correct, lossless crop; only the embedded thumbnail is stale
        }
    }

    private static CropInfo GetInfo(byte[] bytes, string path)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, ImageRotator.DecodeOptions, BitmapCacheOption.None).Frames[0];
        var (turns, mirror) = ImageRotator.Combine(ImageLoader.ReadOrientation(frame), 0);
        if (!IsJpeg(path))
            return new CropInfo(frame.PixelWidth, frame.PixelHeight, turns, mirror, 1, 1);

        var header = JpegHeader.Read(bytes) ?? throw new EditException("This file isn't a valid JPEG.");
        if (!header.IsSequential)
            throw new EditException(header.FrameMarker is 0xC2 or 0xC6 or 0xCA or 0xCE
                ? "Progressive JPEGs can't be cropped losslessly."
                : "This kind of JPEG can't be cropped losslessly.");
        return new CropInfo(header.Width, header.Height, turns, mirror, header.BlockWidth, header.BlockHeight);
    }

    private static bool IsJpeg(string path) => ImageRotator.JpegExtensions.Contains(Path.GetExtension(path));

    private static byte[] CropLossless(BitmapFrame source, Int32Rect stored, string extension)
    {
        var pixels = new CroppedBitmap(source, stored);
        bool png = extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
        byte[] result;
        try
        {
            var metadata = (source.Metadata as BitmapMetadata)?.Clone();
            result = ImageRotator.Encode(png ? new PngBitmapEncoder() : new BmpBitmapEncoder(),
                BitmapFrame.Create(pixels, null, metadata, source.ColorContexts));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            result = ImageRotator.Encode(png ? new PngBitmapEncoder() : new BmpBitmapEncoder(), BitmapFrame.Create(pixels));
        }
        Verify(source, stored, ImageRotator.Decode(result), edgeBand: 0);
        return result;
    }

    private static byte[] UpdateJpegMetadata(byte[] jpeg, int width, int height)
    {
        using var stream = new MemoryStream(jpeg);
        var frame = new JpegBitmapDecoder(stream, ImageRotator.DecodeOptions, BitmapCacheOption.None).Frames[0];
        var metadata = (frame.Metadata as BitmapMetadata)?.Clone();
        if (metadata != null)
        {
            SetDimension(metadata, "/app1/ifd/exif/{ushort=40962}", width);  // PixelXDimension
            SetDimension(metadata, "/app1/ifd/exif/{ushort=40963}", height); // PixelYDimension
        }
        BitmapSource? thumbnail = null;
        if (frame.Thumbnail != null)
        {
            double scale = 160.0 / Math.Max(width, height);
            thumbnail = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        }
        var encoder = new JpegBitmapEncoder(); // no transform: Windows transcodes the existing data losslessly
        encoder.Frames.Add(BitmapFrame.Create(frame, thumbnail, metadata, frame.ColorContexts));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static void SetDimension(BitmapMetadata metadata, string query, int value)
    {
        try
        {
            if (!metadata.ContainsQuery(query)) return;
            object typed = metadata.GetQuery(query) is ushort && value <= ushort.MaxValue ? (ushort)value : (uint)value;
            metadata.SetQuery(query, typed);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
    }

    /// <summary>
    /// Refuses the result unless it decodes to exactly the selected part of the original. With
    /// <paramref name="edgeBand"/>, that many pixels along each edge aren't compared: JPEG decoders blend each pixel's
    /// colour with neighbouring (half-resolution) colour samples, and the ones just outside the crop are gone. Every
    /// 8×8 block still reaches into the compared interior, so a changed or misplaced block is always caught.
    /// </summary>
    private static void Verify(BitmapSource original, Int32Rect region, BitmapSource result, int edgeBand)
    {
        var expectedSource = new CroppedBitmap(original, region);
        var (format, expected) = ImageRotator.Pixels(expectedSource, result.Format);
        var (_, actual) = ImageRotator.Pixels(result, expectedSource.Format);
        bool ok = result.PixelWidth == region.Width && result.PixelHeight == region.Height && expected.Length == actual.Length;

        if (edgeBand == 0)
        {
            if (!ok || !expected.AsSpan().SequenceEqual(actual))
                throw new EditException("This image couldn't be cropped without losing quality, so the file was not changed.");
            return;
        }

        int stride = expected.Length / Math.Max(1, region.Height);
        int bytesPerPixel = Math.Max(1, format.BitsPerPixel / 8);
        for (int y = edgeBand; ok && y < region.Height - edgeBand; y++)
        {
            int start = y * stride + edgeBand * bytesPerPixel, end = y * stride + (region.Width - edgeBand) * bytesPerPixel;
            ok = expected.AsSpan(start, Math.Max(0, end - start)).SequenceEqual(actual.AsSpan(start, Math.Max(0, end - start)));
        }
        if (!ok)
            throw new EditException("This image couldn't be cropped without losing quality, so the file was not changed.");
    }
}
