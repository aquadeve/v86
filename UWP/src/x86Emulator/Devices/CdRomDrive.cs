using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using x86Emulator.Devices;

namespace x86Emulator.ATADevice
{
    public class CdRomDrive : ATADrive
    {
        private const int SectorSize = 2048;

        private Stream isoStream;
        private readonly byte[] packetBuffer = new byte[12];
        private bool awaitingPacket;
        private byte senseKey;
        private byte additionalSenseCode;
        private byte additionalSenseQualifier;

        public CdRomDrive()
        {
            string isoPath = Resources.CdRomImagePath;
            if (string.IsNullOrEmpty(isoPath) || !File.Exists(isoPath))
            {
                Debug.WriteLine("[CDROM] No ISO file found to load.");
                return;
            }

            try
            {
                isoStream = File.OpenRead(isoPath);
                Status = DeviceStatus.Ready;
                Debug.WriteLine($"[CDROM] ISO loaded: {isoPath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CDROM] Failed to load ISO: {ex.Message}");
            }
        }

        public CdRomDrive(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                isoStream = File.OpenRead(path);
                Status = DeviceStatus.Ready;
                Debug.WriteLine($"[CDROM] ISO loaded manually: {path}");
            }
            else
            {
                Debug.WriteLine($"[CDROM] Invalid or missing ISO path: {path}");
            }
        }

        public override async Task LoadImage(StorageFile filename)
        {
            if (filename == null)
                return;

            isoStream = await DiskImageLoader.OpenFromStorageFileAsync(filename, readOnly: true);
            Status = DeviceStatus.Ready;
            Error = DeviceError.None;
            ClearSense();
            Debug.WriteLine($"[CDROM] ISO loaded (UWP): {filename.Path}");
        }

        public override void Reset()
        {
            CylinderLow = 0x14;
            CylinderHigh = 0xEB;
            Status = DeviceStatus.Ready | DeviceStatus.SeekComplete;
            // ATA spec: after SRST/DEVICE RESET the master must set Error = 0x01
            // (Diagnostic Passed). Bochs BIOS reads this to verify the device.
            Error = DeviceError.DiagnosticPassed;
            awaitingPacket = false;
            ClearSense();
            Debug.WriteLine("[CDROM] Reset complete - ATAPI signature set (0xEB14)");
        }

        public override void RunCommand(byte command)
        {
            Debug.WriteLine($"[CDROM] RunCommand 0x{command:X2}");
            switch (command)
            {
                case 0x08:
                    Reset();
                    break;
                case 0x90: // EXECUTE DEVICE DIAGNOSTIC
                    // ATAPI devices must respond to this during BIOS initialisation.
                    // Setting DiagnosticPassed tells the BIOS the device is healthy.
                    CylinderLow = 0x14;
                    CylinderHigh = 0xEB;
                    Status = DeviceStatus.Ready | DeviceStatus.SeekComplete;
                    Error = DeviceError.DiagnosticPassed;
                    break;
                case 0x91: // INITIALIZE DEVICE PARAMETERS - accept and ignore
                case 0xEF: // SET FEATURES – accept and ignore
                    Status = DeviceStatus.Ready | DeviceStatus.SeekComplete;
                    Error = DeviceError.None;
                    break;
                case 0xEC: // IDENTIFY DEVICE (ATA, not ATAPI)
                    // ATAPI devices must abort this and expose the ATAPI
                    // signature (0xEB14) so the BIOS issues 0xA1 instead.
                    CylinderLow = 0x14;
                    CylinderHigh = 0xEB;
                    Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                    Error = DeviceError.Aborted;
                    break;
                case 0xA0:
                    StartPacketCommand();
                    break;
                case 0xA1:
                    IdentifyPacketDevice();
                    break;
                default:
                    Debug.WriteLine($"[CDROM] Unsupported ATA command 0x{command:X2}");
                    SetSense(0x05, 0x20, 0x00);
                    Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                    Error = DeviceError.Aborted;
                    break;
            }
        }

        public override void FinishCommand()
        {
            if (!awaitingPacket)
            {
                Status = DeviceStatus.Ready;
                return;
            }

            awaitingPacket = false;
            Array.Clear(packetBuffer, 0, packetBuffer.Length);
            if (sectorBuffer != null)
            {
                Buffer.BlockCopy(sectorBuffer, 0, packetBuffer, 0, Math.Min(packetBuffer.Length, sectorBuffer.Length * 2));
            }

            ExecutePacketCommand(packetBuffer);
        }

