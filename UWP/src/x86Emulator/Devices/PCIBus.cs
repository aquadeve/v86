using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace x86Emulator.Devices
{
    /// <summary>
    /// Minimal PCI bus controller that handles the standard PCI configuration
    /// space access mechanism (ports 0xCF8 / 0xCFC).
    ///
    /// Ported from src/pci.js in the v86 JavaScript project.
    ///
    /// Supports registering PCI devices with their 64-byte configuration space,
    /// optional I/O BARs (Base Address Registers), and IRQ routing.
    ///
    /// Reference: http://wiki.osdev.org/PCI
    /// </summary>
    public class PCIBus : IDevice
    {
        // ── PCI configuration mechanism I/O ports ──────────────────────────────
        private const ushort PCI_CONFIG_ADDRESS = 0xCF8;
        private const ushort PCI_CONFIG_DATA    = 0xCFC;

        private readonly int[] portsUsed = {
            0xCF8, 0xCF9, 0xCFA, 0xCFB,   // config address
            0xCFC, 0xCFD, 0xCFE, 0xCFF,   // config data
        };

        // ── Internal state ─────────────────────────────────────────────────────
        // pciAddr[3] is written last and triggers a query; pciAddr[3].bit7 = enable
        private readonly byte[] pciAddr     = new byte[4];
        private readonly byte[] pciResponse = new byte[4];
        private readonly byte[] pciStatus   = new byte[4];

        private readonly Dictionary<int, PCIDevice> devices = new Dictionary<int, PCIDevice>();

        // ── Current pending IRQ state ──────────────────────────────────────────
        private readonly PIC8259 pic;

        // ── IDevice ────────────────────────────────────────────────────────────
        public int[] PortsUsed => portsUsed;

        public PCIBus(PIC8259 pic)
        {
            this.pic = pic ?? throw new ArgumentNullException(nameof(pic));
        }

        // ══════════════════════════════════════════════════════════════════════
        // IDevice
        // ══════════════════════════════════════════════════════════════════════

        public uint Read(ushort addr, int size)
        {
            int offset = addr - PCI_CONFIG_ADDRESS;

            if (offset >= 0 && offset < 4)
            {
                // CONFIG_ADDRESS read
                return pciStatus[offset];
            }

            offset = addr - PCI_CONFIG_DATA;
            if (offset >= 0 && offset < 4)
            {
                return pciResponse[offset];
            }

            return 0xFFFFFFFF;
        }

        public void Write(ushort addr, uint value, int size)
        {
            int offset = addr - PCI_CONFIG_ADDRESS;
            if (offset >= 0 && offset < 4)
            {
                WritePciAddress(offset, (byte)(value & 0xFF), size);
                return;
            }

            offset = addr - PCI_CONFIG_DATA;
            if (offset >= 0 && offset < 4)
            {
                WritePciData(offset, value, size);
                return;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Public API – device registration
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Registers a PCI device with the bus.
        /// </summary>
        /// <param name="bdf">Bus/Device/Function identifier (device_id &lt;&lt; 3 for function 0).</param>
        /// <param name="device">The device descriptor.</param>
        public void RegisterDevice(int bdf, PCIDevice device)
        {
            devices[bdf] = device;
            Debug.WriteLine($"[PCI] Registered device '{device.Name}' at bdf=0x{bdf:X4}");
        }

        /// <summary>
        /// Raises an IRQ on behalf of a PCI device.
        /// </summary>
        public void RaiseIrq(int bdf)
        {
            if (devices.TryGetValue(bdf, out var dev) && dev.Irq >= 0)
                pic?.RequestInterrupt((byte)dev.Irq);
        }

        /// <summary>
        /// Lowers an IRQ on behalf of a PCI device (no-op if not active).
        /// </summary>
        public void LowerIrq(int bdf) { /* PIC has no explicit IRQ-lower; IRQ is level-triggered */ }

        // ══════════════════════════════════════════════════════════════════════
        // Private
        // ══════════════════════════════════════════════════════════════════════

        private void WritePciAddress(int byteIndex, byte value, int size)
        {
            switch (byteIndex)
            {
                case 0: pciAddr[0] = (byte)(value & 0xFC); break;
                case 1: pciAddr[1] = value;                break;
                case 2: pciAddr[2] = value;                break;
                case 3:
                    pciAddr[3] = value;
                    PciQuery();
                    break;
            }
        }

        private void WritePciData(int byteOffset, uint value, int size)
        {
            // Reconstruct the full 32-bit address from the current pciAddr
            int bdf  = (pciAddr[2] << 8) | pciAddr[1];
            int addr = (pciAddr[0] & 0xFC) + byteOffset;

            if (!devices.TryGetValue(bdf, out var dev)) return;

            var space = dev.ConfigSpace;
            if (space == null) return;

            switch (size)
            {
                case 8:
                    if (addr < space.Length)
                        WritePciConfigByte(dev, space, addr, (byte)(value & 0xFF));
                    break;
                case 16:
                    if (addr + 1 < space.Length)
                    {
                        WritePciConfigByte(dev, space, addr,     (byte)(value & 0xFF));
                        WritePciConfigByte(dev, space, addr + 1, (byte)((value >> 8) & 0xFF));
                    }
                    break;
                default: // 32
                    if (addr + 3 < space.Length)
                        WritePciConfig32(dev, space, addr, value);
                    break;
            }
        }

        private void WritePciConfigByte(PCIDevice dev, byte[] space, int addr, byte value)
        {
            // Protect read-only fields (vendor/device IDs etc.)
            if (addr < 4) return;
            if (addr < space.Length)
                space[addr] = value;
        }

        private void WritePciConfig32(PCIDevice dev, byte[] space, int addr, uint written)
        {
            // BAR range: offsets 0x10–0x27 (6 BARs × 4 bytes each)
            if (addr >= 0x10 && addr < 0x28)
            {
                int barNr = (addr - 0x10) >> 2;
                if (barNr < dev.IoBars.Length)
                {
                    var bar = dev.IoBars[barNr];
                    if (bar != null)
                    {
                        uint type = (uint)(space[addr] & 1);
                        if (type == 1) // I/O BAR
                        {
                            uint from = (uint)((space[addr] | (space[addr + 1] << 8)) & ~1 & 0xFFFF);
                            uint to   = written & ~1u & 0xFFFF;
                            SetIoBars(bar, (int)from, (int)to);

                            space[addr]     = (byte)((written | 1) & 0xFF);
                            space[addr + 1] = (byte)((written >> 8) & 0xFF);
                            space[addr + 2] = (byte)((written >> 16) & 0xFF);
                            space[addr + 3] = (byte)((written >> 24) & 0xFF);
                        }
                        return;
                    }
                }
                // No BAR – write zero
                space[addr] = space[addr + 1] = space[addr + 2] = space[addr + 3] = 0;
                return;
            }

            if (addr < space.Length - 3)
            {
                space[addr]     = (byte)(written & 0xFF);
                space[addr + 1] = (byte)((written >> 8) & 0xFF);
                space[addr + 2] = (byte)((written >> 16) & 0xFF);
                space[addr + 3] = (byte)((written >> 24) & 0xFF);
            }
        }

        private void SetIoBars(IoPCIBar bar, int from, int to)
        {
            // Devices that use dynamically-mapped I/O BARs call this
            // to update their I/O port base.  Not all devices need this.
            Debug.WriteLine($"[PCI] I/O BAR moved 0x{from:X4} → 0x{to:X4} size={bar.Size}");
            bar.CurrentBase = to;
            bar.OnBaseChanged?.Invoke(from, to);
        }

        private void PciQuery()
        {
            bool enabled = (pciAddr[3] & 0x80) != 0;
            if (!enabled)
            {
                Array.Clear(pciResponse, 0, 4);
                Array.Clear(pciStatus, 0, 4);
                return;
            }

            int bdf  = (pciAddr[2] << 8) | pciAddr[1];
            int addr = pciAddr[0] & 0xFC;

            if (devices.TryGetValue(bdf, out var dev))
            {
                // pci_status[3] bit7 = 1 means device found
                pciStatus[3] = 0x80;

                var space = dev.ConfigSpace;
                if (space != null && addr + 3 < space.Length)
                {
                    pciResponse[0] = space[addr];
                    pciResponse[1] = space[addr + 1];
                    pciResponse[2] = space[addr + 2];
                    pciResponse[3] = space[addr + 3];
                }
                else
                {
                    pciResponse[0] = pciResponse[1] = pciResponse[2] = pciResponse[3] = 0;
                }
            }
            else
            {
                // No device: return 0xFFFFFFFF
                pciResponse[0] = pciResponse[1] = pciResponse[2] = pciResponse[3] = 0xFF;
                pciStatus[3] = 0;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Supporting types
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Descriptor for a PCI device registered with <see cref="PCIBus"/>.</summary>
    public class PCIDevice
    {
        /// <summary>Human-readable device name for debug output.</summary>
        public string Name { get; set; }

        /// <summary>
        /// 64-byte (or larger) PCI configuration space, little-endian byte array.
        /// Offsets 0x00–0x3F are the standard header; additional bytes are device-specific.
        /// </summary>
        public byte[] ConfigSpace { get; set; }

        /// <summary>
        /// I/O Base Address Register (BAR) descriptors (up to 6 entries).
        /// Null entries indicate that the corresponding BAR is not used.
        /// </summary>
        public IoPCIBar[] IoBars { get; set; } = new IoPCIBar[6];

        /// <summary>Host IRQ number to raise on the PIC when the device signals an interrupt, or -1 if none.</summary>
        public int Irq { get; set; } = -1;
    }

    /// <summary>Represents a PCI I/O BAR (type = I/O, bit 0 set).</summary>
    public class IoPCIBar
    {
        /// <summary>Size of the I/O address range in bytes (must be a power of 2).</summary>
        public int Size { get; set; }

        /// <summary>Current base I/O port as programmed by the BIOS/OS.</summary>
        public int CurrentBase { get; set; }

        /// <summary>
        /// Optional callback invoked when the OS re-maps this BAR.
        /// Parameters: (oldBase, newBase).
        /// </summary>
        public Action<int, int> OnBaseChanged { get; set; }
    }
}
