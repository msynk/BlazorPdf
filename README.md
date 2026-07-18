**Note**: moved to bit BlazorUI (https://blazorui.bitplatform.dev)

---

# BlazorPdf

A pure-C# PDF viewer component for Blazor. No browser PDF plugin, no native
interop, no commercial SDK.

The rendering engine is written entirely in C#. By default it parses a PDF and
renders each page into plain, positioned HTML/CSS DOM - `<div>` with CSS
`clip-path` for vector fills and clips, `<span>` for selectable text, `<img>`
for rasters, and CSS gradients for shadings. Because text stays as real DOM,
selection, find-in-page and accessibility work for free, and pages prerender
under server-side rendering.

An optional **canvas renderer** (`RenderMode="BlazorPdfRenderMode.Canvas"`, the pdf.js
model) paints the same engine's output onto a per-page `<canvas>` via a compact
display list — far fewer DOM nodes on graphics-heavy documents — while
selection, search and links still work through a DOM text layer, and zoom
changes re-rasterize each page so text stays crisp. Canvas pages need
JavaScript to paint, so they do not prerender.

> **License:** Apache-2.0. See [`LICENSE`](LICENSE).

## Features

**Viewer component**
- Page navigation (prev/next, jump-to-page, scroll-spy current page)
- Zoom: in/out, fit-width, fit-page, actual size
- Rotation, thumbnail sidebar, **bookmarks/outline sidebar**
- Find-in-page (CSS Custom Highlight API) with match navigation
- Native text selection, download, **print**, and fullscreen

**Rendering engine**
- Parsing: cross-reference tables, xref streams, object streams, trailers, `/Prev` chains
- Filters: Flate, LZW, ASCIIHex, ASCII85, RunLength, CCITT (G3/G4) with PNG/TIFF predictors
- Fonts: simple, Type0/CID and **Type3** text; embedded TrueType/OpenType via `@font-face`;
  **Type3 glyphs rendered by executing their content-stream procedures**;
  Core-14 metrics; **base encodings (Standard/WinAnsi/MacRoman/Symbol/ZapfDingbats) + `/Differences`**;
  glyph-name → Unicode (Adobe Glyph List subset, Symbol Greek/math, ZapfDingbats,
  plus `uniXXXX`/`uXXXXXX`); ToUnicode CMaps
- Color: DeviceGray/RGB/CMYK, ICCBased (by component count), Indexed, Separation/DeviceN
  (tint transforms), and **CIE L\*a\*b\*** with white-point conversion
- Color operators: `cs`/`CS` color-space tracking so `sc`/`scn`/`SC`/`SCN` paint
  through the actual current space
- Functions: sampled (type 0, **multi-dimensional input**), exponential (2),
  stitching (3), PostScript calculator (4)
- Paths: fills (nonzero/even-odd), clipping (`W`/`W*`), **strokes as real SVG paths**
  (smooth Béziers, **dash patterns (`d`)**, line caps/joins)
- Images: RGB/Gray/CMYK/Indexed/Separation, JPEG passthrough, CCITT fax, image masks, soft masks
- Shadings: axial (type 2) and radial (type 3) as CSS gradients; shading-pattern fills;
  **tiling patterns (type 1)** replayed as clipped cells across the fill
- Blend modes (`BM`) via CSS `mix-blend-mode`
- Annotations: appearance-stream rendering and clickable URI links
- Document: page tree with inherited **MediaBox/CropBox/Bleed/Trim/Art boxes** and
  rotation; **bookmarks/outline** with destinations resolved to a page plus
  **view parameters (XYZ/Fit/FitH/FitR…)**; **document metadata**
  (`/Info` fields + parsed dates + raw XMP `/Metadata`); **page labels** (`/PageLabels`)
- Security: standard handler decryption (RC4, AES-128/256, revisions 2–6, empty user password)

## Installation

```bash
dotnet add package BlazorPdf
```

Targets `net10.0`.

## Usage

Register nothing special - the component ships its own JS module under
`_content/BlazorPdf/`. Provide a `BlazorPdfSource` built from in-memory bytes:

```razor
@using BlazorPdf

<BlazorPdfViewer Source="@_source"
                 Height="800px"
                 InitialZoomMode="BlazorPdfZoomMode.FitWidth"
                 OnDocumentLoaded="OnLoaded"
                 OnError="OnError" />

@code {
    private BlazorPdfSource? _source;

    protected override async Task OnInitializedAsync()
    {
        byte[] bytes = await File.ReadAllBytesAsync("sample.pdf");
        _source = BlazorPdfSource.FromBytes(bytes, "sample.pdf");
    }

    private void OnLoaded() { /* document ready */ }
    private void OnError(string message) { /* loading/rendering failed */ }
}
```

### Component parameters

| Parameter           | Type                  | Default        | Description                                  |
| ------------------- | --------------------- | -------------- | -------------------------------------------- |
| `Source`            | `BlazorPdfSource?`          | `null`         | The document to display (in-memory bytes).   |
| `Height`            | `string`              | `"780px"`      | CSS height of the viewer container.          |
| `ShowToolbar`       | `bool`                | `true`         | Show the toolbar.                            |
| `InitialZoomMode`   | `BlazorPdfZoomMode`         | `FitWidth`     | Initial zoom behavior.                       |
| `RenderMode`        | `BlazorPdfRenderMode`       | `Html`         | `Canvas` paints page content onto a per-page `<canvas>` from a display list (selection/search/links stay DOM; zoom re-rasterizes for crisp text). Fewer DOM nodes, but requires JS — no prerender. |
| `TextCoalescing`    | `BlazorPdfTextCoalescing`   | `Exact`        | `Compact` merges same-line, same-style text runs into one span per line — far fewer DOM nodes on per-glyph PDFs, with small intra-line position drift (kerning between runs is approximated). Rotated text always stays exact. HTML render mode only. |
| `OnDocumentLoaded`  | `EventCallback`       | -              | Raised after a document loads.               |
| `OnError`           | `EventCallback<string>` | -            | Raised on load/render failure.               |

### Using the engine directly

The parsing/rendering core has no Blazor dependency and can be used on its own:

```csharp
using BlazorPdf;

BlazorPdfDocument doc = BlazorPdfDocument.Load(bytes);

foreach (BlazorPdfPage page in doc.Pages)
{
    string html = new BlazorPdfHtmlRenderer(page, doc.XRef).Render();
    // html is a self-contained, positioned <div> for the page
}

// Document outline (bookmarks), with destinations resolved to page numbers:
foreach (BlazorPdfOutlineItem item in doc.Outline)
{
    Console.WriteLine($"{item.Title} -> page {item.PageNumber}");
}

// Metadata and per-page labels:
Console.WriteLine(doc.Metadata.Title);
Console.WriteLine(doc.Metadata.CreationDate);
Console.WriteLine(doc.PageLabels[0]);  // e.g. "i" or "1"
```

## Project layout

```
src/
  BlazorPdf/           # the library (engine + Blazor component)
    Core/              # pure-C# PDF engine (no Blazor dependency)
    BlazorPdfViewer.*  # the Razor component, code-behind, CSS, JS
  BlazorPdf.Demo/      # sample Blazor host
  BlazorPdf.Tests/     # xUnit test suite
```

See [`src/BlazorPdf/README.md`](src/BlazorPdf/README.md) for the full module map.

## Building and testing

```bash
# from the repo root
dotnet build src/BlazorPdf.slnx

# run the test suite
dotnet test src/BlazorPdf.Tests/BlazorPdf.Tests.csproj
```

Tests use hand-built, spec-valid PDFs (`TestPdf`) to exercise parsing, encodings,
color spaces, fonts, rendering operators and outline resolution end-to-end.

## Limitations

These degrade gracefully - affected pages still load:

- **Bare CFF/Type1 embedded fonts** render via substitute fonts with correct
  Unicode rather than the embedded glyph outlines. (Type3 fonts now render from
  their glyph procedures.)
- **JBIG2 and JPEG2000** images are not decoded and are skipped.
- **Mesh shadings (types 4–7) and function-based shadings (type 1)** fall back to
  a solid color.
- **Password-protected documents** (non-empty user password) cannot be opened;
  the standard-handler crypto also requires a non-WASM host.

## Contributing

When adding a feature, keep the `Core` engine free of Blazor dependencies and
add tests under `BlazorPdf.Tests`.

## License

Distributed under the **Apache License 2.0**. See [`LICENSE`](LICENSE).
