// BlazorPdf - dependency-free interop module.
// Renders PDFs using the browser's built-in engine and manages blob lifetimes,
// printing, downloading and fullscreen. No third-party libraries.

/**
 * Per-instance state keyed by an opaque handle returned from `create`.
 * @type {Map<string, { iframe: HTMLIFrameElement, container: HTMLElement, blobUrl: string|null, dotNet: any }>}
 */
const instances = new Map();

function newId() {
    return 'pdf-' + Math.random().toString(36).slice(2) + Date.now().toString(36);
}

/**
 * Registers a viewer instance.
 * @param {HTMLElement} container Root element of the component.
 * @param {HTMLIFrameElement} iframe The iframe used for rendering.
 * @param {any} dotNet DotNetObjectReference for callbacks (may be null).
 * @returns {string} An instance handle.
 */
export function create(container, iframe, dotNet) {
    const id = newId();
    instances.set(id, { iframe, container, blobUrl: null, dotNet });

    iframe.addEventListener('load', () => {
        // Fires whenever the iframe finishes loading a document (or about:blank).
        const src = iframe.getAttribute('src');
        if (src && src !== 'about:blank' && dotNet) {
            dotNet.invokeMethodAsync('OnDocumentLoadedInternal').catch(() => { });
        }
    });

    return id;
}

function buildFragment(fragment) {
    if (!fragment) return '';
    const parts = [];
    for (const [key, value] of Object.entries(fragment)) {
        if (value === null || value === undefined || value === '') continue;
        parts.push(`${key}=${value}`);
    }
    return parts.length ? '#' + parts.join('&') : '';
}

/**
 * Points the viewer at a URL source.
 * @param {string} id Instance handle.
 * @param {string} url The document URL.
 * @param {object} fragment PDF open-parameter key/values.
 */
export function setUrl(id, url, fragment) {
    const state = instances.get(id);
    if (!state) return;
    revokeBlob(state);
    state.iframe.setAttribute('src', url + buildFragment(fragment));
}

/**
 * Builds a blob URL from base64 bytes and points the viewer at it.
 * @param {string} id Instance handle.
 * @param {string} base64 Base64-encoded PDF bytes.
 * @param {object} fragment PDF open-parameter key/values.
 */
export function setBytes(id, base64, fragment) {
    const state = instances.get(id);
    if (!state) return;
    revokeBlob(state);

    const bytes = base64ToUint8Array(base64);
    const blob = new Blob([bytes], { type: 'application/pdf' });
    const blobUrl = URL.createObjectURL(blob);
    state.blobUrl = blobUrl;
    state.iframe.setAttribute('src', blobUrl + buildFragment(fragment));
}

/** Navigates the built-in viewer to a page by reloading with a #page fragment. */
export function goToPage(id, page) {
    const state = instances.get(id);
    if (!state) return;
    const src = state.iframe.getAttribute('src');
    if (!src || src === 'about:blank') return;
    const base = src.split('#')[0];
    state.iframe.setAttribute('src', `${base}#page=${page}`);
}

/** Triggers a download of the current document. */
export function download(id, fileName) {
    const state = instances.get(id);
    if (!state) return;
    const src = state.iframe.getAttribute('src');
    if (!src || src === 'about:blank') return;

    const a = document.createElement('a');
    a.href = src.split('#')[0];
    a.download = fileName || 'document.pdf';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
}

/** Opens the current document in a new browser tab. */
export function openInNewTab(id) {
    const state = instances.get(id);
    if (!state) return;
    const src = state.iframe.getAttribute('src');
    if (src && src !== 'about:blank') {
        window.open(src.split('#')[0], '_blank', 'noopener');
    }
}

/**
 * Prints the current document via a hidden iframe.
 * Reliable in Chromium; best-effort elsewhere.
 */
export function print(id) {
    const state = instances.get(id);
    if (!state) return;
    const src = state.iframe.getAttribute('src');
    if (!src || src === 'about:blank') return;

    const frame = document.createElement('iframe');
    frame.style.position = 'fixed';
    frame.style.right = '0';
    frame.style.bottom = '0';
    frame.style.width = '0';
    frame.style.height = '0';
    frame.style.border = '0';
    frame.src = src.split('#')[0];
    frame.onload = () => {
        try {
            frame.contentWindow.focus();
            frame.contentWindow.print();
        } catch {
            // Fallback: open in a new tab so the user can print manually.
            window.open(src.split('#')[0], '_blank', 'noopener');
        }
        setTimeout(() => frame.remove(), 60000);
    };
    document.body.appendChild(frame);
}

/** Toggles fullscreen on the component container. */
export function toggleFullscreen(id) {
    const state = instances.get(id);
    if (!state) return;
    if (document.fullscreenElement) {
        document.exitFullscreen?.();
    } else {
        state.container.requestFullscreen?.();
    }
}

/** Releases resources for an instance. */
export function dispose(id) {
    const state = instances.get(id);
    if (!state) return;
    revokeBlob(state);
    instances.delete(id);
}

function revokeBlob(state) {
    if (state.blobUrl) {
        URL.revokeObjectURL(state.blobUrl);
        state.blobUrl = null;
    }
}

function base64ToUint8Array(base64) {
    const binary = atob(base64);
    const len = binary.length;
    const bytes = new Uint8Array(len);
    for (let i = 0; i < len; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}
