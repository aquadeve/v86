// src/x86Emulator/ATADevice/ATADrive.cs
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace x86Emulator.ATADevice
{
    [Flags]
    public enum DeviceStatus : byte
    {
        None = 0x00,
        Error = 0x01,
        Index = 0x02,
        CorrectedData = 0x04,
        DataRequest = 0x08,
        SeekComplete = 0x10,
        WriteFault = 0x20,
        Ready = 0x40,
        Busy = 0x80
    }

    [Flags]
    public enum DeviceError : byte
    {
        None = 0x00,
        DiagnosticPassed = 0x01,
        Aborted = 0x04,
        MediaChange = 0x08,
        IDNotFound = 0x10,
        MediaChanged = 0x20,
        Uncorrectable = 0x40,
        BadBlock = 0x80
    }

    public abstract class ATADrive
    {
        public DeviceError Error { get; set; } = DeviceError.None;
        public byte SectorCount { get; set; }
        public byte SectorNumber { get; set; }
        public byte CylinderLow { get; set; }
        public byte CylinderHigh { get; set; }
        public byte DriveHead { get; set; }
        public DeviceStatus Status { get; set; } = DeviceStatus.None;

        protected ushort[] sectorBuffer;
        protected int bufferIndex;
        protected int transferWordCount;

        public ushort Cylinder
        {
            get { return (ushort)((CylinderHigh << 8) + CylinderLow); }
            set
            {
                CylinderLow = (byte)value;
                CylinderHigh = (byte)(value >> 8);
            }
        }

        protected void StartReadTransfer(ushort[] data)
        {
            sectorBuffer = data ?? Array.Empty<ushort>();
            bufferIndex = 0;
            transferWordCount = sectorBuffer.Length;
        }

        protected void StartWriteTransfer(int wordCount)
        {
            if (wordCount < 0)
                wordCount = 0;

            sectorBuffer = new ushort[wordCount];
            bufferIndex = 0;
            transferWordCount = sectorBuffer.Length;
        }

        public ushort SectorBuffer
        {
            get
            {
                if (sectorBuffer == null || transferWordCount == 0 || bufferIndex >= transferWordCount)
                    return 0;

                ushort value = sectorBuffer[bufferIndex++];
                if (bufferIndex >= transferWordCount)
                {
                    Status &= ~DeviceStatus.DataRequest;
                    FinishRead();
                }

                return value;
            }
            set
            {
                if (sectorBuffer == null || transferWordCount == 0 || bufferIndex >= transferWordCount)
                    return;

                sectorBuffer[bufferIndex++] = value;
                if (bufferIndex >= transferWordCount)
                {
                    Status &= ~DeviceStatus.DataRequest;
                    FinishCommand();
                }
            }
        }

        // Implementations must provide these
        public abstract Task LoadImage(StorageFile filename);
        public abstract void Reset();
        public abstract void RunCommand(byte command);
        public abstract void FinishCommand();
        public abstract void FinishRead();
    }
}
