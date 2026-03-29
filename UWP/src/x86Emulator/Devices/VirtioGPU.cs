using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Windows.UI;
using x86Emulator.GUI;

namespace x86Emulator.Devices
{
    /// <summary>
    /// VirtIO GPU 2D device (virtio-gpu, PCI device-id 0x1050).
    ///
    /// Ported from src/virtio_gpu.js in the v86 JavaScript project.
    ///
    /// Instead of WebGL (used in the browser), this implementation renders
    /// to a D3D11 texture via the existing <see cref="GpuPassthrough"/> class,
    /// which provides Map/Unmap-based zero-copy frame upload into a persistent
    /// <c>D3D11_USAGE_DYNAMIC</c> texture.
    ///
    /// The device exposes:
    ///   - I/O BARs at base 0xE600 (device-specific) and 0xE800 (common config)
    ///     and 0xE900 (notification) and 0xE700 (ISR status).
    ///   - Two VirtQueues: controlq (index 0) and cursorq (index 1).
    ///   - VirtIO 1.x feature bits.
    ///
    /// References:
    ///   https://docs.oasis-open.org/virtio/virtio/v1.2/csd01/virtio-v1.2-csd01.html
    ///   https://github.com/torvalds/linux/blob/master/include/uapi/linux/virtio_gpu.h
    /// </summary>
    public class VirtioGPU : IDevice, INeedsIRQ, IShutdown
    {
        // ── VirtIO GPU command types ───────────────────────────────────────────
        private const uint VIRTIO_GPU_CMD_GET_DISPLAY_INFO        = 0x100;
        private const uint VIRTIO_GPU_CMD_RESOURCE_CREATE_2D      = 0x101;
        private const uint VIRTIO_GPU_CMD_RESOURCE_UNREF          = 0x102;
        private const uint VIRTIO_GPU_CMD_SET_SCANOUT              = 0x103;
        private const uint VIRTIO_GPU_CMD_RESOURCE_FLUSH          = 0x104;
        private const uint VIRTIO_GPU_CMD_TRANSFER_TO_HOST_2D     = 0x105;
        private const uint VIRTIO_GPU_CMD_RESOURCE_ATTACH_BACKING = 0x106;
        private const uint VIRTIO_GPU_CMD_RESOURCE_DETACH_BACKING = 0x107;
        private const uint VIRTIO_GPU_CMD_GET_CAPSET_INFO         = 0x108;
        private const uint VIRTIO_GPU_CMD_GET_CAPSET              = 0x109;
        private const uint VIRTIO_GPU_CMD_GET_EDID                = 0x10A;

        // ── Response codes ────────────────────────────────────────────────────
        private const uint VIRTIO_GPU_RESP_OK_NODATA              = 0x1100;
        private const uint VIRTIO_GPU_RESP_OK_DISPLAY_INFO        = 0x1101;
        private const uint VIRTIO_GPU_RESP_OK_CAPSET_INFO         = 0x1104;
        private const uint VIRTIO_GPU_RESP_OK_CAPSET              = 0x1105;
        private const uint VIRTIO_GPU_RESP_OK_EDID                = 0x1106;
        private const uint VIRTIO_GPU_RESP_ERR_UNSPEC             = 0x1200;
        private const uint VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID = 0x1203;
        private const uint VIRTIO_GPU_RESP_ERR_INVALID_SCANOUT_ID  = 0x1202;
        private const uint VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER   = 0x1205;

        // ── Pixel formats ─────────────────────────────────────────────────────
        private const uint FMT_B8G8R8A8 = 1;
        private const uint FMT_B8G8R8X8 = 2;
        private const uint FMT_A8R8G8B8 = 3;
        private const uint FMT_X8R8G8B8 = 4;
        private const uint FMT_R8G8B8A8 = 67;
        private const uint FMT_X8B8G8R8 = 68;
        private const uint FMT_A8B8G8R8 = 121;
        private const uint FMT_R8G8B8X8 = 134;

        private static readonly HashSet<uint> SupportedFormats = new HashSet<uint>
        { FMT_B8G8R8A8, FMT_B8G8R8X8, FMT_A8R8G8B8, FMT_X8R8G8B8,
          FMT_R8G8B8A8, FMT_X8B8G8R8, FMT_A8B8G8R8, FMT_R8G8B8X8 };

