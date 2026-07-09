// Minimal browser glue for the BlazorPdfViewer component: page scrolling,
// viewport measurement, current-page tracking, download and fullscreen.
// This is the only JavaScript in BlazorPdf and ships with the component.

export function getViewport(container) {
    if (!container) {
        return { width: 0, height: 0 };
    }
    return { width: container.clientWidth, height: container.clientHeight };
}

export function scrollToPage(container, pageNumber) {
    if (!container) {
        return;
    }
    const target = container.querySelector(`[data-page='${pageNumber}']`);
    if (target) {
        target.scrollIntoView({ behavior: "smooth", block: "start" });
        // Render the destination immediately so jumps don't land on a placeholder.
        scheduleRender(container, container.__bpfDotnet);
    }
}

// Throttles render passes to one per animation frame.
function scheduleRender(container, dotnetRef) {
    if (!container || !dotnetRef || container.__bpfRenderScheduled) {
        return;
    }
    container.__bpfRenderScheduled = true;
    requestAnimationFrame(() => {
        container.__bpfRenderScheduled = false;
        renderVisiblePages(container, dotnetRef);
    });
}

// Computes which pages intersect the viewport (expanded by a buffer) and asks
// .NET to render any that are not rendered yet.
function renderVisiblePages(container, dotnetRef) {
    const pages = container.querySelectorAll("[data-page]");
    if (!pages.length) {
        return;
    }
    const rect = container.getBoundingClientRect();
    const buffer = Math.max(container.clientHeight * 1.5, 800);
    const lo = rect.top - buffer;
    const hi = rect.bottom + buffer;

    const needed = [];
    for (const page of pages) {
        const r = page.getBoundingClientRect();
        if (r.bottom >= lo && r.top <= hi) {
            const n = parseInt(page.getAttribute("data-page"), 10);
            if (!Number.isNaN(n)) {
                needed.push(n);
            }
        }
    }
    if (needed.length) {
        dotnetRef.invokeMethodAsync("EnsurePagesRendered", needed);
    }
}

export function registerScrollSpy(container, dotnetRef) {
    if (!container) {
        return;
    }
    disposeScrollSpy(container);
    container.__bpfDotnet = dotnetRef;

    const ratios = new Map();
    const observer = new IntersectionObserver(
        (entries) => {
            for (const entry of entries) {
                ratios.set(entry.target, entry.isIntersecting ? entry.intersectionRatio : 0);
            }
            let best = null;
            let bestRatio = 0;
            for (const [el, ratio] of ratios) {
                if (ratio > bestRatio) {
                    bestRatio = ratio;
                    best = el;
                }
            }
            if (best) {
                const n = parseInt(best.getAttribute("data-page"), 10);
                if (!Number.isNaN(n)) {
                    dotnetRef.invokeMethodAsync("OnPageVisible", n);
                }
            }
        },
        { root: container, threshold: [0, 0.25, 0.5, 0.75, 1] }
    );

    container.querySelectorAll("[data-page]").forEach((p) => observer.observe(p));
    container.__bpfObserver = observer;

    // Lazy rendering: on every scroll (throttled to animation frames) work out
    // which pages fall within the viewport plus a generous buffer and ask .NET
    // to render any that are still placeholders. This fills the surface ahead of
    // the user like the browser's built-in viewer, instead of rendering the whole
    // document up front. A geometry check is used (rather than a second
    // IntersectionObserver with rootMargin) because it fires reliably on every
    // scroll for an element scroll-container.
    const onScroll = () => scheduleRender(container, dotnetRef);
    container.addEventListener("scroll", onScroll, { passive: true });
    container.__bpfScroll = onScroll;
    scheduleRender(container, dotnetRef); // initial fill

    // Delegate clicks on internal-link hotspots ([data-bp-page]) to page nav.
    const onClick = (e) => {
        const hot = e.target.closest && e.target.closest("[data-bp-page]");
        if (hot) {
            const n = parseInt(hot.getAttribute("data-bp-page"), 10);
            if (!Number.isNaN(n)) {
                e.preventDefault();
                scrollToPage(container, n);
                dotnetRef.invokeMethodAsync("OnPageVisible", n);
            }
        }
    };
    container.addEventListener("click", onClick);
    container.__bpfClick = onClick;

    // Ctrl+wheel (and pinch, which browsers report as ctrl+wheel) zooms.
    const onWheel = (e) => {
        if (e.ctrlKey) {
            e.preventDefault();
            dotnetRef.invokeMethodAsync("OnWheelZoom", e.deltaY);
        }
    };
    container.addEventListener("wheel", onWheel, { passive: false });
    container.__bpfWheel = onWheel;

    // Notify .NET when the container resizes (used for fit-to-width/page).
    if (typeof ResizeObserver !== "undefined") {
        const resize = new ResizeObserver(() => {
            dotnetRef.invokeMethodAsync("OnViewportResized");
            scheduleRender(container, dotnetRef);
        });
        resize.observe(container);
        container.__bpfResize = resize;
    }
}

