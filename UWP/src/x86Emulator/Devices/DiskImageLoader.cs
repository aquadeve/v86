using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace x86Emulator.Devices
{
    /// <summary>
    /// Identifies a disk image format based on file extension and content.
    /// </summary>
    public enum DiskImageType
    {
        /// <summary>Raw disk image (.img or unrecognized extension).</summary>
        Raw,
        /// <summary>Microsoft VHD (fixed or dynamic).</summary>
        Vhd,
        /// <summary>ISO 9660 CD-ROM / DVD image.</summary>
        Iso,
    }

    /// <summary>
    /// Utility class that opens VHD, raw-IMG and ISO files and returns
    /// a seekable <see cref="Stream"/> positioned at byte 0 of the
    /// virtual disk.  VHD containers are transparently unwrapped via
    /// <see cref="VhdStream"/>.
    /// </summary>
    public static class DiskImageLoader
    {
        private const int DefaultBufferSize = 4096;
        /// <summary>
        /// Determines the image type from the file name extension.
        /// </summary>
        public static DiskImageType DetectType(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return DiskImageType.Raw;

            if (fileName.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
                return DiskImageType.Vhd;

            if (fileName.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                return DiskImageType.Iso;

            // .img and everything else is treated as a raw disk image
            return DiskImageType.Raw;
        }

        /// <summary>
        /// Opens a disk image file from a file-system path and returns
        /// a seekable stream over its raw disk content.
        /// <para>
        /// For <c>.vhd</c> files the returned stream is a
        /// <see cref="VhdStream"/> that transparently maps virtual
        /// sectors to the underlying VHD container.
        /// </para>
        /// </summary>
        public static Stream OpenFromPath(string path, bool readOnly = false)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Disk image not found.", path);

            FileAccess access = readOnly ? FileAccess.Read : FileAccess.ReadWrite;
            FileShare share = readOnly ? FileShare.ReadWrite : FileShare.Read;

            Stream raw;
            try
            {
                raw = new FileStream(path, FileMode.Open, access, share,
                    bufferSize: DefaultBufferSize, FileOptions.RandomAccess);
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to read-only if the image or folder is write-protected
                Debug.WriteLine($"[DiskImageLoader] Read-write denied, falling back to read-only: {path}");
                raw = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, bufferSize: DefaultBufferSize, FileOptions.RandomAccess);
            }

            return WrapIfNeeded(raw, path);
        }

        /// <summary>
        /// Opens a disk image from a UWP <see cref="StorageFile"/> and returns
        /// a seekable stream over its raw disk content.
        /// </summary>
        public static async Task<Stream> OpenFromStorageFileAsync(StorageFile file, bool readOnly = false)
        {
            if (file == null)
                throw new ArgumentNullException(nameof(file));

            Stream raw;
            if (readOnly)
            {
                raw = await file.OpenStreamForReadAsync();
            }
            else
            {
                try
                {
                    var rStream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                    raw = rStream.AsStream();
                }
                catch
                {
                    Debug.WriteLine($"[DiskImageLoader] Read-write denied, falling back to read-only: {file.Path}");
                    raw = await file.OpenStreamForReadAsync();
                }
            }

            return WrapIfNeeded(raw, file.Name);
        }

        /// <summary>
        /// Wraps a raw stream in a <see cref="VhdStream"/> when the file
        /// name indicates a VHD container.  All other formats pass through
        /// unchanged.
        /// </summary>
        private static Stream WrapIfNeeded(Stream raw, string fileName)
        {
            DiskImageType type = DetectType(fileName);
            if (type == DiskImageType.Vhd)
            {
                Debug.WriteLine($"[DiskImageLoader] Detected VHD format: {fileName}");
                return VhdStream.OpenOrPassThrough(raw);
            }

            Debug.WriteLine($"[DiskImageLoader] Opened {type} image: {fileName}");
            return raw;
        }
    }
}
