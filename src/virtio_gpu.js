import { dbg_assert, dbg_log } from "./log.js";
import { VirtIO, VIRTIO_F_VERSION_1 } from "./virtio.js";
import { LOG_VIRTIO } from "./const.js";

// For Types Only
import { CPU } from "./cpu.js";
import { BusConnector } from "./bus.js";

// https://docs.oasis-open.org/virtio/virtio/v1.2/csd01/virtio-v1.2-csd01.html
// https://www.kraxel.org/virtio/virtio-v1.1-csprd01.html#x1-3200007

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

// Number of virtual displays (scanouts)
const VIRTIO_GPU_MAX_SCANOUTS                   = 1;

// Header size for virtio_gpu_ctrl_hdr
// struct: type(4) + flags(4) + fence_id(8) + ctx_id(4) + padding(4) = 24 bytes
const CTRL_HDR_SIZE                             = 24;

// Default display size
const DEFAULT_WIDTH                             = 1024;
const DEFAULT_HEIGHT                            = 768;

/**
 * VirtIO GPU Device
 * Implements the virtio-gpu 2D protocol for hardware-accelerated
 * graphics in the browser via WebGL.
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

    // Scanout state: maps scanout_id -> { resource_id, x, y, width, height, enabled }
    this.scanouts = [];
    for(let i = 0; i < VIRTIO_GPU_MAX_SCANOUTS; i++)
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
    // pixels: Uint8Array (RGBA, width*height*4 bytes) or null
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
                // Notify initial display size
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
                    read: () => VIRTIO_GPU_MAX_SCANOUTS,
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
            default:
                dbg_log("virtio-gpu: unknown command 0x" + cmd_type.toString(16), LOG_VIRTIO);
                this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
                break;
        }
    }
};

/**
 * Handle cursorq notifications (cursor updates/moves).
 * @param {number} queue_id
 */
VirtioGPU.prototype.handle_cursor_queue = function(queue_id)
{
    dbg_assert(queue_id === 1, "VirtioGPU: cursorq must be queue 1");
    const queue = this.virtio.queues[queue_id];

    while(queue.has_request())
    {
        const bufchain = queue.pop_request();
        // Acknowledge cursor commands without processing for now
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
    }
};

/**
 * GET_DISPLAY_INFO: Return virtual display configuration.
 * Response: virtio_gpu_display_info (hdr + 16 * virtio_gpu_display_one)
 * virtio_gpu_display_one: rect(16) + enabled(4) + flags(4) = 24 bytes
 */
VirtioGPU.prototype.cmd_get_display_info = function(queue_id, bufchain, view)
{
    // Response: 24 (hdr) + 16 * 24 (pmodes) = 408 bytes
    const resp = new Uint8Array(24 + 16 * 24);
    const resp_view = new DataView(resp.buffer);

    resp_view.setUint32(0, VIRTIO_GPU_RESP_OK_DISPLAY_INFO, true);

    for(let i = 0; i < VIRTIO_GPU_MAX_SCANOUTS; i++)
    {
        const base = 24 + i * 24;
        const scanout = this.scanouts[i];
        resp_view.setUint32(base + 0,  0, true);                    // x
        resp_view.setUint32(base + 4,  0, true);                    // y
        resp_view.setUint32(base + 8,  scanout.width, true);        // width
        resp_view.setUint32(base + 12, scanout.height, true);       // height
        resp_view.setUint32(base + 16, scanout.enabled ? 1 : 0, true);  // enabled
        resp_view.setUint32(base + 20, 0, true);                    // flags
    }

    this.send_response_data(queue_id, bufchain, resp);
};

/**
 * RESOURCE_CREATE_2D: Allocate a new 2D GPU resource.
 * Command: hdr(24) + resource_id(4) + format(4) + width(4) + height(4) = 40 bytes
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

    if(resource_id === 0)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
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
 * RESOURCE_UNREF: Delete a resource.
 * Command: hdr(24) + resource_id(4) + padding(4) = 32 bytes
 */