export function disposeScrollSpy(container) {
    if (!container) {
        return;
    }
    clearSearch(container);
    if (container.__bpfObserver) {
        container.__bpfObserver.disconnect();
        container.__bpfObserver = null;
    }
    if (container.__bpfScroll) {
        container.removeEventListener("scroll", container.__bpfScroll);
        container.__bpfScroll = null;
    }
    if (container.__bpfClick) {
        container.removeEventListener("click", container.__bpfClick);
        container.__bpfClick = null;
    }
    if (container.__bpfWheel) {
        container.removeEventListener("wheel", container.__bpfWheel);
        container.__bpfWheel = null;
    }
    container.__bpfDotnet = null;
    if (container.__bpfResize) {
        container.__bpfResize.disconnect();
        container.__bpfResize = null;
    }
}

// ----- Thumbnail sidebar (its own lazy-render cycle) -----
//
// The sidebar renders thumbnails on demand as they scroll into its own viewport,
// independent of the main surface — mirroring pdf.js's PDFThumbnailViewer. Its
// element only exists in the DOM while the panel is open, so registration is
// driven from .NET after that element renders.

function scheduleThumbRender(container, dotnetRef) {
    if (!container || !dotnetRef || container.__bpfThumbScheduled) {
        return;
    }
    container.__bpfThumbScheduled = true;
    requestAnimationFrame(() => {
        container.__bpfThumbScheduled = false;
        renderVisibleThumbs(container, dotnetRef);
    });
}

// Works out which thumbnails fall within the sidebar viewport (plus a buffer)
// and asks .NET to render any that are still placeholders.
function renderVisibleThumbs(container, dotnetRef) {
    const thumbs = container.querySelectorAll("[data-thumb]");
    if (!thumbs.length) {
        return;
    }
    const rect = container.getBoundingClientRect();
    const buffer = Math.max(container.clientHeight, 400);
    const lo = rect.top - buffer;
    const hi = rect.bottom + buffer;

    const needed = [];
    for (const thumb of thumbs) {
        const r = thumb.getBoundingClientRect();
        if (r.bottom >= lo && r.top <= hi) {
            const n = parseInt(thumb.getAttribute("data-thumb"), 10);
            if (!Number.isNaN(n)) {
                needed.push(n);
            }
        }
    }
    if (needed.length) {
        dotnetRef.invokeMethodAsync("EnsureThumbsRendered", needed);
    }
}

export function registerThumbSpy(container, dotnetRef) {
    if (!container) {
        return;
    }
    disposeThumbSpy(container);
    container.__bpfThumbDotnet = dotnetRef;
    const onScroll = () => scheduleThumbRender(container, dotnetRef);
    container.addEventListener("scroll", onScroll, { passive: true });
    container.__bpfThumbScroll = onScroll;
    scheduleThumbRender(container, dotnetRef); // initial fill
}

export function disposeThumbSpy(container) {
    if (!container) {
        return;
    }
    if (container.__bpfThumbScroll) {
        container.removeEventListener("scroll", container.__bpfThumbScroll);
        container.__bpfThumbScroll = null;
    }
    container.__bpfThumbDotnet = null;
    container.__bpfThumbScheduled = false;
}