        private const int VIRTIO_GPU_MAX_SCANOUTS      = 16;
        private const int VIRTIO_GPU_SUPPORTED_SCANOUTS = 1;
        private const int CTRL_HDR_SIZE                = 24;
        private const int DEFAULT_WIDTH                = 1024;
        private const int DEFAULT_HEIGHT               = 768;

        // ── VirtIO common config port base addresses ──────────────────────────
        private const ushort PORT_COMMON_CFG  = 0xE800; // common config (20 bytes)
        private const ushort PORT_NOTIFY      = 0xE900; // notification (queue notify)
        private const ushort PORT_ISR_STATUS  = 0xE700; // ISR status
        private const ushort PORT_DEVICE_SPEC = 0xE600; // device-specific config (16 bytes)

        // ── I/O port list (all ranges for the four BARs) ──────────────────────
        private readonly int[] portsUsed;

        // ── VirtIO device/driver status ───────────────────────────────────────
        private byte  deviceStatus;
        private uint  driverFeaturesLo;
        private uint  driverFeaturesHi;
        private ushort queueSelect;
        private uint  eventsRead;

        // ── Queues ────────────────────────────────────────────────────────────
        private readonly VirtQueue[] queues = new VirtQueue[]
        {
            new VirtQueue(64, 0),   // controlq
            new VirtQueue(16, 1),   // cursorq
        };

        // ── GPU resources ─────────────────────────────────────────────────────
        private struct GpuResource
        {
            public uint ResourceId;
            public uint Format;
            public uint Width;
            public uint Height;
            public List<(uint addr, uint length)> BackingEntries;
            public byte[] Pixels; // RGBA pixel buffer (width * height * 4 bytes)
        }

        private readonly Dictionary<uint, GpuResource> resources = new Dictionary<uint, GpuResource>();

        // ── Scanouts ──────────────────────────────────────────────────────────
        private struct Scanout
        {
            public uint ResourceId;
            public uint X, Y, Width, Height;
            public bool Enabled;
        }

        private readonly Scanout[] scanouts = new Scanout[VIRTIO_GPU_SUPPORTED_SCANOUTS];

        // ── D3D11 rendering backend ───────────────────────────────────────────
        private readonly GpuPassthrough gpuPassthrough;

        // Reusable BGRA scratch buffer (avoid per-frame heap allocation)
        private byte[] bgraBuffer;

        // ── IRQ ───────────────────────────────────────────────────────────────
        private const int IRQ_NUMBER = 11; // PCI INTA on slot 0x0D

        public event EventHandler IRQ;
        public int IRQNumber => IRQ_NUMBER;

        // ── IDevice ───────────────────────────────────────────────────────────
        public int[] PortsUsed => portsUsed;

        // ── Latest rendered frame (for WIN2D display loop) ─────────────────────
        /// <summary>The most recently uploaded GPU frame, or null.</summary>
        public CanvasBitmap CurrentFrame { get; private set; }

        /// <summary>
        /// Fired when a RESOURCE_FLUSH command updates the screen.
        /// The WIN2D render loop should redraw when this fires.
        /// </summary>
        public event EventHandler FrameUpdated;

        public VirtioGPU(GpuPassthrough passthrough)
        {
            gpuPassthrough = passthrough ?? throw new ArgumentNullException(nameof(passthrough));

            // Initialise scanouts
            for (int i = 0; i < scanouts.Length; i++)
                scanouts[i] = new Scanout { Width = DEFAULT_WIDTH, Height = DEFAULT_HEIGHT };

            // Build port list: four I/O BARs, 16 ports each
            var ports = new List<int>();
            for (int i = 0; i < 16; i++) ports.Add(PORT_COMMON_CFG  + i);
            for (int i = 0; i < 16; i++) ports.Add(PORT_NOTIFY      + i);
            for (int i = 0; i <  4; i++) ports.Add(PORT_ISR_STATUS  + i);
            for (int i = 0; i < 16; i++) ports.Add(PORT_DEVICE_SPEC + i);
            portsUsed = ports.ToArray();

            Debug.WriteLine("[VirtioGPU] Device initialized (D3D11 backend)");
        }

