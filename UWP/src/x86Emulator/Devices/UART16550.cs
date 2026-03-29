using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace x86Emulator.Devices
{
    /// <summary>
    /// Emulates a 16550A UART (Universal Asynchronous Receiver/Transmitter) compatible
    /// serial port device.  Supports COM1 (0x3F8, IRQ4) and COM2 (0x2F8, IRQ3).
    ///
    /// Ported from src/uart.js in the v86 JavaScript project.
    /// Reference: http://wiki.osdev.org/UART
    /// </summary>
    public class UART16550 : IDevice, INeedsIRQ, IShutdown
    {
        // ── DLAB flag ──────────────────────────────────────────────────────────
        private const byte DLAB = 0x80;

        // ── IER bits ──────────────────────────────────────────────────────────
        private const byte UART_IER_MSI  = 0x08; // Modem Status Changed
        private const byte UART_IER_THRI = 0x02; // TX Holding Register Empty
        private const byte UART_IER_RDI  = 0x01; // Receiver Data Available

        // ── IIR / interrupt source identifiers ────────────────────────────────
        private const byte UART_IIR_MSI    = 0x00; // Modem status (lowest priority)
        private const byte UART_IIR_NO_INT = 0x01; // No pending interrupt
        private const byte UART_IIR_THRI   = 0x02; // TX holding register empty
        private const byte UART_IIR_RDI    = 0x04; // Receiver data available
        private const byte UART_IIR_RLSI   = 0x06; // Receiver line status (highest)
        private const byte UART_IIR_CTI    = 0x0C; // Character timeout

        // ── MCR bits ──────────────────────────────────────────────────────────
        private const byte UART_MCR_LOOPBACK = 0x10;

        // ── LSR bits ──────────────────────────────────────────────────────────
        private const byte UART_LSR_DATA_READY        = 0x01;
        private const byte UART_LSR_TX_EMPTY          = 0x20;
        private const byte UART_LSR_TRANSMITTER_EMPTY = 0x40;

        // ── MSR bit indices (bit positions within modem_status) ───────────────
        private const int UART_MSR_DCD  = 7; // Data Carrier Detect
        private const int UART_MSR_RI   = 6; // Ring Indicator
        private const int UART_MSR_DSR  = 5; // Data Set Ready
        private const int UART_MSR_CTS  = 4; // Clear To Send
        private const int UART_MSR_DDCD = 3; // Delta DCD
        private const int UART_MSR_TERI = 2; // Trailing Edge RI
        private const int UART_MSR_DDSR = 1; // Delta DSR
        private const int UART_MSR_DCTS = 0; // Delta CTS

        // ── State ─────────────────────────────────────────────────────────────
        private int  ints;             // pending interrupt bits (one bit per IIR source)
        private uint baudRate;
        private byte lineControl;
        private byte lsr;              // Line Status Register
        private byte fifoControl;
        private byte ier;              // Interrupt Enable Register
        private byte iir;              // Interrupt Identification Register
        private byte modemControl;
        private byte modemStatus;
        private byte scratchRegister;
        private readonly int irq;
        private readonly int comPort;  // 0-based COM index (0 = COM1, 1 = COM2, …)
        private readonly Queue<byte> inputQueue = new Queue<byte>();
        private readonly int[] portsUsed;
        private readonly ushort basePort;

        /// <summary>Raised when the guest OS writes a byte to the serial port.</summary>
        public event EventHandler<ByteEventArgs> SerialOutput;

        /// <inheritdoc/>
        public event EventHandler IRQ;

        /// <inheritdoc/>
        public int IRQNumber => irq;

        /// <inheritdoc/>
        public int[] PortsUsed => portsUsed;

        /// <summary>
        /// Creates a new UART instance at the given base I/O port address.
        /// </summary>
        /// <param name="port">Base I/O address (e.g. 0x3F8 for COM1, 0x2F8 for COM2).</param>
        public UART16550(ushort port)
        {
            basePort = port;

            switch (port)
            {
                case 0x3F8: comPort = 0; irq = 4; break;
                case 0x2F8: comPort = 1; irq = 3; break;
                case 0x3E8: comPort = 2; irq = 4; break;
                case 0x2E8: comPort = 3; irq = 3; break;
                default:
                    Debug.WriteLine($"[UART] Unknown port {port:X4}, defaulting to COM1 config.");
                    comPort = 0;
                    irq = 4;
                    break;
            }

            // Ports: base+0 … base+7 (8 registers)
            portsUsed = new int[8];
            for (int i = 0; i < 8; i++)
                portsUsed[i] = port + i;

            // Initialise LSR: TX buffer empty + transmitter idle
            lsr = UART_LSR_TX_EMPTY | UART_LSR_TRANSMITTER_EMPTY;

            // THRI is pending by default (TX buffer empty on reset)
            ints = 1 << UART_IIR_THRI;
            iir  = UART_IIR_NO_INT;
        }

        // ── IDevice ───────────────────────────────────────────────────────────

        public uint Read(ushort addr, int size)
        {
            int reg = addr - basePort;
            switch (reg)
            {
                case 0: // RBR / baud-rate divisor low
                    if ((lineControl & DLAB) != 0)
                        return (byte)(baudRate & 0xFF);

                    if (inputQueue.Count == 0)
                    {
                        Debug.WriteLine($"[UART COM{comPort + 1}] Read RBR: queue empty");
                        return 0;
                    }
                    else
                    {
                        byte data = inputQueue.Dequeue();
                        if (inputQueue.Count == 0)
                        {
                            lsr &= unchecked((byte)~UART_LSR_DATA_READY);
                            ClearInterrupt(UART_IIR_CTI);
                            ClearInterrupt(UART_IIR_RDI);
                        }
                        return data;
                    }

                case 1: // IER / baud-rate divisor high
                    if ((lineControl & DLAB) != 0)
                        return (byte)(baudRate >> 8);
                    return (byte)(ier & 0x0F);

                case 2: // IIR (read-only)
                {
                    byte ret = (byte)(iir & 0x0F);
                    if (iir == UART_IIR_THRI)
                        ClearInterrupt(UART_IIR_THRI);
                    if ((fifoControl & 1) != 0)
                        ret |= 0xC0;
                    return ret;
                }

                case 3: // LCR
                    return lineControl;

                case 4: // MCR
                    return modemControl;

                case 5: // LSR
                    return lsr;

                case 6: // MSR — reading clears delta bits
                {
                    byte ret = modemStatus;
                    modemStatus &= 0xF0; // clear delta bits
                    return ret;
                }

                case 7: // SCR
                    return scratchRegister;

                default:
                    return 0xFF;
            }
        }

        public void Write(ushort addr, uint value, int size)
        {
            int reg = addr - basePort;
            byte b = (byte)(value & 0xFF);
            switch (reg)
            {
                case 0: // THR / baud-rate divisor low
                    if ((lineControl & DLAB) != 0)
                    {
                        baudRate = (baudRate & 0xFF00) | b;
                        break;
                    }
                    WriteData(b);
                    break;

                case 1: // IER / baud-rate divisor high
                    if ((lineControl & DLAB) != 0)
                    {
                        baudRate = (baudRate & 0x00FF) | (uint)(b << 8);
                        break;
                    }
                    if ((ier & UART_IIR_THRI) == 0 && (b & UART_IIR_THRI) != 0)
                        ThrowInterrupt(UART_IIR_THRI); // re-throw if was masked
                    ier = (byte)(b & 0x0F);
                    CheckInterrupt();
                    break;

                case 2: // FCR (write-only)
                    fifoControl = b;
                    break;

                case 3: // LCR
                    lineControl = b;
                    break;

                case 4: // MCR
                    modemControl = b;
                    break;

                case 5: // LSR factory test write – ignored
                    break;

                case 6: // MSR
                    SetModemStatus(b);
                    break;

                case 7: // SCR
                    scratchRegister = b;
                    break;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Delivers a byte received from the outside world to the guest.</summary>
        public void DataReceived(byte data)
        {
            inputQueue.Enqueue(data);
            lsr |= UART_LSR_DATA_READY;

            if ((fifoControl & 1) != 0)
                ThrowInterrupt(UART_IIR_CTI);
            else
                ThrowInterrupt(UART_IIR_RDI);
        }

        // ── IShutdown ─────────────────────────────────────────────────────────

        public void Shutdown()
        {
            inputQueue.Clear();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void WriteData(byte b)
        {
            ThrowInterrupt(UART_IIR_THRI);

            if ((modemControl & UART_MCR_LOOPBACK) != 0)
            {
                DataReceived(b);
            }
            else
            {
                SerialOutput?.Invoke(this, new ByteEventArgs(b));
            }
        }

        private void CheckInterrupt()
        {
            if ((ints & (1 << UART_IIR_CTI)) != 0 && (ier & UART_IER_RDI) != 0)
            {
                iir = UART_IIR_CTI;
                RaiseIRQ();
            }
            else if ((ints & (1 << UART_IIR_RDI)) != 0 && (ier & UART_IER_RDI) != 0)
            {
                iir = UART_IIR_RDI;
                RaiseIRQ();
            }
            else if ((ints & (1 << UART_IIR_THRI)) != 0 && (ier & UART_IER_THRI) != 0)
            {
                iir = UART_IIR_THRI;
                RaiseIRQ();
            }
            else if ((ints & (1 << UART_IIR_MSI)) != 0 && (ier & UART_IER_MSI) != 0)
            {
                iir = UART_IIR_MSI;
                RaiseIRQ();
            }
            else
            {
                iir = UART_IIR_NO_INT;
            }
        }

        private void ThrowInterrupt(int line)
        {
            ints |= 1 << line;
            CheckInterrupt();
        }

        private void ClearInterrupt(int line)
        {
            ints &= ~(1 << line);
            CheckInterrupt();
        }

        private void SetModemStatus(byte status)
        {
            byte prevDelta = (byte)(modemStatus & 0x0F);
            byte delta = (byte)((modemStatus ^ status) >> 4);
            delta |= prevDelta;
            modemStatus = status;
            modemStatus |= delta;
        }

        private void RaiseIRQ()
        {
            IRQ?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>EventArgs that carries a single byte value.</summary>
    public class ByteEventArgs : EventArgs
    {
        public byte Value { get; }
        public ByteEventArgs(byte v) { Value = v; }
    }
}
