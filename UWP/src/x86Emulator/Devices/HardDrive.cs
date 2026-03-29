using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using x86Emulator.Devices;

namespace x86Emulator.ATADevice
{
    public class HardDrive : ATADrive
    {
        private Stream diskStream;
        private string imagePath;
        private const int SectorSize = 512;
        private long totalSectors;

        // Geometry computed from image size; Bochs BIOS needs non-zero values
        public uint Cylinders { get; private set; } = 1024;
        public uint Heads { get; private set; } = 16;
        public uint Sectors { get; private set; } = 63;

        // LBA saved when a write command is issued (registers may change before FinishCommand)
        private uint pendingWriteLBA;
        private int pendingWriteCount;
        private bool writeTransferActive;

        public HardDrive() { }

        public HardDrive(string path)
        {
            if (File.Exists(path))
            {
                imagePath = path;
                Stream raw = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.Read, SectorSize * 8, FileOptions.RandomAccess);
                diskStream = IsVhd(path) ? VhdStream.OpenOrPassThrough(raw) : raw;
                UpdateGeometry();
                Reset();
            }
        }

        public override async Task LoadImage(StorageFile file)
        {
            if (file != null)
            {
                imagePath = file.Path;
                Stream raw;
                try
                {
                    var rStream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                    raw = rStream.AsStream();
                }
                catch
                {
                    // Fall back to read-only if the image is write-protected or locked.
                    System.Diagnostics.Debug.WriteLine("[HDD] Could not open image for write; falling back to read-only.");
                    raw = await file.OpenStreamForReadAsync();
                }
                diskStream = IsVhd(file.Name) ? VhdStream.OpenOrPassThrough(raw) : raw;
                UpdateGeometry();
                Reset();
            }
        }

