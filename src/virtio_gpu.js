import { dbg_assert, dbg_log } from "./log.js";
import { VirtIO, VIRTIO_F_VERSION_1, VirtQueueBufferChain } from "./virtio.js";
import { LOG_VIRTIO } from "./const.js";

// For Types Only
import { CPU } from "./cpu.js";
import { BusConnector } from "./bus.js";

// https://docs.oasis-open.org/virtio/virtio/v1.2/csd01/virtio-v1.2-csd01.html
// https://github.com/torvalds/linux/blob/master/include/uapi/linux/virtio_gpu.h

// Control commands
const VIRTIO_GPU_CMD_GET_DISPLAY_INFO           = 0x100;
const VIRTIO_GPU_CMD_RESOURCE_CREATE_2D         = 0x101;
const VIRTIO_GPU_CMD_RESOURCE_UNREF             = 0x102;
const VIRTIO_GPU_CMD_SET_SCANOUT                = 0x103;
const VIRTIO_GPU_CMD_RESOURCE_FLUSH             = 0x104;
const VIRTIO_GPU_CMD_TRANSFER_TO_HOST_2D        = 0x105;
const VIRTIO_GPU_CMD_RESOURCE_ATTACH_BACKING    = 0x106;
const VIRTIO_GPU_CMD_RESOURCE_DETACH_BACKING    = 0x107;
const VIRTIO_GPU_CMD_GET_CAPSET_INFO            = 0x108;
const VIRTIO_GPU_CMD_GET_CAPSET                 = 0x109;
const VIRTIO_GPU_CMD_GET_EDID                   = 0x10A;

// Cursor commands
const VIRTIO_GPU_CMD_UPDATE_CURSOR              = 0x300;
const VIRTIO_GPU_CMD_MOVE_CURSOR                = 0x301;

// Success responses
const VIRTIO_GPU_RESP_OK_NODATA                 = 0x1100;
const VIRTIO_GPU_RESP_OK_DISPLAY_INFO           = 0x1101;
const VIRTIO_GPU_RESP_OK_CAPSET_INFO            = 0x1104;
const VIRTIO_GPU_RESP_OK_CAPSET                 = 0x1105;
const VIRTIO_GPU_RESP_OK_EDID                   = 0x1106;

// Error responses
const VIRTIO_GPU_RESP_ERR_UNSPEC                = 0x1200;
const VIRTIO_GPU_RESP_ERR_OUT_OF_MEMORY         = 0x1201;
const VIRTIO_GPU_RESP_ERR_INVALID_SCANOUT_ID    = 0x1202;
const VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID   = 0x1203;
const VIRTIO_GPU_RESP_ERR_INVALID_CONTEXT_ID    = 0x1204;
const VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER     = 0x1205;

// Feature bits
const VIRTIO_GPU_F_VIRGL                        = 0;
const VIRTIO_GPU_F_EDID                         = 1;

// Pixel formats (matching DRM format definitions)
const VIRTIO_GPU_FORMAT_B8G8R8A8_UNORM          = 1;
const VIRTIO_GPU_FORMAT_B8G8R8X8_UNORM          = 2;
const VIRTIO_GPU_FORMAT_A8R8G8B8_UNORM          = 3;
const VIRTIO_GPU_FORMAT_X8R8G8B8_UNORM          = 4;
const VIRTIO_GPU_FORMAT_R8G8B8A8_UNORM          = 67;
const VIRTIO_GPU_FORMAT_X8B8G8R8_UNORM          = 68;
const VIRTIO_GPU_FORMAT_A8B8G8R8_UNORM          = 121;
const VIRTIO_GPU_FORMAT_R8G8B8X8_UNORM          = 134;

// The protocol response layout reserves 16 scanout entries in GET_DISPLAY_INFO.
// This device currently exposes one active scanout, but the response still uses
// the standard 16-entry array.
const VIRTIO_GPU_MAX_SCANOUTS                   = 16;
const VIRTIO_GPU_SUPPORTED_SCANOUTS             = 1;

