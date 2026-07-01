# BlazorPdf

A pure-C# PDF viewer component for Blazor. No `<canvas>` pixel blitting, no browser
PDF plugin, no native interop, no commercial SDK.

The rendering engine is a **clean-room C# port of [Mozilla pdf.js](https://github.com/mozilla/pdf.js)**.
It parses a PDF and renders each page into plain, positioned HTML/CSS DOM —
`<div>` with CSS `clip-path` for vector fills and clips, `<span>` for selectable
text, `<img>` for rasters, and CSS gradients for shadings. Because text stays as
real DOM, selection, find-in-page and accessibility work for free.

> **License:** Apache-2.0. The engine is a derivative work of pdf.js; see
> [`NOTICE`](src/BlazorPdf/NOTICE) for attribution.

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
- Paths: fills (nonzero/even-odd), clipping (`W`/`W*`), strokes, **dash patterns (`d`)**,
  line cap/join tracking
- Images: RGB/Gray/CMYK/Indexed/Separation, JPEG passthrough, CCITT fax, image masks, soft masks
- Shadings: axial (type 2) and radial (type 3) as CSS gradients; shading-pattern fills
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

Register nothing special — the component ships its own JS module under
`_content/BlazorPdf/`. Provide a `PdfSource` built from in-memory bytes:

```razor
@using BlazorPdf

<BlazorPdfViewer Source="@_source"
                 Height="800px"
                 InitialZoomMode="PdfZoomMode.FitWidth"
                 OnDocumentLoaded="OnLoaded"
                 OnError="OnError" />

@code {
    private PdfSource? _source;

    protected override async Task OnInitializedAsync()
    {
        byte[] bytes = await File.ReadAllBytesAsync("sample.pdf");
        _source = PdfSource.FromBytes(bytes, "sample.pdf");
    }

    private void OnLoaded() { /* document ready */ }
    private void OnError(string message) { /* loading/rendering failed */ }
}
```

### Component parameters

| Parameter           | Type                  | Default        | Description                                  |
| ------------------- | --------------------- | -------------- | -------------------------------------------- |
| `Source`            | `PdfSource?`          | `null`         | The document to display (in-memory bytes).   |
| `Height`            | `string`              | `"780px"`      | CSS height of the viewer container.          |
| `ShowToolbar`       | `bool`                | `true`         | Show the toolbar.                            |
| `InitialZoomMode`   | `PdfZoomMode`         | `FitWidth`     | Initial zoom behavior.                       |
| `OnDocumentLoaded`  | `EventCallback`       | —              | Raised after a document loads.               |
| `OnError`           | `EventCallback<string>` | —            | Raised on load/render failure.               |

### Using the engine directly

The parsing/rendering core has no Blazor dependency and can be used on its own:

```csharp
using BlazorPdf.Core;
using BlazorPdf.Core.Render;

PdfDocument doc = PdfDocument.Load(bytes);

foreach (PdfPage page in doc.Pages)
{
    string html = new HtmlRenderer(page, doc.XRef).Render();
    // html is a self-contained, positioned <div> for the page
}

// Document outline (bookmarks), with destinations resolved to page numbers:
foreach (OutlineItem item in doc.Outline)
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
    Core/              # pure-C# pdf.js port (no Blazor dependency)
    BlazorPdfViewer.*  # the Razor component, code-behind, CSS, JS
  BlazorPdf.Demo/      # sample Blazor host
  BlazorPdf.Tests/     # xUnit test suite
```

The `Core` namespace mirrors pdf.js module-for-module (`Core/Parser.cs` ≈
`core/parser.js`, `Core/Render/ColorSpace.cs` ≈ `core/colorspace.js`, and so on).
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

These degrade gracefully — affected pages still load:

- **Bare CFF/Type1 embedded fonts** render via substitute fonts with correct
  Unicode rather than the embedded glyph outlines. (Type3 fonts now render from
  their glyph procedures.)
- **JBIG2 and JPEG2000** images are not decoded and are skipped.
- **Mesh shadings (types 4–7), function-based shadings (type 1) and tiling
  patterns** fall back to a solid color.
- **Password-protected documents** (non-empty user password) cannot be opened;
  the standard-handler crypto also requires a non-WASM host.
- Strokes are approximated by filled outlines (caps/joins are not pixel-exact).

## Contributing

The project is being built by porting pdf.js incrementally. When adding a
feature, mirror the corresponding pdf.js module, keep the `Core` engine free of
Blazor dependencies, and add tests under `BlazorPdf.Tests`.

## License & attribution

Distributed under the **Apache License 2.0**. The rendering engine is a
clean-room derivative of [Mozilla pdf.js](https://github.com/mozilla/pdf.js)
(Copyright Mozilla Foundation, Apache-2.0). See [`NOTICE`](src/BlazorPdf/NOTICE)
and [`LICENSE`](LICENSE).
