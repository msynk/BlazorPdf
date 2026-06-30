# BlazorPdf

A pure-C# PDF viewer component for Blazor.

The rendering engine is a **clean-room C# port of [Mozilla pdf.js](https://github.com/mozilla/pdf.js)**.
Like pdf.js, it parses PDF files and renders their content into plain DOM
elements (HTML/CSS/SVG) produced by Blazor components — no `<canvas>` pixel
blitting, no browser PDF plugin, no commercial SDK.

## Status

This project is being built incrementally by porting pdf.js module-by-module.

| pdf.js module                          | BlazorPdf equivalent                              | Status |
| -------------------------------------- | ------------------------------------------------- | ------ |
| `core/primitives.js`                   | `Core/Primitives.cs`                              | Done   |
| `core/base_stream.js`, `stream.js`     | `Core/BaseStream.cs`, `Core/PdfStream.cs`         | Done   |
| `core/parser.js` (Lexer/Parser)        | `Core/Lexer.cs`, `Core/Parser.cs`                 | Done   |
| filters (Flate/LZW/ASCII/RunLength)    | `Core/Filters/*`                                  | Done   |
| `core/xref.js`                         | `Core/XRef.cs`                                    | Done   |
| `core/document.js`, `catalog.js`, `page.js` | `Core/PdfDocument.cs`, `Core/PdfPage.cs`     | Done   |
| `core/evaluator.js` (content ops)      | `Core/Content/*`                                  | Done   |
| `core/function.js`                     | `Core/Functions/*` (types 0/2/3/4)                | Done   |
| `core/colorspace.js`                   | `Core/Render/ColorSpace.cs`                       | Done   |
| `core/image.js` (raster)               | `Core/Render/PdfImage.cs` + `PngEncoder.cs`       | Done   |
| `core/pattern.js` (axial/radial)       | `Core/Render/ShadingBuilder.cs`                   | Done   |
| `core/pattern.js` (tiling/shading fill)| `SvgRenderer` `scn` patterns (`<pattern>`/gradient) | Done |
| blend modes (`BM`)                     | `SvgRenderer` `mix-blend-mode`                    | Done   |
| `core/ccitt.js` (CCITTFaxDecode)       | `Core/Filters/CcittFaxDecoder.cs` (G3/G4)         | Done   |
| `core/crypto.js` (standard handler)    | `Core/Security/*` (RC4, AES-128/256, R2–R6)       | Done   |
| `core/annotation.js`                   | `SvgRenderer` annotation pass (appearances + links) | Done |
| `core/fonts.js` (embedded programs)    | `PdfFont` + `@font-face` emission (TrueType/OpenType) | Done |
| `display/svg.js`                       | `Core/Render/SvgRenderer.cs`                      | Mostly |

Working today: parsing (tables + xref streams + object streams), Flate/LZW/ASCII
/RunLength/CCITT(G3/G4) filters with predictors, the page tree, content-stream
operators, simple and Type0 font text extraction (ToUnicode + WinAnsi + Core-14
metrics), SVG output with vector paths and selectable text, embedded TrueType/
OpenType fonts (emitted as `@font-face`) with serif/sans/mono substitution and
bold/italic otherwise, image XObjects and inline images (RGB/Gray/CMYK/Indexed/
Separation color, JPEG passthrough, CCITT fax, soft masks), form XObjects,
axial/radial shadings (`sh`) and tiling/shading pattern fills (`scn`) as SVG
gradients and `<pattern>`s, separable blend modes (`BM`), clipping paths
(`W`/`W*`), decryption of documents secured with the standard handler (RC4 and
AES, revisions 2–6, empty user password), and annotations (appearance-stream
rendering plus clickable URI links).

Known limitations (degrade gracefully — pages still load):
- **Bare CFF/Type1 embedded programs**: text renders via substitute fonts with
  correct Unicode rather than the embedded glyph outlines (a browser cannot load
  a bare CFF/PFB without OpenType wrapping and a synthetic cmap).
- **JBIG2 and JPEG2000 images**: not decoded (these are large, specialized
  codecs — an arithmetic coder + region/symbol decoding for JBIG2, a wavelet/
  EBCOT pipeline for JPEG2000); such images are skipped rather than rendered.

Not yet ported: embedded font glyph outlines (text uses host/substitute fonts),
JPEG2000/CCITT/JBIG2 image codecs, tiling/shading pattern fills (`scn`
patterns), and blend modes.

## Component

```razor
<BlazorPdfViewer Source="@source" />
```

## License & attribution

The engine is a derivative work of pdf.js and is distributed under the
**Apache License 2.0**. See the `NOTICE` file for attribution details.