// Keeps the active thumbnail visible in the sidebar as the current page changes.
// Scrolls the minimum amount (block:"nearest") so it never fights the user, then
// nudges the lazy renderer to fill anything the scroll brought into view.
export function scrollThumbIntoView(container, pageNumber) {
    if (!container) {
        return;
    }
    const target = container.querySelector(`[data-thumb='${pageNumber}']`);
    if (target) {
        target.scrollIntoView({ block: "nearest" });
        if (container.__bpfThumbDotnet) {
            scheduleThumbRender(container, container.__bpfThumbDotnet);
        }
    }
}

export function download(fileName, base64) {
    const link = document.createElement("a");
    link.href = "data:application/pdf;base64," + base64;
    link.download = fileName || "document.pdf";
    document.body.appendChild(link);
    link.click();
    link.remove();
}

// Streams the document bytes from .NET (via a DotNetStreamReference) into a Blob
// and triggers a download, avoiding a multi-megabyte base64 string over SignalR.
export async function downloadStream(fileName, streamRef) {
    const buffer = await streamRef.arrayBuffer();
    const blob = new Blob([buffer], { type: "application/pdf" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName || "document.pdf";
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10000);
}

// Corrects each text run's horizontal extent to the PDF-computed advance stored
// in its data-w attribute. Because runs are laid out with a substitute font when
// the real font isn't embedded, their natural width differs from the PDF's; this
// scales each run (via the --bp-sx custom property consumed by its transform) so
// it occupies exactly its intended advance, restoring correct word spacing.
export async function correctTextWidths(container) {
    if (!container) {
        return;
    }
    // Wait for any @font-face fonts to load so measurements are stable.
    try {
        if (document.fonts && document.fonts.ready) {
            await document.fonts.ready;
        }
    } catch (e) {
        /* ignore */
    }

    const spans = Array.prototype.slice.call(container.querySelectorAll("span[data-w]"));
    // Batch all reads before all writes to avoid layout thrashing.
    const naturalWidths = spans.map((s) => s.offsetWidth);
    for (let i = 0; i < spans.length; i++) {
        const target = parseFloat(spans[i].getAttribute("data-w"));
        const natural = naturalWidths[i];
        if (natural > 0 && target > 0) {
            spans[i].style.setProperty("--bp-sx", (target / natural).toString());
        }
    }
}

export function toggleFullscreen(element) {
    if (!element) {
        return;
    }
    if (document.fullscreenElement) {
        document.exitFullscreen();
    } else if (element.requestFullscreen) {
        element.requestFullscreen();
    }
}

// Prints the rendered pages at their true physical size by cloning each page
// into a hidden iframe (one sheet per page) and invoking the browser dialog.
export function printDocument(container) {
    if (!container) {
        return;
    }
    const pages = container.querySelectorAll("[data-page] .bp-html-page");
    if (!pages.length) {
        return;
    }

    const frame = document.createElement("iframe");
    frame.setAttribute("aria-hidden", "true");
    frame.style.cssText = "position:fixed;right:0;bottom:0;width:0;height:0;border:0";
    document.body.appendChild(frame);

    const doc = frame.contentDocument || frame.contentWindow.document;
    doc.open();
    doc.write(
        "<!DOCTYPE html><html><head><meta charset='utf-8'><style>" +
        "@page{margin:0}html,body{margin:0;padding:0;background:#fff}" +
        ".bpf-sheet{position:relative;overflow:hidden;page-break-after:always;break-after:page}" +
        ".bpf-sheet:last-child{page-break-after:auto;break-after:auto}" +
        "</style></head><body></body></html>");
    doc.close();

    const ptToPx = 96 / 72; // PDF points to CSS pixels for physical-size output
    pages.forEach((inner) => {
        const w = parseFloat(inner.style.width) || 0;
        const h = parseFloat(inner.style.height) || 0;
        const sheet = doc.createElement("div");
        sheet.className = "bpf-sheet";
        sheet.style.width = (w * ptToPx).toFixed(2) + "px";
        sheet.style.height = (h * ptToPx).toFixed(2) + "px";
        sheet.innerHTML = inner.outerHTML;
        const clone = sheet.firstElementChild;
        if (clone) {
            clone.style.transform = "scale(" + ptToPx + ")";
            clone.style.transformOrigin = "top left";
        }
        doc.body.appendChild(sheet);
    });

    const cleanup = () => setTimeout(() => frame.remove(), 1000);
    if (frame.contentWindow) {
        frame.contentWindow.addEventListener("afterprint", cleanup, { once: true });
    }
    // Allow a tick for fonts/images to lay out before printing.
    setTimeout(() => {
        try {
            frame.contentWindow.focus();
            frame.contentWindow.print();
        } catch {
            frame.remove();
        }
    }, 300);
}

// ----- Text search (CSS Custom Highlight API) -----

function ensureSearchStyles() {
    if (document.getElementById("bpf-search-style")) {
        return;
    }
    const style = document.createElement("style");
    style.id = "bpf-search-style";
    style.textContent =
        "::highlight(bpf-search){background:#ffe066;color:#000}" +
        "::highlight(bpf-search-current){background:#ff8f00;color:#000}";
    document.head.appendChild(style);
}

function searchSupported() {
    return typeof Highlight !== "undefined" && typeof CSS !== "undefined" && !!CSS.highlights;
}

function locate(nodes, pos) {
    for (const entry of nodes) {
        if (pos >= entry.start && pos <= entry.start + entry.node.nodeValue.length) {
            return { node: entry.node, offset: pos - entry.start };
        }
    }
    return null;
}

function buildRange(nodes, start, end) {
    const a = locate(nodes, start);
    const b = locate(nodes, end);
    if (!a || !b) {
        return null;
    }
    const range = document.createRange();
    try {
        range.setStart(a.node, a.offset);
        range.setEnd(b.node, b.offset);
    } catch {
        return null;
    }
    return range;
}

// Finds every case-insensitive occurrence of `query` across all pages and
// registers a highlight. Returns the match count, or -1 if unsupported.
export function searchAll(container, query) {
    clearSearch(container);
    if (!container || !query) {
        return 0;
    }
    if (!searchSupported()) {
        return -1;
    }
    ensureSearchStyles();

    const needle = query.toLowerCase();
    const ranges = [];

    container.querySelectorAll("[data-page]").forEach((page) => {
        // Skip the painted-glyph layer ([data-bp-glyph]) — it holds Private-Use
        // codepoints, not real text; search the selectable/real-Unicode spans.
        const walker = document.createTreeWalker(page, NodeFilter.SHOW_TEXT, {
            acceptNode(n) {
                return n.parentElement && n.parentElement.hasAttribute("data-bp-glyph")
                    ? NodeFilter.FILTER_REJECT
                    : NodeFilter.FILTER_ACCEPT;
            },
        });
        const nodes = [];
        let text = "";
        let node;
        while ((node = walker.nextNode())) {
            nodes.push({ node, start: text.length });
            text += node.nodeValue;
        }
        const haystack = text.toLowerCase();
        let idx = haystack.indexOf(needle);
        while (idx !== -1) {
            const range = buildRange(nodes, idx, idx + needle.length);
            if (range) {
                ranges.push(range);
            }
            idx = haystack.indexOf(needle, idx + needle.length);
        }
    });

    container.__bpfRanges = ranges;
    if (ranges.length) {
        CSS.highlights.set("bpf-search", new Highlight(...ranges));
    }
    return ranges.length;
}

// Marks the match at `index` as current and scrolls it into view.
export function gotoMatch(container, index) {
    const ranges = container && container.__bpfRanges;
    if (!ranges || !ranges.length || !searchSupported()) {
        return;
    }
    const i = ((index % ranges.length) + ranges.length) % ranges.length;
    const range = ranges[i];
    CSS.highlights.set("bpf-search-current", new Highlight(range));
    const el = range.startContainer.parentElement;
    if (el) {
        el.scrollIntoView({ behavior: "smooth", block: "center" });
    }
}

export function clearSearch(container) {
    if (searchSupported()) {
        CSS.highlights.delete("bpf-search");
        CSS.highlights.delete("bpf-search-current");
    }
    if (container) {
        container.__bpfRanges = null;
    }
}
