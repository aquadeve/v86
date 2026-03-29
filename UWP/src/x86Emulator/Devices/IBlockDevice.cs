namespace x86Emulator
{
    /// <summary>
    /// Minimal interface that ATA controller can use to read/write sectors.
    /// </summary>
    public interface IBlockDevice
    {
        /// <summary>Sector size in bytes (e.g. 512 for HDD, 2048 for CD).</summary>
        int SectorSize { get; }

        /// <summary>Read exactly sectorCount sectors starting at lba (0-based).</summary>
        byte[] ReadSectors(ulong lba, int sectorCount);

        /// <summary>Write sectors (HDD only). Throws or no-op for read-only devices.</summary>
        void WriteSectors(ulong lba, int sectorCount, byte[] data);
    }
}
