using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageViewer;

/// <summary>
/// Turns the frames of an animated GIF into full-size images. GIF frames are often partial patches drawn over the
/// previous frame, so each one is composited according to its position and disposal method.
/// </summary>
internal static class GifComposer
{
    private const long MaxBytes = 256L * 1024 * 1024; // larger animations just show their first frame

    public static (List<BitmapSource> Frames, List<TimeSpan> Delays)? Compose(BitmapDecoder decoder)
    {
        int count = decoder.Frames.Count;
        if (count < 2) return null;

        int width = Query(decoder.Metadata, "/logscrdesc/Width") ?? decoder.Frames[0].PixelWidth;
        int height = Query(decoder.Metadata, "/logscrdesc/Height") ?? decoder.Frames[0].PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height * 4 * count > MaxBytes) return null;

        int stride = width * 4;
        var canvas = new byte[stride * height];
        var frames = new List<BitmapSource>(count);
        var delays = new List<TimeSpan>(count);

        foreach (var frame in decoder.Frames)
        {
            var metadata = frame.Metadata;
            int left = Query(metadata, "/imgdesc/Left") ?? 0;
            int top = Query(metadata, "/imgdesc/Top") ?? 0;
            int disposal = Query(metadata, "/grctlext/Disposal") ?? 0;
            int delay = Query(metadata, "/grctlext/Delay") ?? 0; // hundredths of a second

            byte[]? previous = disposal == 3 ? (byte[])canvas.Clone() : null;

            var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = pixels.PixelWidth, fh = pixels.PixelHeight;
            var patch = new byte[fw * fh * 4];
            pixels.CopyPixels(patch, fw * 4, 0);

            for (int y = 0; y < fh; y++)
            {
                int cy = top + y;
                if (cy < 0 || cy >= height) continue;
                for (int x = 0; x < fw; x++)
                {
                    int cx = left + x;
                    if (cx < 0 || cx >= width) continue;
                    int s = (y * fw + x) * 4;
                    if (patch[s + 3] == 0) continue; // transparent pixels leave the canvas as it was
                    Buffer.BlockCopy(patch, s, canvas, cy * stride + cx * 4, 4);
                }
            }

            var composed = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
            composed.Freeze();
            frames.Add(composed);
            // Browsers treat very short delays as 100 ms; matching them keeps animations at their intended speed.
            delays.Add(TimeSpan.FromMilliseconds(delay < 2 ? 100 : delay * 10));

            if (disposal == 2) // restore the frame's area to transparent
            {
                for (int y = Math.Max(0, top); y < Math.Min(height, top + fh); y++)
                    Array.Clear(canvas, y * stride + Math.Max(0, left) * 4, Math.Max(0, Math.Min(width, left + fw) - Math.Max(0, left)) * 4);
            }
            else if (previous != null) // restore to how it looked before this frame
            {
                canvas = previous;
            }
        }
        return (frames, delays);
    }

    private static int? Query(ImageMetadata? metadata, string query)
    {
        try
        {
            if (metadata is BitmapMetadata md && md.GetQuery(query) is { } value)
                return Convert.ToInt32(value);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or InvalidCastException) { }
        return null;
    }
}
