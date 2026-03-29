using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace x86Emulator.Devices
{
    /// <summary>
    /// VirtIO Network device (virtio-net, PCI device-id 0x1000 / 0x1041).
    ///
    /// Ported from src/virtio_net.js in the v86 JavaScript project.
    ///
    /// Provides a high-performance network adapter over VirtIO, supplementing
    /// (or replacing) the NE2000 ISA adapter.
    ///
    /// I/O BAR layout:
    ///   0xC800 – common config
    ///   0xC900 – notification
    ///   0xC700 – ISR status
    ///   0xC600 – device-specific config (MAC, status, MTU)
    ///
    /// Queues:
    ///   0 – receiveq 0  (host→guest, device pushes received frames here)
    ///   1 – transmitq 0 (guest→host, driver posts outgoing frames here)
    ///   2 – controlq    (MAC/multiqueue management)
    ///
    /// Received Ethernet frames are injected via <see cref="Receive"/>.
    /// Transmitted frames are delivered via <see cref="PacketSent"/>.
    ///
    /// References:
    ///   https://docs.oasis-open.org/virtio/virtio/v1.2/csd01/virtio-v1.2-csd01.html
    /// </summary>
    public class VirtioNet : IDevice, INeedsIRQ, IShutdown
    {
        // ── Feature bits ──────────────────────────────────────────────────────
        private const uint VIRTIO_NET_F_MAC          = 1u << 5;
        private const uint VIRTIO_NET_F_STATUS       = 1u << 16;
        private const uint VIRTIO_NET_F_MQ           = 1u << 22;
        private const uint VIRTIO_NET_F_MTU          = 1u << 3;
        private const uint VIRTIO_NET_F_CTRL_VQ      = 1u << 17;
        private const uint VIRTIO_NET_F_CTRL_MAC_ADDR= 1u << 23;

        // Control queue commands
        private const byte VIRTIO_NET_CTRL_MAC_ADDR_SET = 1;

        // ── VirtIO net header (prepended to every packet, 12 bytes) ───────────
        private const int VIRTIO_NET_HDR_SIZE = 12;

        // ── Port base addresses ───────────────────────────────────────────────
        private const ushort PORT_COMMON_CFG  = 0xC800;
        private const ushort PORT_NOTIFY      = 0xC900;
        private const ushort PORT_ISR_STATUS  = 0xC700;
        private const ushort PORT_DEVICE_SPEC = 0xC600;

        private const int MTU_DEFAULT = 1500;
        private const int IRQ_NUMBER  = 10;

        // ── Queues ────────────────────────────────────────────────────────────
        private readonly VirtQueue[] queues = new VirtQueue[]
        {
            new VirtQueue(1024, 0), // receiveq 0
            new VirtQueue(1024, 1), // transmitq 0
            new VirtQueue(16,   2), // controlq
        };

        // ── Device/driver state ───────────────────────────────────────────────
        private byte   deviceStatus;
        private ushort queueSelect;
        private uint   deviceFeatureSelect;
        private readonly uint[] queueDescLo  = new uint[3];
        private readonly uint[] queueAvailLo = new uint[3];
        private readonly uint[] queueUsedLo  = new uint[3];

        private readonly byte[] mac;
        private ushort netStatus = 1; // link up

        private static readonly uint[] DeviceFeatures =
        {
            VIRTIO_NET_F_MAC | VIRTIO_NET_F_STATUS | VIRTIO_NET_F_MQ |
            VIRTIO_NET_F_MTU | VIRTIO_NET_F_CTRL_VQ | VIRTIO_NET_F_CTRL_MAC_ADDR,
            1  // VERSION_1
        };

        // ── I/O ports ─────────────────────────────────────────────────────────
        private readonly int[] portsUsed;

        // ── Events ────────────────────────────────────────────────────────────
        /// <summary>Raised when the guest sends an Ethernet frame.</summary>
        public event EventHandler<ByteArrayEventArgs> PacketSent;

        public event EventHandler IRQ;
        public int IRQNumber => IRQ_NUMBER;
        public int[] PortsUsed => portsUsed;

        /// <summary>The MAC address of this VirtIO network card.</summary>
        public string MacAddress => FormatMac(mac);

        public VirtioNet()
        {
            var rng = new Random();
            mac = new byte[] {
                0x00, 0x22, 0x15,
                (byte)rng.Next(256),
                (byte)rng.Next(256),
                (byte)rng.Next(256)
            };

            var ports = new List<int>();
            for (int i = 0; i < 16; i++) ports.Add(PORT_COMMON_CFG  + i);
            for (int i = 0; i < 16; i++) ports.Add(PORT_NOTIFY      + i);
            for (int i = 0; i <  4; i++) ports.Add(PORT_ISR_STATUS  + i);
            for (int i = 0; i < 16; i++) ports.Add(PORT_DEVICE_SPEC + i);
            portsUsed = ports.ToArray();

            Debug.WriteLine($"[VirtioNet] MAC: {FormatMac(mac)}");
        }

        // ══════════════════════════════════════════════════════════════════════
        // IDevice
        // ══════════════════════════════════════════════════════════════════════

        public uint Read(ushort addr, int size)
        {
            if (addr >= PORT_ISR_STATUS && addr < PORT_ISR_STATUS + 4)
                return (deviceStatus & VirtIOConst.DEVICE_STATUS_DRIVER_OK) != 0 ? 1u : 0u;

            if (addr >= PORT_COMMON_CFG && addr < PORT_COMMON_CFG + 64)
                return ReadCommonConfig(addr - PORT_COMMON_CFG);

            if (addr >= PORT_DEVICE_SPEC && addr < PORT_DEVICE_SPEC + 16)
                return ReadDeviceSpec(addr - PORT_DEVICE_SPEC, size);

            return 0;
        }

        public void Write(ushort addr, uint value, int size)
        {
            if (addr >= PORT_COMMON_CFG && addr < PORT_COMMON_CFG + 64)
            { WriteCommonConfig(addr - PORT_COMMON_CFG, value); return; }

            if (addr >= PORT_NOTIFY && addr < PORT_NOTIFY + 16)
            { NotifyQueue((addr - PORT_NOTIFY) / 2); return; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // IShutdown
        // ══════════════════════════════════════════════════════════════════════

        public void Shutdown()
        {
            deviceStatus = 0;
            netStatus = 0;
            foreach (var q in queues) q.Reset();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Public – inject received packet
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Injects a received Ethernet frame into the VirtIO receiveq.
        /// A 12-byte VirtIO net header is prepended automatically.
        /// </summary>
        public void Receive(byte[] frame)
        {
            if ((deviceStatus & VirtIOConst.DEVICE_STATUS_DRIVER_OK) == 0) return;
            if (!queues[0].HasRequest()) return;
            if (!queues[0].TryPopRequest(out _, out uint token, out _)) return;

            // Prepend a 12-byte virtio_net_hdr (all zeros = no offloads)
            var pkt = new byte[VIRTIO_NET_HDR_SIZE + frame.Length];
            Buffer.BlockCopy(frame, 0, pkt, VIRTIO_NET_HDR_SIZE, frame.Length);

            queues[0].PushUsed(token, pkt);
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Common config
        // ══════════════════════════════════════════════════════════════════════

        private uint ReadCommonConfig(int off)
        {
            switch (off)
            {
                case 0:  return deviceFeatureSelect < DeviceFeatures.Length
                             ? DeviceFeatures[deviceFeatureSelect] : 0;
                case 18: return (uint)queues.Length;
                case 20: return deviceStatus;
                case 22: return queueSelect;
                case 24: return queueSelect < queues.Length
                             ? (uint)queues[queueSelect].Size : 0;
                case 28: return 1;
                case 30: return queueSelect < queues.Length
                             ? (uint)queues[queueSelect].NotifyOffset : 0;
                default: return 0;
            }
        }

        private void WriteCommonConfig(int off, uint value)
        {
            switch (off)
            {
                case 0:  deviceFeatureSelect = value; break;
                case 20:
                    deviceStatus = (byte)(value & 0xFF);
                    if (deviceStatus == 0) ResetDevice();
                    break;
                case 22: queueSelect = (ushort)(value & 0xFFFF); break;
                case 32:
                    if (queueSelect < queueDescLo.Length)
                    { queueDescLo[queueSelect] = value; TryActivateQueue(queueSelect); }
                    break;
                case 40:
                    if (queueSelect < queueAvailLo.Length)
                    { queueAvailLo[queueSelect] = value; TryActivateQueue(queueSelect); }
                    break;
                case 48:
                    if (queueSelect < queueUsedLo.Length)
                    { queueUsedLo[queueSelect] = value; TryActivateQueue(queueSelect); }
                    break;
            }
        }

        private uint ReadDeviceSpec(int off, int size)
        {
            // MAC (6 bytes) | status(2) | max_virtqueue_pairs(2) | mtu(2)
            if (off < 6) return mac[off];
            if (off == 6) return (ushort)netStatus;
            if (off == 8) return 1; // max_virtqueue_pairs
            if (off == 10) return MTU_DEFAULT;
            return 0;
        }

        private void TryActivateQueue(int q)
        {
            if (queueDescLo[q] != 0 && queueAvailLo[q] != 0 && queueUsedLo[q] != 0)
                queues[q].SetAddresses(queueDescLo[q], queueAvailLo[q], queueUsedLo[q]);
        }

        private void ResetDevice()
        {
            foreach (var q in queues) q.Reset();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Queue notification
        // ══════════════════════════════════════════════════════════════════════

        private void NotifyQueue(int queueIdx)
        {
            if (queueIdx == 1) HandleTransmitQ();
            else if (queueIdx == 2) HandleControlQ();
        }

        private void HandleTransmitQ()
        {
            var txq = queues[1];
            while (txq.HasRequest())
            {
                if (!txq.TryPopRequest(out byte[] buf, out uint token, out _)) break;
                if (buf != null && buf.Length > VIRTIO_NET_HDR_SIZE)
                {
                    // Strip the 12-byte VirtIO net header before passing to the network
                    var frame = new byte[buf.Length - VIRTIO_NET_HDR_SIZE];
                    Buffer.BlockCopy(buf, VIRTIO_NET_HDR_SIZE, frame, 0, frame.Length);
                    PacketSent?.Invoke(this, new ByteArrayEventArgs(frame));
                }
                txq.PushUsed(token, null);
            }
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        private void HandleControlQ()
        {
            var ctrlq = queues[2];
            while (ctrlq.HasRequest())
            {
                if (!ctrlq.TryPopRequest(out byte[] buf, out uint token, out _)) break;
                if (buf != null && buf.Length >= 2)
                {
                    byte cmd = buf[1];
                    if (cmd == VIRTIO_NET_CTRL_MAC_ADDR_SET && buf.Length >= 2 + 6)
                    {
                        Buffer.BlockCopy(buf, 2, mac, 0, 6);
                        Debug.WriteLine($"[VirtioNet] MAC updated to {FormatMac(mac)}");
                    }
                }
                // Respond with VIRTIO_NET_OK (0)
                ctrlq.PushUsed(token, new byte[] { 0 });
            }
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        private static string FormatMac(byte[] m) =>
            $"{m[0]:X2}:{m[1]:X2}:{m[2]:X2}:{m[3]:X2}:{m[4]:X2}:{m[5]:X2}";
    }
}