        public override void FinishRead()
        {
            Status = DeviceStatus.Ready;
        }

        private void StartPacketCommand()
        {
            awaitingPacket = true;
            StartWriteTransfer(packetBuffer.Length / 2);
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
            Error = DeviceError.None;
        }

        private void IdentifyPacketDevice()
        {
            ushort[] data = new ushort[256];

            data[0] = 0x8500; // ATAPI removable CD-ROM
            data[49] = 0x0200; // LBA supported
            data[50] = 0x4000;
            data[53] = 0x0003;
            data[63] = 0x0103;
            data[64] = 0x0001;
            data[71] = 30;
            data[72] = 30;
            data[80] = 0x003E;
            data[82] = 0x4000;
            data[83] = 0x4000;
            data[85] = 0x4000;
            data[87] = 0x4000;

            WriteIdentifyString(data, 10, 20, "CDROM0001");
            WriteIdentifyString(data, 23, 8, "1.00");
            WriteIdentifyString(data, 27, 40, "GPT-EMU ATAPI CD-ROM");

            StartReadTransfer(data);
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
            Error = DeviceError.None;
        }

        private void ExecutePacketCommand(byte[] packet)
        {
            byte command = packet[0];
            Debug.WriteLine($"[CDROM] ExecutePacketCommand 0x{command:X2}");

            switch (command)
            {
                case 0x00:
                    FinishPacketWithoutData();
                    break;
                case 0x03:
                    SendRequestSense(packet);
                    break;
                case 0x12:
                    SendInquiryResponse(packet);
                    break;
                case 0x1A:
                    SendModeSense6(packet);
                    break;
                case 0x1E:
                    FinishPacketWithoutData();
                    break;
                case 0x25:
                    SendReadCapacity();
                    break;
                case 0x28:
                case 0xA8:
                    HandleReadPacket(packet);
                    break;
                case 0x43:
                    SendReadToc(packet);
                    break;
                default:
                    Debug.WriteLine($"[CDROM] Unsupported packet command 0x{command:X2}");
                    SetSense(0x05, 0x20, 0x00);
                    Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                    Error = DeviceError.Aborted;
                    break;
            }
        }

        private void SendInquiryResponse(byte[] packet)
        {
            int allocationLength = packet[4];
            byte[] response = new byte[36];
            response[0] = 0x05; // CD/DVD
            response[1] = 0x80; // removable
            response[2] = 0x00;
            response[3] = 0x21;
            response[4] = 31;
            Encoding.ASCII.GetBytes("GPT-EMU ").CopyTo(response, 8);
            Encoding.ASCII.GetBytes("ATAPI CD-ROM    ").CopyTo(response, 16);
            Encoding.ASCII.GetBytes("1.00").CopyTo(response, 32);

            SendDataToHost(TruncateResponse(response, allocationLength));
            ClearSense();
        }

        private void SendModeSense6(byte[] packet)
        {
            int allocationLength = packet[4];
            byte[] response = new byte[8];
            response[0] = 0x06;
            response[1] = 0x00;
            response[2] = 0x80;
            response[3] = 0x00;

            SendDataToHost(TruncateResponse(response, allocationLength));
            ClearSense();
        }

        private void SendRequestSense(byte[] packet)
        {
            int allocationLength = packet[4];
            byte[] response = new byte[18];
            response[0] = 0x70;
            response[2] = senseKey;
            response[7] = 10;
            response[12] = additionalSenseCode;
            response[13] = additionalSenseQualifier;

            SendDataToHost(TruncateResponse(response, allocationLength));
            ClearSense();
        }