        // ══════════════════════════════════════════════════════════════════════
        // IDevice
        // ══════════════════════════════════════════════════════════════════════

        public uint Read(ushort addr, int size)
        {
            // ISR status
            if (addr >= PORT_ISR_STATUS && addr < PORT_ISR_STATUS + 4)
            {
                uint v = 0;
                // Return 0x01 (queue interrupt) after driver-ok to satisfy driver
                if ((deviceStatus & VirtIOConst.DEVICE_STATUS_DRIVER_OK) != 0)
                    v = 0x01;
                return v;
            }

            // Common config
            if (addr >= PORT_COMMON_CFG && addr < PORT_COMMON_CFG + 32)
                return ReadCommonConfig(addr - PORT_COMMON_CFG);

            // Device-specific config
            if (addr >= PORT_DEVICE_SPEC && addr < PORT_DEVICE_SPEC + 16)
                return ReadDeviceSpecific(addr - PORT_DEVICE_SPEC);

            // Notify (read not defined in spec; return 0)
            return 0;
        }

        public void Write(ushort addr, uint value, int size)
        {
            // Common config
            if (addr >= PORT_COMMON_CFG && addr < PORT_COMMON_CFG + 32)
            {
                WriteCommonConfig(addr - PORT_COMMON_CFG, value, size);
                return;
            }

            // Device-specific config
            if (addr >= PORT_DEVICE_SPEC && addr < PORT_DEVICE_SPEC + 16)
            {
                WriteDeviceSpecific(addr - PORT_DEVICE_SPEC, value);
                return;
            }

            // Queue notification
            if (addr >= PORT_NOTIFY && addr < PORT_NOTIFY + 8)
            {
                int queueIdx = (addr - PORT_NOTIFY) / 2;
                NotifyQueue(queueIdx);
                return;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // IShutdown
        // ══════════════════════════════════════════════════════════════════════

        public void Shutdown()
        {
            deviceStatus = 0;
            resources.Clear();
        }

        // ══════════════════════════════════════════════════════════════════════
        // VirtIO common config (PORT_COMMON_CFG offsets)
        // Implements the VirtIO 1.x common configuration structure
        // ══════════════════════════════════════════════════════════════════════

        // Offsets within the common config structure
        private const int OFF_DEVICE_FEATURE_SELECT = 0;  // 4 bytes
        private const int OFF_DEVICE_FEATURE        = 4;  // 4 bytes (read-only)
        private const int OFF_DRIVER_FEATURE_SELECT = 8;  // 4 bytes
        private const int OFF_DRIVER_FEATURE        = 12; // 4 bytes
        private const int OFF_CONFIG_MSIX_VECTOR    = 16; // 2 bytes
        private const int OFF_NUM_QUEUES            = 18; // 2 bytes (read-only)
        private const int OFF_DEVICE_STATUS         = 20; // 1 byte
        private const int OFF_CONFIG_GENERATION     = 21; // 1 byte (read-only)
        private const int OFF_QUEUE_SELECT          = 22; // 2 bytes
        private const int OFF_QUEUE_SIZE            = 24; // 2 bytes
        private const int OFF_QUEUE_MSIX_VECTOR     = 26; // 2 bytes
        private const int OFF_QUEUE_ENABLE          = 28; // 2 bytes
        private const int OFF_QUEUE_NOTIFY_OFF      = 30; // 2 bytes (read-only)
        // Extended queue addresses (virtio-1.0)
        private const int OFF_QUEUE_DESC_LO         = 32; // 4 bytes
        private const int OFF_QUEUE_DESC_HI         = 36; // 4 bytes
        private const int OFF_QUEUE_DRIVER_LO       = 40; // 4 bytes (avail ring)
        private const int OFF_QUEUE_DRIVER_HI       = 44; // 4 bytes
        private const int OFF_QUEUE_DEVICE_LO       = 48; // 4 bytes (used ring)
        private const int OFF_QUEUE_DEVICE_HI       = 52; // 4 bytes

        private uint deviceFeatureSelect;

        // Device features: advertise VERSION_1 only (no VIRGL, no EDID)
        private static readonly uint[] DeviceFeatures = { 0, 1 }; // word0=0, word1=1 (VERSION_1 bit 0)

        private uint ReadCommonConfig(int off)
        {
            switch (off)
            {
                case OFF_DEVICE_FEATURE:
                    return deviceFeatureSelect < DeviceFeatures.Length
                        ? DeviceFeatures[deviceFeatureSelect] : 0;

                case OFF_NUM_QUEUES:      return (uint)queues.Length;
                case OFF_DEVICE_STATUS:   return deviceStatus;
                case OFF_CONFIG_GENERATION: return 0;

                case OFF_QUEUE_SELECT:    return queueSelect;
                case OFF_QUEUE_SIZE:
                    return queueSelect < queues.Length ? (uint)queues[queueSelect].Size : 0;
                case OFF_QUEUE_NOTIFY_OFF:
                    return queueSelect < queues.Length ? (uint)queues[queueSelect].NotifyOffset : 0;
                case OFF_QUEUE_ENABLE:    return 1;

                default: return 0;
            }
        }

        private void WriteCommonConfig(int off, uint value, int size)
        {
            switch (off)
            {
                case OFF_DEVICE_FEATURE_SELECT:
                    deviceFeatureSelect = value;
                    break;

                case OFF_DRIVER_FEATURE_SELECT: break; // we don't need to track this separately

                case OFF_DRIVER_FEATURE:
                    if (deviceFeatureSelect == 0) driverFeaturesLo = value;
                    else                           driverFeaturesHi = value;
                    break;

                case OFF_DEVICE_STATUS:
                    deviceStatus = (byte)(value & 0xFF);
                    if (deviceStatus == VirtIOConst.DEVICE_STATUS_RESET)
                        ResetDevice();
                    else if ((deviceStatus & VirtIOConst.DEVICE_STATUS_DRIVER_OK) != 0)
                        OnDriverOk();
                    break;

                case OFF_QUEUE_SELECT:
                    queueSelect = (ushort)(value & 0xFFFF);
                    break;

                case OFF_QUEUE_DESC_LO:
                    if (queueSelect < queues.Length)
                        SetQueueDescLo(queueSelect, value);
                    break;
                case OFF_QUEUE_DESC_HI: break; // 64-bit guest only; we use 32-bit addrs
                case OFF_QUEUE_DRIVER_LO:
                    if (queueSelect < queues.Length)
                        SetQueueAvailLo(queueSelect, value);
                    break;
                case OFF_QUEUE_DRIVER_HI: break;
                case OFF_QUEUE_DEVICE_LO:
                    if (queueSelect < queues.Length)
                        SetQueueUsedLo(queueSelect, value);
                    break;
                case OFF_QUEUE_DEVICE_HI: break;
            }
        }

        // Queue address staging (desc/avail/used written separately)
        private readonly uint[] queueDescLo  = new uint[2];
        private readonly uint[] queueAvailLo = new uint[2];
        private readonly uint[] queueUsedLo  = new uint[2];

        private void SetQueueDescLo(int q, uint addr)
        {
            queueDescLo[q] = addr;
            TryActivateQueue(q);
        }

        private void SetQueueAvailLo(int q, uint addr)
        {
            queueAvailLo[q] = addr;
            TryActivateQueue(q);
        }

        private void SetQueueUsedLo(int q, uint addr)
        {
            queueUsedLo[q] = addr;
            TryActivateQueue(q);
        }

        private void TryActivateQueue(int q)
        {
            if (queueDescLo[q] != 0 && queueAvailLo[q] != 0 && queueUsedLo[q] != 0)
            {
                queues[q].SetAddresses(queueDescLo[q], queueAvailLo[q], queueUsedLo[q]);
                Debug.WriteLine($"[VirtioGPU] Queue {q} activated desc=0x{queueDescLo[q]:X8} avail=0x{queueAvailLo[q]:X8} used=0x{queueUsedLo[q]:X8}");
            }
        }

        private void ResetDevice()
        {
            driverFeaturesLo = driverFeaturesHi = 0;
            queueSelect = 0;
            foreach (var q in queues) q.Reset();
            resources.Clear();
            Debug.WriteLine("[VirtioGPU] Device reset");
        }

        private void OnDriverOk()
        {
            Debug.WriteLine("[VirtioGPU] Driver OK – device ready");
        }

        // ── Device-specific config (events_read, events_clear, num_scanouts, num_capsets)
        private uint ReadDeviceSpecific(int off)
        {
            switch (off)
            {
                case 0: return eventsRead;
                case 4: return 0;  // events_clear (write-only)
                case 8: return VIRTIO_GPU_SUPPORTED_SCANOUTS;
                case 12: return 0; // num_capsets
                default: return 0;
            }
        }

        private void WriteDeviceSpecific(int off, uint value)
        {
            if (off == 4) eventsRead &= ~value; // events_clear
        }

        // ══════════════════════════════════════════════════════════════════════
        // Queue notification handler
        // ══════════════════════════════════════════════════════════════════════

        private void NotifyQueue(int queueIdx)
        {
            if (queueIdx == 0) HandleControlQueue();
            else if (queueIdx == 1) HandleCursorQueue();
        }

        private void HandleControlQueue()
        {
            while (queues[0].HasRequest())
            {
                if (!queues[0].TryPopRequest(out byte[] buf, out uint token, out int _))
                    break;

                if (buf.Length < CTRL_HDR_SIZE)
                {
                    SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC);
                    continue;
                }

                uint cmdType = ReadU32LE(buf, 0);
                HandleCommand(cmdType, buf, token);
            }

            // Raise interrupt so driver processes used ring
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        private void HandleCursorQueue()
        {
            while (queues[1].HasRequest())
            {
                if (!queues[1].TryPopRequest(out _, out uint token, out _))
                    break;
                SendResponse(1, token, VIRTIO_GPU_RESP_OK_NODATA);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // GPU command dispatch
        // ══════════════════════════════════════════════════════════════════════

        private void HandleCommand(uint cmdType, byte[] buf, uint token)
        {
            switch (cmdType)
            {
                case VIRTIO_GPU_CMD_GET_DISPLAY_INFO:
                    CmdGetDisplayInfo(token);
                    break;
                case VIRTIO_GPU_CMD_RESOURCE_CREATE_2D:
                    CmdResourceCreate2D(buf, token);
                    break;
                case VIRTIO_GPU_CMD_RESOURCE_UNREF:
                    CmdResourceUnref(buf, token);
                    break;
                case VIRTIO_GPU_CMD_SET_SCANOUT:
                    CmdSetScanout(buf, token);
                    break;
                case VIRTIO_GPU_CMD_RESOURCE_FLUSH:
                    CmdResourceFlush(buf, token);
                    break;
                case VIRTIO_GPU_CMD_TRANSFER_TO_HOST_2D:
                    CmdTransferToHost2D(buf, token);
                    break;
                case VIRTIO_GPU_CMD_RESOURCE_ATTACH_BACKING:
                    CmdResourceAttachBacking(buf, token);
                    break;
                case VIRTIO_GPU_CMD_RESOURCE_DETACH_BACKING:
                    CmdResourceDetachBacking(buf, token);
                    break;
                case VIRTIO_GPU_CMD_GET_CAPSET_INFO:
                    CmdGetCapsetInfo(token);
                    break;
                case VIRTIO_GPU_CMD_GET_CAPSET:
                    CmdGetCapset(token);
                    break;
                case VIRTIO_GPU_CMD_GET_EDID:
                    SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC);
                    break;
                default:
                    Debug.WriteLine($"[VirtioGPU] Unknown command 0x{cmdType:X4}");
                    SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC);
                    break;
            }
        }

        // ── GET_DISPLAY_INFO ──────────────────────────────────────────────────

        private void CmdGetDisplayInfo(uint token)
        {
            // Response: hdr(24) + 16 × display_slot(24) = 24 + 384 = 408 bytes
            var resp = new byte[CTRL_HDR_SIZE + VIRTIO_GPU_MAX_SCANOUTS * 24];
            WriteU32LE(resp, 0, VIRTIO_GPU_RESP_OK_DISPLAY_INFO);

            for (int i = 0; i < VIRTIO_GPU_MAX_SCANOUTS; i++)
            {
                int b = CTRL_HDR_SIZE + i * 24;
                if (i < scanouts.Length)
                {
                    var s = scanouts[i];
                    WriteU32LE(resp, b + 0,  s.X);
                    WriteU32LE(resp, b + 4,  s.Y);
                    WriteU32LE(resp, b + 8,  s.Width);
                    WriteU32LE(resp, b + 12, s.Height);
                    WriteU32LE(resp, b + 16, 1); // enabled
                    WriteU32LE(resp, b + 20, 0); // flags
                }
            }

            queues[0].PushUsed(token, resp);
        }

        // ── RESOURCE_CREATE_2D ────────────────────────────────────────────────

        private void CmdResourceCreate2D(byte[] buf, uint token)
        {
            if (buf.Length < 40) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }

            uint resourceId = ReadU32LE(buf, 24);
            uint format     = ReadU32LE(buf, 28);
            uint width      = ReadU32LE(buf, 32);
            uint height     = ReadU32LE(buf, 36);

            if (resourceId == 0 || resources.ContainsKey(resourceId))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            if (width == 0 || height == 0 || !SupportedFormats.Contains(format))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_PARAMETER); return; }

