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
