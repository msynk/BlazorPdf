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
| Tiling pattern fills                    | `HtmlRenderer` (falls back to solid color)        | Partial |
| Blend modes (`BM`)                      | `HtmlRenderer` `mix-blend-mode`                   | Done   |
| CCITTFaxDecode                          | `Core/Filters/CcittFaxDecoder.cs` (G3/G4)         | Done   |
| Encryption (standard handler)           | `Core/Security/*` (RC4, AES-128/256, R2–R6)       | Done   |
| Annotations                             | `HtmlRenderer` annotation pass (appearances + links) | Done |
| Embedded font programs                  | `PdfFont` + `@font-face` emission (TrueType/OpenType) | Done |
| Page rendering                          | `Core/Render/HtmlRenderer.cs`                     | Mostly |

Working today: parsing (tables + xref streams + object streams), Flate/LZW/ASCII
/RunLength/CCITT(G3/G4) filters with predictors, the page tree, content-stream
operators, simple and Type0 font text extraction (ToUnicode + WinAnsi + Core-14
metrics), HTML output with vector paths (CSS `clip-path`) and selectable text
(positioned `<span>`s), embedded TrueType/
OpenType fonts (emitted as `@font-face`) with serif/sans/mono substitution and
bold/italic otherwise, image XObjects and inline images (RGB/Gray/CMYK/Indexed/
Separation color, JPEG passthrough, CCITT fax, soft masks), form XObjects,
axial/radial shadings (`sh`) and shading pattern fills (`scn`) as CSS
gradients, separable blend modes (`BM`), clipping paths
(`W`/`W*`), decryption of documents secured with the standard handler (RC4 and
AES, revisions 2–6, empty user password), and annotations (appearance-stream
rendering plus clickable URI links).

Known limitations (degrade gracefully - pages still load):
- **Bare CFF/Type1 embedded programs**: text renders via substitute fonts with
  correct Unicode rather than the embedded glyph outlines (a browser cannot load
  a bare CFF/PFB without OpenType wrapping and a synthetic cmap).
- **JBIG2 and JPEG2000 images**: not decoded (these are large, specialized
  codecs - an arithmetic coder + region/symbol decoding for JBIG2, a wavelet/
  EBCOT pipeline for JPEG2000); such images are skipped rather than rendered.

Not yet implemented: embedded font glyph outlines (text uses host/substitute fonts),
JPEG2000/CCITT/JBIG2 image codecs, tiling/shading pattern fills (`scn`
patterns), and blend modes.

## Component

```razor
<BlazorPdfViewer Source="@source" />
```

## License

Distributed under the **Apache License 2.0**.
