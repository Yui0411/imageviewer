using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>An edit (rotation or crop) that was refused or failed; the message is shown to the user and the file is untouched.</summary>
internal sealed class EditException(string message) : Exception(message);

/// <summary>
/// Rotates image files in place without losing quality.
/// JPEGs are transcoded losslessly by the Windows JPEG codec (only possible when the dimensions are a multiple of the
/// JPEG block size); PNG and BMP are re-encoded, which is inherently lossless. Every result is decoded and compared
/// against the expected pixels before the original file is replaced.
/// </summary>
internal static class ImageRotator
{
    internal static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif" };
    private static readonly HashSet<string> LosslessExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".bmp" };

    internal const BitmapCreateOptions DecodeOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;

    // A lossless JPEG transcode decodes to within a few levels of the expected pixels (chroma upsampling rounding);
    // a lossy re-encode differs by much more.
    internal const int JpegTolerance = 6;

    public static bool CanRotate(string path)
    {
        var ext = Path.GetExtension(path);
        return JpegExtensions.Contains(ext) || LosslessExtensions.Contains(ext);
    }

    /// <summary>Rotates the image as it is displayed (i.e. honoring its EXIF orientation) by 90° clockwise per turn.</summary>
    public static void Rotate(string path, int clockwiseTurns)
    {
        var original = File.ReadAllBytes(path);
        var rotated = JpegExtensions.Contains(Path.GetExtension(path))
            ? RotateJpeg(original, clockwiseTurns)
            : RotateLossless(original, clockwiseTurns, Path.GetExtension(path));
        if (rotated != null) Replace(path, rotated);
    }

    // ---------------------------------------------------------------- JPEG

    /// <summary>Quick check (header and orientation only) that throws if the rotation would be refused.</summary>
    public static void EnsureCanRotate(string path, int clockwiseTurns)
    {
        if (!JpegExtensions.Contains(Path.GetExtension(path))) return; // PNG/BMP can always be rotated
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes);
        var frame = BitmapDecoder.Create(stream, DecodeOptions, BitmapCacheOption.None).Frames[0];
        EnsureJpegAligned(bytes, Combine(ImageLoader.ReadOrientation(frame), clockwiseTurns));
    }

    private static void EnsureJpegAligned(byte[] data, (int turns, bool mirror) transform)
    {
        var header = JpegHeader.Read(data) ?? throw new EditException("This file isn't a valid JPEG.");
        bool movesPixels = transform.turns != 0 || transform.mirror; // otherwise only the orientation tag changes
        if (movesPixels && (header.Width % header.BlockWidth != 0 || header.Height % header.BlockHeight != 0))
            throw new EditException(
                $"Can't rotate losslessly: {header.Width} × {header.Height} isn't a multiple of the JPEG block size " +
                $"({header.BlockWidth} × {header.BlockHeight}). The file was not changed.");
    }

    private static byte[]? RotateJpeg(byte[] original, int clockwiseTurns)
    {
        // Work out the transform from the stored pixels to the desired upright result, then reset the orientation tag.
        var source = Decode(original);
        var (turns, mirror) = Combine(ImageLoader.ReadOrientation(source), clockwiseTurns);
        EnsureJpegAligned(original, (turns, mirror));

        // The codec can't combine a mirror and a rotation losslessly in one pass, so mirror first.
        var result = original;
        if (mirror) result = TranscodeJpeg(result, Rotation.Rotate0, flipHorizontal: true);
        result = TranscodeJpeg(result, (Rotation)turns, flipHorizontal: false);

        Verify(source, Decode(result), turns, mirror, JpegTolerance);
        return result;
    }

    private static byte[] TranscodeJpeg(byte[] input, Rotation rotation, bool flipHorizontal)
    {
        using var stream = new MemoryStream(input);
        var frame = new JpegBitmapDecoder(stream, DecodeOptions, BitmapCacheOption.None).Frames[0];

        var encoder = new JpegBitmapEncoder { Rotation = rotation, FlipHorizontal = flipHorizontal };
        var thumbnail = frame.Thumbnail == null ? null : Transform(frame.Thumbnail, (int)rotation, flipHorizontal);
        encoder.Frames.Add(BitmapFrame.Create(frame, thumbnail, UprightMetadata(frame), frame.ColorContexts));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    // ---------------------------------------------------------------- PNG / BMP

    private static byte[] RotateLossless(byte[] original, int clockwiseTurns, string extension)
    {
        BitmapFrame source;
        using (var stream = new MemoryStream(original))
            source = BitmapDecoder.Create(stream, DecodeOptions, BitmapCacheOption.OnLoad).Frames[0];

        var (turns, mirror) = Combine(ImageLoader.ReadOrientation(source), clockwiseTurns);
        var pixels = Transform(source, turns, mirror);

        BitmapEncoder encoder = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? new PngBitmapEncoder()
            : new BmpBitmapEncoder();
        byte[] result;
        try
        {
            result = Encode(encoder, BitmapFrame.Create(pixels, null, UprightMetadata(source), source.ColorContexts));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // Some metadata can't be carried over by the encoder; keep the pixels, which is what matters.
            encoder = encoder is PngBitmapEncoder ? new PngBitmapEncoder() : new BmpBitmapEncoder();
            result = Encode(encoder, BitmapFrame.Create(pixels));
        }

        Verify(source, Decode(result), turns, mirror, tolerance: 0);
        return result;
    }

    internal static byte[] Encode(BitmapEncoder encoder, BitmapFrame frame)
    {
        encoder.Frames.Add(frame);
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Combines the file's EXIF orientation with the requested rotation into a single transform applied to the stored
    /// pixels: optionally mirror horizontally, then rotate <c>turns</c> × 90° clockwise.
    /// </summary>
    internal static (int turns, bool mirror) Combine(int orientation, int clockwiseTurns)
    {
        var (turns, mirror) = orientation switch
        {
            2 => (0, true),
            3 => (2, false),
            4 => (2, true),
            5 => (3, true),
            6 => (1, false),
            7 => (1, true),
            8 => (3, false),
            _ => (0, false),
        };
        return (((turns + clockwiseTurns) % 4 + 4) % 4, mirror);
    }

    private static BitmapSource Transform(BitmapSource source, int turns, bool mirror)
    {
        if (turns == 0 && !mirror) return source;
        var transform = new TransformGroup();
        if (mirror) transform.Children.Add(new ScaleTransform(-1, 1));
        if (turns != 0) transform.Children.Add(new RotateTransform(turns * 90));
        return new TransformedBitmap(source, transform);
    }

    /// <summary>Copy of the frame's metadata with the orientation reset to "upright", since the pixels now are.</summary>
    private static BitmapMetadata? UprightMetadata(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata) return null;
        var copy = metadata.Clone();
        try
        {
            if (copy.ContainsQuery("System.Photo.Orientation"))
                copy.SetQuery("System.Photo.Orientation", (ushort)1);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
        return copy;
    }

    internal static BitmapFrame Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return BitmapDecoder.Create(stream, DecodeOptions, BitmapCacheOption.OnLoad).Frames[0];
    }

    /// <summary>Refuses to save unless the new file decodes to the original pixels, transformed.</summary>
    private static void Verify(BitmapSource original, BitmapSource result, int turns, bool mirror, int tolerance)
    {
        var transformed = Transform(original, turns, mirror);
        var (_, expected) = Pixels(transformed, result.Format);
        var (_, actual) = Pixels(result, transformed.Format);
        bool ok = transformed.PixelWidth == result.PixelWidth && expected.Length == actual.Length;
        for (int i = 0; ok && i < expected.Length; i++)
            ok = Math.Abs(expected[i] - actual[i]) <= tolerance;
        if (!ok)
            throw new EditException("This image couldn't be rotated without losing quality, so the file was not changed.");
    }

    /// <summary>Raw pixels; converted to 16 bits per channel only if the format changed, so no precision is hidden.</summary>
    internal static (PixelFormat format, byte[] data) Pixels(BitmapSource source, PixelFormat? compareAs = null)
    {
        if (compareAs is { } target && target != source.Format)
            source = new FormatConvertedBitmap(source, PixelFormats.Rgba64, null, 0);
        int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
        var buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(buffer, stride, 0);
        return (source.Format, buffer);
    }

    /// <summary>Writes to a temporary file next to the original, then swaps it in, so a failure never leaves a broken image.</summary>
    internal static void Replace(string path, byte[] contents)
    {
        var temp = path + ".saving.tmp";
        try
        {
            File.WriteAllBytes(temp, contents);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(50); // briefly held open by another reader (e.g. the preloader or Explorer's thumbnailer)
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EditException($"Couldn't save the rotated image: {ex.Message}");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Image size and block (MCU) size from a JPEG's start-of-frame header.</summary>
internal sealed record JpegHeader(int Width, int Height, int BlockWidth, int BlockHeight, int FrameMarker)
{
    /// <summary>Baseline or extended sequential Huffman — the kinds that can be cropped losslessly.</summary>
    public bool IsSequential => FrameMarker is 0xC0 or 0xC1;

    public static JpegHeader? Read(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) return null;
            byte marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill byte
            if (marker == 0xD9 || marker == 0xDA) return null; // end of image / start of scan before any frame header
            int length = (data[pos + 2] << 8) | data[pos + 3];

            // SOF0–SOF15, excluding DHT (C4), JPG (C8) and DAC (CC).
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                int p = pos + 4;
                if (p + 6 > data.Length) return null;
                int height = (data[p + 1] << 8) | data[p + 2];
                int width = (data[p + 3] << 8) | data[p + 4];
                int components = data[p + 5];
                if (p + 6 + components * 3 > data.Length) return null;

                int maxH = 1, maxV = 1;
                for (int c = 0; c < components; c++)
                {
                    byte sampling = data[p + 6 + c * 3 + 1];
                    maxH = Math.Max(maxH, sampling >> 4);
                    maxV = Math.Max(maxV, sampling & 0x0F);
                }
                // A single-component (grayscale) image is coded in 8×8 blocks regardless of its sampling factors.
                return components == 1
                    ? new JpegHeader(width, height, 8, 8, marker)
                    : new JpegHeader(width, height, 8 * maxH, 8 * maxV, marker);
            }
            pos += 2 + length;
        }
        return null;
    }
}
