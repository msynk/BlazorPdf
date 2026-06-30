// BlazorPdf native renderer interop.
// Paints a C#-produced RGBA buffer onto a <canvas> via putImageData.
// These are OUR pixels, computed in C# - the browser's PDF engine is never used.

/**
 * Draws a base64-encoded RGBA buffer onto a canvas.
 * @param {HTMLCanvasElement} canvas Target canvas.
 * @param {number} width Image width in pixels.
 * @param {number} height Image height in pixels.
 * @param {string} base64 Base64 of the raw RGBA8888 buffer (width*height*4 bytes).
 */
export function draw(canvas, width, height, base64) {
    if (!canvas) return;
    canvas.width = width;
    canvas.height = height;

    const ctx = canvas.getContext('2d');
    const binary = atob(base64);
    const len = binary.length;
    const bytes = new Uint8ClampedArray(len);
    for (let i = 0; i < len; i++) {
        bytes[i] = binary.charCodeAt(i);
    }

    const image = new ImageData(bytes, width, height);
    ctx.putImageData(image, 0, 0);
}