        private void SendReadCapacity()
        {
            if (isoStream == null)
            {
                SetSense(0x02, 0x3A, 0x00);
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            uint totalSectors = (uint)(isoStream.Length / SectorSize);
            uint lastLba = totalSectors == 0 ? 0 : totalSectors - 1;
            byte[] response = new byte[8];
            WriteUInt32BE(response, 0, lastLba);
            WriteUInt32BE(response, 4, SectorSize);

            SendDataToHost(response);
            ClearSense();
        }

        private void SendReadToc(byte[] packet)
        {
            if (isoStream == null)
            {
                SetSense(0x02, 0x3A, 0x00);
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            bool msf = (packet[1] & 0x02) != 0;
            int allocationLength = (packet[7] << 8) | packet[8];
            uint totalSectors = (uint)(isoStream.Length / SectorSize);
            uint leadOutLba = totalSectors;

            byte[] response = new byte[20];
            response[1] = 18;
            response[2] = 1;
            response[3] = 1;

            response[5] = 0x14;
            response[6] = 1;
            WriteAddress(response, 8, 0, msf);

            response[13] = 0x16;
            response[14] = 0xAA;
            WriteAddress(response, 16, leadOutLba, msf);

            SendDataToHost(TruncateResponse(response, allocationLength));
            ClearSense();
        }

        private void HandleReadPacket(byte[] packet)
        {
            if (isoStream == null)
            {
                SetSense(0x02, 0x3A, 0x00);
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            byte opcode = packet[0];
            uint lba = (uint)((packet[2] << 24) | (packet[3] << 16) | (packet[4] << 8) | packet[5]);
            uint count;

            if (opcode == 0x28)
            {
                count = (uint)((packet[7] << 8) | packet[8]);
                if (count == 0)
                    count = 0x10000;
            }
            else
            {
                count = (uint)((packet[6] << 24) | (packet[7] << 16) | (packet[8] << 8) | packet[9]);
            }

            if (count == 0)
            {
                FinishPacketWithoutData();
                return;
            }

            long totalSectors = isoStream.Length / SectorSize;
            if (lba >= totalSectors || lba + count > totalSectors)
            {
                SetSense(0x05, 0x21, 0x00);
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.IDNotFound;
                return;
            }

            long totalBytes = (long)count * SectorSize;
            if (totalBytes > int.MaxValue)
            {
                SetSense(0x05, 0x24, 0x00);
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.Aborted;
                return;
            }

            byte[] buffer = new byte[(int)totalBytes];

            try
            {
                isoStream.Seek((long)lba * SectorSize, SeekOrigin.Begin);
                ReadExact(isoStream, buffer, buffer.Length);
                SendDataToHost(buffer);
                ClearSense();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CDROM] Read error: {ex.Message}");
                SetSense(0x03, 0x11, 0x00);
                Status = DeviceStatus.Error | DeviceStatus.Ready | DeviceStatus.SeekComplete;
                Error = DeviceError.BadBlock;
            }
        }

        private void FinishPacketWithoutData()
        {
            Status = DeviceStatus.Ready;
            Error = DeviceError.None;
            ClearSense();
            CylinderLow = 0;
            CylinderHigh = 0;
        }

        private void SendDataToHost(byte[] data)
        {
            int paddedLength = (data.Length + 1) & ~1;
            byte[] padded = data;
            if (paddedLength != data.Length)
            {
                padded = new byte[paddedLength];
                Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            }

            ushort[] words = new ushort[paddedLength / 2];
            Buffer.BlockCopy(padded, 0, words, 0, paddedLength);
            StartReadTransfer(words);
            Status = DeviceStatus.DataRequest | DeviceStatus.Ready;
            Error = DeviceError.None;
            CylinderLow = (byte)(paddedLength & 0xFF);
            CylinderHigh = (byte)(paddedLength >> 8);
        }

        private void SetSense(byte key, byte asc, byte ascq)
        {
            senseKey = key;
            additionalSenseCode = asc;
            additionalSenseQualifier = ascq;
        }

        private void ClearSense()
        {
            SetSense(0x00, 0x00, 0x00);
        }

        private static void WriteIdentifyString(ushort[] dest, int startWord, int maxBytes, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text.PadRight(maxBytes, ' '));
            int wordCount = maxBytes / 2;
            for (int i = 0; i < wordCount; i++)
                dest[startWord + i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        }

        private static byte[] TruncateResponse(byte[] data, int allocationLength)
        {
            if (allocationLength <= 0 || allocationLength >= data.Length)
                return data;

            byte[] truncated = new byte[allocationLength];
            Buffer.BlockCopy(data, 0, truncated, 0, allocationLength);
            return truncated;
        }

        private static void WriteUInt32BE(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteAddress(byte[] buffer, int offset, uint lba, bool msf)
        {
            if (msf)
            {
                uint address = lba + 150;
                buffer[offset] = 0;
                buffer[offset + 1] = (byte)(address / (60 * 75));
                buffer[offset + 2] = (byte)((address / 75) % 60);
                buffer[offset + 3] = (byte)(address % 75);
                return;
            }

            WriteUInt32BE(buffer, offset, lba);
        }

        private static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                    throw new EndOfStreamException("Unexpected end of ISO image.");
                offset += read;
            }
        }
    }
}
