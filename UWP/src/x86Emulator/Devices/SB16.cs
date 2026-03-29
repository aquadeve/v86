using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace x86Emulator.Devices
{
    /// <summary>
    /// Sound Blaster 16 compatible audio device emulation.
    ///
    /// Ported from src/sb16.js in the v86 JavaScript project.
    ///
    /// Implements:
    ///   - DSP commands (version, DMA transfers, sampling rate, etc.)
    ///   - Mixer register set (0x224/0x225)
    ///   - FM/OPL placeholder (0x220–0x223, 0x388–0x389)
    ///   - MPU-401 UART mode (0x330/0x331)
    ///   - DMA-based 8-bit and 16-bit PCM playback
    ///   - Windows AudioGraph output for actual UWP audio
    ///
    /// Default configuration: I/O base 0x220, IRQ 5, DMA1 (8-bit), DMA5 (16-bit).
    /// Reference: https://pdos.csail.mit.edu/6.828/2011/readings/hardware/SoundBlaster.pdf
    /// </summary>
    public class SB16 : IDevice, INeedsIRQ, INeedsDMA, IShutdown
    {
        // ── Constants ──────────────────────────────────────────────────────────
        private const string DSP_COPYRIGHT   = "COPYRIGHT (C) CREATIVE TECHNOLOGY LTD, 1992.";
        private const int    DSP_NO_COMMAND  = 0;
        private const int    DSP_BUFSIZE     = 64;
        private const int    SB_DMA_BUFSIZE  = 65536;

        private const int SB_DMA_CHANNEL_8BIT  = 1;
        private const int SB_DMA_CHANNEL_16BIT = 5;
        private const int SB_IRQ               = 5;

        // IRQ trigger indices
        private const int SB_IRQ_8BIT  = 0x1;
        private const int SB_IRQ_16BIT = 0x2;
        private const int SB_IRQ_MPU   = 0x4;

        // DSP command sizes (bytes of data following the command byte)
        private static readonly byte[] DSP_COMMAND_SIZES = new byte[256];

        // ── I/O port lists ─────────────────────────────────────────────────────
        private readonly int[] portsUsed = {
            0x220, 0x221, 0x222, 0x223,  // FM primary + secondary
            0x224, 0x225,                 // Mixer address/data
            0x226, 0x227,                 // DSP reset / undocumented
            0x228, 0x229,                 // FM music
            0x22A, 0x22B, 0x22C, 0x22D, 0x22E, 0x22F, // DSP
            0x330, 0x331,                 // MPU-401
            0x388, 0x389,                 // OPL2/OPL3 (AdLib compatible)
        };

        // ── DSP state ──────────────────────────────────────────────────────────
        private int    command = DSP_NO_COMMAND;
        private int    commandSize;
        private readonly Queue<byte> writeBuffer = new Queue<byte>();
        private readonly Queue<byte> readBuffer  = new Queue<byte>();
        private byte   readBufferLastValue;
        private int    samplingRate    = 22050;
        private bool   dsp16bit;
        private bool   dspSigned;
        private bool   dspStereo;
        private bool   dspHighspeed;
        private bool   dmaAutoinit;
        private int    dmaIrq         = SB_IRQ_8BIT;
        private int    dmaChannel;
        private int    dmaChannel8bit  = SB_DMA_CHANNEL_8BIT;
        private int    dmaChannel16bit = SB_DMA_CHANNEL_16BIT;
        private int    dmaTransferSize = 0xFFFF;
        private bool   dmaWaitingTransfer;
        private bool   dmaPaused;
        private readonly byte[] dmaBuffer = new byte[SB_DMA_BUFSIZE];
        private int    dmaBufferPos;

        // ── Mixer state ────────────────────────────────────────────────────────
        private int  mixerCurrentAddress;
        private readonly byte[] mixerRegisters = new byte[256];

        // ── FM/OPL state ───────────────────────────────────────────────────────
        private int fmCurrentAddress0;
        private int fmCurrentAddress1;

        // ── MPU-401 state ──────────────────────────────────────────────────────
        private readonly Queue<byte> mpuReadBuffer      = new Queue<byte>();
        private byte                 mpuReadBufferLast;

        // ── IRQ state ──────────────────────────────────────────────────────────
        private int irqTriggered; // bitmask: SB_IRQ_8BIT | SB_IRQ_16BIT | SB_IRQ_MPU

        // ── ASP registers ──────────────────────────────────────────────────────
        private readonly byte[] aspRegisters = new byte[256];

        // ── Audio output (Windows AudioGraph) ─────────────────────────────────
        private AudioGraph         audioGraph;
        private AudioFrameInputNode audioFrameInput;
        private AudioDeviceOutputNode audioOutput;
        private bool               audioInitialized;
        private readonly float[]   audioSampleBuffer = new float[SB_DMA_BUFSIZE];
        private int                audioSampleCount;

        // ── IDevice / INeedsIRQ / INeedsDMA ────────────────────────────────────
        public event EventHandler                IRQ;
        public event EventHandler<ByteArrayEventArgs> DMA;
        public int IRQNumber  => SB_IRQ;
        public int DMAChannel => dmaChannel;
        public int[] PortsUsed => portsUsed;

        // ── Static constructor – fill DSP command size table ──────────────────
        static SB16()
        {
            // Most commands have no data bytes; fill explicit sizes
            DSP_COMMAND_SIZES[0x0E] = 2;  // ASP set register
            DSP_COMMAND_SIZES[0x0F] = 1;  // ASP get register
            DSP_COMMAND_SIZES[0x10] = 1;  // 8-bit direct output
            DSP_COMMAND_SIZES[0x14] = 2;  // 8-bit single-cycle DMA output
            DSP_COMMAND_SIZES[0x15] = 2;
            DSP_COMMAND_SIZES[0x16] = 2;
            DSP_COMMAND_SIZES[0x17] = 2;
            DSP_COMMAND_SIZES[0x24] = 2;  // 8-bit single-cycle DMA input
            DSP_COMMAND_SIZES[0x40] = 1;  // Set time constant
            DSP_COMMAND_SIZES[0x41] = 2;  // Set output sampling rate
            DSP_COMMAND_SIZES[0x42] = 2;  // Set input sampling rate
            DSP_COMMAND_SIZES[0x48] = 2;  // Set block transfer size
            DSP_COMMAND_SIZES[0x74] = 2;
            DSP_COMMAND_SIZES[0x75] = 2;
            DSP_COMMAND_SIZES[0x76] = 2;
            DSP_COMMAND_SIZES[0x77] = 2;
            DSP_COMMAND_SIZES[0x80] = 2;  // Pause DAC for duration
            for (int i = 0xB0; i <= 0xBF; i++) DSP_COMMAND_SIZES[i] = 3; // 16-bit DMA
            for (int i = 0xC0; i <= 0xCF; i++) DSP_COMMAND_SIZES[i] = 3; // 8-bit DMA
            DSP_COMMAND_SIZES[0xE2] = 1;  // DMA identification
            DSP_COMMAND_SIZES[0xE4] = 1;  // Write test register
        }

        public SB16()
        {
            dmaChannel = dmaChannel8bit;
            MixerReset();
            InitAudioAsync();
        }

        // ══════════════════════════════════════════════════════════════════════
        // IDevice
        // ══════════════════════════════════════════════════════════════════════

        public uint Read(ushort addr, int size)
        {
            switch (addr)
            {
                // FM status / music status
                case 0x220: case 0x388: return 0; // timer status (no timers active)
                case 0x221: case 0x222: case 0x223: case 0x389: return 0xFF;

                // Mixer address
                case 0x224: return (byte)mixerCurrentAddress;

                // Mixer data
                case 0x225: return MixerRead((byte)mixerCurrentAddress);

                // DSP reset (write-only, read returns undocumented)
                case 0x226: return 0xFF;

                // Undocumented
                case 0x227: case 0x22B: case 0x22D: case 0x22F: return 0xFF;

                // FM music data (write-only) / FM status
                case 0x228: return 0xFF;
                case 0x229: return 0xFF;

                // Read Data
                case 0x22A:
                    if (readBuffer.Count > 0)
                        readBufferLastValue = readBuffer.Dequeue();
                    return readBufferLastValue;

                // Write-Buffer Status (bit7 = 0 means ready)
                case 0x22C: return 0x7F;

                // Read-Buffer Status / IRQ 8-bit ack
                case 0x22E:
                {
                    if ((irqTriggered & SB_IRQ_8BIT) != 0)
                        LowerIrq(SB_IRQ_8BIT);
                    bool ready = readBuffer.Count > 0 && !dspHighspeed;
                    return (byte)((ready ? 0x80 : 0) | 0x7F);
                }

                // IRQ 16-bit ack
                case 0x22F:
                    LowerIrq(SB_IRQ_16BIT);
                    return 0;

                // MPU-401 data
                case 0x330:
                    if (mpuReadBuffer.Count > 0)
                        mpuReadBufferLast = mpuReadBuffer.Dequeue();
                    return mpuReadBufferLast;

                // MPU-401 status
                case 0x331:
                {
                    byte status = 0;
                    status |= (byte)(0x40 * 0);                            // output ready
                    status |= (byte)(0x80 * (mpuReadBuffer.Count == 0 ? 1 : 0)); // input ready
                    return status;
                }

                default:
                    return 0xFF;
            }
        }

        public void Write(ushort addr, uint value, int size)
        {
            byte b = (byte)(value & 0xFF);
            switch (addr)
            {
                // FM primary address
                case 0x220: case 0x388: fmCurrentAddress0 = b; break;
                // FM primary data
                case 0x221: case 0x389: FmWrite(b, 0, fmCurrentAddress0); break;
                // FM secondary address
                case 0x222: fmCurrentAddress1 = b; break;
                // FM secondary data
                case 0x223: FmWrite(b, 1, fmCurrentAddress1); break;

                // Mixer address
                case 0x224: mixerCurrentAddress = b; break;
                // Mixer data
                case 0x225: MixerWrite((byte)mixerCurrentAddress, b); break;

                // DSP Reset
                case 0x226:
                    if (b == 1)
                    {
                        // rising edge of reset
                    }
                    else if (b == 0)
                    {
                        DspReset();
                    }
                    break;

                case 0x227: break; // undocumented

                // FM music
                case 0x228: break;
                case 0x229: break;

                // DSP Write Command/Data
                case 0x22A: break; // read-data port (read-only when written)
                case 0x22B: break; // undocumented
                case 0x22C:
                    if (command == DSP_NO_COMMAND)
                    {
                        command = b;
                        writeBuffer.Clear();
                        commandSize = DSP_COMMAND_SIZES[b];
                    }
                    else
                    {
                        writeBuffer.Enqueue(b);
                    }
                    if (writeBuffer.Count >= commandSize)
                        CommandDo();
                    break;
                case 0x22D: break;
                case 0x22E: break; // read-buffer status (read-only)
                case 0x22F: break;

                // MPU-401 data
                case 0x330: break; // unimplemented MIDI output
                // MPU-401 command
                case 0x331:
                    if (b == 0xFF)
                    {
                        mpuReadBuffer.Clear();
                        mpuReadBuffer.Enqueue(0xFE); // acknowledge
                    }
                    break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // DMA
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by the DMAController when a DMA transfer for our channel is ready.
        /// Accepts the transferred bytes, converts them to PCM and schedules playback.
        /// </summary>
        public void OnDmaData(byte[] data)
        {
            if (!dmaWaitingTransfer) return;
            dmaWaitingTransfer = false;

            int sampleCount = dsp16bit ? data.Length / 2 : data.Length;
            int channels    = dspStereo ? 2 : 1;

            // Convert DMA bytes to normalised float32 samples
            int outSamples = sampleCount / channels;
            EnsureAudioBuffer(outSamples * channels);
            int dst = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                float sample;
                if (dsp16bit)
                {
                    short s = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
                    sample = dspSigned ? s / 32768f : (s - 32768) / 32768f;
                }
                else
                {
                    byte raw = data[i];
                    sample = dspSigned ? (raw - 128) / 128f : (raw - 128) / 128f;
                }
                audioSampleBuffer[dst++] = sample;
            }
            audioSampleCount = dst;
            PushAudioFrame();

            // Raise DMA IRQ
            if (!dmaPaused)
            {
                if (dmaAutoinit)
                {
                    // Re-arm transfer
                    DmaTransferStart();
                }
                RaiseIrq(dmaIrq);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // IShutdown
        // ══════════════════════════════════════════════════════════════════════

        public void Shutdown()
        {
            audioGraph?.Stop();
            audioGraph?.Dispose();
            audioGraph = null;
            audioInitialized = false;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Private – DSP command dispatch
        // ══════════════════════════════════════════════════════════════════════

        private void CommandDo()
        {
            int cmd = command;
            command     = DSP_NO_COMMAND;
            commandSize = 0;

            switch (cmd)
            {
                // ASP set register
                case 0x0E:
                    aspRegisters[writeBuffer.Dequeue()] = writeBuffer.Dequeue();
                    break;

                // ASP get register
                case 0x0F:
                    readBuffer.Clear();
                    readBuffer.Enqueue(aspRegisters[writeBuffer.Dequeue()]);
                    break;

                // 8-bit direct output
                case 0x10:
                {
                    byte raw = writeBuffer.Dequeue();
                    float s = (raw - 128) / 128f;
                    audioSampleBuffer[0] = s;
                    audioSampleBuffer[1] = s;
                    audioSampleCount = 2;
                    PushAudioFrame();
                    break;
                }

                // 8-bit single-cycle DMA output
                case 0x14: case 0x15:
                    dmaIrq      = SB_IRQ_8BIT;
                    dmaChannel  = dmaChannel8bit;
                    dmaAutoinit = false;
                    dspSigned   = false;
                    dsp16bit    = false;
                    dspHighspeed = false;
                    DmaTransferSizeSet();
                    DmaTransferStart();
                    break;

                // 8-bit auto-init DMA output
                case 0x1C:
                    dmaIrq      = SB_IRQ_8BIT;
                    dmaChannel  = dmaChannel8bit;
                    dmaAutoinit = true;
                    dspSigned   = false;
                    dsp16bit    = false;
                    dspHighspeed = false;
                    DmaTransferStart();
                    break;

                // 8-bit high-speed single-cycle
                case 0x91:
                    dmaIrq      = SB_IRQ_8BIT;
                    dmaChannel  = dmaChannel8bit;
                    dmaAutoinit = false;
                    dspSigned   = false;
                    dspHighspeed = true;
                    dsp16bit    = false;
                    DmaTransferSizeSet();
                    DmaTransferStart();
                    break;

                // 8-bit high-speed auto-init
                case 0x90:
                    dmaIrq      = SB_IRQ_8BIT;
                    dmaChannel  = dmaChannel8bit;
                    dmaAutoinit = true;
                    dspSigned   = false;
                    dspHighspeed = true;
                    dsp16bit    = false;
                    DmaTransferStart();
                    break;

                // 8-bit direct input (silent)
                case 0x20:
                    readBuffer.Clear();
                    readBuffer.Enqueue(0x7F);
                    break;

                // Set time constant
                case 0x40:
                {
                    byte tc = writeBuffer.Dequeue();
                    int channels = dspStereo ? 2 : 1;
                    samplingRate = 1000000 / ((256 - tc) * channels);
                    UpdateAudioSampleRate();
                    break;
                }

                // Set sampling rate output/input
                case 0x41: case 0x42:
                {
                    int hi = writeBuffer.Dequeue();
                    int lo = writeBuffer.Dequeue();
                    samplingRate = (hi << 8) | lo;
                    UpdateAudioSampleRate();
                    break;
                }

                // Set block transfer size
                case 0x48:
                    DmaTransferSizeSet();
                    break;

                // 16-bit DMA output/input (0xBx)
                case 0xB0: case 0xB1: case 0xB2: case 0xB3:
                case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                case 0xB8: case 0xB9: case 0xBA: case 0xBB:
                case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                    if ((cmd & 0x08) == 0) // skip A/D
                    {
                        byte mode16 = writeBuffer.Dequeue();
                        dmaIrq       = SB_IRQ_16BIT;
                        dmaChannel   = dmaChannel16bit;
                        dmaAutoinit  = (cmd & 0x04) != 0;
                        dspSigned    = (mode16 & 0x10) != 0;
                        dspStereo    = (mode16 & 0x20) != 0;
                        dsp16bit     = true;
                        DmaTransferSizeSet();
                        DmaTransferStart();
                    }
                    else { writeBuffer.Clear(); }
                    break;

                // 8-bit DMA output/input (0xCx)
                case 0xC0: case 0xC1: case 0xC2: case 0xC3:
                case 0xC4: case 0xC5: case 0xC6: case 0xC7:
                case 0xC8: case 0xC9: case 0xCA: case 0xCB:
                case 0xCC: case 0xCD: case 0xCE: case 0xCF:
                    if ((cmd & 0x08) == 0)
                    {
                        byte mode8 = writeBuffer.Dequeue();
                        dmaIrq       = SB_IRQ_8BIT;
                        dmaChannel   = dmaChannel8bit;
                        dmaAutoinit  = (cmd & 0x04) != 0;
                        dspSigned    = (mode8 & 0x10) != 0;
                        dspStereo    = (mode8 & 0x20) != 0;
                        dsp16bit     = false;
                        DmaTransferSizeSet();
                        DmaTransferStart();
                    }
                    else { writeBuffer.Clear(); }
                    break;

                // Pause 8-bit DMA
                case 0xD0:
                    dmaPaused = true;
                    break;

                // Enable/continue 8-bit DMA
                case 0xD4:
                    dmaPaused = false;
                    break;

                // Pause 16-bit DMA
                case 0xD5:
                    dmaPaused = true;
                    break;

                // Enable/continue 16-bit DMA
                case 0xD6:
                    dmaPaused = false;
                    break;

                // Get speaker status
                case 0xD8:
                    readBuffer.Clear();
                    readBuffer.Enqueue(0);
                    break;

                // Exit auto-init 8-bit DMA
                case 0xDA:
                    dmaAutoinit = false;
                    break;

                // Exit auto-init 16-bit DMA
                case 0xD9:
                    dmaAutoinit = false;
                    break;

                // DSP identification (E0)
                case 0xE0:
                {
                    byte b = writeBuffer.Dequeue();
                    readBuffer.Clear();
                    readBuffer.Enqueue((byte)~b);
                    break;
                }

                // Get version (major=4, minor=5 → SB16)
                case 0xE1:
                    readBuffer.Clear();
                    readBuffer.Enqueue(4);
                    readBuffer.Enqueue(5);
                    break;

                // DMA identification
                case 0xE2: writeBuffer.Clear(); break;

                // Copyright string
                case 0xE3:
                    readBuffer.Clear();
                    foreach (char c in DSP_COPYRIGHT)
                        readBuffer.Enqueue((byte)c);
                    readBuffer.Enqueue(0);
                    break;

                // Write test register
                case 0xE4: aspRegisters[0] = writeBuffer.Dequeue(); break;

                // Read test register
                case 0xE8:
                    readBuffer.Clear();
                    readBuffer.Enqueue(aspRegisters[0]);
                    break;

                // Trigger IRQ
                case 0xF2:
                    RaiseIrq(SB_IRQ_8BIT);
                    break;
                case 0xF3:
                    RaiseIrq(SB_IRQ_16BIT);
                    break;

                default:
                    Debug.WriteLine($"[SB16] Unhandled DSP command: 0x{cmd:X2}");
                    writeBuffer.Clear();
                    break;
            }
        }

        private void DspReset()
        {
            command       = DSP_NO_COMMAND;
            commandSize   = 0;
            writeBuffer.Clear();
            readBuffer.Clear();
            readBuffer.Enqueue(0xAA); // ready byte after reset
            dmaWaitingTransfer = false;
            dmaPaused          = false;
        }

        private void DmaTransferSizeSet()
        {
            if (writeBuffer.Count >= 2)
            {
                int lo = writeBuffer.Dequeue();
                int hi = writeBuffer.Dequeue();
                dmaTransferSize = (hi << 8) | lo;
            }
        }

        private void DmaTransferStart()
        {
            dmaWaitingTransfer = true;
            int byteCount = (dmaTransferSize + 1) * (dsp16bit ? 2 : 1);
            var buf = new byte[Math.Min(byteCount, SB_DMA_BUFSIZE)];
            DMA?.Invoke(this, new ByteArrayEventArgs(buf));
        }

        // ══════════════════════════════════════════════════════════════════════
        // Private – Mixer
        // ══════════════════════════════════════════════════════════════════════

        private void MixerReset()
        {
            Array.Clear(mixerRegisters, 0, mixerRegisters.Length);
            mixerRegisters[0x22] = (12 << 4) | 12;
            mixerRegisters[0x26] = (12 << 4) | 12;
            mixerRegisters[0x30] = 24 << 3;
            mixerRegisters[0x31] = 24 << 3;
            mixerRegisters[0x32] = 24 << 3;
            mixerRegisters[0x33] = 24 << 3;
            mixerRegisters[0x34] = 24 << 3;
            mixerRegisters[0x35] = 24 << 3;
            mixerRegisters[0x3C] = 0x1F;
            mixerRegisters[0x3D] = 0x15;
            mixerRegisters[0x3E] = 0x0B;
        }

        private byte MixerRead(byte addr)
        {
            return mixerRegisters[addr];
        }

        private void MixerWrite(byte addr, byte value)
        {
            switch (addr)
            {
                case 0x80: // IRQ select
                    if ((value & 0x1) != 0) SB_IRQ_SetIRQ(2);
                    if ((value & 0x2) != 0) SB_IRQ_SetIRQ(5);
                    if ((value & 0x4) != 0) SB_IRQ_SetIRQ(7);
                    if ((value & 0x8) != 0) SB_IRQ_SetIRQ(10);
                    break;
                case 0x81: // DMA select
                    if ((value & 0x01) != 0) dmaChannel8bit  = 0;
                    if ((value & 0x02) != 0) dmaChannel8bit  = 1;
                    if ((value & 0x08) != 0) dmaChannel8bit  = 3;
                    if ((value & 0x20) != 0) dmaChannel16bit = 5;
                    if ((value & 0x40) != 0) dmaChannel16bit = 6;
                    if ((value & 0x80) != 0) dmaChannel16bit = 7;
                    break;
                default:
                    mixerRegisters[addr] = value;
                    break;
            }
        }

        private int currentIrq = SB_IRQ;
        private void SB_IRQ_SetIRQ(int irqNum) { currentIrq = irqNum; }

        // ══════════════════════════════════════════════════════════════════════
        // Private – FM / OPL (placeholder – full OPL3 synthesis not implemented)
        // ══════════════════════════════════════════════════════════════════════

        private void FmWrite(byte value, int bank, int reg)
        {
            // OPL3 synthesis is not implemented; writes are silently accepted.
            Debug.WriteLine($"[SB16] FM bank={bank} reg=0x{reg:X2} val=0x{value:X2} (unimplemented)");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Private – IRQ
        // ══════════════════════════════════════════════════════════════════════

        private void RaiseIrq(int irqType)
        {
            irqTriggered |= irqType;
            IRQ?.Invoke(this, EventArgs.Empty);
        }

        private void LowerIrq(int irqType)
        {
            irqTriggered &= ~irqType;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Private – Windows Audio (AudioGraph)
        // ══════════════════════════════════════════════════════════════════════

        private async void InitAudioAsync()
        {
            try
            {
                var settings = new AudioGraphSettings(AudioRenderCategory.GameEffects)
                {
                    DesiredSamplesPerQuantum = 1024,
                    QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
                    EncodingProperties = AudioEncodingProperties.CreatePcm(
                        (uint)samplingRate, 2, 32),
                };

                var result = await AudioGraph.CreateAsync(settings);
                if (result.Status != AudioGraphCreationStatus.Success)
                {
                    Debug.WriteLine($"[SB16] AudioGraph create failed: {result.Status}");
                    return;
                }

                audioGraph = result.Graph;

                var outResult = await audioGraph.CreateDeviceOutputNodeAsync();
                if (outResult.Status != AudioDeviceNodeCreationStatus.Success)
                {
                    Debug.WriteLine($"[SB16] AudioDeviceOutputNode failed: {outResult.Status}");
                    return;
                }
                audioOutput = outResult.DeviceOutputNode;

                var encoding = AudioEncodingProperties.CreatePcm(
                    (uint)samplingRate, 2, 32);
                audioFrameInput = audioGraph.CreateFrameInputNode(encoding);
                audioFrameInput.AddOutgoingConnection(audioOutput);

                audioGraph.Start();
                audioInitialized = true;
                Debug.WriteLine("[SB16] AudioGraph initialised OK");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SB16] Audio init exception: {ex.Message}");
            }
        }

        private void UpdateAudioSampleRate()
        {
            // Re-creating the graph on rate change would cause glitches; skip for now.
            Debug.WriteLine($"[SB16] Sampling rate → {samplingRate} Hz");
        }

        private void EnsureAudioBuffer(int needed)
        {
            // audioSampleBuffer is pre-allocated at SB_DMA_BUFSIZE; resize if needed
        }

        private unsafe void PushAudioFrame()
        {
            if (!audioInitialized || audioFrameInput == null || audioSampleCount == 0) return;

            try
            {
                var frame = new AudioFrame((uint)(audioSampleCount * sizeof(float)));
                using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
                using (var reference = buffer.CreateReference())
                {
                    byte* data;
                    uint  cap;
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out cap);

                    int bytes = Math.Min(audioSampleCount * sizeof(float), (int)cap);
                    fixed (float* src = audioSampleBuffer)
                        Buffer.MemoryCopy(src, data, cap, bytes);
                }
                audioFrameInput.AddFrame(frame);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SB16] PushAudioFrame exception: {ex.Message}");
            }

            audioSampleCount = 0;
        }
    }

    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComImport]
    internal unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
