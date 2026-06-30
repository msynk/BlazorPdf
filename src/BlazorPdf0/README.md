# BlazorPdf

A dependency-free, native PDF viewer component for Blazor.

BlazorPdf renders PDFs using the **browser's own built-in PDF engine** and wraps
it in a fully customizable Blazor toolbar. There is **no PDF.js, no commercial SDK, and
no third-party NuGet package** — the only dependency is `Microsoft.AspNetCore.Components.Web`.
It ships a single, self-authored JavaScript interop module.

Works in Blazor Server, Blazor WebAssembly, and Blazor Hybrid (.NET 8+).

## Three rendering backends

| Component | How it renders | Best for |
| --- | --- | --- |
| `PdfViewer` | Browser's built-in PDF engine in an `<iframe>` | Zero-effort display, native toolbar |
| `PdfNativeViewer` | Pure-C# rasterizer → pixels on a `<canvas>` | Full control, no browser PDF engine, server-side export |
| `PdfSvgViewer` | Pure-C# renderer → **SVG (standard DOM)** | Selectable text, crisp vector zoom, no JS interop |

## Install

```bash
dotnet add package BlazorPdf
```

## Quick start

```razor
@using BlazorPdf

<PdfViewer Url="/files/report.pdf" Height="720px" />
```

Load from bytes, a stream, base64, or an uploaded file:

```razor
<PdfViewer Source="_source" />

@code {
    private PdfSource? _source;

    protected override void OnInitialized()
        => _source = PdfSource.FromBytes(myBytes, "report.pdf");

    // Other factories:
    // PdfSource.FromUrl("https://.../doc.pdf")
    // PdfSource.FromBase64(base64String)
    // await PdfSource.FromStreamAsync(stream, "doc.pdf")
}
```

## Parameters

| Parameter | Type | Default | Description |
| --- | --- | --- | --- |
| `Source` | `PdfSource?` | `null` | Document to display. |
| `Url` | `string?` | `null` | Shortcut for a URL source. |
| `Data` | `byte[]?` | `null` | Shortcut for a byte[] source. |
| `Width` / `Height` | `string` | `100%` / `600px` | CSS size. |
| `ShowToolbar` | `bool` | `true` | Show the custom Blazor toolbar. |
| `ShowNativeToolbar` | `bool` | `true` | Show the browser's own PDF toolbar in the frame. |
| `ShowDownloadButton` / `ShowPrintButton` / `ShowOpenInNewTabButton` / `ShowFullscreenButton` / `ShowPageNavigation` | `bool` | `true` | Toggle individual toolbar buttons. |
| `InitialPage` | `int` | `1` | Page to open initially. |
| `ZoomMode` | `PdfZoomMode` | `Auto` | `Auto`, `PageFit`, `PageWidth`, or `Custom`. |
| `ZoomPercent` | `int` | `100` | Used when `ZoomMode` is `Custom`. |
| `ToolbarContent` | `RenderFragment?` | `null` | Custom toolbar content. |
| `OnDocumentLoaded` | `EventCallback` | — | Raised when a document finishes loading. |
| `OnError` | `EventCallback<string>` | — | Raised on load/render errors. |

## Programmatic API

Capture a reference with `@ref` and call:

```razor
<PdfViewer @ref="_viewer" Url="/files/report.pdf" />

@code {
    private PdfViewer? _viewer;

    async Task Demo()
    {
        await _viewer!.GoToPageAsync(3);
        await _viewer.DownloadAsync();
        await _viewer.PrintAsync();
        await _viewer.OpenInNewTabAsync();
        await _viewer.ToggleFullscreenAsync();
        await _viewer.LoadAsync(PdfSource.FromUrl("/files/other.pdf"));
    }
}
```

## Styling

The component exposes CSS variables you can override:

```css
.nbp-viewer {
    --nbp-border: #d0d5dd;
    --nbp-toolbar-bg: #ffffff;
    --nbp-accent: #2563eb;
    --nbp-radius: 8px;
}
```