// Header size for virtio_gpu_ctrl_hdr
const CTRL_HDR_SIZE                             = 24;

// Default display size
const DEFAULT_WIDTH                              = 1024;
const DEFAULT_HEIGHT                             = 768;

const SUPPORTED_FORMATS = new Set([
    VIRTIO_GPU_FORMAT_B8G8R8A8_UNORM,
    VIRTIO_GPU_FORMAT_B8G8R8X8_UNORM,
    VIRTIO_GPU_FORMAT_A8R8G8B8_UNORM,
    VIRTIO_GPU_FORMAT_X8R8G8B8_UNORM,
    VIRTIO_GPU_FORMAT_R8G8B8A8_UNORM,
    VIRTIO_GPU_FORMAT_X8B8G8R8_UNORM,
    VIRTIO_GPU_FORMAT_A8B8G8R8_UNORM,
    VIRTIO_GPU_FORMAT_R8G8B8X8_UNORM,
]);

/**
 * VirtIO GPU Device
 * Implements the virtio-gpu 2D protocol for graphics in the browser.
 *
 * @constructor
 * @param {CPU} cpu
 * @param {BusConnector} bus
 */
export function VirtioGPU(cpu, bus)
{
    /** @const @type {BusConnector} */
    this.bus = bus;

    /** @const @type {CPU} */
    this.cpu = cpu;

    /** @type {number} */
    this.events_read = 0;

    // Active scanouts supported by this device
    this.scanouts = [];
    for(let i = 0; i < VIRTIO_GPU_SUPPORTED_SCANOUTS; i++)
    {
        this.scanouts.push({
            resource_id: 0,
            x: 0,
            y: 0,
            width: DEFAULT_WIDTH,
            height: DEFAULT_HEIGHT,
            enabled: false,
        });
    }

    // GPU resources: resource_id -> { format, width, height, backing_entries, pixels }
    // backing_entries: [{ addr, length }, ...]
    // pixels: Uint8Array (guest bytes copied into a linear buffer)
    this.resources = new Map();

    const queues = [
        { size_supported: 64, notify_offset: 0 },   // controlq
        { size_supported: 16, notify_offset: 1 },   // cursorq
    ];

    /** @type {VirtIO} */
    this.virtio = new VirtIO(cpu,
    {
        name: "virtio-gpu",
        pci_id: 0x0D << 3,
        device_id: 0x1050,
        subsystem_device_id: 16,
        common:
        {
            initial_port: 0xE800,
            queues: queues,
            features:
            [
                VIRTIO_F_VERSION_1,
            ],
            on_driver_ok: () =>
            {
                dbg_log("virtio-gpu: driver ready", LOG_VIRTIO);
                this.bus.send("virtio-gpu-ready");
                this.bus.send("virtio-gpu-set-size",
                [
                    this.scanouts[0].width,
                    this.scanouts[0].height,
                ]);
            },
        },
        notification:
        {
            initial_port: 0xE900,
            single_handler: false,
            handlers:
            [
                (queue_id) => { this.handle_control_queue(queue_id); },
                (queue_id) => { this.handle_cursor_queue(queue_id); },
            ],
        },
        isr_status:
        {
            initial_port: 0xE700,
        },
        device_specific:
        {
            initial_port: 0xE600,
            struct:
            [
                {
                    bytes: 4,
                    name: "events_read",
                    read: () => this.events_read,
                    write: data => { /* read-only */ },
                },
                {
                    bytes: 4,
                    name: "events_clear",
                    read: () => 0,
                    write: data => { this.events_read &= ~data; },
                },
                {
                    bytes: 4,
                    name: "num_scanouts",
                    read: () => VIRTIO_GPU_SUPPORTED_SCANOUTS,
                    write: data => { /* read-only */ },
                },
                {
                    bytes: 4,
                    name: "num_capsets",
                    read: () => 0,
                    write: data => { /* read-only */ },
                },
            ],
        },
    });
}

