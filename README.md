# Astro Colour Editor

A WinUI 3 desktop tool for selective colour editing of PixInsight `.xisf` images, modelled on
Capture One's colour editor.

## Using it

1. **Open** (Ctrl+O) an `.xisf` image, or pass a file path on the command line / drag it onto the exe.
2. Turn on **Pick colour from image** (P) and click a colour in the image. A new entry appears in the list.
3. Adjust that colour's **Hue**, **Saturation** and **Lightness**. Similar hues are adjusted too,
   fading out smoothly.
4. Refine which colours are affected under **Selected range**:
   - **View selected colour range** greys out everything not being edited.
   - **Centre hue / Range / Smoothness** set the hue band and how gently it fades.
   - **Protect neutrals** leaves near-grey pixels (background, white stars) alone.
5. Pick more colours as needed. Each entry can be toggled or deleted, and you can double-click any slider label to reset it.
6. **Before** (B) toggles the original image for comparison.
7. **Save** (Ctrl+S) overwrites the file after confirmation; **Save as** (Ctrl+Shift+S) writes a new file.

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

## Building

Requires the .NET 10 SDK on Windows 10/11.

```
dotnet build src/AstroColour.App -c Release
dotnet test tests/AstroColour.Tests
```

The app is unpackaged with the Windows App SDK bundled, so the output
`src/AstroColour.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/AstroColourEditor.exe` runs directly.
To produce a folder you can copy elsewhere:

```
dotnet publish src/AstroColour.App -c Release -r win-x64
```

`samples/MakeSample.cs` generates a synthetic test image: `dotnet run samples/MakeSample.cs`.

## Layout

- `src/AstroColour.Core`: XISF reader/writer (`Xisf/XisfFile.cs`) and the colour engine (`Colour/`), with no UI dependencies.
- `src/AstroColour.App`: the WinUI 3 app.
- `tests/AstroColour.Tests`: unit tests for XISF decoding/encoding and the colour maths.

If the app crashes, details are written to `%LOCALAPPDATA%\AstroColourEditor\crash.log`.