## SVG rendering (`PdfSvgViewer` + `SvgRenderer`)

For a viewer built from **standard DOM elements** instead of a canvas, use
`PdfSvgViewer`. Each page is rendered to an SVG document in C# — paths become
`<path>`, clips `<clipPath>`, axial/radial shadings become gradients, and text becomes
selectable `<text>` (CID fonts fall back to glyph outlines). Images embed as data
URIs: JPEG/JPEG2000 (`DCTDecode`/`JPXDecode`) are embedded in their original encoding
so the browser decodes them, and all other images are decoded in C# and embedded as
PNG. No JavaScript interop is involved, text is selectable, and zooming is pure vector
scaling.

```razor
@using BlazorPdf

<PdfSvgViewer Data="@bytes" Zoom="1.0" Height="720px" />
```

Get the raw SVG markup for a page (e.g., for server-side rendering or embedding):

```csharp
using BlazorPdf.Engine;
using BlazorPdf.Engine.Svg;

var doc = PdfDocument.Load(bytes);
string svg = SvgRenderer.Render(doc.Pages[0]); // standalone <svg> with a viewBox
```

## Native rendering (`PdfNativeViewer` + `PdfRenderer`)
For a viewer that does **not** rely on the browser's built-in PDF engine at all, use
`PdfNativeViewer`. It rasterizes each page to pixels in C# with `PdfRenderer` and
paints the buffer onto a `<canvas>` via `putImageData` — every pixel is computed by
this library.

```razor
@using BlazorPdf

<PdfNativeViewer Data="@bytes" Scale="1.5" Height="720px" />
```

Render a page to a bitmap directly (e.g., for thumbnails or server-side export):

```csharp
using BlazorPdf.Engine;
using BlazorPdf.Engine.Rendering;

var doc = PdfDocument.Load(bytes);
RenderedImage img = PdfRenderer.Render(doc.Pages[0], scale: 2.0);
// img.Width, img.Height, img.Pixels (RGBA8888, top-down), img.ToBase64()
byte[] png = img.ToPng();   // encode to PNG (8-bit RGBA), pure BCL
```

## Validating against your own PDFs

A console harness renders a folder of PDFs to PNGs and prints a report (pages,
extracted-text length, timings, and per-page errors), which is handy for spotting
rendering gaps:

```bash
dotnet run --project tools/BlazorPdf.Validate -- <input-dir> [output-dir] [scale]
```

If the input folder has no PDFs, it writes a self-test sample so you can confirm the
pipeline, then drop your own files in and re-run.

The rasterizer implements:

- Graphics state stack (`q`/`Q`), CTM (`cm`), device mapping with page rotation.
- Path construction (`m l c v y re h`) with Bézier flattening.
- Painting (`f F f* S s B B* b b* n`) with anti-aliased nonzero/even-odd scanline fill
  and stroke-to-fill conversion, and clipping paths (`W`/`W*`) enforced via a per-state
  coverage mask.
- Color spaces: DeviceGray/RGB/CMYK (`g G rg RG k K`, plus `cs/sc/scn`).
- Axial (type 2) and radial (type 3) shadings via the `sh` operator and shading
  patterns (`/Pattern` color space, PatternType 2), driven by Type 0/2/3 PDF functions.
  Tiling patterns (PatternType 1) are approximated with a neutral fill.
- Transparency: constant fill/stroke alpha (`ca`/`CA` via the `gs` operator) and
  separable blend modes (Multiply, Screen, Overlay, Darken, Lighten, HardLight,
  SoftLight, Difference, Exclusion).
- Image XObjects (`Do`): DeviceGray/RGB/CMYK, ICCBased (by component count), Indexed
  palettes, image masks (stencils) and soft masks (`SMask`), at 1/2/4/8/16 bits per
  component. **Baseline JPEG (`DCTDecode`)** is decoded by a built-in decoder
  (grayscale, YCbCr→RGB, and YCCK/CMYK, with chroma subsampling and restart intervals).
  Form XObjects are executed with their `/Matrix` and `/Resources`.
