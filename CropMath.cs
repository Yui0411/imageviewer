using System.Windows;

namespace ImageViewer;

/// <summary>Crop-box geometry in image pixels, kept inside a <c>width × height</c> image.</summary>
internal static class CropMath
{
    public const double MinSize = 16;

    /// <summary>
    /// Drags a handle: <paramref name="hx"/>/<paramref name="hy"/> are -1 (left/top), 0 or 1 (right/bottom).
    /// With a locked ratio, corners keep the opposite corner fixed and edges keep the box centered across the edge.
    /// </summary>
    public static Rect Resize(Rect start, int hx, int hy, Point p, double width, double height, double? ratio)
    {
        double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
        if (ratio is not { } r)
        {
            if (hx < 0) left = Math.Clamp(p.X, 0, right - MinSize);
            if (hx > 0) right = Math.Clamp(p.X, left + MinSize, width);
            if (hy < 0) top = Math.Clamp(p.Y, 0, bottom - MinSize);
            if (hy > 0) bottom = Math.Clamp(p.Y, top + MinSize, height);
            return new Rect(left, top, right - left, bottom - top);
        }

        if (hx != 0 && hy != 0)
            return FromCorner(new Point(hx < 0 ? right : left, hy < 0 ? bottom : top), hx, hy, p, width, height, r);

        if (hx != 0)
        {
            double anchor = hx < 0 ? right : left;
            double w = Math.Clamp(hx < 0 ? anchor - p.X : p.X - anchor, MinSize, hx < 0 ? anchor : width - anchor);
            double h = w / r;
            if (h > height) { h = height; w = h * r; }
            double y = Math.Clamp((top + bottom) / 2 - h / 2, 0, height - h);
            return new Rect(hx < 0 ? anchor - w : anchor, y, w, h);
        }
        else
        {
            double anchor = hy < 0 ? bottom : top;
            double h = Math.Clamp(hy < 0 ? anchor - p.Y : p.Y - anchor, MinSize, hy < 0 ? anchor : height - anchor);
            double w = h * r;
            if (w > width) { w = width; h = w / r; }
            double x = Math.Clamp((left + right) / 2 - w / 2, 0, width - w);
            return new Rect(x, hy < 0 ? anchor - h : anchor, w, h);
        }
    }

    /// <summary>A new box dragged out from <paramref name="anchor"/> to <paramref name="p"/>.</summary>
    public static Rect Draw(Point anchor, Point p, double width, double height, double? ratio)
    {
        int sx = p.X >= anchor.X ? 1 : -1, sy = p.Y >= anchor.Y ? 1 : -1;
        if (ratio is { } r) return FromCorner(anchor, sx, sy, p, width, height, r);
        var end = new Point(Math.Clamp(p.X, 0, width), Math.Clamp(p.Y, 0, height));
        return new Rect(anchor, end);
    }

    public static Rect Move(Rect start, Vector delta, double width, double height) => new(
        Math.Clamp(start.X + delta.X, 0, Math.Max(0, width - start.Width)),
        Math.Clamp(start.Y + delta.Y, 0, Math.Max(0, height - start.Height)),
        start.Width, start.Height);

    /// <summary>The largest box with the given ratio that fits the image, centered as close to <paramref name="center"/> as possible.</summary>
    public static Rect Largest(double width, double height, double ratio, Point center)
    {
        double w = width, h = w / ratio;
        if (h > height) { h = height; w = h * ratio; }
        return new Rect(Math.Clamp(center.X - w / 2, 0, width - w), Math.Clamp(center.Y - h / 2, 0, height - h), w, h);
    }

    /// <summary>Rounds to whole pixels, kept inside the image.</summary>
    public static Int32Rect ToPixels(Rect r, int width, int height)
    {
        int left = Math.Clamp((int)Math.Round(r.Left), 0, width - 1), top = Math.Clamp((int)Math.Round(r.Top), 0, height - 1);
        int right = Math.Clamp((int)Math.Round(r.Right), left + 1, width), bottom = Math.Clamp((int)Math.Round(r.Bottom), top + 1, height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    private static Rect FromCorner(Point anchor, int sx, int sy, Point p, double width, double height, double r)
    {
        double w = Math.Max(MinSize, sx > 0 ? p.X - anchor.X : anchor.X - p.X);
        double h = Math.Max(MinSize, sy > 0 ? p.Y - anchor.Y : anchor.Y - p.Y);
        if (w / h > r) h = w / r; else w = h * r;
        double maxW = sx > 0 ? width - anchor.X : anchor.X, maxH = sy > 0 ? height - anchor.Y : anchor.Y;
        if (w > maxW) { w = maxW; h = w / r; }
        if (h > maxH) { h = maxH; w = h * r; }
        return new Rect(sx > 0 ? anchor.X : anchor.X - w, sy > 0 ? anchor.Y : anchor.Y - h, w, h);
    }
}
