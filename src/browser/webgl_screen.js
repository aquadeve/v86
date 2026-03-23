import { dbg_log } from "../log.js";

/**
 * WebGL-accelerated screen adapter for virtio-gpu.
 *
 * Listens for bus events emitted by the VirtioGPU device and renders
 * the framebuffer onto an HTML5 canvas using WebGL for hardware
 * acceleration. Falls back to Canvas 2D when WebGL is unavailable
 * (e.g. older browsers, headless environments).
 *
 * Bus events consumed:
 *   "virtio-gpu-set-size"      [width, height]   — resize display
 *   "virtio-gpu-update-buffer" { buffer, resource_width, resource_height, format, ... }
 *
 * @constructor
 * @param {Object} options
 * @param {Element} options.container  DOM element that will host the canvas
 * @param {Object} bus                 v86 BusConnector
 */
export function WebGLScreenAdapter(options, bus)
{
    const container = options.container;
    dbg_log("WebGLScreenAdapter: container=" + (container ? container.tagName : "null"));

    // ----------------------------------------------------------------
    // Canvas setup
    // ----------------------------------------------------------------

    let canvas = container.getElementsByTagName("canvas")[0];
    if(!canvas)
    {
        canvas = document.createElement("canvas");
        container.appendChild(canvas);
    }

    canvas.width  = 1024;
    canvas.height = 768;

    // ----------------------------------------------------------------
    // Try to get a WebGL context (WebGL2 preferred, WebGL1 fallback)
    // ----------------------------------------------------------------

    /** @type {WebGL2RenderingContext|WebGLRenderingContext|null} */
    let gl = canvas.getContext("webgl2", { alpha: false, antialias: false }) ||
             canvas.getContext("webgl",  { alpha: false, antialias: false }) ||
             canvas.getContext("experimental-webgl", { alpha: false, antialias: false });

    /** @type {CanvasRenderingContext2D|null} */
    let ctx2d = null;

    if(gl)
    {
        init_webgl(gl);
    }
    else
    {
        dbg_log("WebGLScreenAdapter: WebGL not available, falling back to Canvas 2D");
        ctx2d = canvas.getContext("2d", { alpha: false });
    }

    // ----------------------------------------------------------------
    // WebGL state
    // ----------------------------------------------------------------
    let gl_program = null;
    let gl_texture = null;
    let gl_vertex_buf = null;
    let gl_attrib_pos = -1;
    let gl_uniform_tex = null;

    // Conversion buffer reused across frames to avoid GC pressure
    let convert_buf = null;
    let convert_buf_size = 0;

    /**
     * Initialise WebGL program, texture and fullscreen quad.
     * @param {WebGL2RenderingContext|WebGLRenderingContext} gl_ctx
     */
    function init_webgl(gl_ctx)
    {
        gl = gl_ctx;

        // ---- Vertex shader ----
        const vert_src = [
            "attribute vec2 a_pos;",
            "varying vec2 v_uv;",
            "void main(){",
            "  gl_Position = vec4(a_pos, 0.0, 1.0);",
            "  v_uv = vec2((a_pos.x + 1.0) * 0.5, 1.0 - (a_pos.y + 1.0) * 0.5);",
            "}",
        ].join("\n");

        // ---- Fragment shader ----
        const frag_src = [
            "precision mediump float;",
            "uniform sampler2D u_tex;",
            "varying vec2 v_uv;",
            "void main(){",
            "  gl_FragColor = texture2D(u_tex, v_uv);",
            "}",
        ].join("\n");

        function compile_shader(type, src)
        {
            const shader = gl.createShader(type);
            gl.shaderSource(shader, src);
            gl.compileShader(shader);
            if(!gl.getShaderParameter(shader, gl.COMPILE_STATUS))
            {
                dbg_log("WebGLScreenAdapter: shader compile error: " + gl.getShaderInfoLog(shader));
                return null;
            }
            return shader;
        }

        const vert = compile_shader(gl.VERTEX_SHADER,   vert_src);
        const frag = compile_shader(gl.FRAGMENT_SHADER, frag_src);
        if(!vert || !frag)
        {
            gl = null;
            ctx2d = canvas.getContext("2d", { alpha: false });
            return;
        }

        gl_program = gl.createProgram();
        gl.attachShader(gl_program, vert);
        gl.attachShader(gl_program, frag);
        gl.linkProgram(gl_program);

        if(!gl.getProgramParameter(gl_program, gl.LINK_STATUS))
        {
            dbg_log("WebGLScreenAdapter: program link error: " + gl.getProgramInfoLog(gl_program));
            gl = null;
            ctx2d = canvas.getContext("2d", { alpha: false });
            return;
        }

        gl.useProgram(gl_program);

        gl_attrib_pos  = gl.getAttribLocation(gl_program, "a_pos");
        gl_uniform_tex = gl.getUniformLocation(gl_program, "u_tex");

        // Fullscreen quad: two triangles covering clip-space [-1,1]x[-1,1]
        const verts = new Float32Array([
            -1, -1,   1, -1,   -1,  1,
            -1,  1,   1, -1,    1,  1,
        ]);
        gl_vertex_buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, gl_vertex_buf);
        gl.bufferData(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);

        // Create the texture that will receive each framebuffer frame
        gl_texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, gl_texture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

        gl.uniform1i(gl_uniform_tex, 0);
    }

    // ----------------------------------------------------------------
    // Pixel-format conversion helpers
    // Each converter receives a Uint8Array view of the source and
    // returns a Uint8Array in RGBA order for upload to WebGL/Canvas.
    // ----------------------------------------------------------------

    /**
     * Convert raw guest pixel data to RGBA for rendering.
     * Pixel formats follow the Vulkan/Gallium naming convention where
     * components are listed from lowest to highest memory address.
     * @param {Uint8Array} src   Source pixels from guest memory
     * @param {number}     fmt   virtio-gpu pixel format constant
     * @return {Uint8Array}      RGBA pixel data
     */
    function to_rgba(src, fmt)
    {
        // Source must be a multiple of 4 bytes (one pixel each)
        if(src.length % 4 !== 0)
        {
            return src;
        }

        const n_pixels = src.length >>> 2;

        // Reuse conversion buffer to minimise allocations
        if(!convert_buf || convert_buf_size < src.length)
        {
            convert_buf = new Uint8Array(src.length);
            convert_buf_size = src.length;
        }
        const dst = convert_buf;

        switch(fmt)
        {
            // mem [B, G, R, A] → RGBA  (swap R↔B, keep A)
            case 1:  // VIRTIO_GPU_FORMAT_B8G8R8A8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 2];
                    dst[s+1] = src[s + 1];
                    dst[s+2] = src[s];
                    dst[s+3] = src[s + 3];
                }
                break;

            // mem [B, G, R, X] → RGBA  (swap R↔B, force A = 255)
            // Most common Linux DRM framebuffer format (DRM_FORMAT_XRGB8888)
            case 2:  // VIRTIO_GPU_FORMAT_B8G8R8X8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 2];
                    dst[s+1] = src[s + 1];
                    dst[s+2] = src[s];
                    dst[s+3] = 255;
                }
                break;

            // mem [A, R, G, B] → RGBA
            case 3:  // VIRTIO_GPU_FORMAT_A8R8G8B8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 1];
                    dst[s+1] = src[s + 2];
                    dst[s+2] = src[s + 3];
                    dst[s+3] = src[s];
                }
                break;

            // mem [X, R, G, B] → RGBA  (force A = 255)
            case 4:  // VIRTIO_GPU_FORMAT_X8R8G8B8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 1];
                    dst[s+1] = src[s + 2];
                    dst[s+2] = src[s + 3];
                    dst[s+3] = 255;
                }
                break;

            // mem [R, G, B, A] — no conversion needed
            case 67: // VIRTIO_GPU_FORMAT_R8G8B8A8_UNORM
                dst.set(src);
                break;

            // mem [X, B, G, R] → RGBA  (force A = 255)
            case 68: // VIRTIO_GPU_FORMAT_X8B8G8R8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 3];
                    dst[s+1] = src[s + 2];
                    dst[s+2] = src[s + 1];
                    dst[s+3] = 255;
                }
                break;

            // mem [A, B, G, R] → RGBA
            case 121: // VIRTIO_GPU_FORMAT_A8B8G8R8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 3];
                    dst[s+1] = src[s + 2];
                    dst[s+2] = src[s + 1];
                    dst[s+3] = src[s];
                }
                break;

            // mem [R, G, B, X] → RGBA  (force A = 255)
            case 134: // VIRTIO_GPU_FORMAT_R8G8B8X8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s];
                    dst[s+1] = src[s + 1];
                    dst[s+2] = src[s + 2];
                    dst[s+3] = 255;
                }
                break;

            default:
                // Unknown format: copy as-is
                dst.set(src);
                break;
        }

        return dst;
    }

    // ----------------------------------------------------------------
    // Rendering
    // ----------------------------------------------------------------

    /**
     * Upload pixel data and draw to screen.
     * @param {Uint8Array} rgba_data  RGBA pixels
     * @param {number}     tex_w      Width of the texture in pixels
     * @param {number}     tex_h      Height of the texture in pixels
     */
    function render_frame(rgba_data, tex_w, tex_h)
    {
        if(gl)
        {
            gl.viewport(0, 0, canvas.width, canvas.height);

            // Upload texture
            gl.bindTexture(gl.TEXTURE_2D, gl_texture);
            gl.texImage2D(
                gl.TEXTURE_2D, 0,
                gl.RGBA,
                tex_w, tex_h, 0,
                gl.RGBA, gl.UNSIGNED_BYTE,
                rgba_data
            );

            // Draw fullscreen quad
            gl.bindBuffer(gl.ARRAY_BUFFER, gl_vertex_buf);
            gl.enableVertexAttribArray(gl_attrib_pos);
            gl.vertexAttribPointer(gl_attrib_pos, 2, gl.FLOAT, false, 0, 0);

            gl.useProgram(gl_program);
            gl.uniform1i(gl_uniform_tex, 0);

            gl.drawArrays(gl.TRIANGLES, 0, 6);
        }
        else if(ctx2d)
        {
            // Canvas 2D fallback: create ImageData from RGBA pixels
            const expected_len = tex_w * tex_h * 4;
            if(rgba_data.byteLength < expected_len)
            {
                return;
            }
            const img = new ImageData(
                new Uint8ClampedArray(rgba_data.buffer, rgba_data.byteOffset, expected_len),
                tex_w, tex_h
            );
            ctx2d.putImageData(img, 0, 0);
        }
    }

    // ----------------------------------------------------------------
    // Bus event handlers
    // ----------------------------------------------------------------

    const on_set_size = function(dimensions)
    {
        const width  = dimensions[0];
        const height = dimensions[1];

        if(width === canvas.width && height === canvas.height)
        {
            return;
        }

        canvas.width  = width;
        canvas.height = height;

        if(gl)
        {
            gl.viewport(0, 0, width, height);
        }
    };

    const on_update_buffer = function(ev)
    {
        const { buffer, resource_width, resource_height, format } = ev;
        if(!buffer || !resource_width || !resource_height)
        {
            return;
        }

        const rgba = to_rgba(buffer, format);
        render_frame(rgba, resource_width, resource_height);
    };

    bus.register("virtio-gpu-set-size",    on_set_size,     this);
    bus.register("virtio-gpu-update-buffer", on_update_buffer, this);

    // ----------------------------------------------------------------
    // Public API  (mirrors the subset of ScreenAdapter used externally)
    // ----------------------------------------------------------------

    /**
     * Take a screenshot and return an <img> element.
     * @return {HTMLImageElement}
     */
    this.make_screenshot = function()
    {
        const img = document.createElement("img");
        img.src = canvas.toDataURL("image/png");
        return img;
    };

    /**
     * Apply CSS scale transform to the canvas.
     * @param {number} sx
     * @param {number} sy
     */
    this.set_scale = function(sx, sy)
    {
        canvas.style.transform = "scale(" + sx + ", " + sy + ")";
        canvas.style.transformOrigin = "0 0";
    };

    /**
     * Destroy / detach this adapter.
     */
    this.destroy = function()
    {
        bus.unregister("virtio-gpu-set-size",      on_set_size);
        bus.unregister("virtio-gpu-update-buffer", on_update_buffer);

        if(gl)
        {
            if(gl_texture)   { gl.deleteTexture(gl_texture); }
            if(gl_vertex_buf){ gl.deleteBuffer(gl_vertex_buf); }
            if(gl_program)   { gl.deleteProgram(gl_program); }
            const ext = gl.getExtension("WEBGL_lose_context");
            if(ext) { ext.loseContext(); }
            gl = null;
        }
        ctx2d = null;
    };

    /** @type {HTMLCanvasElement} */
    this.canvas = canvas;
}