            resources[resourceId] = new GpuResource
            {
                ResourceId = resourceId,
                Format     = format,
                Width      = width,
                Height     = height,
                BackingEntries = new List<(uint, uint)>(),
                Pixels     = null,
            };

            Debug.WriteLine($"[VirtioGPU] Create resource id={resourceId} fmt={format} {width}×{height}");
            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── RESOURCE_UNREF ────────────────────────────────────────────────────

        private void CmdResourceUnref(byte[] buf, uint token)
        {
            if (buf.Length < 32) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }
            uint id = ReadU32LE(buf, 24);

            if (!resources.ContainsKey(id))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            for (int i = 0; i < scanouts.Length; i++)
            {
                if (scanouts[i].ResourceId == id)
                    scanouts[i] = new Scanout { Width = DEFAULT_WIDTH, Height = DEFAULT_HEIGHT };
            }

            resources.Remove(id);
            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── SET_SCANOUT ───────────────────────────────────────────────────────

        private void CmdSetScanout(byte[] buf, uint token)
        {
            if (buf.Length < 48) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }

            uint scanoutId  = ReadU32LE(buf, 40);
            uint resourceId = ReadU32LE(buf, 44);

            if (scanoutId >= VIRTIO_GPU_SUPPORTED_SCANOUTS)
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_SCANOUT_ID); return; }

            if (resourceId == 0)
            {
                scanouts[scanoutId] = new Scanout { Width = DEFAULT_WIDTH, Height = DEFAULT_HEIGHT };
                SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
                return;
            }

            if (!resources.ContainsKey(resourceId))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            var res = resources[resourceId];
            scanouts[scanoutId] = new Scanout
            {
                ResourceId = resourceId,
                X      = ReadU32LE(buf, 24),
                Y      = ReadU32LE(buf, 28),
                Width  = ReadU32LE(buf, 32) == 0 ? res.Width  : ReadU32LE(buf, 32),
                Height = ReadU32LE(buf, 36) == 0 ? res.Height : ReadU32LE(buf, 36),
                Enabled = true,
            };

            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── TRANSFER_TO_HOST_2D ───────────────────────────────────────────────

        private void CmdTransferToHost2D(byte[] buf, uint token)
        {
            if (buf.Length < 56) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }

            uint rectX      = ReadU32LE(buf, 24);
            uint rectY      = ReadU32LE(buf, 28);
            uint rectW      = ReadU32LE(buf, 32);
            uint rectH      = ReadU32LE(buf, 36);
            uint offsetLo   = ReadU32LE(buf, 40);
            // offsetHi (bytes 44-47) ignored – 32-bit guest
            uint resourceId = ReadU32LE(buf, 48);

            if (!resources.ContainsKey(resourceId))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            var res = resources[resourceId];
            if (res.BackingEntries.Count == 0 || rectW == 0 || rectH == 0)
            { SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA); return; }

            if (res.Pixels == null || res.Pixels.Length != res.Width * res.Height * 4)
                res.Pixels = new byte[res.Width * res.Height * 4];

            uint stride   = res.Width * 4;
            uint rowBytes = rectW * 4;

            for (uint row = 0; row < rectH; row++)
            {
                uint srcLogical = offsetLo + row * stride;
                uint dstStart   = (rectY + row) * stride + rectX * 4;

                // Read from guest memory via backing entries
                CopyFromBacking(res, (int)srcLogical, res.Pixels, (int)dstStart, (int)rowBytes);
            }

            resources[resourceId] = res; // struct – write back
            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── RESOURCE_FLUSH ────────────────────────────────────────────────────

        private void CmdResourceFlush(byte[] buf, uint token)
        {
            if (buf.Length < 48) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }

            uint rectX      = ReadU32LE(buf, 24);
            uint rectY      = ReadU32LE(buf, 28);
            uint rectW      = ReadU32LE(buf, 32);
            uint rectH      = ReadU32LE(buf, 36);
            uint resourceId = ReadU32LE(buf, 40);

            if (!resources.ContainsKey(resourceId))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            var res = resources[resourceId];

            for (int i = 0; i < scanouts.Length; i++)
            {
                var s = scanouts[i];
                if (s.ResourceId == resourceId && s.Enabled && res.Pixels != null)
                    FlushToDisplay(res, s, rectX, rectY, rectW, rectH);
            }

            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── RESOURCE_ATTACH_BACKING ───────────────────────────────────────────

        private void CmdResourceAttachBacking(byte[] buf, uint token)
        {
            if (buf.Length < 32) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }

            uint resourceId = ReadU32LE(buf, 24);
            uint nrEntries  = ReadU32LE(buf, 28);

            if (!resources.ContainsKey(resourceId))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            int needed = 32 + (int)nrEntries * 16;
            if (buf.Length < needed)
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }

            var res = resources[resourceId];
            res.BackingEntries = new List<(uint, uint)>();
            res.Pixels = null;

            for (int i = 0; i < (int)nrEntries; i++)
            {
                int b      = 32 + i * 16;
                uint addr  = ReadU32LE(buf, b);     // 64-bit, but we only use lower 32
                uint len   = ReadU32LE(buf, b + 8);
                res.BackingEntries.Add((addr, len));
            }

            resources[resourceId] = res;
            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── RESOURCE_DETACH_BACKING ───────────────────────────────────────────

        private void CmdResourceDetachBacking(byte[] buf, uint token)
        {
            if (buf.Length < 32) { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_UNSPEC); return; }
            uint resourceId = ReadU32LE(buf, 24);

            if (!resources.ContainsKey(resourceId))
            { SendResponse(0, token, VIRTIO_GPU_RESP_ERR_INVALID_RESOURCE_ID); return; }

            var res = resources[resourceId];
            res.BackingEntries.Clear();
            res.Pixels = null;
            resources[resourceId] = res;
            SendResponse(0, token, VIRTIO_GPU_RESP_OK_NODATA);
        }

        // ── GET_CAPSET_INFO ───────────────────────────────────────────────────

        private void CmdGetCapsetInfo(uint token)
        {
            // No capsets supported (no VIRGL)
            var resp = new byte[CTRL_HDR_SIZE + 8];
            WriteU32LE(resp, 0, VIRTIO_GPU_RESP_OK_CAPSET_INFO);
            queues[0].PushUsed(token, resp);
        }

        private void CmdGetCapset(uint token)
        {
            var resp = new byte[CTRL_HDR_SIZE];
            WriteU32LE(resp, 0, VIRTIO_GPU_RESP_OK_CAPSET);
            queues[0].PushUsed(token, resp);
        }

        // ══════════════════════════════════════════════════════════════════════
        // D3D11 display flush (replaces WebGL path from v86)
        // ══════════════════════════════════════════════════════════════════════

        private void FlushToDisplay(GpuResource res, Scanout scanout,
                                    uint rectX, uint rectY, uint rectW, uint rectH)
        {
            if (res.Pixels == null || !gpuPassthrough.IsInitialized) return;

            int width  = (int)res.Width;
            int height = (int)res.Height;
            int pixelCount = width * height;

            EnsureBgraBuffer(pixelCount * 4);

            // Convert from VirtIO pixel format to BGRA (D3D11 native)
            ConvertToBgra(res.Pixels, res.Format, bgraBuffer, pixelCount);

            var bitmap = gpuPassthrough.UploadBgraFrameRaw(bgraBuffer, (uint)width, (uint)height);
            if (bitmap != null)
            {
                CurrentFrame = bitmap;
                FrameUpdated?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ConvertToBgra(byte[] src, uint format, byte[] dst, int pixelCount)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int s = i * 4;
                int d = i * 4;
                byte a = src[s + 3];

                switch (format)
                {
                    case FMT_B8G8R8A8: // BGRA → BGRA (no-op)
                    case FMT_B8G8R8X8: // BGRX → BGRA
                        dst[d]     = src[s];
                        dst[d + 1] = src[s + 1];
                        dst[d + 2] = src[s + 2];
                        dst[d + 3] = format == FMT_B8G8R8X8 ? (byte)0xFF : a;
                        break;

                    case FMT_R8G8B8A8: // RGBA → BGRA
                    case FMT_R8G8B8X8:
                        dst[d]     = src[s + 2];
                        dst[d + 1] = src[s + 1];
                        dst[d + 2] = src[s];
                        dst[d + 3] = format == FMT_R8G8B8X8 ? (byte)0xFF : a;
                        break;

                    case FMT_A8R8G8B8: // ARGB → BGRA  (src: A,R,G,B)
                    case FMT_X8R8G8B8:
                        dst[d]     = src[s + 3];          // B
                        dst[d + 1] = src[s + 2];          // G
                        dst[d + 2] = src[s + 1];          // R
                        dst[d + 3] = format == FMT_X8R8G8B8 ? (byte)0xFF : src[s]; // A
                        break;

                    case FMT_A8B8G8R8: // ABGR → BGRA  (src: A,B,G,R)
                    case FMT_X8B8G8R8:
                        dst[d]     = src[s + 1];          // B
                        dst[d + 1] = src[s + 2];          // G
                        dst[d + 2] = src[s + 3];          // R
                        dst[d + 3] = format == FMT_X8B8G8R8 ? (byte)0xFF : src[s]; // A
                        break;

                    default:
                        dst[d] = dst[d + 1] = dst[d + 2] = dst[d + 3] = 0;
                        break;
                }
            }
        }

        private void EnsureBgraBuffer(int needed)
        {
            if (bgraBuffer == null || bgraBuffer.Length < needed)
                bgraBuffer = new byte[needed];
        }

        // ══════════════════════════════════════════════════════════════════════
        // Guest memory helpers
        // ══════════════════════════════════════════════════════════════════════

        private static void CopyFromBacking(GpuResource res, int logicalOffset,
                                            byte[] target, int targetOffset, int length)
        {
            if (length <= 0 || logicalOffset < 0) return;

            int skip = logicalOffset;
            int outOff = targetOffset;
            int remaining = length;

            foreach (var (addr, entryLen) in res.BackingEntries)
            {
                if (remaining <= 0) break;
                int el = (int)entryLen;

                if (skip >= el) { skip -= el; continue; }

                int take     = Math.Min(el - skip, remaining);
                uint srcAddr = addr + (uint)skip;

                var chunk = new byte[take];
                Memory.BlockRead(srcAddr, chunk, take);
                Buffer.BlockCopy(chunk, 0, target, outOff, take);

                outOff    += take;
                remaining -= take;
                skip       = 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Response helpers
        // ══════════════════════════════════════════════════════════════════════

        private void SendResponse(int queueIdx, uint token, uint responseType)
        {
            var resp = new byte[CTRL_HDR_SIZE];
            WriteU32LE(resp, 0, responseType);
            queues[queueIdx].PushUsed(token, resp);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Little-endian integer helpers
        // ══════════════════════════════════════════════════════════════════════

        private static uint ReadU32LE(byte[] buf, int offset) =>
            (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

        private static void WriteU32LE(byte[] buf, int offset, uint value)
        {
            buf[offset]     = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