VirtioGPU.prototype.cmd_resource_unref = function(queue_id, bufchain, view)
{
    const resource_id = view.getUint32(24, true);

    dbg_log("virtio-gpu: unref resource id=" + resource_id, LOG_VIRTIO);

    if(!this.resources.has(resource_id))
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    this.resources.delete(resource_id);
    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * SET_SCANOUT: Assign a resource to a scanout (display output).
 * Command: hdr(24) + rect(16) + scanout_id(4) + resource_id(4) = 48 bytes
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

    if(scanout_id >= VIRTIO_GPU_MAX_SCANOUTS)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_SCANOUT_ID);
        return;
    }

    const scanout = this.scanouts[scanout_id];

    if(resource_id === 0)
    {
        // Disable scanout
        scanout.enabled = false;
        scanout.resource_id = 0;
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
        return;
    }

    if(!this.resources.has(resource_id))
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    scanout.resource_id = resource_id;
    scanout.x = rect_x;
    scanout.y = rect_y;
    scanout.width = rect_w || this.resources.get(resource_id).width;
    scanout.height = rect_h || this.resources.get(resource_id).height;
    scanout.enabled = true;

    this.bus.send("virtio-gpu-set-size", [scanout.width, scanout.height]);

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * TRANSFER_TO_HOST_2D: Copy guest memory (backing store) into a resource.
 * Command: hdr(24) + rect(16) + offset_lo(4) + offset_hi(4) + resource_id(4) + padding(4) = 56 bytes
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
    const offset_lo   = view.getUint32(40, true);
    // offset_hi is ignored: we only support 32-bit physical addresses
    const resource_id = view.getUint32(48, true);

    dbg_log("virtio-gpu: transfer_to_host_2d resource=" + resource_id +
        " rect=(" + rect_x + "," + rect_y + "," + rect_w + "x" + rect_h + ")" +
        " offset=" + offset_lo, LOG_VIRTIO);

    const resource = this.resources.get(resource_id);
    if(!resource)
    {
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID);
        return;
    }

    if(!resource.backing_entries.length)
    {
        dbg_log("virtio-gpu: transfer on resource with no backing store", LOG_VIRTIO);
        this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_ERR_UNSPEC);
        return;
    }

    if(!resource.pixels)
    {
        resource.pixels = new Uint8Array(resource.width * resource.height * 4);
    }

    const bpp = 4;
    const stride = resource.width * bpp;
    const mem_size = this.cpu.mem8.length;

    // backing_entries[0].addr + offset_lo is the byte offset in guest physical
    // memory of the top-left pixel of the transfer rectangle.
    // Each row in the backing store is resource.width * bpp bytes wide.
    const base_addr = resource.backing_entries[0].addr + offset_lo;

    for(let row = 0; row < rect_h; row++)
    {
        const src_addr  = base_addr + row * stride;
        const row_bytes = rect_w * bpp;

        // Guard against invalid/out-of-range addresses supplied by the guest
        if(src_addr < 0 || src_addr + row_bytes > mem_size)
        {
            break;
        }

        const dst_start = (rect_y + row) * stride + rect_x * bpp;
        resource.pixels.set(this.cpu.mem8.subarray(src_addr, src_addr + row_bytes), dst_start);
    }

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * RESOURCE_FLUSH: Flush a resource to the display.
 * Command: hdr(24) + rect(16) + resource_id(4) + padding(4) = 48 bytes
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

    // Find all scanouts that use this resource and send an update
    for(let i = 0; i < VIRTIO_GPU_MAX_SCANOUTS; i++)
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
 * RESOURCE_ATTACH_BACKING: Attach guest memory pages to a resource.
 * Command: hdr(24) + resource_id(4) + nr_entries(4) = 32 bytes
 * Followed by nr_entries of virtio_gpu_mem_entry: addr_lo(4) + addr_hi(4) + length(4) + padding(4) = 16 bytes
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

    resource.backing_entries = [];
    resource.pixels = null; // Invalidate existing pixel data

    for(let i = 0; i < nr_entries; i++)
    {
        const entry_base = 32 + i * 16;
        if(entry_base + 16 > bufchain.length_readable)
        {
            break;
        }
        const addr_lo = view.getUint32(entry_base,     true);
        // addr_hi (offset entry_base+4) is ignored for 32-bit addressing
        const length  = view.getUint32(entry_base + 8, true);

        resource.backing_entries.push({ addr: addr_lo, length });
        dbg_log("virtio-gpu:   entry[" + i + "] addr=0x" + addr_lo.toString(16) +
            " len=" + length, LOG_VIRTIO);
    }

    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_NODATA);
};

/**
 * RESOURCE_DETACH_BACKING: Detach guest memory pages from a resource.
 * Command: hdr(24) + resource_id(4) + padding(4) = 32 bytes
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
 * GET_CAPSET_INFO: Return capability set info (we report no capsets for 2D-only mode).
 * Command: hdr(24) + capset_index(4) + padding(4) = 32 bytes
 */
VirtioGPU.prototype.cmd_get_capset_info = function(queue_id, bufchain, view)
{
    // Response: hdr(24) + capset_id(4) + capset_max_version(4) + capset_max_size(4) + padding(4) = 40 bytes
    const resp = new Uint8Array(40);
    const resp_view = new DataView(resp.buffer);
    resp_view.setUint32(0, VIRTIO_GPU_RESP_OK_CAPSET_INFO, true);
    // All zeros: no capsets supported
    this.send_response_data(queue_id, bufchain, resp);
};

/**
 * GET_CAPSET: Return capability set data (empty for 2D-only mode).
 * Command: hdr(24) + capset_id(4) + capset_version(4) = 32 bytes
 */
VirtioGPU.prototype.cmd_get_capset = function(queue_id, bufchain, view)
{
    // Response: hdr(24) only (no capset data for 2D mode)
    this.send_response(queue_id, bufchain, VIRTIO_GPU_RESP_OK_CAPSET);
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
 * @param {Uint8Array} data  Full response buffer (header + payload)
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
    // Scanout state
    const scanout_state = this.scanouts.map(s => [s.resource_id, s.x, s.y, s.width, s.height, s.enabled ? 1 : 0]);
    state[2] = scanout_state;
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
    // Resources with pixel data are not persisted (they live in guest RAM)
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
        scanout.width = DEFAULT_WIDTH;
        scanout.height = DEFAULT_HEIGHT;
    }
};


