# Astro Colour Editor

A WinUI 3 desktop tool for selective colour editing of `.xisf` and FITS images, modelled on
Capture One's colour editor.

## Using it

1. **Open** (Ctrl+O) an `.xisf`, `.fits`, `.fit` or `.fts` image, or pass a file path on the command line / drag it onto the exe.
2. Turn on **Pick colour from image** (P) and click a colour in the image. A new entry appears in the list.
3. Adjust that colour's **Hue**, **Saturation** and **Lightness**. Similar hues are adjusted too,
   fading out smoothly.
4. Refine which colours are affected under **Selected range**:
   - **View selected colour range** greys out everything not being edited.
   - **Centre hue / Range / Smoothness** set the hue band and how gently it fades.
   - **Protect neutrals** leaves near-grey pixels (background, white stars) alone.
5. Pick more colours as needed. Each entry can be toggled or deleted, and you can double-click any slider label to reset it.
6. **Before** (B) toggles the original image for comparison.
7. **Save** (Ctrl+S) overwrites the file after confirmation; **Save as** (Ctrl+Shift+S) writes a new file
   in the same format as the original.

Navigation: scroll wheel zooms around the cursor, drag pans (right-drag also pans while the picker is on),
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

## Building

Requires the .NET 10 SDK on Windows 10/11.

```
dotnet build src/AstroColour.App -c Release
dotnet test tests/AstroColour.Tests
```

The app is unpackaged with the Windows App SDK bundled, so the output
`src/AstroColour.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/AstroColourEditor.exe` runs directly.
To produce a self-contained folder (`publish/win-x64`) you can copy to a PC without .NET installed:

```
dotnet publish src/AstroColour.App -p:PublishProfile=win-x64
```

`samples/MakeSample.cs` generates a synthetic test image: `dotnet run samples/MakeSample.cs`.

## Layout

- `src/AstroColour.Core`: XISF and FITS readers/writers (`Xisf/XisfFile.cs`, `Fits/FitsFile.cs`) and the colour engine (`Colour/`), with no UI dependencies.
- `src/AstroColour.App`: the WinUI 3 app.
- `tests/AstroColour.Tests`: unit tests for XISF and FITS decoding/encoding and the colour maths.
- `tools/make_icon.py`: draws the app icon (`src/AstroColour.App/Assets/AppIcon.ico`).

If the app crashes, details are written to `%LOCALAPPDATA%\AstroColourEditor\crash.log`.
