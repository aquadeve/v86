using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace x86Emulator.Devices
{
    /// <summary>
    /// NE2000 compatible ISA Ethernet controller emulation.
    ///
    /// Ported from src/ne2k.js in the v86 JavaScript project.
    ///
    /// The card exposes a 32-byte I/O window at base port 0x300 and uses IRQ 9.
    /// Internal NIC memory is 16 KiB (pages 0x40–0x80).  The host driver
    /// communicates via the DMA port (0x310) to copy packet data in/out.
    ///
    /// To inject a received packet from outside the emulator call
    /// <see cref="Receive"/>.  Sent packets are delivered via the
    /// <see cref="PacketSent"/> event.
    ///
    /// Reference: http://www.ethernut.de/pdf/8019asds.pdf
    /// </summary>
    public class NE2000 : IDevice, INeedsIRQ, IShutdown
    {
        // ── Register offsets from base port ────────────────────────────────────
        private const int E8390_CMD  = 0x00;
        private const int EN0_STARTPG = 0x01;
        private const int EN0_STOPPG  = 0x02;
        private const int EN0_BOUNDARY= 0x03;
        private const int EN0_TSR     = 0x04;  // TX status (r) / TX page (w)
        private const int EN0_TPSR    = 0x04;
        private const int EN0_NCR     = 0x05;
        private const int EN0_TCNTLO  = 0x05;  // TX count low (w)
        private const int EN0_FIFO    = 0x06;
        private const int EN0_TCNTHI  = 0x06;  // TX count high (w)
        private const int EN0_ISR     = 0x07;
        private const int EN0_CRDALO  = 0x08;
        private const int EN0_RSARLO  = 0x08;  // Remote Start Address low (w)
        private const int EN0_CRDAHI  = 0x09;
        private const int EN0_RSARHI  = 0x09;  // Remote Start Address high (w)
        private const int EN0_RCNTLO  = 0x0A;
        private const int EN0_RCNTHI  = 0x0B;
        private const int EN0_RSR     = 0x0C;
        private const int EN0_RXCR    = 0x0C;  // RX config (w)
        private const int EN0_TXCR    = 0x0D;
        private const int EN0_COUNTER0= 0x0D;
        private const int EN0_DCFG    = 0x0E;
        private const int EN0_COUNTER1= 0x0E;
        private const int EN0_IMR     = 0x0F;
        private const int EN0_COUNTER2= 0x0F;
        private const int NE_DATAPORT = 0x10;
        private const int NE_RESET    = 0x1F;

        // ── ISR bits ───────────────────────────────────────────────────────────
        private const byte ENISR_RX     = 0x01;
        private const byte ENISR_TX     = 0x02;
        private const byte ENISR_RX_ERR = 0x04;
        private const byte ENISR_TX_ERR = 0x08;
        private const byte ENISR_OVER   = 0x10;
        private const byte ENISR_COUNTERS = 0x20;
        private const byte ENISR_RDC   = 0x40;
        private const byte ENISR_RESET  = 0x80;

        private const byte ENRSR_RXOK  = 0x01;

        // ── Ring buffer layout ────────────────────────────────────────────────
        private const int START_PAGE    = 0x40;
        private const int START_RX_PAGE = START_PAGE + 12;
        private const int STOP_PAGE     = 0x80;
        private const int MEM_SIZE      = 256 * 0x80; // 32 KiB

        // ── I/O port list ─────────────────────────────────────────────────────
        private readonly int[] portsUsed;

        // ── Registers ─────────────────────────────────────────────────────────
        private byte cr   = 0x21; // Command Register (initially stopped)
        private byte isr;         // Interrupt Status Register
        private byte imr;         // Interrupt Mask Register
        private byte dcfg;        // Data Config
        private byte rxcr;        // RX Config
        private byte txcr;        // TX Config
        private byte tsr  = 0x01; // TX Status (OK)
        private int  rcnt;        // Remote Byte Count
        private int  tcnt;        // TX Byte Count
        private int  tpsr;        // TX Page Start
        private int  rsar;        // Remote Start Address Register
        private int  pstart = START_PAGE;   // Page start
        private int  pstop  = STOP_PAGE;    // Page stop
        private int  curpg  = START_RX_PAGE;
        private int  boundary = START_RX_PAGE;

        // ── NIC memory (pages 0x40–0x80 = RX ring; lower area = PROM) ────────
        private readonly byte[] memory = new byte[MEM_SIZE];

        // ── MAC and multicast ─────────────────────────────────────────────────
        private readonly byte[] mac;
        private readonly byte[] mar = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };

        // ── Base port ─────────────────────────────────────────────────────────
        private readonly ushort basePort;
        private const int IRQ_NUMBER = 9;

        // ── Events ────────────────────────────────────────────────────────────
        /// <summary>Raised when the guest sends an Ethernet frame.</summary>
        public event EventHandler<ByteArrayEventArgs> PacketSent;

        /// <inheritdoc/>
        public event EventHandler IRQ;

        /// <inheritdoc/>
        public int IRQNumber => IRQ_NUMBER;

        /// <inheritdoc/>
        public int[] PortsUsed => portsUsed;

        public NE2000(ushort port = 0x300)
        {
            basePort = port;

            // Build port list: 32 registers (base + 0 … base + 31)
            portsUsed = Enumerable.Range(port, 32).ToArray();

            // Generate a random locally-administered MAC address (OUI 00:22:15)
            var rng = new Random();
            mac = new byte[] {
                0x00, 0x22, 0x15,
                (byte)rng.Next(256),
                (byte)rng.Next(256),
                (byte)rng.Next(256)
            };

            // Initialise PROM area: each MAC byte is duplicated on even/odd addresses
            for (int i = 0; i < 6; i++)
            {
                memory[i * 2]     = mac[i];
                memory[i * 2 + 1] = mac[i];
            }
            // NE2000 PROM signature at bytes 28 / 29 / 30 / 31
            memory[14 * 2]     = memory[14 * 2 + 1] = 0x57;
            memory[15 * 2]     = memory[15 * 2 + 1] = 0x57;

            Debug.WriteLine($"[NE2000] MAC: {FormatMac(mac)}  base: 0x{port:X3}  IRQ: {IRQ_NUMBER}");
        }

        // ══════════════════════════════════════════════════════════════════════
        // IDevice
        // ══════════════════════════════════════════════════════════════════════

        public uint Read(ushort addr, int size)
        {
            int reg = addr - basePort;
            int pg  = GetPage();

            switch (reg)
            {
                case E8390_CMD:
                    return cr;

                case EN0_STARTPG: // 0x01
                    if (pg == 0) return (byte)pstart;
                    if (pg == 1) return mac[0];
                    if (pg == 2) return (byte)pstart;
                    return 0;

                case EN0_STOPPG: // 0x02
                    if (pg == 0) return (byte)pstop;
                    if (pg == 1) return mac[1];
                    if (pg == 2) return (byte)pstop;
                    return 0;

                case EN0_BOUNDARY: // 0x03
                    if (pg == 0) return (byte)boundary;
                    if (pg == 1) return mac[2];
                    return 0;

                case EN0_TSR: // 0x04 (tsr/tpsr)
                    if (pg == 0) return tsr;
                    if (pg == 1) return mac[3];
                    return 0;

                case EN0_NCR: // 0x05
                    if (pg == 0) return 0;
                    if (pg == 1) return mac[4];
                    return 0;

                case EN0_TCNTHI: // 0x06
                    if (pg == 1) return mac[5];
                    return 0;

                case EN0_ISR: // 0x07
                    if (pg == 0) return isr;
                    if (pg == 1) return (byte)curpg;
                    return 0;

                case EN0_RSARLO: // 0x08
                    if (pg == 0) return (byte)(rsar & 0xFF);
                    if (pg == 1) return mar[0];
                    return 0;

                case EN0_RSARHI: // 0x09
                    if (pg == 0) return (byte)((rsar >> 8) & 0xFF);
                    if (pg == 1) return mar[1];
                    return 0;

                case EN0_RCNTLO: // 0x0A
                    if (pg == 0) return 0x50;
                    if (pg == 1) return mar[2];
                    return 0;

                case EN0_RCNTHI: // 0x0B
                    if (pg == 0) return 0x43;
                    if (pg == 1) return mar[3];
                    return 0;

                case EN0_RSR: // 0x0C
                    if (pg == 0) return 0x01 | (1 << 3); // ENRSR_RXOK | PHY
                    if (pg == 1) return mar[4];
                    return 0;

                case EN0_COUNTER0: // 0x0D
                    if (pg == 1) return mar[5];
                    return 0;

                case EN0_COUNTER1: // 0x0E
                    if (pg == 1) return mar[6];
                    return 0;

                case EN0_COUNTER2: // 0x0F
                    if (pg == 1) return mar[7];
                    return 0;

                case NE_DATAPORT: // 0x10 – DMA data port
                    return DataPortRead(size);

                case NE_RESET: // 0x1F
                    DoInterrupt(ENISR_RESET);
                    return 0;

                default:
                    return 0xFF;
            }
        }

        public void Write(ushort addr, uint value, int size)
        {
            int reg = addr - basePort;
            byte b  = (byte)(value & 0xFF);
            int pg  = GetPage();

            switch (reg)
            {
                case E8390_CMD:
                    cr = b;
                    if ((cr & 1) != 0) return; // stop bit

                    // Remote DMA complete if rcnt is 0 and DMA requested
                    if ((b & 0x18) != 0 && rcnt == 0)
                        DoInterrupt(ENISR_RDC);

                    // Transmit bit (TXP)
                    if ((b & 0x04) != 0)
                    {
                        int start = tpsr << 8;
                        int end   = start + tcnt;
                        end = Math.Min(end, MEM_SIZE);
                        var data = new byte[end - start];
                        Buffer.BlockCopy(memory, start, data, 0, data.Length);
                        PacketSent?.Invoke(this, new ByteArrayEventArgs(data));
                        cr &= unchecked((byte)~0x04);
                        DoInterrupt(ENISR_TX);
                    }
                    break;

                case EN0_STARTPG: // 0x01
                    if (pg == 0) pstart = b;
                    else if (pg == 1) mac[0] = b;
                    break;

                case EN0_STOPPG: // 0x02
                    if (pg == 0)
                    {
                        if (b > (MEM_SIZE >> 8)) b = (byte)(MEM_SIZE >> 8);
                        pstop = b;
                    }
                    else if (pg == 1) mac[1] = b;
                    break;

                case EN0_BOUNDARY: // 0x03
                    if (pg == 0) boundary = b;
                    else if (pg == 1) mac[2] = b;
                    break;

                case EN0_TPSR: // 0x04
                    if (pg == 0) tpsr = b;
                    else if (pg == 1) mac[3] = b;
                    break;

                case EN0_TCNTLO: // 0x05
                    if (pg == 0) tcnt = (tcnt & ~0xFF) | b;
                    else if (pg == 1) mac[4] = b;
                    break;

                case EN0_TCNTHI: // 0x06
                    if (pg == 0) tcnt = (tcnt & 0xFF) | (b << 8);
                    else if (pg == 1) mac[5] = b;
                    break;

                case EN0_ISR: // 0x07 – writing clears bits (ACK)
                    if (pg == 0) { isr &= unchecked((byte)~b); UpdateIrq(); }
                    else if (pg == 1) curpg = b;
                    break;

                case EN0_RSARLO: // 0x08
                    if (pg == 0) rsar = (rsar & 0xFF00) | b;
                    else if (pg == 1) mar[0] = b;
                    break;

                case EN0_RSARHI: // 0x09
                    if (pg == 0) rsar = (rsar & 0xFF) | (b << 8);
                    else if (pg == 1) mar[1] = b;
                    break;

                case EN0_RCNTLO: // 0x0A
                    if (pg == 0) rcnt = (rcnt & 0xFF00) | b;
                    else if (pg == 1) mar[2] = b;
                    break;

                case EN0_RCNTHI: // 0x0B
                    if (pg == 0) rcnt = (rcnt & 0xFF) | (b << 8);
                    else if (pg == 1) mar[3] = b;
                    break;

                case EN0_RXCR: // 0x0C
                    if (pg == 0) rxcr = b;
                    else if (pg == 1) mar[4] = b;
                    break;

                case EN0_TXCR: // 0x0D
                    if (pg == 0) txcr = b;
                    else if (pg == 1) mar[5] = b;
                    break;

                case EN0_DCFG: // 0x0E
                    if (pg == 0) dcfg = b;
                    else if (pg == 1) mar[6] = b;
                    break;

                case EN0_IMR: // 0x0F
                    if (pg == 0) { imr = b; UpdateIrq(); }
                    else if (pg == 1) mar[7] = b;
                    break;

                case NE_DATAPORT: // 0x10
                    if (size == 8) DataPortWrite8(b);
                    else if (size == 16) DataPortWrite16((ushort)value);
                    else DataPortWrite32(value);
                    break;

                case NE_RESET: // 0x1F – write to reset clears reset ISR bit
                    break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Public – receive packet from external source
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Injects a received Ethernet frame into the NIC ring buffer.</summary>
        public void Receive(byte[] data)
        {
            if ((cr & 1) != 0) return; // stop bit – not running

            // Accept packet based on RX configuration
            bool accept = false;
            if ((rxcr & 0x10) != 0)
            {
                accept = true; // promiscuous
            }
            else if ((rxcr & 0x04) != 0 &&
                     data[0] == 0xFF && data[1] == 0xFF && data[2] == 0xFF &&
                     data[3] == 0xFF && data[4] == 0xFF && data[5] == 0xFF)
            {
                accept = true; // broadcast
            }
            else if (data[0] == mac[0] && data[1] == mac[1] &&
                     data[2] == mac[2] && data[3] == mac[3] &&
                     data[4] == mac[4] && data[5] == mac[5])
            {
                accept = true; // unicast to us
            }

            if (!accept) return;

            int packetLen  = Math.Max(60, data.Length);
            int offset     = curpg << 8;
            int totalLen   = packetLen + 4;
            int dataStart  = offset + 4;
            int next       = curpg + 1 + (totalLen >> 8);
            int end        = offset + totalLen;
            int needed     = 1 + (totalLen >> 8);

            // Ring buffer space check
            int available = boundary > curpg
                ? boundary - curpg
                : (pstop - curpg) + (boundary - pstart);

            if (available < needed && boundary != 0)
            {
                Debug.WriteLine($"[NE2000] Buffer full, dropping packet (needed={needed}, avail={available})");
                return;
            }

            if (end > (pstop << 8))
            {
                // Packet wraps ring buffer
                int cut = (pstop << 8) - dataStart;
                if (cut > 0)
                    Buffer.BlockCopy(data, 0, memory, dataStart, Math.Min(cut, data.Length));
                int remaining = data.Length - cut;
                if (remaining > 0)
                    Buffer.BlockCopy(data, cut, memory, pstart << 8, remaining);
            }
            else
            {
                Buffer.BlockCopy(data, 0, memory, dataStart, data.Length);
                if (data.Length < 60)
                    Array.Clear(memory, dataStart + data.Length, 60 - data.Length);
            }

            if (next >= pstop)
                next += pstart - pstop;

            // Write receive header (4 bytes)
            memory[offset]     = ENRSR_RXOK;
            memory[offset + 1] = (byte)next;
            memory[offset + 2] = (byte)totalLen;
            memory[offset + 3] = (byte)(totalLen >> 8);

            curpg = next;
            DoInterrupt(ENISR_RX);
        }

        // ══════════════════════════════════════════════════════════════════════
        // IShutdown
        // ══════════════════════════════════════════════════════════════════════

        public void Shutdown()
        {
            cr = 0x01; // stop
        }

        // ══════════════════════════════════════════════════════════════════════
        // Private helpers
        // ══════════════════════════════════════════════════════════════════════

        private int GetPage() => (cr >> 6) & 3;

        private void DoInterrupt(byte irMask)
        {
            isr |= irMask;
            UpdateIrq();
        }

        private void UpdateIrq()
        {
            if ((imr & isr) != 0)
                IRQ?.Invoke(this, EventArgs.Empty);
        }

        // ── DMA port access ───────────────────────────────────────────────────

        private void DataPortWriteByte(byte b)
        {
            if (rsar < memory.Length)
                memory[rsar] = b;

            rsar++;
            rcnt--;

            if (rsar >= (pstop << 8))
                rsar += (pstart - pstop) << 8;

            if (rcnt == 0)
                DoInterrupt(ENISR_RDC);
        }

        private void DataPortWrite8(byte b)  => DataPortWriteByte(b);

        private void DataPortWrite16(ushort v)
        {
            DataPortWriteByte((byte)(v & 0xFF));
            if ((dcfg & 1) != 0)
                DataPortWriteByte((byte)(v >> 8));
        }

        private void DataPortWrite32(uint v)
        {
            DataPortWriteByte((byte)(v & 0xFF));
            DataPortWriteByte((byte)((v >> 8) & 0xFF));
            DataPortWriteByte((byte)((v >> 16) & 0xFF));
            DataPortWriteByte((byte)((v >> 24) & 0xFF));
        }

        private byte DataPortReadByte()
        {
            byte data = rsar < memory.Length ? memory[rsar] : (byte)0;

            rsar++;
            rcnt--;

            if (rsar >= (pstop << 8))
                rsar += (pstart - pstop) << 8;

            if (rcnt == 0)
                DoInterrupt(ENISR_RDC);

            return data;
        }

        private uint DataPortRead(int size)
        {
            switch (size)
            {
                case 8:
                    if ((dcfg & 1) != 0)
                        return (uint)(DataPortReadByte() | DataPortReadByte() << 8);
                    return DataPortReadByte();
                case 32:
                    return (uint)(DataPortReadByte() |
                                  DataPortReadByte() << 8 |
                                  DataPortReadByte() << 16 |
                                  DataPortReadByte() << 24);
                default: // 16
                    if ((dcfg & 1) != 0)
                        return (uint)(DataPortReadByte() | DataPortReadByte() << 8);
                    return DataPortReadByte();
            }
        }

        private static string FormatMac(byte[] m) =>
            $"{m[0]:X2}:{m[1]:X2}:{m[2]:X2}:{m[3]:X2}:{m[4]:X2}:{m[5]:X2}";
    }
}
