# Image Viewer

A small, fast image viewer for Windows with lossless rotation and cropping. The window is just the image — the toolbar
and the previous/next buttons appear when you move the mouse near the top or the sides.

## Shortcuts

| Keys | Action |
| --- | --- |
| ← / → | Previous / next image in the folder (Explorer name order) |
| Home / End | First / last image |
| R / Shift+R | Rotate clockwise / counter-clockwise — **overwrites the file** |
| F / Shift+F | Add to / remove from favourites · show favourites only |
| C | Crop (see below) |
| Ctrl+Z | Undo the last crop (until you move to another image) |
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

## Favourites

For picking the best shots: press **F** (or the heart in the toolbar) on the ones you like. The toolbar shows how
many are marked, and **Shift+F** shows only them, so ← / → skips everything else.

Then, from the menu (right-click → **Favourites**):

- **Copy favourites to…** / **Move favourites to…** a folder you choose, or straight into a **Favourites**
  subfolder. Moving asks first; nothing is ever overwritten (a clash becomes `name (2).jpg`); copies keep their
  modified date. The viewer stays in the current folder.
- **Clear favourites in this folder…** when you're done.

Marks are kept in `%AppData%\ImageViewer\favourites.json`; the photos themselves aren't changed.

## Cropping

Press **C** or the crop button in the toolbar. Drag the box, its edges or corners, or drag outside it to draw a new
one; the arrow keys nudge it (Shift for bigger steps) and the middle mouse button pans when zoomed in.

- **Ratios:** Free, Original, 1:1, 4:3, 3:2, 16:9, 5:4 or Custom (type any ratio, e.g. 21:9). **⇄** (or X) swaps
  landscape/portrait, **#** (or G) toggles the rule-of-thirds grid. The last choice is remembered.
- **Apply** (or Enter) overwrites the file; **Ctrl+Z** puts the original back until you move to another image.
- **Save copy** writes `name (cropped).jpg` next to the original and leaves the original alone.

Crops are lossless too:

- **JPEG** blocks inside the crop are copied as they are, without re-compressing. This needs the crop's top-left
  corner on the JPEG block grid, so the box snaps outward by up to 15 px there (never cutting off what you
  selected). Progressive JPEGs can't be cropped this way and are refused with a message.
- **PNG and BMP** are cropped exactly.
- The result is decoded and must match the original pixels before the file is written; the EXIF size and embedded
  thumbnail are updated.

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

**Remove from Windows image apps…** in the same menu undoes the registration. Window position and crop preferences
are stored in `%AppData%\ImageViewer\settings.json`.