VirtioGPU.prototype._u64 = function(view, offset)
{
    const lo = view.getUint32(offset, true);
    const hi = view.getUint32(offset + 4, true);
    return hi * 0x100000000 + lo;
};

VirtioGPU.prototype._is_supported_format = function(format)
{
    return SUPPORTED_FORMATS.has(format);
};

/**
 * Read a logical byte range from concatenated backing entries.
 * @param {!Object} resource
 * @param {number} logical_offset
 * @param {number} length
 * @returns {Uint8Array|null}
 */
VirtioGPU.prototype._read_from_backing = function(resource, logical_offset, length)
{
    if(length <= 0 || logical_offset < 0)
    {
        return null;
    }

    const out = new Uint8Array(length);
    let out_off = 0;
    let skip = logical_offset;
    let remaining = length;

    for(let i = 0; i < resource.backing_entries.length && remaining > 0; i++)
    {
        const entry = resource.backing_entries[i];
        const entry_len = entry.length >>> 0;

        if(skip >= entry_len)
        {
            skip -= entry_len;
            continue;
        }

        const take = Math.min(entry_len - skip, remaining);
        const src_start = entry.addr + skip;
        const src_end = src_start + take;

        if(src_start < 0 || src_end > this.cpu.mem8.length)
        {
            return null;
        }

        out.set(this.cpu.mem8.subarray(src_start, src_end), out_off);

        out_off += take;
        remaining -= take;
        skip = 0;
    }

    return remaining === 0 ? out : null;
};

/**
 * Copy a logical byte range from concatenated backing entries into an existing buffer.
 * @param {!Object} resource
 * @param {number} logical_offset
 * @param {!Uint8Array} target
 * @param {number} target_offset
 * @param {number} length
 * @returns {boolean}
 */
VirtioGPU.prototype._copy_from_backing = function(resource, logical_offset, target, target_offset, length)
{
    if(length < 0 || logical_offset < 0 || target_offset < 0 || target_offset + length > target.length)
    {
        return false;
    }

    if(length === 0)
    {
        return true;
    }

    let skip = logical_offset;

    for(let i = 0; i < resource.backing_entries.length; i++)
    {
        const entry = resource.backing_entries[i];
        const entry_len = entry.length >>> 0;

        if(skip >= entry_len)
        {
            skip -= entry_len;
            continue;
        }

        const src_start = entry.addr + skip;
        const src_end = src_start + length;

        if(skip + length <= entry_len)
        {
            if(src_start < 0 || src_end > this.cpu.mem8.length)
            {
                return false;
            }

            target.set(this.cpu.mem8.subarray(src_start, src_end), target_offset);
            return true;
        }

        break;
    }

    let out_off = target_offset;
    let remaining = length;
    skip = logical_offset;

    for(let i = 0; i < resource.backing_entries.length && remaining > 0; i++)
    {
        const entry = resource.backing_entries[i];
        const entry_len = entry.length >>> 0;

        if(skip >= entry_len)
        {
            skip -= entry_len;
            continue;
        }

        const take = Math.min(entry_len - skip, remaining);
        const src_start = entry.addr + skip;
        const src_end = src_start + take;

        if(src_start < 0 || src_end > this.cpu.mem8.length)
        {
            return false;
        }

        target.set(this.cpu.mem8.subarray(src_start, src_end), out_off);

        out_off += take;
        remaining -= take;
        skip = 0;
    }

    return remaining === 0;
};

/**
 * Handle controlq notifications from the driver.
 * @param {number} queue_id
 */
