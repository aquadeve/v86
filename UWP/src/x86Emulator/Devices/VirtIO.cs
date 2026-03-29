using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace x86Emulator.Devices
{
    // ══════════════════════════════════════════════════════════════════════════
    // VirtIO shared constants
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Feature bits shared by all VirtIO devices.</summary>
    internal static class VirtIOConst
    {
        public const uint VIRTIO_F_VERSION_1  = 1u << 32; // using bit index 32 of feature word 1
        public const uint VIRTIO_F_VERSION_1_BIT = 1;     // bit 0 of features_high word (word 1)

        // Device status register bits (VIRTIO spec §2.1)
        public const byte DEVICE_STATUS_ACKNOWLEDGE = 0x01;
        public const byte DEVICE_STATUS_DRIVER      = 0x02;
        public const byte DEVICE_STATUS_DRIVER_OK   = 0x04;
        public const byte DEVICE_STATUS_FEATURES_OK = 0x08;
        public const byte DEVICE_STATUS_RESET        = 0x00;
        public const byte DEVICE_STATUS_FAILED       = 0x80;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // VirtQueue
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Represents a single VirtIO virtqueue.
    ///
    /// The queue lives in guest physical memory; this object tracks the GPA base
    /// addresses for the descriptor table, available ring, and used ring.
    /// When the driver posts a buffer chain, <see cref="HasRequest"/> returns
    /// true and <see cref="PopRequest"/> returns the chain's bytes.
    ///
    /// Reference: VIRTIO 1.1 spec §2.6
    /// </summary>
    internal class VirtQueue
    {
        // Descriptor table layout (16 bytes per entry)
        private const int DESC_ADDR  = 0;  // 8-byte guest physical address
        private const int DESC_LEN   = 8;  // 4-byte length
        private const int DESC_FLAGS = 12; // 2-byte flags
        private const int DESC_NEXT  = 14; // 2-byte next index
        private const int DESC_SIZE  = 16;

        private const ushort VRING_DESC_F_NEXT     = 0x1;
        private const ushort VRING_DESC_F_WRITE    = 0x2; // device-writable (write-only from guest POV)
        private const ushort VRING_DESC_F_INDIRECT = 0x4;

        // Available ring layout
        // [flags:2][idx:2][ring[size]:2 …][used_event:2]
        private const int AVAIL_FLAGS = 0;
        private const int AVAIL_IDX   = 2;
        private const int AVAIL_RING  = 4;

        // Used ring layout
        // [flags:2][idx:2][ring[size]:8 …][avail_event:2]
        private const int USED_FLAGS = 0;
        private const int USED_IDX   = 2;
        private const int USED_RING  = 4;
        private const int USED_ELEM_SIZE = 8; // id(4) + len(4)

        public readonly int Size;   // Maximum number of descriptors (power-of-two)
        public readonly int NotifyOffset;

        private uint descTableGpa;
        private uint availRingGpa;
        private uint usedRingGpa;
        private ushort lastAvailIdx;
        private bool ready;

        public VirtQueue(int size, int notifyOffset)
        {
            Size         = size;
            NotifyOffset = notifyOffset;
        }

        public void SetAddresses(uint descTable, uint availRing, uint usedRing)
        {
            descTableGpa = descTable;
            availRingGpa = availRing;
            usedRingGpa  = usedRing;
            ready        = descTable != 0;
        }

        public void Reset()
        {
            descTableGpa = availRingGpa = usedRingGpa = 0;
            lastAvailIdx = 0;
            ready = false;
        }

        public bool HasRequest()
        {
            if (!ready) return false;
            ushort availIdx = ReadU16(availRingGpa + AVAIL_IDX);
            return availIdx != lastAvailIdx;
        }

        /// <summary>
        /// Pops the next request from the available ring and returns all the
        /// readable descriptor data concatenated, plus a token used to
        /// complete the request via <see cref="PushUsed"/>.
        /// </summary>
        public bool TryPopRequest(out byte[] readable, out uint token, out int writableOffset)
        {
            readable      = null;
            token         = 0;
            writableOffset = 0;

            if (!HasRequest()) return false;

            ushort availIdx = ReadU16(availRingGpa + AVAIL_IDX);
            ushort headIdx  = ReadU16(availRingGpa + AVAIL_RING + (lastAvailIdx % Size) * 2);
            lastAvailIdx++;

            // Walk descriptor chain, collecting readable bytes
            var readBuf  = new System.IO.MemoryStream();
            int writeStart = -1;
            int descIdx  = headIdx;
            int limit    = Size;

            while (limit-- > 0)
            {
                uint  descBase = descTableGpa + (uint)(descIdx * DESC_SIZE);
                uint  addr     = ReadU32(descBase + DESC_ADDR);
                uint  len      = ReadU32(descBase + DESC_LEN);
                ushort flags   = ReadU16(descBase + DESC_FLAGS);
                ushort next    = ReadU16(descBase + DESC_NEXT);

                bool isWrite = (flags & VRING_DESC_F_WRITE) != 0;
                if (!isWrite)
                {
                    // Readable by device
                    if (writeStart >= 0)
                        Debug.WriteLine("[VirtQueue] Read descriptor after write descriptor (unusual)");

                    byte[] chunk = new byte[len];
                    Memory.BlockRead(addr, chunk, (int)len);
                    readBuf.Write(chunk, 0, chunk.Length);
                }
                else
                {
                    if (writeStart < 0)
                        writeStart = (int)readBuf.Length;
                }

                if ((flags & VRING_DESC_F_NEXT) == 0) break;
                descIdx = next;
            }

            readable      = readBuf.ToArray();
            token         = headIdx;
            writableOffset = writeStart;
            return true;
        }

        /// <summary>
        /// Writes the response bytes into the device-writable region and
        /// appends an entry to the used ring.
        /// </summary>
        public void PushUsed(uint token, byte[] response)
        {
            if (!ready) return;

            // Walk descriptor chain from token to find the writable buffer
            int descIdx = (int)token;
            int limit   = Size;
            uint written = 0;
            int respOffset = 0;

            while (limit-- > 0)
            {
                uint  descBase = descTableGpa + (uint)(descIdx * DESC_SIZE);
                uint  addr     = ReadU32(descBase + DESC_ADDR);
                uint  len      = ReadU32(descBase + DESC_LEN);
                ushort flags   = ReadU16(descBase + DESC_FLAGS);
                ushort next    = ReadU16(descBase + DESC_NEXT);

                if ((flags & VRING_DESC_F_WRITE) != 0 && response != null && respOffset < response.Length)
                {
                    int toWrite = Math.Min((int)len, response.Length - respOffset);
                    var chunk = new byte[toWrite];
                    Buffer.BlockCopy(response, respOffset, chunk, 0, toWrite);
                    Memory.BlockWrite(addr, chunk, toWrite);
                    respOffset += toWrite;
                    written    += (uint)toWrite;
                }

                if ((flags & VRING_DESC_F_NEXT) == 0) break;
                descIdx = next;
            }

            // Write used ring entry
            ushort usedIdx = ReadU16(usedRingGpa + USED_IDX);
            uint elemBase  = usedRingGpa + USED_RING + (uint)((usedIdx % Size) * USED_ELEM_SIZE);
            WriteU32(elemBase,     token);
            WriteU32(elemBase + 4, written);

            // Advance used index (memory barrier implied by write)
            WriteU16(usedRingGpa + USED_IDX, (ushort)(usedIdx + 1));
        }

        // ── Memory helpers ─────────────────────────────────────────────────────

        private static ushort ReadU16(uint addr) =>
            (ushort)(Memory.Read(addr, 8) | (Memory.Read(addr + 1, 8) << 8));

        private static uint ReadU32(uint addr) =>
            Memory.Read(addr, 8) |
            (Memory.Read(addr + 1, 8) << 8) |
            (Memory.Read(addr + 2, 8) << 16) |
            (Memory.Read(addr + 3, 8) << 24);

        private static void WriteU16(uint addr, ushort v)
        {
            Memory.Write(addr,     (byte)(v & 0xFF), 8);
            Memory.Write(addr + 1, (byte)(v >> 8),   8);
        }

        private static void WriteU32(uint addr, uint v)
        {
            Memory.Write(addr,     (byte)(v & 0xFF),         8);
            Memory.Write(addr + 1, (byte)((v >> 8) & 0xFF),  8);
            Memory.Write(addr + 2, (byte)((v >> 16) & 0xFF), 8);
            Memory.Write(addr + 3, (byte)((v >> 24) & 0xFF), 8);
        }
    }
}
