# BlazorPdf

A pure-C# PDF viewer component for Blazor.

The rendering engine is written entirely in C#. It parses PDF files and renders
their content into plain HTML/CSS DOM elements (positioned
`<div>`/`<span>`/`<img>`) produced by Blazor components - no `<canvas>` pixel
blitting, no browser PDF plugin, no commercial SDK.

## Status

The engine is being built incrementally, module by module.

| Engine area                             | BlazorPdf module                                  | Status |
| --------------------------------------- | ------------------------------------------------- | ------ |
| Object primitives                       | `Core/Primitives.cs`                              | Done   |
| Byte streams                            | `Core/BaseStream.cs`, `Core/PdfStream.cs`         | Done   |
| Lexer / parser                          | `Core/Lexer.cs`, `Core/Parser.cs`                 | Done   |
| Filters (Flate/LZW/ASCII/RunLength)     | `Core/Filters/*`                                  | Done   |
| Cross-reference tables and streams      | `Core/XRef.cs`                                    | Done   |
| Document, catalog and page tree         | `Core/PdfDocument.cs`, `Core/PdfPage.cs`          | Done   |
| Content-stream operators                | `Core/Content/*`                                  | Done   |
| Functions (types 0/2/3/4)               | `Core/Functions/*`                                | Done   |
| Color spaces                            | `Core/Render/ColorSpace.cs`                       | Done   |
| Raster images                           | `Core/Render/PdfImage.cs` + `PngEncoder.cs`       | Done   |
| Axial/radial shadings                   | `Core/Render/CssShadingBuilder.cs`                | Done   |
| Shading pattern fills                   | `HtmlRenderer` `scn` shading patterns (CSS gradients) | Done |
| Tiling pattern fills                    | `HtmlRenderer` `RunPatternCell` (replayed cells)  | Done   |
| Baseline JPEG decoder (CMYK/masked)     | `Core/Filters/JpegDecoder.cs`                     | Done   |
| Optional content groups (layers)        | `HtmlRenderer` `BDC /OC … EMC`                    | Done   |
| Damaged-file recovery                   | `XRef` object-scan rebuild + `PdfDocument.Warnings` | Done |
| TrueType sfnt sanitizer                 | `Core/Fonts/TrueTypeSanitizer.cs`                 | Done   |
| Type0 embedded CMap (code→CID)          | `Core/Fonts/CMap.cs`                              | Done   |
| Text extraction / search index          | `Core/Content/TextExtractor.cs`, `PdfPage.ExtractText()` | Done |
| Internal GoTo links                     | `HtmlRenderer` link overlay + viewer navigation   | Done   |
| Tagged-PDF structure tree               | `PdfDocument.StructureTree`                       | Done   |
| AcroForm field extraction               | `PdfDocument.FormFields`                          | Done   |
| Blend modes (`BM`)                      | `HtmlRenderer` `mix-blend-mode`                   | Done   |
| CCITTFaxDecode                          | `Core/Filters/CcittFaxDecoder.cs` (G3/G4)         | Done   |
| Encryption (standard handler)           | `Core/Security/*` (RC4, AES-128/256, R2–R6, user/owner password, managed crypto for WASM) | Done |
| Annotations                             | `HtmlRenderer` annotation pass (appearances + links) | Done |
| Embedded font programs                  | `PdfFont` + `@font-face` emission (TrueType/OpenType) | Done |
| Page rendering                          | `Core/Render/HtmlRenderer.cs`                     | Mostly |

Working today: parsing (tables + xref streams + object streams) with **damaged-file
recovery** (a corrupt cross-reference table is rebuilt by scanning the file, with
diagnostics on `PdfDocument.Warnings`), Flate/LZW/ASCII/RunLength/CCITT(G3/G4,
including 2-D vertical modes and EOL/K>0) filters with predictors, the page tree,
content-stream operators (with per-operator error recovery), simple and Type0 font
text extraction (ToUnicode + WinAnsi + Core-14 metrics), HTML output with vector
paths (CSS `clip-path`) and selectable text (positioned `<span>`s, including
invisible OCR-layer text), embedded TrueType/OpenType fonts (emitted once per
document as `@font-face`) with serif/sans/mono substitution and bold/italic
otherwise, image XObjects and inline images (RGB/Gray/CMYK/Indexed/Separation/Lab
color, a baseline **JPEG decoder** for CMYK/masked images plus browser passthrough
for plain JPEG, CCITT fax, `/Decode`, colour-key and stencil `/Mask`, soft masks),
form XObjects (with `/BBox` clipping), axial/radial shadings (`sh`) and shading
pattern fills (`scn`) as CSS gradients, tiling patterns, optional content groups
(layer visibility), separable blend modes (`BM`), clipping paths (`W`/`W*`),
decryption of documents secured with the standard handler (RC4 and AES, revisions
2–6, **user and owner password verification**, working in Blazor WebAssembly via a
managed MD5/AES implementation), and annotations (appearance-stream rendering plus
scheme-whitelisted clickable URI links).

## Limitations

These degrade gracefully — affected pages still load:

- **Bare CFF/Type1 embedded programs**: the glyph *shapes* render via a substitute
  font (a browser cannot load a bare CFF/PFB without OpenType wrapping and a
  synthetic cmap), but **spacing stays correct** — each text run is scaled
  horizontally to its exact PDF advance (the pdf.js text-layer technique), so
  substitute-font pages (e.g. LaTeX/Computer Modern) read correctly even though the
  letterforms differ. Embedded TrueType/OpenType fonts are emitted directly, after
  sfnt-structure sanitization (directory sort, checksum/padding repair) so subset
  fonts pass the browser's strict font parser.
- **JBIG2 and JPEG2000 images**: not decoded (large, specialized codecs); such
  images are skipped rather than rendered as noise.
- **Progressive JPEG with CMYK**: the built-in decoder handles baseline sequential
  DCT; progressive JPEGs fall back to browser passthrough (fine for RGB, wrong for
  CMYK).
- **Two-circle radial shadings** are approximated with a single-circle CSS radial
  gradient; **knockout transparency groups** are treated as non-knockout; and
  **anisotropic stroke widths** under a skewed CTM use SVG's uniform scaling.
- **CJK / composite fonts without ToUnicode** may not extract text without the
  predefined Adobe CMap set.

Browser support: text selection highlighting uses the CSS Custom Highlight API
where available and falls back to `<mark>` overlays otherwise.

## Component

```razor
<BlazorPdfViewer Source="@source" />
```

## License

Distributed under the **Apache License 2.0**.
