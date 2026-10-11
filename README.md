# Astro Colour Editor

A desktop tool for selective colour editing of `.xisf`, FITS and TIFF images, modelled on
Capture One's colour editor. It runs on Windows and macOS (Avalonia UI).

## Using it

Shortcuts below are for Windows; on a Mac use ⌘ in place of Ctrl.

1. **Open** (Ctrl+O) an `.xisf`, `.fits`, `.fit`, `.fts`, `.tif` or `.tiff` image, or pass a file path on the command line / drag it onto the exe
   (on a Mac: Finder's "Open With", or drop it on the Dock icon).
2. Turn on **Pick colour from image** (P) and click a colour in the image. A new entry appears in the list.
3. Adjust that colour's **Hue**, **Saturation** and **Lightness**. Similar hues are adjusted too,
   fading out smoothly.
4. Refine which colours are affected under **Selected range**:
   - **View selected colour range** greys out everything not being edited.
   - **Centre hue / Range / Smoothness** set the hue band and how gently it fades.
   - **Protect neutrals** leaves near-grey pixels (background, white stars) alone.
5. Pick more colours as needed. Each entry can be toggled or deleted, and you can double-click any slider label to reset it.
6. On the **Curves** tab, shape the tones of the composite **RGB** curve or of the **Red**, **Green** and **Blue**
   channels over the image's histogram. Click to add a point and drag it; double-click, right-click or drag a point
   off the graph to remove it. Moving the end points sets the black and white points. Curves are applied after
   the colour editor, and also work on monochrome images (RGB curve only).
7. **Before** (B) toggles the original image for comparison.
8. **Save** (Ctrl+S) overwrites the file after confirmation; **Save as** (Ctrl+Shift+S) writes a new file
   in the same format as the original.

Navigation: scroll wheel (or trackpad pinch) zooms around the cursor, drag pans (right-drag also pans while the picker is on),
**Fit** (Ctrl+0) and **100%** (Ctrl+1).

On large images (longest side over 2400 px), the display uses a reduced-resolution preview while
a slider moves, then sharpens to full resolution once you stop. Saving always uses full resolution.

Colour editing is intended for stretched (non-linear) images; the app warns when an image looks linear.

## XISF support

- Reads monolithic XISF 1.0: UInt8/16/32 and Float32/64 samples, planar or normal pixel storage,
  attached / inline / embedded blocks, zlib, LZ4, LZ4HC and Zstandard compression, byte shuffling and sub-blocks.
- Edits the first image in the file.
- Saving keeps the original sample format and all header metadata (FITS keywords, properties, ICC profile,
  other attachments). The edited image is written uncompressed, and stale thumbnails are dropped.

## FITS support

- Reads the first image HDU (primary or IMAGE extension): 8/16/32-bit integer or 32/64-bit float, mono, or
  colour as three planes along `NAXIS3`. `BZERO`/`BSCALE` are applied; 16/32-bit data with the standard
  `BZERO` offset is treated as unsigned.
- Integer data is normalised by its type's range. Float data is taken as [0,1], or [0,65535] when its
  values go well above 1. NaN and infinite values are read as 0.
- `ROWORDER = 'BOTTOM-UP'` images are flipped for display; otherwise rows are shown as stored (top row first).
- Saving keeps the original sample format, scaling, row order, every header card and every other HDU.
  `CHECKSUM`/`DATASUM` are removed, since they would no longer match. Unedited, the file is written back byte for byte.
- Compressed (fpack `.fz`) images are not supported.

## TIFF support

- Reads the first image in the file: greyscale or RGB, optionally with extra channels such as alpha;
  8/16/32-bit integer or 16/32/64-bit float; contiguous or planar, stripped or tiled; any compression
  LibTiff.NET can decode (LZW, Deflate, PackBits, …) with horizontal or floating-point predictors.
- Integer data is normalised by its type's range; float data as for FITS. `MinIsWhite` images are inverted.
- Saving keeps the sample format, channel layout, compression (LZW if the original was lossy or exotic), predictor,
  resolution, orientation, ICC profile, XMP, IPTC and text tags. Images are written in strips. Further pages
  and EXIF data are not kept.
- Palette, CMYK and YCbCr images are not supported.

## Building

Requires the .NET 10 SDK.

```
dotnet run --project src/AstroColour.Desktop
dotnet test tests/AstroColour.Tests
```

To produce a self-contained Windows folder (`publish/win-x64`) you can copy to a PC without .NET installed:

```
dotnet publish src/AstroColour.Desktop -c Release -r win-x64 --self-contained -o publish/win-x64
```

To make a Mac app, run this on a Mac:

```
tools/package-macos.sh            # Apple Silicon; pass osx-x64 for Intel Macs
```

It writes `publish/macos-arm64/Astro Colour Editor.app` and a zip of it. Without signing settings the app is
only ad-hoc signed: it runs on the Mac that built it, but other Macs block it until it is opened with
right-click > Open. To distribute it, set `SIGN_IDENTITY` (a Developer ID certificate) and `NOTARY_PROFILE`
(a `notarytool` keychain profile); see the script's header.

`samples/MakeSample.cs` generates a synthetic test image: `dotnet run samples/MakeSample.cs`.

## Layout

- `src/AstroColour.Core`: XISF, FITS and TIFF readers/writers (`Xisf/XisfFile.cs`, `Fits/FitsFile.cs`, `Tiff/TiffFile.cs`) and the colour engine (`Colour/`), with no UI dependencies.
- `src/AstroColour.Desktop`: the Avalonia app (`macOS/` holds the app bundle's Info.plist and entitlements).
- `tests/AstroColour.Tests`: unit tests for XISF, FITS and TIFF decoding/encoding and the colour maths.
- `tools/make_icon.py`: draws the app icon (`src/AstroColour.Desktop/Assets/AppIcon.ico`, `.png` and `.icns`).
- `tools/package-macos.sh`: builds, signs and zips the macOS app bundle.

If the app crashes, details are written to `%LOCALAPPDATA%\AstroColourEditor\crash.log`
(`~/Library/Application Support/AstroColourEditor/crash.log` on a Mac).
