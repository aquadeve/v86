#!/usr/bin/env node

import assert from "assert/strict";

import { VirtioGPU } from "../../src/virtio_gpu.js";

process.on("unhandledRejection", exn => { throw exn; });

const VIRTIO_GPU_RESP_OK_NODATA = 0x1100;

function make_transfer_view({ x, y, width, height, offset, resource_id })
{
    const buffer = new ArrayBuffer(56);
    const view = new DataView(buffer);
    view.setUint32(24, x, true);
    view.setUint32(28, y, true);
    view.setUint32(32, width, true);
    view.setUint32(36, height, true);
    view.setUint32(40, offset >>> 0, true);
    view.setUint32(44, Math.floor(offset / 0x100000000), true);
    view.setUint32(48, resource_id, true);
    return view;
}

function make_flush_view({ x, y, width, height, resource_id })
{
    const buffer = new ArrayBuffer(48);
    const view = new DataView(buffer);
    view.setUint32(24, x, true);
    view.setUint32(28, y, true);
    view.setUint32(32, width, true);
    view.setUint32(36, height, true);
    view.setUint32(40, resource_id, true);
    return view;
}

function make_gpu(mem8, resource)
{
    const bus_messages = [];
    const responses = [];
    const gpu = {
        cpu: { mem8 },
        resources: new Map([[resource.resource_id, resource]]),
        scanouts: [{ resource_id: resource.resource_id, enabled: true }],
        bus: {
            send(name, payload)
            {
                bus_messages.push({ name, payload });
            },
        },
        send_response(queue_id, bufchain, response_type)
        {
            responses.push(response_type);
        },
    };

    gpu._u64 = VirtioGPU.prototype._u64;
    gpu._copy_from_backing = VirtioGPU.prototype._copy_from_backing;

    return { gpu, bus_messages, responses };
}

{
    const mem8 = new Uint8Array(64);
    for(let i = 0; i < 48; i++)
    {
        mem8[i] = i;
    }

    const resource = {
        resource_id: 1,
        width: 4,
        height: 3,
        format: 67,
        backing_entries: [{ addr: 0, length: 48 }],
        pixels: new Uint8Array(48),
    };

    const { gpu, responses } = make_gpu(mem8, resource);
    const view = make_transfer_view({ x: 0, y: 1, width: 4, height: 2, offset: 16, resource_id: 1 });

    VirtioGPU.prototype.cmd_transfer_to_host_2d.call(gpu, 0, { length_readable: 56 }, view);

    assert.deepEqual(
        Array.from(resource.pixels.subarray(16, 48)),
        Array.from(mem8.subarray(16, 48)),
        "full-width transfers should copy contiguous rows without changing pixel layout"
    );
    assert.deepEqual(responses, [VIRTIO_GPU_RESP_OK_NODATA]);
}

{
    const mem8 = new Uint8Array(32);
    for(let i = 0; i < 24; i++)
    {
        mem8[i] = 100 + i;
    }

    const resource = {
        resource_id: 2,
        width: 3,
        height: 2,
        format: 67,
        backing_entries: [
            { addr: 0, length: 8 },
            { addr: 8, length: 16 },
        ],
        pixels: new Uint8Array(24),
    };

    const { gpu, responses } = make_gpu(mem8, resource);
    const view = make_transfer_view({ x: 1, y: 0, width: 2, height: 2, offset: 4, resource_id: 2 });

    VirtioGPU.prototype.cmd_transfer_to_host_2d.call(gpu, 0, { length_readable: 56 }, view);

    assert.deepEqual(
        Array.from(resource.pixels),
        [
            0, 0, 0, 0,
            104, 105, 106, 107,
            108, 109, 110, 111,
            0, 0, 0, 0,
            116, 117, 118, 119,
            120, 121, 122, 123,
        ],
        "split backing entries should still fill the requested destination rectangle"
    );
    assert.deepEqual(responses, [VIRTIO_GPU_RESP_OK_NODATA]);
}

{
    const mem8 = new Uint8Array(16);
    const pixels = new Uint8Array(16);
    const resource = {
        resource_id: 3,
        width: 2,
        height: 2,
        format: 67,
        backing_entries: [{ addr: 0, length: 16 }],
        pixels,
    };

    const { gpu, bus_messages, responses } = make_gpu(mem8, resource);
    const view = make_flush_view({ x: 0, y: 1, width: 2, height: 1, resource_id: 3 });

    VirtioGPU.prototype.cmd_resource_flush.call(gpu, 0, { length_readable: 48 }, view);

    assert.equal(bus_messages.length, 1);
    assert.equal(bus_messages[0].name, "virtio-gpu-update-buffer");
    assert.equal(bus_messages[0].payload.x, 0);
    assert.equal(bus_messages[0].payload.y, 1);
    assert.equal(bus_messages[0].payload.width, 2);
    assert.equal(bus_messages[0].payload.height, 1);
    assert.equal(bus_messages[0].payload.buffer, pixels);
    assert.deepEqual(responses, [VIRTIO_GPU_RESP_OK_NODATA]);
}

console.log("virtio_gpu tests passed");
