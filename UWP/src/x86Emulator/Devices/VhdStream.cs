using System;
using System.IO;
using System.Text;

namespace x86Emulator.Devices
{
    internal sealed class VhdStream : Stream
    {
        private const int FooterSize = 512;
        private const int DynamicHeaderSize = 1024;
        private const uint BatEntryFree = 0xFFFFFFFF;
        private const int SectorSize = 512;

        private static readonly byte[] FooterCookie = Encoding.ASCII.GetBytes("conectix");
        private static readonly byte[] DynamicCookie = Encoding.ASCII.GetBytes("cxsparse");

        private const uint DiskTypeFixed = 2;
        private const uint DiskTypeDynamic = 3;

        private readonly Stream baseStream;
        private readonly bool ownsBase;
        private readonly uint diskType;
        private readonly long diskSize;

        private uint[] bat;
        private int blockSectors;
        private int bitmapSectors;
        private int blockSizeBytes;
        private long batOffset;

        private long position;

        public static Stream OpenOrPassThrough(Stream sourceStream, bool ownsBase = true)
        {
            if (sourceStream == null)
                throw new ArgumentNullException(nameof(sourceStream));
            if (sourceStream.Length < FooterSize)
                return sourceStream;

            byte[] footer = ReadFooter(sourceStream);
            if (!StartsWithCookie(footer, 0, FooterCookie))
                return sourceStream;

            uint diskType = ReadUInt32BE(footer, 60);
            if (diskType != DiskTypeFixed && diskType != DiskTypeDynamic)
                return sourceStream;

            return new VhdStream(sourceStream, ownsBase, footer, diskType);
        }

