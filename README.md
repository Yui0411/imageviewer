# Image Viewer

A small, fast image viewer for Windows with lossless rotation. The window is just the image — the toolbar
and the previous/next buttons appear when you move the mouse near the top or the sides.

## Shortcuts

| Keys | Action |
| --- | --- |
| ← / → | Previous / next image in the folder (Explorer name order) |
| Home / End | First / last image |
| R / Shift+R | Rotate clockwise / counter-clockwise — **overwrites the file** |
| Scroll, + / − | Zoom toward the cursor |
| Drag | Pan when zoomed in, otherwise move the window |
| Double-click, 0 | Toggle fit-to-window / actual size |
| F11 | Fullscreen |
| Esc | Leave fullscreen, or close |
| Ctrl+C | Copy the image (and the file, for pasting in Explorer) |
| Ctrl+E | Show in Explorer |
| Del | Move to the Recycle Bin |
| Ctrl+O | Open a file (or drop one onto the window) |
| F1 | Shortcut reference |
| Right-click | Menu |

## Rotation

- **JPEG** is rotated without re-compressing, so quality never degrades. That's only possible when the width and
  height are multiples of the JPEG block size (8 or 16 px — true for almost all camera photos); other JPEGs are
  left untouched and a warning explains why.
- **PNG and BMP** are always lossless.
- The EXIF orientation tag is taken into account and reset, and camera metadata is kept.
- Before the original is replaced, the new file is decoded and compared against the expected pixels; if anything
  doesn't match, the file isn't changed.

GIF, WebP, TIFF, ICO and HEIC are view-only (HEIC needs the free *HEIF Image Extensions* from the Microsoft Store).

## Build

Requires the .NET 9 SDK (to build) or the .NET 9 Desktop Runtime (to run).

```
dotnet publish -c Release
```

This produces a single `publish\ImageViewer.exe` (about 240 KB).

## Make it the default viewer

1. Put `ImageViewer.exe` where you want to keep it.
2. Run it, right-click → **Set as default image viewer…** — this registers it for your user account only.
3. In the Settings page that opens, choose **Image Viewer** under *Photo viewer* (or right-click an image in
   Explorer → *Open with* → *Choose another app* → *Always use this app*).

**Remove from Windows image apps…** in the same menu undoes the registration. Window position is stored in
`%AppData%\ImageViewer\settings.json`.
