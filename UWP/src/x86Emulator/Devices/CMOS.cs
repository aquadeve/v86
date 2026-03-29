using System;
using x86Emulator.Configuration;
using x86Emulator.ATADevice;
using System.Collections.Generic;

namespace x86Emulator.Devices
{
    public class CMOS : IDevice
    {
        private readonly int[] portsUsed = {0x70, 0x71};
        private byte currentReg;
        private byte statusA;
        private byte statusB;
        private byte statusC;
        private byte statusD;
        private ATA ataDevice;

        public int[] PortsUsed
        {
            get { return portsUsed; }
        }

        public CMOS(ATA ata)
        {
            statusA = 0x26; /* default 32.768 divider and default rate selection */
            statusB = 0x02;  /* no DST, 12 hour clock, BCD, all flags cleared */
            statusC = 0x00;
            statusD = 0x80;

            ataDevice = ata;
        }

        public uint Read(ushort addr, int size)
        {
            DateTime currTime = DateTime.Now;
            ushort ret = 0;

            switch (addr)
            {
                case 0x70: /* Write only */
                    ret = 0xff;
                    break;
                case 0x71:
                    switch (currentReg)
                    {
                        case 0x00:
                            return Util.ToBCD(currTime.Second);
                        case 0x02:
                            return Util.ToBCD(currTime.Minute);
                        case 0x04:
                            return Util.ToBCD(currTime.Hour);
                        case 0x06:
                            return Util.ToBCD((int)currTime.DayOfWeek);
                        case 0x07:
                            return Util.ToBCD(currTime.Day);
                        case 0x08:
                            return Util.ToBCD(currTime.Month);
                        case 0x09:
                            return Util.ToBCD(currTime.Year % 100);
                        case 0x0a:
                            return statusA; 
                        case 0x0b:
                            return statusB;
                        case 0x0c:
                            return statusC;
                        case 0x0d:
                            return statusD;
                        case 0x0f:
                            return 0x00;
                        case 0x10:
                            return 0x40;  /* 1.44M floppy drive */
                        case 0x12:
                            switch (ataDevice.HardDrives.Length)
                            {
                                case 1:
                                    return 0xf0;
                                case 2:
                                    return 0xff;
                                default:
                                    return 0;
                            }
                        case 0x13: /* typematic rate */
                            return 0;
                        case 0x14:
                            return 0x05;    /* Machine config byte */
                        case 0x15:
                            return 0x71;    /* Low byte of 640K memory available */
                        case 0x16:
                            return 0x02;    /* High byte of above */
                        case 0x17:          /* Low byte of memory 1M -> 65M */
                            if (SystemConfig.MemorySize > 64)
                                return 0xff;
                            else
                                return (byte)(((SystemConfig.MemorySize - 1) * 1024));
                        case 0x18:          /* High byte of above */
                            if (SystemConfig.MemorySize > 64)
                                return 0xff;
                            else
                                return (byte)(((SystemConfig.MemorySize - 1) * 1024) >> 8);
                        case 0x19:
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return 47;
                        case 0x1a:
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return 47;
                        case 0x1b:  /* HDD1 - Cylinders Low */
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return (byte)ataDevice.HardDrives[0].Cylinders;
                        case 0x1c:  /* HDD1 - Cylinders High */
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return (byte)(ataDevice.HardDrives[0].Cylinders >> 8);
                        case 0x1d:  /* HDD1 - Heads */
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return ataDevice.HardDrives[0].Heads;
                        case 0x1e:  /* Drive 0 (C:) Write Precomp - low */
                            return 0xFF;
                        case 0x1f:  /* Drive 0 (C:) Write Precomp - high */
                            return 0xFF;
                        case 0x20:  /* Drive 0 (C:) Drive control byte */
                            // Bit 3 = more than 8 heads, bit 6/7 per v86 reference
                            return 0xC8;
                        case 0x21:  /* Drive 0 (C:) Landing zone - low */
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return (byte)ataDevice.HardDrives[0].Cylinders;
                        case 0x22:  /* Drive 0 (C:) Landing zone - high */
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return (byte)(ataDevice.HardDrives[0].Cylinders >> 8);
                        case 0x23:  /* HDD1 - Sectors */
                            if (ataDevice.HardDrives.Length == 0)
                                return 0;
                            return ataDevice.HardDrives[0].Sectors;

                        // ── Second hard drive geometry (0x24-0x2C) ──
                        // Bochs BIOS reads these when CMOS 0x1A = 47 (user-defined type)
                        case 0x24:  /* HDD2 - Cylinders Low */
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return (byte)ataDevice.HardDrives[1].Cylinders;
                        case 0x25:  /* HDD2 - Cylinders High */
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return (byte)(ataDevice.HardDrives[1].Cylinders >> 8);
                        case 0x26:  /* HDD2 - Heads */
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return ataDevice.HardDrives[1].Heads;
                        case 0x27:  /* HDD2 Write Precomp - low */
                            return 0xFF;
                        case 0x28:  /* HDD2 Write Precomp - high */
                            return 0xFF;
                        case 0x29:  /* HDD2 Drive control byte */
                            return 0xC8;
                        case 0x2a:  /* HDD2 Landing zone - low */
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return (byte)ataDevice.HardDrives[1].Cylinders;
                        case 0x2b:  /* HDD2 Landing zone - high */
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return (byte)(ataDevice.HardDrives[1].Cylinders >> 8);
                        case 0x2c:  /* HDD2 - Sectors */
                            if (ataDevice.HardDrives.Length < 2)
                                return 0;
                            return ataDevice.HardDrives[1].Sectors;
                        case 0x30:
                            if (SystemConfig.MemorySize > 64)
                                return 0xff;
                            else
                                return (byte)(((SystemConfig.MemorySize - 1) * 1024));
                        case 0x31:          /* High byte of above */
                            if (SystemConfig.MemorySize > 64)
                                return 0xff;
                            else
                                return (byte)(((SystemConfig.MemorySize - 1) * 1024) >> 8);
                        case 0x32:
                            return Util.ToBCD(currTime.Year / 100);
                        case 0x34:          /* Low byte of memory 16MB to 4GB */
                            return (byte)(((SystemConfig.MemorySize - 16) * 1024 * 1024) >> 16);
                        case 0x35:          /* High byte */
                            return (byte)((((SystemConfig.MemorySize - 16) * 1024 * 1024) >> 16) >> 8);
                        case 0x3d:
                            // Boot sequence per v86 / SeaBIOS convention:
                            //   Low nibble  = highest priority (1st boot device)
                            //   High nibble = medium priority  (2nd boot device)
                            //   Device codes: 0=none, 1=floppy, 2=HDD, 3=CDROM
                            //
                            // BOOT_ORDER_CD_FIRST  = 0x123 → 0x3d = 0x23
                            // BOOT_ORDER_HD_FIRST  = 0x312 → 0x3d = 0x12
                            // BOOT_ORDER_FD_FIRST  = 0x321 → 0x3d = 0x21
                            if (ataDevice.HasCdRom)
                                return 0x23;  /* CD-ROM first, HDD second */
                            if (ataDevice.HardDrives.Length > 0)
                                return 0x12;  /* HDD first, floppy second */
                            return 0x21;      /* Floppy first, HDD second */
                        case 0x38:
                            // BOOTFLAG1 per v86 / SeaBIOS convention:
                            //   Bit 0      = 1: disable floppy boot-sector signature check
                            //   High nibble = lowest priority (3rd boot device)
                            //
                            // Bit 0 MUST be set so the BIOS falls through to the
                            // next device when the floppy is empty or has no boot sig.
                            if (ataDevice.HasCdRom)
                                return 0x11;  /* skip floppy check, floppy 3rd */
                            if (ataDevice.HardDrives.Length > 0)
                                return 0x31;  /* skip floppy check, CD-ROM 3rd */
                            return 0x31;      /* skip floppy check, CD-ROM 3rd */
                        case 0x39:
                            // Disk translation flags – one nibble per drive (LBA translation)
                            // Bit 0 = drive 0, bit 4 = drive 1
                            if (ataDevice.HardDrives.Length >= 2)
                                return 0x11;
                            if (ataDevice.HardDrives.Length == 1)
                                return 0x01;
                            return 0x00;
                        case 0x5b:
                            return 0x00;
                        case 0x5c:
                            return 0x00;
                        case 0x5d:
                            return 0x00;
                        case 0x3f:
                            return 0x00;
                        default:
                            // Return 0 for unhandled CMOS registers to prevent
                            // the BIOS from hanging on unexpected debug breaks.
                            return 0x00;
                    }
                    break;
            }
            return ret;
        }

        public void Write(ushort addr, uint value, int size)
        {
            var tmp = (ushort)(value & 0x7f);

            switch (addr)
            {
                case 0x70:         
                    currentReg = (byte)tmp;
                    break;
                case 0x71:
                    switch (currentReg)
                    {
                        case 0x0a:
                            statusA = (byte)value;
                            break;
                        case 0x0b:
                            statusB = (byte)value;
                            break;
                        case 0x0f:
                            break;
                        default:
                            //System.Diagnostics.Debugger.Break();
                            break;
                    }
                    break;
            }
        }
    }
}
