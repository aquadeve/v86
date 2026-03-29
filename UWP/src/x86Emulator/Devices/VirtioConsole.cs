using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace x86Emulator.Devices
{
    /// <summary>
    /// VirtIO Console device (virtio-console, PCI device-id 0x1003).
    ///
    /// Ported from src/virtio_console.js in the v86 JavaScript project.
    ///
    /// Provides one or more multiplexed serial-console ports over VirtIO.
    /// Useful for receiving Linux kernel serial output and sending input to
    /// the guest without a full UART emulation.
    ///
    /// I/O BAR layout (follows the standard 4-BAR VirtIO-1.x pattern):
    ///   0xB800 – common config
    ///   0xB900 – notification
    ///   0xB700 – ISR status
    ///   0xB600 – device-specific config (rows/cols/ports)
    ///
    /// Queues (per port pair):
    ///   0 – receiveq 0 (host→guest)
    ///   1 – transmitq 0 (guest→host)
    ///   2 – receiveq ctrl (host→guest)
    ///   3 – transmitq ctrl (guest→host)
    ///   …additional pairs for ports 1-N…
    ///
    /// References:
    ///   https://docs.oasis-open.org/virtio/virtio/v1.2/csd01/virtio-v1.2-csd01.html
    /// </summary>
    public class VirtioConsole : IDevice, INeedsIRQ, IShutdown
    {
        // ── Control message types ──────────────────────────────────────────────
        private const uint VIRTIO_CONSOLE_DEVICE_READY = 0;
        private const uint VIRTIO_CONSOLE_DEVICE_ADD   = 1;
        private const uint VIRTIO_CONSOLE_DEVICE_REMOVE= 2;
        private const uint VIRTIO_CONSOLE_PORT_READY   = 3;
        private const uint VIRTIO_CONSOLE_CONSOLE_PORT = 4;
        private const uint VIRTIO_CONSOLE_RESIZE       = 5;
        private const uint VIRTIO_CONSOLE_PORT_OPEN    = 6;
        private const uint VIRTIO_CONSOLE_PORT_NAME    = 7;

        // ── Feature bits ──────────────────────────────────────────────────────
        private const uint VIRTIO_CONSOLE_F_SIZE      = 1u << 0;
        private const uint VIRTIO_CONSOLE_F_MULTIPORT = 1u << 1;
        private const uint VIRTIO_CONSOLE_F_EMERG_WRITE = 1u << 2;

        // ── Port base addresses ───────────────────────────────────────────────
        private const ushort PORT_COMMON_CFG  = 0xB800;
        private const ushort PORT_NOTIFY      = 0xB900;
        private const ushort PORT_ISR_STATUS  = 0xB700;
        private const ushort PORT_DEVICE_SPEC = 0xB600;

        private const int NUM_PORTS = 4;
        private const int CTRL_HDR_SIZE = 8; // id(4) + event(2) + value(2)

        // ── Queues: 4 base queues + 2 per additional port ─────────────────────
        private readonly VirtQueue[] queues;

        // ── Device/driver state ───────────────────────────────────────────────
        private byte   deviceStatus;
        private ushort queueSelect;
        private uint   deviceFeatureSelect;
        private readonly uint[] queueDescLo;
        private readonly uint[] queueAvailLo;
        private readonly uint[] queueUsedLo;

        private static readonly uint[] DeviceFeatures = {
            VIRTIO_CONSOLE_F_SIZE | VIRTIO_CONSOLE_F_MULTIPORT,
            1  // VERSION_1
        };

        // ── I/O ports ─────────────────────────────────────────────────────────
        private readonly int[] portsUsed;
        private const int IRQ_NUMBER = 12;

        // ── Events ────────────────────────────────────────────────────────────
        /// <summary>Raised when the guest writes data to a console transmitq.</summary>
        public event EventHandler<ConsoleOutputEventArgs> ConsoleOutput;

        public event EventHandler IRQ;
        public int IRQNumber => IRQ_NUMBER;
        public int[] PortsUsed => portsUsed;

        public VirtioConsole()
        {
            int totalQueues = 4 + (NUM_PORTS - 1) * 2; // 4 base + 2 per extra port
            queues = new VirtQueue[totalQueues];
            for (int i = 0; i < totalQueues; i++)
                queues[i] = new VirtQueue(16, i < 4 ? i % 2 : i % 2);

            queueDescLo  = new uint[totalQueues];
            queueAvailLo = new uint[totalQueues];
            queueUsedLo  = new uint[totalQueues];

            var ports = new List<int>();
            for (int i = 0; i < 16; i++) ports.Add(PORT_COMMON_CFG  + i);
            for (int i = 0; i < 16; i++) ports.Add(PORT_NOTIFY      + i);
            for (int i = 0; i <  4; i++) ports.Add(PORT_ISR_STATUS  + i);
            for (int i = 0; i < 16; i++) ports.Add(PORT_DEVICE_SPEC + i);
            portsUsed = ports.ToArray();

            Debug.WriteLine("[VirtioConsole] Device initialized");
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

            if (addr >= PORT_DEVICE_SPEC && addr < PORT_DEVICE_SPEC + 8)
                return ReadDeviceSpec(addr - PORT_DEVICE_SPEC);

            return 0;
        }

        public void Write(ushort addr, uint value, int size)
        {
            if (addr >= PORT_COMMON_CFG && addr < PORT_COMMON_CFG + 64)
            { WriteCommonConfig(addr - PORT_COMMON_CFG, value); return; }

            if (addr >= PORT_NOTIFY && addr < PORT_NOTIFY + 32)
            { NotifyQueue((addr - PORT_NOTIFY) / 2); return; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // IShutdown
        // ══════════════════════════════════════════════════════════════════════

        public void Shutdown()
        {
            deviceStatus = 0;
            foreach (var q in queues) q.Reset();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Public – send data to the guest (injects into receiveq 0)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Sends bytes to the guest on port 0 (primary console).</summary>
        public void SendToGuest(byte[] data)
        {
            // receiveq for port 0 is queue 0
            // The guest must have set up the queue first
            if (!queues[0].HasRequest()) return;
            if (!queues[0].TryPopRequest(out _, out uint token, out _)) return;
            queues[0].PushUsed(token, data);
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
                    else if ((deviceStatus & VirtIOConst.DEVICE_STATUS_DRIVER_OK) != 0)
                        OnDriverOk();
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

        private uint ReadDeviceSpec(int off)
        {
            // cols(2) | rows(2) | max_nr_ports(4) | emerg_wr(4)
            switch (off)
            {
                case 0: return 80;          // cols
                case 2: return 25;          // rows
                case 4: return NUM_PORTS;   // max_nr_ports
                default: return 0;
            }
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

        private void OnDriverOk()
        {
            Debug.WriteLine("[VirtioConsole] Driver OK");
            // Advertise port 0 as the console port
            SendControlMessage(0, VIRTIO_CONSOLE_DEVICE_ADD, 1);
            SendControlMessage(0, VIRTIO_CONSOLE_CONSOLE_PORT, 1);
            SendControlMessage(0, VIRTIO_CONSOLE_PORT_OPEN, 1);
        }

        private void NotifyQueue(int queueIdx)
        {
            // Transmit queues are odd-numbered: 1 (port 0 tx), 3 (ctrl tx), 5+...
            if (queueIdx >= queues.Length) return;
            var q = queues[queueIdx];
            bool isTransmit = (queueIdx % 2) == 1;
            if (!isTransmit) return;

            while (q.HasRequest())
            {
                if (!q.TryPopRequest(out byte[] buf, out uint token, out _)) break;
                if (buf != null && buf.Length > 0)
                {
                    int portIdx = queueIdx == 3 ? -1 : (queueIdx - 1) / 2; // -1 for ctrl
                    if (portIdx >= 0)
                        ConsoleOutput?.Invoke(this, new ConsoleOutputEventArgs(portIdx, buf));
                    else
                        HandleControlMessage(buf);
                }
                q.PushUsed(token, null);
            }
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        private void HandleControlMessage(byte[] buf)
        {
            if (buf.Length < CTRL_HDR_SIZE) return;
            uint portId = ReadU32LE(buf, 0);
            ushort evt  = ReadU16LE(buf, 4);
            ushort val  = ReadU16LE(buf, 6);
            Debug.WriteLine($"[VirtioConsole] Ctrl: port={portId} event={evt} value={val}");

            switch (evt)
            {
                case (ushort)VIRTIO_CONSOLE_DEVICE_READY:
                    // Guest is ready – send port add notifications
                    for (uint p = 0; p < NUM_PORTS; p++)
                        SendControlMessage(p, VIRTIO_CONSOLE_DEVICE_ADD, 1);
                    break;
                case (ushort)VIRTIO_CONSOLE_PORT_READY:
                    if (portId == 0)
                    {
                        SendControlMessage(0, VIRTIO_CONSOLE_CONSOLE_PORT, 1);
                        SendControlMessage(0, VIRTIO_CONSOLE_PORT_OPEN, 1);
                    }
                    break;
            }
        }

        private void SendControlMessage(uint portId, uint eventType, ushort value)
        {
            // Control receiveq is queue 2
            if (!queues[2].HasRequest()) return;
            if (!queues[2].TryPopRequest(out _, out uint token, out _)) return;

            var msg = new byte[CTRL_HDR_SIZE];
            WriteU32LE(msg, 0, portId);
            WriteU16LE(msg, 4, (ushort)eventType);
            WriteU16LE(msg, 6, value);
            queues[2].PushUsed(token, msg);
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        // ── Integer helpers ───────────────────────────────────────────────────
        private static uint ReadU32LE(byte[] b, int o) =>
            (uint)(b[o] | (b[o+1]<<8) | (b[o+2]<<16) | (b[o+3]<<24));
        private static ushort ReadU16LE(byte[] b, int o) =>
            (ushort)(b[o] | (b[o+1]<<8));
        private static void WriteU32LE(byte[] b, int o, uint v) {
            b[o]=(byte)(v&0xFF); b[o+1]=(byte)((v>>8)&0xFF);
            b[o+2]=(byte)((v>>16)&0xFF); b[o+3]=(byte)((v>>24)&0xFF);
        }
        private static void WriteU16LE(byte[] b, int o, ushort v) {
            b[o]=(byte)(v&0xFF); b[o+1]=(byte)(v>>8);
        }
    }

    /// <summary>Event args for console output data from the guest.</summary>
    public class ConsoleOutputEventArgs : EventArgs
    {
        public int Port { get; }
        public byte[] Data { get; }
        public ConsoleOutputEventArgs(int port, byte[] data) { Port = port; Data = data; }
    }
}
