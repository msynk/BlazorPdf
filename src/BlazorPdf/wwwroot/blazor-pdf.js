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
    }
}

export function registerScrollSpy(container, dotnetRef) {
    if (!container) {
        return;
    }
    disposeScrollSpy(container);

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

    // Notify .NET when the container resizes (used for fit-to-width/page).
    if (typeof ResizeObserver !== "undefined") {
        const resize = new ResizeObserver(() => {
            dotnetRef.invokeMethodAsync("OnViewportResized");
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
    if (container.__bpfResize) {
        container.__bpfResize.disconnect();
        container.__bpfResize = null;
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
        const walker = document.createTreeWalker(page, NodeFilter.SHOW_TEXT);
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