        private static bool IsVhd(string filename) =>
            !string.IsNullOrEmpty(filename) &&
            filename.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase);

        private void UpdateGeometry()
        {
            if (diskStream == null) return;
            totalSectors = diskStream.Length / SectorSize;
            if (totalSectors > 0)
            {
                Heads = 16;
                Sectors = 63;
                Cylinders = (uint)(totalSectors / (Heads * Sectors));
                if (Cylinders == 0) Cylinders = 1;
                if (Cylinders > 16383) Cylinders = 16383; // ATA-7 CHS maximum
            }
        }

        public override void Reset()
        {
            SetIdleState();
            // ATA spec: after SRST/DEVICE RESET the master must set Error = 0x01
            // (Diagnostic Passed). Bochs BIOS reads this register to verify the drive.
            Error = DeviceError.DiagnosticPassed;
            CylinderLow = 0;
            CylinderHigh = 0;
            SectorNumber = 1;
            SectorCount = 1;
            DriveHead = (byte)((DriveHead & 0x10) | 0xA0);
            sectorBuffer = null;
            bufferIndex = 0;
            transferWordCount = 0;
            pendingWriteLBA = 0;
            pendingWriteCount = 0;
            writeTransferActive = false;
        }

        public override void RunCommand(byte command)
        {
            switch (command)
            {
                case 0x08: // DEVICE RESET
                case 0x10: // RECALIBRATE
                case 0x70: // SEEK
                    CompleteSeekLikeCommand();
                    break;

                case 0x20: // READ SECTOR(S)
                case 0x21: // READ SECTOR(S) WITHOUT RETRY
                case 0x24: // READ SECTOR(S) EXT (LBA48 – treat as LBA28 for small disks)
                case 0xC4: // READ MULTIPLE
                    ReadSectors();
                    break;

                case 0x30: // WRITE SECTOR(S)
                case 0x31: // WRITE SECTOR(S) WITHOUT RETRY
                case 0xC5: // WRITE MULTIPLE
                    WriteSectors();
                    break;

                case 0xEC: // IDENTIFY DEVICE
                    IdentifyDevice();
                    break;

                case 0x91: // INITIALIZE DEVICE PARAMETERS (set geometry – accept and ignore)
                case 0xC6: // SET MULTIPLE MODE
                case 0xE7: // FLUSH CACHE
                case 0xEA: // FLUSH CACHE EXT
                case 0xEF: // SET FEATURES
                    SetIdleState();
                    break;

                case 0x40: // READ VERIFY SECTOR(S)
                case 0x41: // READ VERIFY SECTOR(S) WITHOUT RETRY
                    VerifySectors();
                    break;

                case 0x90: // EXECUTE DEVICE DIAGNOSTIC
                    ExecuteDeviceDiagnostic();
                    break;

                default:
                    // ATA spec: after command abort the drive must still assert DRDY.
                    Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                    Error = DeviceError.Aborted;
                    break;
            }
        }

        // Returns the effective sector count: ATA SectorCount=0 means 256 sectors per the ATA spec.
        private int GetEffectiveSectorCount() => SectorCount == 0 ? 256 : SectorCount;

        // Returns LBA28 address from the current ATA registers (supports LBA and CHS modes).
        private uint GetLBA()
        {
            if ((DriveHead & 0x40) != 0)
            {
                // LBA28 mode: LBA[27:24]=DriveHead[3:0], LBA[23:16]=CylinderHigh, LBA[15:8]=CylinderLow, LBA[7:0]=SectorNumber
                return (uint)(((DriveHead & 0x0F) << 24) | (CylinderHigh << 16) | (CylinderLow << 8) | SectorNumber);
            }
            else
            {
                // CHS mode
                uint cylinder = (uint)((CylinderHigh << 8) | CylinderLow);
                uint head = (uint)(DriveHead & 0x0F);
                uint sector = SectorNumber;
                // CHS mode – sector numbers are 1-based; 0 is invalid
                if (sector == 0)
                {
                    return 0; // treat as first sector to avoid crash; caller should validate
                }
                return (cylinder * Heads + head) * Sectors + (sector - 1u);
            }
        }

        private void IdentifyDevice()
        {
            ushort[] data = new ushort[256];

            data[0]  = 0x0040; // General config: fixed disk, non-removable
            data[1]  = (ushort)Cylinders;
            data[3]  = (ushort)Heads;
            data[6]  = (ushort)Sectors;

            WriteIdentifyString(data, 10, 20, "GPTEMU-HDD-0001     "); // Serial number
            WriteIdentifyString(data, 23,  8, "1.0     ");             // Firmware revision
            WriteIdentifyString(data, 27, 40, "GPTEMU Virtual Hard Drive               "); // Model

            data[47] = 0x0000; // READ/WRITE MULTIPLE not supported – prevents BIOS from using 0xC4
            data[49] = 0x0200; // Capabilities: LBA supported (bit 9)
            data[50] = 0x4000; // Reserved (bit 14 must be 1 per ATA spec)
            data[51] = 0x0200; // PIO timing mode
            data[53] = 0x0007; // Words 54-58, 64-70, 88 are valid

            data[54] = (ushort)Cylinders;
            data[55] = (ushort)Heads;
            data[56] = (ushort)Sectors;

            uint chsSectors = Cylinders * Heads * Sectors;
            uint lbaSectors = totalSectors > uint.MaxValue ? uint.MaxValue : (uint)totalSectors;
            data[57] = (ushort)(chsSectors & 0xFFFF); // Current capacity (low)
            data[58] = (ushort)(chsSectors >> 16);    // Current capacity (high)

            data[60] = (ushort)(lbaSectors & 0xFFFF); // LBA total sectors (low)
            data[61] = (ushort)(lbaSectors >> 16);    // LBA total sectors (high)

            data[63] = 0x0000; // DMA not supported by the emulator yet
            data[64] = 0x0003; // Advanced PIO modes 3-4 supported
            data[65] = 120;
            data[66] = 120;
            data[67] = 120;
            data[68] = 120;
            data[80] = 0x001E; // ATA/ATAPI-2 through ATA/ATAPI-4 supported
            data[82] = 0x4000; // Command sets supported
            data[83] = 0x4000;
            data[84] = 0x4000;
            data[85] = 0x4000;
            data[87] = 0x4000;
            data[88] = 0x0000; // Ultra DMA not supported

            StartReadTransfer(data);
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready | DeviceStatus.SeekComplete;
            Error = DeviceError.None;
        }

        // ATA string encoding: within each word, the high byte holds the first character.
        private static void WriteIdentifyString(ushort[] dest, int startWord, int maxBytes, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text.PadRight(maxBytes, ' '));
            int wordCount = maxBytes / 2;
            for (int i = 0; i < wordCount; i++)
                dest[startWord + i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        }

        private void ReadSectors()
        {
            if (diskStream == null)
            {
                // ATA spec: even on error, DRDY must remain asserted so that
                // ata_is_ready() in the BIOS still sees the drive as present.
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            uint lba = GetLBA();
            int count = GetEffectiveSectorCount();
            long byteOffset = (long)lba * SectorSize;

            if (byteOffset + (long)count * SectorSize > diskStream.Length)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.IDNotFound;
                return;
            }

            int totalBytes = count * SectorSize;
            byte[] buffer = new byte[totalBytes];
            try
            {
                diskStream.Seek(byteOffset, SeekOrigin.Begin);
                ReadExact(diskStream, buffer, totalBytes);
            }
            catch (IOException)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            ushort[] words = new ushort[totalBytes / 2];
            Buffer.BlockCopy(buffer, 0, words, 0, totalBytes);
            StartReadTransfer(words);

            Status = DeviceStatus.DataRequest | DeviceStatus.Ready | DeviceStatus.SeekComplete;
            Error = DeviceError.None;
            writeTransferActive = false;
        }

        private void WriteSectors()
        {
            if (diskStream == null)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            pendingWriteLBA = GetLBA();
            pendingWriteCount = GetEffectiveSectorCount();

            long byteOffset = (long)pendingWriteLBA * SectorSize;
            if (byteOffset + (long)pendingWriteCount * SectorSize > diskStream.Length)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.IDNotFound;
                return;
            }

            // Prepare buffer for the host to fill; FinishCommand() writes it to disk.
            StartWriteTransfer(pendingWriteCount * SectorSize / 2);
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready | DeviceStatus.SeekComplete;
            Error = DeviceError.None;
            writeTransferActive = true;
        }

        public override void FinishCommand()
        {
            if (!writeTransferActive)
            {
                SetIdleState();
                return;
            }

            writeTransferActive = false;
            if (diskStream != null && sectorBuffer != null && sectorBuffer.Length > 0)
            {
                int totalBytes = sectorBuffer.Length * 2;
                byte[] buffer = new byte[totalBytes];
                Buffer.BlockCopy(sectorBuffer, 0, buffer, 0, totalBytes);
                long byteOffset = (long)pendingWriteLBA * SectorSize;
                diskStream.Seek(byteOffset, SeekOrigin.Begin);
                diskStream.Write(buffer, 0, totalBytes);
                diskStream.Flush();
            }
            SetIdleState();
        }

        public override void FinishRead()
        {
            SetIdleState();
        }

        private void CompleteSeekLikeCommand()
        {
            if (diskStream == null)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            VerifyAddressOnly();
        }

        private void VerifySectors()
        {
            if (diskStream == null)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            VerifyAddressOnly();
        }

        private void VerifyAddressOnly()
        {
            uint lba = GetLBA();
            int count = GetEffectiveSectorCount();
            long byteOffset = (long)lba * SectorSize;

            if (byteOffset + (long)count * SectorSize > diskStream.Length)
            {
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.IDNotFound;
                return;
            }

            SetIdleState();
        }

        private void ExecuteDeviceDiagnostic()
        {
            SectorCount = 1;
            SectorNumber = 1;
            CylinderLow = 0;
            CylinderHigh = 0;
            Status = DeviceStatus.Ready | DeviceStatus.SeekComplete;
            Error = DeviceError.DiagnosticPassed;
            writeTransferActive = false;
        }

        private void SetIdleState()
        {
            Status = DeviceStatus.Ready | DeviceStatus.SeekComplete;
            Error = DeviceError.None;
        }

        private static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                    throw new EndOfStreamException("Unexpected end of disk image.");
                offset += read;
            }
        }
    }
}