VirtioGPU.prototype.handle_control_queue = function(queue_id)
{
    dbg_assert(queue_id === 0, "VirtioGPU: controlq must be queue 0");
    const queue = this.virtio.queues[queue_id];

    while(queue.has_request())
    {
        const bufchain = queue.pop_request();

        if(bufchain.length_readable < CTRL_HDR_SIZE)
        {
            dbg_log("virtio-gpu: command too short (" + bufchain.length_readable + " bytes)", LOG_VIRTIO);
            this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
            continue;
        }

        const cmd_buf = new Uint8Array(bufchain.length_readable);
        bufchain.get_next_blob(cmd_buf);
        const view = new DataView(cmd_buf.buffer, cmd_buf.byteOffset, cmd_buf.byteLength);
        const cmd_type = view.getUint32(0, true);

        dbg_log("virtio-gpu: command 0x" + cmd_type.toString(16), LOG_VIRTIO);

        switch(cmd_type)
        {
            case VIRTIO_GPU_CMD_GET_DISPLAY_INFO:
                this.cmd_get_display_info(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_RESOURCE_CREATE_2D:
                this.cmd_resource_create_2d(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_RESOURCE_UNREF:
                this.cmd_resource_unref(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_SET_SCANOUT:
                this.cmd_set_scanout(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_RESOURCE_FLUSH:
                this.cmd_resource_flush(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_TRANSFER_TO_HOST_2D:
                this.cmd_transfer_to_host_2d(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_RESOURCE_ATTACH_BACKING:
                this.cmd_resource_attach_backing(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_RESOURCE_DETACH_BACKING:
                this.cmd_resource_detach_backing(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_GET_CAPSET_INFO:
                this.cmd_get_capset_info(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_GET_CAPSET:
                this.cmd_get_capset(queue_id, bufchain, view);
                break;
            case VIRTIO_GPU_CMD_GET_EDID:
                this.cmd_get_edid(queue_id, bufchain, view);
                break;
            default:
                dbg_log("virtio-gpu: unknown command 0x" + cmd_type.toString(16), LOG_VIRTIO);
                this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
                break;
        }
    }
};

/**
 * Handle cursorq notifications.
 * @param {number} queue_id
 */
VirtioGPU.prototype.handle_cursor_queue = function(queue_id)
{
    dbg_assert(queue_id === 1, "VirtioGPU: cursorq must be queue 1");
    const queue = this.virtio.queues[queue_id];

    while(queue.has_request())
    {
        const bufchain = queue.pop_request();
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
    }
};

/**
 * GET_DISPLAY_INFO
 * Response is hdr + 16 display slots.
 */
VirtioGPU.prototype.cmd_get_display_info = function(queue_id, bufchain, view)
{
    const resp = new Uint8Array(24 + VIRTIO_GPU_MAX_SCANOUTS * 24);
    const resp_view = new DataView(resp.buffer);

    resp_view.setUint32(0, VIRTIO_GPU_RESP_OK_DISPLAY_INFO, true);

    for(let i = 0; i < VIRTIO_GPU_MAX_SCANOUTS; i++)
    {
        const base = 24 + i * 24;
        const scanout = i < this.scanouts.length ? this.scanouts[i] : null;

        if(scanout)
        {
            // Always advertise configured scanouts as enabled so that the
            // guest driver (e.g. Linux virtio-gpu DRM) recognises them and
            // sets up a framebuffer.  The enabled/disabled distinction is
            // handled separately via the resource_id check in
            // cmd_resource_flush.
            resp_view.setUint32(base + 0,  scanout.x, true);
            resp_view.setUint32(base + 4,  scanout.y, true);
            resp_view.setUint32(base + 8,  scanout.width, true);
            resp_view.setUint32(base + 12, scanout.height, true);
            resp_view.setUint32(base + 16, 1, true);
            resp_view.setUint32(base + 20, 0, true);
        }
        else
        {
            resp_view.setUint32(base + 0,  0, true);
            resp_view.setUint32(base + 4,  0, true);
            resp_view.setUint32(base + 8,  0, true);
            resp_view.setUint32(base + 12, 0, true);
            resp_view.setUint32(base + 16, 0, true);
            resp_view.setUint32(base + 20, 0, true);
        }
    }

    this.send_response_data(queue_id, bufchain, resp);
};

/**
 * RESOURCE_CREATE_2D
 * hdr(24) + resource_id(4) + format(4) + width(4) + height(4) = 40
 */
VirtioGPU.prototype.cmd_resource_create_2d = function(queue_id, bufchain, view)
{
    if(bufchain.length_readable < 40)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    const resource_id = view.getUint32(24, true);
    const format      = view.getUint32(28, true);
    const width       = view.getUint32(32, true);
    const height      = view.getUint32(36, true);

    dbg_log("virtio-gpu: create resource id=" + resource_id +
        " format=" + format + " " + width + "x" + height, LOG_VIRTIO);

    if(resource_id === 0 || this.resources.has(resource_id))
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    if(width === 0 || height === 0)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER);
        return;
    }

    if(!this._is_supported_format(format))
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER);
        return;
    }

    this.resources.set(resource_id,
    {
        resource_id,
        format,
        width,
        height,
        backing_entries: [],
        pixels: null,
    });

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * RESOURCE_UNREF
 * hdr(24) + resource_id(4) + padding(4) = 32
 */
VirtioGPU.prototype.cmd_resource_unref = function(queue_id, bufchain, view)
{
    const resource_id = view.getUint32(24, true);

    dbg_log("virtio-gpu: unref resource id=" + resource_id, LOG_VIRTIO);

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    for(const scanout of this.scanouts)
    {
        if(scanout.resource_id === resource_id)
        {
            scanout.resource_id = 0;
            scanout.enabled = false;
        }
    }

    this.resources.delete(resource_id);
    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * SET_SCANOUT
 * hdr(24) + rect(16) + scanout_id(4) + resource_id(4) = 48
 */
VirtioGPU.prototype.cmd_set_scanout = function(queue_id, bufchain, view)
{
    if(bufchain.length_readable < 48)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    const rect_x      = view.getUint32(24, true);
    const rect_y      = view.getUint32(28, true);
    const rect_w      = view.getUint32(32, true);
    const rect_h      = view.getUint32(36, true);
    const scanout_id  = view.getUint32(40, true);
    const resource_id = view.getUint32(44, true);

    dbg_log("virtio-gpu: set_scanout scanout=" + scanout_id +
        " resource=" + resource_id + " " + rect_w + "x" + rect_h, LOG_VIRTIO);

    if(scanout_id >= this.scanouts.length)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_SCANOUT_ID);
        return;
    }

    const scanout = this.scanouts[scanout_id];

    if(resource_id === 0)
    {
        scanout.enabled = false;
        scanout.resource_id = 0;
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
        return;
    }

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    scanout.resource_id = resource_id;
    scanout.x = rect_x;
    scanout.y = rect_y;
    scanout.width = rect_w || resource.width;
    scanout.height = rect_h || resource.height;
    scanout.enabled = true;

    this.bus.send("virtio-gpu-set-size", [scanout.width, scanout.height]);

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * TRANSFER_TO_HOST_2D
 * hdr(24) + rect(16) + offset(8) + resource_id(4) + padding(4) = 56
 */
VirtioGPU.prototype.cmd_transfer_to_host_2d = function(queue_id, bufchain, view)
{
    if(bufchain.length_readable < 56)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    const rect_x      = view.getUint32(24, true);
    const rect_y      = view.getUint32(28, true);
    const rect_w      = view.getUint32(32, true);
    const rect_h      = view.getUint32(36, true);
    const offset      = this._u64(view, 40);
    const resource_id = view.getUint32(48, true);

    dbg_log(
        "virtio-gpu: transfer_to_host_2d resource=" + resource_id +
        " rect=(" + rect_x + "," + rect_y + "," + rect_w + "x" + rect_h + ")" +
        " offset=" + offset,
        LOG_VIRTIO
    );

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    if(!resource.backing_entries.length)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    if(rect_w === 0 || rect_h === 0)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
        return;
    }

    if(rect_x + rect_w > resource.width || rect_y + rect_h > resource.height)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER);
        return;
    }

    if(!resource.pixels || resource.pixels.length !== resource.width * resource.height * 4)
    {
        resource.pixels = new Uint8Array(resource.width * resource.height * 4);
    }

    const bpp = 4;
    const stride = resource.width * bpp;
    const row_bytes = rect_w * bpp;

    if(rect_x === 0 && rect_w === resource.width)
    {
        const dst_start = rect_y * stride;
        const total_bytes = rect_h * stride;

        if(!this._copy_from_backing(resource, offset, resource.pixels, dst_start, total_bytes))
        {
            this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
            return;
        }

        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
        return;
    }

    for(let row = 0; row < rect_h; row++)
    {
        const src_offset = offset + row * stride;
        const dst_start = (rect_y + row) * stride + rect_x * bpp;

        if(!this._copy_from_backing(resource, src_offset, resource.pixels, dst_start, row_bytes))
        {
            this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
            return;
        }
    }

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * RESOURCE_FLUSH
 * hdr(24) + rect(16) + resource_id(4) + padding(4) = 48
 */
VirtioGPU.prototype.cmd_resource_flush = function(queue_id, bufchain, view)
{
    if(bufchain.length_readable < 48)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    const rect_x      = view.getUint32(24, true);
    const rect_y      = view.getUint32(28, true);
    const rect_w      = view.getUint32(32, true);
    const rect_h      = view.getUint32(36, true);
    const resource_id = view.getUint32(40, true);

    dbg_log("virtio-gpu: resource_flush resource=" + resource_id +
        " rect=(" + rect_x + "," + rect_y + "," + rect_w + "x" + rect_h + ")", LOG_VIRTIO);

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    if(rect_x + rect_w > resource.width || rect_y + rect_h > resource.height)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER);
        return;
    }

    for(let i = 0; i < this.scanouts.length; i++)
    {
        const scanout = this.scanouts[i];
        if(scanout.resource_id === resource_id && scanout.enabled && resource.pixels)
        {
            this.bus.send("virtio-gpu-update-buffer",
            {
                scanout_id: i,
                x: rect_x,
                y: rect_y,
                width: rect_w,
                height: rect_h,
                buffer: resource.pixels,
                resource_width: resource.width,
                resource_height: resource.height,
                format: resource.format,
            });
        }
    }

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * RESOURCE_ATTACH_BACKING
 * hdr(24) + resource_id(4) + nr_entries(4) = 32
 * Each virtio_gpu_mem_entry: addr(8) + length(4) + padding(4) = 16
 */
VirtioGPU.prototype.cmd_resource_attach_backing = function(queue_id, bufchain, view)
{
    if(bufchain.length_readable < 32)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    const resource_id = view.getUint32(24, true);
    const nr_entries  = view.getUint32(28, true);

    dbg_log("virtio-gpu: attach_backing resource=" + resource_id +
        " nr_entries=" + nr_entries, LOG_VIRTIO);

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    const needed = 32 + nr_entries * 16;
    if(bufchain.length_readable < needed)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    resource.backing_entries = [];
    resource.pixels = null;

    for(let i = 0; i < nr_entries; i++)
    {
        const entry_base = 32 + i * 16;
        const addr = this._u64(view, entry_base);
        const length = view.getUint32(entry_base + 8, true);

        if(length === 0)
        {
            continue;
        }

        resource.backing_entries.push({ addr, length });

        dbg_log(
            "virtio-gpu:   entry[" + i + "] addr=0x" + addr.toString(16) +
            " len=" + length,
            LOG_VIRTIO
        );
    }

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * RESOURCE_DETACH_BACKING
 * hdr(24) + resource_id(4) + padding(4) = 32
 */
VirtioGPU.prototype.cmd_resource_detach_backing = function(queue_id, bufchain, view)
{
    const resource_id = view.getUint32(24, true);

    dbg_log("virtio-gpu: detach_backing resource=" + resource_id, LOG_VIRTIO);

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    resource.backing_entries = [];
    resource.pixels = null;

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * GET_CAPSET_INFO
 * If you only want 2D mode, report zero capsets cleanly.
 */
VirtioGPU.prototype.cmd_get_capset_info = function(queue_id, bufchain, view)
{
    const resp = new Uint8Array(40);
    const resp_view = new DataView(resp.buffer);

    resp_view.setUint32(0, VIRTIO_GPU_RESP_OK_CAPSET_INFO, true);
    resp_view.setUint32(24, 0, true); // capset_id
    resp_view.setUint32(28, 0, true); // capset_max_version
    resp_view.setUint32(32, 0, true); // capset_max_size

    this.send_response_data(queue_id, bufchain, resp);
};

/**
 * GET_CAPSET
 * Zero-capset mode: return an empty payload with OK_CAPSET.
 */
VirtioGPU.prototype.cmd_get_capset = function(queue_id, bufchain, view)
{
    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_CAPSET);
};

/**
 * GET_EDID
 * Not enabled unless you advertise VIRTIO_GPU_F_EDID.
 */
VirtioGPU.prototype.cmd_get_edid = function(queue_id, bufchain, view)
{
    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
};

// --- Helpers ---

/**
 * Send a simple response with only a control header.
 * @param {number} queue_id
 * @param {VirtQueueBufferChain} bufchain
 * @param {number} resp_type
 */
VirtioGPU.prototype.send_response = function(queue_id, bufchain, resp_type)
{
    const resp = new Uint8Array(CTRL_HDR_SIZE);
    const resp_view = new DataView(resp.buffer);
    resp_view.setUint32(0, resp_type, true);

    bufchain.set_next_blob(resp);
    this.virtio.queues[queue_id].push_reply(bufchain);
    this.virtio.queues[queue_id].flush_replies();
};

/**
 * Send a response with additional data after the header.
 * @param {number} queue_id
 * @param {VirtQueueBufferChain} bufchain
 * @param {Uint8Array} data
 */
VirtioGPU.prototype.send_response_data = function(queue_id, bufchain, data)
{
    bufchain.set_next_blob(data);
    this.virtio.queues[queue_id].push_reply(bufchain);
    this.virtio.queues[queue_id].flush_replies();
};

// --- State Management ---

VirtioGPU.prototype.get_state = function()
{
    const state = [];
    state[0] = this.virtio;
    state[1] = this.events_read;
    state[2] = this.scanouts.map(s => [s.resource_id, s.x, s.y, s.width, s.height, s.enabled ? 1 : 0]);
    return state;
};

VirtioGPU.prototype.set_state = function(state)
{
    this.virtio.set_state(state[0]);
    this.events_read = state[1];

    if(state[2])
    {
        for(let i = 0; i < Math.min(state[2].length, this.scanouts.length); i++)
        {
            const s = state[2][i];
            this.scanouts[i].resource_id = s[0];
            this.scanouts[i].x = s[1];
            this.scanouts[i].y = s[2];
            this.scanouts[i].width = s[3];
            this.scanouts[i].height = s[4];
            this.scanouts[i].enabled = s[5] !== 0;
        }
    }

    this.resources.clear();
};

VirtioGPU.prototype.reset = function()
{
    this.virtio.reset();
    this.resources.clear();

    for(const scanout of this.scanouts)
    {
        scanout.resource_id = 0;
        scanout.enabled = false;
        scanout.x = 0;
        scanout.y = 0;
        scanout.width = DEFAULT_WIDTH;
        scanout.height = DEFAULT_HEIGHT;
    }
};