        private VhdStream(Stream sourceStream, bool ownsBase, byte[] footer, uint diskType)
        {
            baseStream = sourceStream;
            this.ownsBase = ownsBase;
            this.diskType = diskType;
            diskSize = (long)ReadUInt64BE(footer, 48);

            if (diskType == DiskTypeDynamic)
                LoadDynamicStructures(footer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => baseStream.CanWrite;
        public override long Length => diskSize;

        public override long Position
        {
            get => position;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                position = value;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPosition;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPosition = offset;
                    break;
                case SeekOrigin.Current:
                    newPosition = position + offset;
                    break;
                case SeekOrigin.End:
                    newPosition = diskSize + offset;
                    break;
                default:
                    throw new ArgumentException("Invalid SeekOrigin", nameof(origin));
            }

            if (newPosition < 0)
                throw new IOException("Seek before beginning of stream.");

            position = newPosition;
            return position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (position >= diskSize)
                return 0;

            int toRead = (int)Math.Min(count, diskSize - position);
            int totalRead = 0;

            while (totalRead < toRead)
            {
                int read = diskType == DiskTypeDynamic
                    ? ReadDynamic(buffer, offset + totalRead, toRead - totalRead)
                    : ReadFixed(buffer, offset + totalRead, toRead - totalRead);

                if (read == 0)
                    break;

                totalRead += read;
                position += read;
            }

            return totalRead;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (!CanWrite)
                throw new NotSupportedException("Stream is read-only.");
            if (position + count > diskSize)
                throw new IOException("Write would exceed disk boundary.");

            int totalWritten = 0;
            while (totalWritten < count)
            {
                int written = diskType == DiskTypeDynamic
                    ? WriteDynamic(buffer, offset + totalWritten, count - totalWritten)
                    : WriteFixed(buffer, offset + totalWritten, count - totalWritten);

                if (written == 0)
                    break;

                totalWritten += written;
                position += written;
            }
        }

        public override void Flush() => baseStream.Flush();

        public override void SetLength(long value) =>
            throw new NotSupportedException("VhdStream does not support SetLength.");

        protected override void Dispose(bool disposing)
        {
            if (disposing && ownsBase)
                baseStream.Dispose();

            base.Dispose(disposing);
        }

        private int ReadFixed(byte[] buffer, int offset, int count)
        {
            int bytesToRead = (int)Math.Min(count, diskSize - position);
            baseStream.Seek(position, SeekOrigin.Begin);
            return baseStream.Read(buffer, offset, bytesToRead);
        }

        private int WriteFixed(byte[] buffer, int offset, int count)
        {
            baseStream.Seek(position, SeekOrigin.Begin);
            baseStream.Write(buffer, offset, count);
            return count;
        }

        private int ReadDynamic(byte[] buffer, int offset, int count)
        {
            long sectorIndex = position / SectorSize;
            int offsetInSector = (int)(position % SectorSize);
            int blockIndex = (int)(sectorIndex / blockSectors);
            int sectorInBlock = (int)(sectorIndex % blockSectors);
            int bytesToRead = Math.Min(count, SectorSize - offsetInSector);

            uint batEntry = blockIndex < bat.Length ? bat[blockIndex] : BatEntryFree;
            if (batEntry == BatEntryFree)
            {
                Array.Clear(buffer, offset, bytesToRead);
                return bytesToRead;
            }

            long fileOffset = (long)batEntry * SectorSize
                            + (long)bitmapSectors * SectorSize
                            + (long)sectorInBlock * SectorSize
                            + offsetInSector;

            baseStream.Seek(fileOffset, SeekOrigin.Begin);
            int bytesRead = baseStream.Read(buffer, offset, bytesToRead);
            if (bytesRead < bytesToRead)
                Array.Clear(buffer, offset + bytesRead, bytesToRead - bytesRead);

            return bytesToRead;
        }

        private int WriteDynamic(byte[] buffer, int offset, int count)
        {
            long sectorIndex = position / SectorSize;
            int offsetInSector = (int)(position % SectorSize);
            int blockIndex = (int)(sectorIndex / blockSectors);
            int sectorInBlock = (int)(sectorIndex % blockSectors);
            int bytesToWrite = Math.Min(count, SectorSize - offsetInSector);

            uint batEntry = blockIndex < bat.Length ? bat[blockIndex] : BatEntryFree;
            if (batEntry == BatEntryFree)
                batEntry = AllocateDynamicBlock(blockIndex);

            long fileOffset = (long)batEntry * SectorSize
                            + (long)bitmapSectors * SectorSize
                            + (long)sectorInBlock * SectorSize
                            + offsetInSector;

            baseStream.Seek(fileOffset, SeekOrigin.Begin);
            baseStream.Write(buffer, offset, bytesToWrite);
            return bytesToWrite;
        }

        private void LoadDynamicStructures(byte[] footer)
        {
            long dynamicHeaderOffset = (long)ReadUInt64BE(footer, 16);

            byte[] dynamicHeader = new byte[DynamicHeaderSize];
            baseStream.Seek(dynamicHeaderOffset, SeekOrigin.Begin);
            ReadExact(baseStream, dynamicHeader, 0, dynamicHeader.Length);

            if (!StartsWithCookie(dynamicHeader, 0, DynamicCookie))
                throw new InvalidDataException("Dynamic VHD header cookie mismatch.");

            batOffset = (long)ReadUInt64BE(dynamicHeader, 16);
            uint maxEntries = ReadUInt32BE(dynamicHeader, 28);
            uint blockSize = ReadUInt32BE(dynamicHeader, 32);

            blockSizeBytes = (int)blockSize;
            blockSectors = (int)(blockSize / SectorSize);

            int bitmapBytes = (blockSectors + 7) / 8;
            bitmapSectors = (bitmapBytes + SectorSize - 1) / SectorSize;

            bat = new uint[maxEntries];
            byte[] batRaw = new byte[maxEntries * 4];
            baseStream.Seek(batOffset, SeekOrigin.Begin);
            ReadExact(baseStream, batRaw, 0, batRaw.Length);

            for (int i = 0; i < bat.Length; i++)
                bat[i] = ReadUInt32BE(batRaw, i * 4);
        }

        private uint AllocateDynamicBlock(int blockIndex)
        {
            if (!baseStream.CanWrite)
                throw new NotSupportedException("Dynamic VHD is read-only.");
            if (blockIndex < 0 || blockIndex >= bat.Length)
                throw new IOException("Dynamic VHD block index is out of range.");

            byte[] footer = new byte[FooterSize];
            baseStream.Seek(-FooterSize, SeekOrigin.End);
            ReadExact(baseStream, footer, 0, footer.Length);

            long newBlockOffset = baseStream.Length - FooterSize;
            if ((newBlockOffset % SectorSize) != 0)
                throw new InvalidDataException("Dynamic VHD footer is not sector aligned.");

            byte[] bitmap = new byte[bitmapSectors * SectorSize];
            int usedBitmapBytes = (blockSectors + 7) / 8;
            for (int i = 0; i < usedBitmapBytes; i++)
                bitmap[i] = 0xFF;

            byte[] zeroBlock = new byte[blockSizeBytes];

            baseStream.Seek(newBlockOffset, SeekOrigin.Begin);
            baseStream.Write(bitmap, 0, bitmap.Length);
            baseStream.Write(zeroBlock, 0, zeroBlock.Length);
            baseStream.Write(footer, 0, footer.Length);

            uint batEntry = (uint)(newBlockOffset / SectorSize);
            WriteUInt32BE(baseStream, batOffset + blockIndex * 4L, batEntry);
            bat[blockIndex] = batEntry;
            baseStream.Flush();

            return batEntry;
        }

        private static byte[] ReadFooter(Stream stream)
        {
            byte[] footer = new byte[FooterSize];
            stream.Seek(-FooterSize, SeekOrigin.End);
            ReadExact(stream, footer, 0, footer.Length);
            return footer;
        }

        private static bool StartsWithCookie(byte[] data, int offset, byte[] cookie)
        {
            if (data.Length - offset < cookie.Length)
                return false;

            for (int i = 0; i < cookie.Length; i++)
            {
                if (data[offset + i] != cookie[i])
                    return false;
            }

            return true;
        }

        private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0)
                    throw new EndOfStreamException("Unexpected end of VHD file.");
                totalRead += read;
            }
        }

        private static void WriteUInt32BE(Stream stream, long offset, uint value)
        {
            byte[] data =
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            };

            stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(data, 0, data.Length);
        }

        private static uint ReadUInt32BE(byte[] buffer, int offset) =>
            ((uint)buffer[offset] << 24) |
            ((uint)buffer[offset + 1] << 16) |
            ((uint)buffer[offset + 2] << 8) |
            buffer[offset + 3];

        private static ulong ReadUInt64BE(byte[] buffer, int offset) =>
            ((ulong)buffer[offset] << 56) |
            ((ulong)buffer[offset + 1] << 48) |
            ((ulong)buffer[offset + 2] << 40) |
            ((ulong)buffer[offset + 3] << 32) |
            ((ulong)buffer[offset + 4] << 24) |
            ((ulong)buffer[offset + 5] << 16) |
            ((ulong)buffer[offset + 6] << 8) |
            buffer[offset + 7];
    }
}