- Text (`BT/ET`, `Td/TD/Tm/T*`, `Tc/Tw/Tz/TL/Ts/Tf/Tr`, `Tj/TJ/'/"`) with **real glyph
  outlines** from embedded **TrueType** (`FontFile2`) and **CFF / OpenType-CFF**
  (`FontFile3`: Type1C and CIDFontType0C) fonts — the latter via a built-in Type2
  charstring interpreter. Covers simple and Identity-encoded Type0/CID fonts. Advance
  widths come from the font's `/Widths` (or `/W`) array, falling back to `hmtx`.

When a font has no usable embedded outline program (e.g., the standard-14 fonts or
bare Type1 `FontFile`), text falls back to a built-in vector font. Advance widths use
the font's `/Widths` when present, otherwise built-in AFM metrics for the standard-14
families (Helvetica/Arial, Times, Courier), so spacing stays correct.

Current rendering limitations (planned next): bare Type1 (`FontFile`, eexec-encrypted)
outlines, standard-14 glyph shapes, progressive JPEG and JPEG2000/CCITT/JBIG2 image
codecs (`JPXDecode`/`CCITTFaxDecode`/`JBIG2Decode`), tiling patterns, mesh shadings
(types 4-7), soft-mask groups and non-separable blend modes (Hue/Saturation/Color/
Luminosity).

## Pure-C# PDF engine (`BlazorPdf.Engine`)

Alongside the browser-native viewer, the package ships a dependency-free PDF parsing
engine written entirely in C# (built only on the .NET base class library). It powers
features the browser doesn't expose — page metadata, text extraction, and search —
without PDF.js or any native library.

```csharp
using BlazorPdf.Engine;

var doc = PdfDocument.Load(bytes);

int pages = doc.PageCount;
double w = doc.Pages[0].Width;   // points (1/72")
double h = doc.Pages[0].Height;
int rot = doc.Pages[0].Rotation; // 0/90/180/270

string allText = doc.ExtractText();          // pages separated by '\f'
string page1   = doc.Pages[0].ExtractText();

foreach (var hit in doc.Search("invoice"))   // case-insensitive by default
    Console.WriteLine($"page {hit.PageNumber}: {hit.Occurrences} match(es)");
```

Implemented today:

- Resilient loader: rebuilds the object table by scanning the file, tolerating broken
  xref tables and incremental updates; supports classic trailers and xref-stream `/Root`.
- Full tokenizer + object parser (numbers, names, strings, arrays, dictionaries,
  streams, indirect references).
- Stream filters: `FlateDecode` (with PNG/TIFF predictors), `LZWDecode`,
  `ASCIIHexDecode`, `ASCII85Decode`, `RunLengthDecode`.
- Page tree walking with inherited attributes (`MediaBox`, `Resources`, `Rotate`).
- Content-stream text extraction (`Tj`, `TJ`, `'`, `"`, line positioning).

Not yet implemented (tracked for future phases): glyph-level rasterization to pixels,
`/ToUnicode` and CID-font text mapping, image codecs (`DCTDecode`/`JPXDecode`), and
encrypted documents. `PdfDocument.IsEncrypted` flags the last case.

## What this approach can and cannot do

Because rendering is delegated to the browser's native engine, you get reliable
cross-browser display, page navigation, print, and download with zero dependencies.

The browser does **not** expose APIs for programmatic full-text search, a text
selection overlay, page thumbnails, or annotation editing. For text search and
extraction, use the bundled `BlazorPdf.Engine` (see above). A glyph-level
rasterizer that renders pages to pixels — enabling a custom thumbnail/text-layer UI
independent of the browser frame — is the next planned phase.

## License

MIT
