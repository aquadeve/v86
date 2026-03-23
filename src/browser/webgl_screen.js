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
 * @param {{container: Element}} options
 * @param {!Object} bus                 v86 BusConnector
 */
export function WebGLScreenAdapter(options, bus)
{
    const container = options.container;
    dbg_log("WebGLScreenAdapter: container=" + (container ? container.tagName : "null"));

    // ----------------------------------------------------------------
    // Canvas setup
    // ----------------------------------------------------------------

    /** @type {HTMLCanvasElement|null} */
    let canvas = null;

    const existing_canvas = container.querySelector("canvas");
    if(existing_canvas instanceof HTMLCanvasElement)
    {
        canvas = existing_canvas;
    }
    else
    {
        canvas = /** @type {HTMLCanvasElement} */ (document.createElement("canvas"));
        container.appendChild(canvas);
    }

    canvas.width = 1024;
    canvas.height = 768;

    // ----------------------------------------------------------------
    // WebGL / Canvas 2D state
    // ----------------------------------------------------------------

    /** @type {WebGL2RenderingContext|WebGLRenderingContext|null} */
    let gl = /** @type {WebGL2RenderingContext|WebGLRenderingContext|null} */ (
        canvas.getContext("webgl2", { alpha: false, antialias: false }) ||
        canvas.getContext("webgl", { alpha: false, antialias: false }) ||
        canvas.getContext("experimental-webgl", { alpha: false, antialias: false })
    );

    /** @type {CanvasRenderingContext2D|null} */
    let ctx2d = null;

    /** @type {WebGLProgram|null} */
    let gl_program = null;

    /** @type {WebGLTexture|null} */
    let gl_texture = null;

    /** @type {WebGLBuffer|null} */
    let gl_vertex_buf = null;

    /** @type {number} */
    let gl_attrib_pos = -1;

    /** @type {WebGLUniformLocation|null} */
    let gl_uniform_tex = null;

    // Conversion buffer reused across frames to avoid GC pressure
    /** @type {Uint8Array|null} */
    let convert_buf = null;

    /** @type {number} */
    let convert_buf_size = 0;

    /**
     * Initialise WebGL program, texture and fullscreen quad.
     * @param {WebGL2RenderingContext|WebGLRenderingContext} gl_ctx
     */
    function init_webgl(gl_ctx)
    {
        gl = gl_ctx;

        const is_webgl2 = typeof WebGL2RenderingContext !== "undefined" && gl_ctx instanceof WebGL2RenderingContext;

        // ---- Vertex shader ----
        const vert_src = is_webgl2 ? [
            "#version 300 es",
            "in vec2 a_pos;",
            "out vec2 v_uv;",
            "void main(){",
            "  gl_Position = vec4(a_pos, 0.0, 1.0);",
            "  v_uv = vec2((a_pos.x + 1.0) * 0.5, 1.0 - (a_pos.y + 1.0) * 0.5);",
            "}",
        ].join("\n") : [
            "attribute vec2 a_pos;",
            "varying vec2 v_uv;",
            "void main(){",
            "  gl_Position = vec4(a_pos, 0.0, 1.0);",
            "  v_uv = vec2((a_pos.x + 1.0) * 0.5, 1.0 - (a_pos.y + 1.0) * 0.5);",
            "}",
        ].join("\n");

        // ---- Fragment shader ----
        const frag_src = is_webgl2 ? [
            "#version 300 es",
            "precision mediump float;",
            "uniform sampler2D u_tex;",
            "in vec2 v_uv;",
            "out vec4 out_color;",
            "void main(){",
            "  out_color = texture(u_tex, v_uv);",
            "}",
        ].join("\n") : [
            "precision mediump float;",
            "uniform sampler2D u_tex;",
            "varying vec2 v_uv;",
            "void main(){",
            "  gl_FragColor = texture2D(u_tex, v_uv);",
            "}",
        ].join("\n");

        /**
         * @param {number} type
         * @param {string} src
         * @return {WebGLShader|null}
         */
        function compile_shader(type, src)
        {
            const shader = gl.createShader(type);
            if(!shader)
            {
                dbg_log("WebGLScreenAdapter: createShader failed");
                return null;
            }

            gl.shaderSource(shader, src);
            gl.compileShader(shader);

            if(!gl.getShaderParameter(shader, gl.COMPILE_STATUS))
            {
                dbg_log("WebGLScreenAdapter: shader compile error: " + gl.getShaderInfoLog(shader));
                gl.deleteShader(shader);
                return null;
            }

            return shader;
        }

        const vert = compile_shader(gl.VERTEX_SHADER, vert_src);
        const frag = compile_shader(gl.FRAGMENT_SHADER, frag_src);
        if(!vert || !frag)
        {
            gl = null;
            ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
            return;
        }

        gl_program = gl.createProgram();
        if(!gl_program)
        {
            dbg_log("WebGLScreenAdapter: createProgram failed");
            gl = null;
            ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
            return;
        }

        gl.attachShader(gl_program, vert);
        gl.attachShader(gl_program, frag);
        gl.linkProgram(gl_program);

        if(!gl.getProgramParameter(gl_program, gl.LINK_STATUS))
        {
            dbg_log("WebGLScreenAdapter: program link error: " + gl.getProgramInfoLog(gl_program));
            gl.deleteProgram(gl_program);
            gl_program = null;
            gl = null;
            ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
            return;
        }

        gl.useProgram(gl_program);

        if(is_webgl2)
        {
            gl_attrib_pos = gl.getAttribLocation(gl_program, "a_pos");
        }
        else
        {
            gl_attrib_pos = gl.getAttribLocation(gl_program, "a_pos");
        }

        gl_uniform_tex = gl.getUniformLocation(gl_program, "u_tex");

        if(gl_attrib_pos < 0 || !gl_uniform_tex)
        {
            dbg_log("WebGLScreenAdapter: failed to resolve shader locations");
            gl.deleteProgram(gl_program);
            gl_program = null;
            gl = null;
            ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
            return;
        }

        // Fullscreen quad: two triangles covering clip-space [-1,1]x[-1,1]
        const verts = new Float32Array([
            -1, -1,   1, -1,   -1,  1,
            -1,  1,   1, -1,    1,  1,
        ]);

        gl_vertex_buf = gl.createBuffer();
        if(!gl_vertex_buf)
        {
            dbg_log("WebGLScreenAdapter: createBuffer failed");
            gl.deleteProgram(gl_program);
            gl_program = null;
            gl = null;
            ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
            return;
        }

        gl.bindBuffer(gl.ARRAY_BUFFER, gl_vertex_buf);
        gl.bufferData(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);

        // Create the texture that will receive each framebuffer frame
        gl_texture = gl.createTexture();
        if(!gl_texture)
        {
            dbg_log("WebGLScreenAdapter: createTexture failed");
            gl.deleteBuffer(gl_vertex_buf);
            gl_vertex_buf = null;
            gl.deleteProgram(gl_program);
            gl_program = null;
            gl = null;
            ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
            return;
        }

        gl.bindTexture(gl.TEXTURE_2D, gl_texture);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.uniform1i(gl_uniform_tex, 0);
    }

    if(gl)
    {
        init_webgl(gl);
    }

    if(!gl)
    {
        dbg_log("WebGLScreenAdapter: WebGL not available, falling back to Canvas 2D");
        ctx2d = /** @type {CanvasRenderingContext2D} */ (canvas.getContext("2d", { alpha: false }));
    }

    // ----------------------------------------------------------------
    // Pixel-format conversion helpers
    // ----------------------------------------------------------------

    /**
     * Convert raw guest pixel data to RGBA for rendering.
     * @param {Uint8Array} src
     * @param {number} fmt
     * @return {Uint8Array}
     */
    function to_rgba(src, fmt)
    {
        if(src.length % 4 !== 0)
        {
            return src;
        }

        const n_pixels = src.length >>> 2;

        if(!convert_buf || convert_buf_size < src.length)
        {
            convert_buf = new Uint8Array(src.length);
            convert_buf_size = src.length;
        }

        const dst = convert_buf;

        switch(fmt)
        {
            case 1: // VIRTIO_GPU_FORMAT_B8G8R8A8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 2];
                    dst[s+1] = src[s + 1];
                    dst[s+2] = src[s];
                    dst[s+3] = src[s + 3];
                }
                break;

            case 2: // VIRTIO_GPU_FORMAT_B8G8R8X8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 2];
                    dst[s+1] = src[s + 1];
                    dst[s+2] = src[s];
                    dst[s+3] = 255;
                }
                break;

            case 3: // VIRTIO_GPU_FORMAT_A8R8G8B8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 1];
                    dst[s+1] = src[s + 2];
                    dst[s+2] = src[s + 3];
                    dst[s+3] = src[s];
                }
                break;

            case 4: // VIRTIO_GPU_FORMAT_X8R8G8B8_UNORM
                for(let i = 0; i < n_pixels; i++)
                {
                    const s = i * 4;
                    dst[s]   = src[s + 1];
                    dst[s+1] = src[s + 2];
                    dst[s+2] = src[s + 3];
                    dst[s+3] = 255;
                }
                break;

            case 67: // VIRTIO_GPU_FORMAT_R8G8B8A8_UNORM
                dst.set(src);
                break;

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
     * @param {Uint8Array} rgba_data
     * @param {number} tex_w
     * @param {number} tex_h
     */
    function render_frame(rgba_data, tex_w, tex_h)
    {
        if(gl)
        {
            if(!gl_program || !gl_texture || !gl_vertex_buf || gl_attrib_pos < 0 || !gl_uniform_tex)
            {
                return;
            }

            gl.viewport(0, 0, canvas.width, canvas.height);

            gl.bindTexture(gl.TEXTURE_2D, gl_texture);
            gl.texImage2D(
                gl.TEXTURE_2D, 0,
                gl.RGBA,
                tex_w, tex_h, 0,
                gl.RGBA, gl.UNSIGNED_BYTE,
                rgba_data
            );

            gl.useProgram(gl_program);
            gl.bindBuffer(gl.ARRAY_BUFFER, gl_vertex_buf);
            gl.enableVertexAttribArray(gl_attrib_pos);
            gl.vertexAttribPointer(gl_attrib_pos, 2, gl.FLOAT, false, 0, 0);
            gl.uniform1i(gl_uniform_tex, 0);
            gl.drawArrays(gl.TRIANGLES, 0, 6);
        }
        else if(ctx2d)
        {
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

    /**
     * @param {!Array<number>} dimensions
     */
    const on_set_size = function(dimensions)
    {
        const width = dimensions[0] >>> 0;
        const height = dimensions[1] >>> 0;

        if(width === canvas.width && height === canvas.height)
        {
            return;
        }

        canvas.width = width;
        canvas.height = height;

        if(gl)
        {
            gl.viewport(0, 0, width, height);
        }
    };

    /**
     * @param {{buffer: Uint8Array, resource_width: number, resource_height: number, format: number}} ev
     */
    const on_update_buffer = function(ev)
    {
        const buffer = ev.buffer;
        const resource_width = ev.resource_width;
        const resource_height = ev.resource_height;
        const format = ev.format;

        if(!buffer || !resource_width || !resource_height)
        {
            return;
        }

        const rgba = to_rgba(buffer, format);
        render_frame(rgba, resource_width, resource_height);
    };

    bus.register("virtio-gpu-set-size", on_set_size, this);
    bus.register("virtio-gpu-update-buffer", on_update_buffer, this);

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------

    /**
     * Take a screenshot and return an <img> element.
     * @return {HTMLImageElement}
     */
    this.make_screenshot = function()
    {
        /** @type {HTMLImageElement} */
        const img = /** @type {HTMLImageElement} */ (document.createElement("img"));
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
        bus.unregister("virtio-gpu-set-size", on_set_size);
        bus.unregister("virtio-gpu-update-buffer", on_update_buffer);

        if(gl)
        {
            if(gl_texture) { gl.deleteTexture(gl_texture); }
            if(gl_vertex_buf) { gl.deleteBuffer(gl_vertex_buf); }
            if(gl_program) { gl.deleteProgram(gl_program); }

            const ext = gl.getExtension("WEBGL_lose_context");
            if(ext)
            {
                ext.loseContext();
            }

            gl = null;
        }

        ctx2d = null;
    };

    /** @type {HTMLCanvasElement} */
    this.canvas = canvas;
}
