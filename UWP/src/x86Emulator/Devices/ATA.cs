using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using x86Emulator.ATADevice;
using x86Emulator.Configuration;

namespace x86Emulator.Devices
{
    public class ATA : IDevice
    {
        private readonly int[] portsUsed =
        {
            0x1f0, 0x1f1, 0x1f2, 0x1f3, 0x1f4, 0x1f5, 0x1f6, 0x1f7,
            0x170, 0x171, 0x172, 0x173, 0x174, 0x175, 0x176, 0x177,
            0x3f6, 0x376
        };

        private readonly ATADrive[] diskDrives = new ATADrive[4];
        private readonly byte[] deviceControl = new byte[2];
        private readonly bool[] masterSelected = { true, true };

        public HardDrive[] HardDrives
        {
            get
            {
                List<HardDrive> hdds = new List<HardDrive>();
                lock (diskDrives)
                {
                    foreach (ATADrive drive in diskDrives)
                    {
                        if (drive is HardDrive hardDrive)
                            hdds.Add(hardDrive);
                    }
                }

                return hdds.ToArray();
            }
        }

        public bool HasCdRom
        {
            get
            {
                lock (diskDrives)
                {
                    return diskDrives.Any(drive => drive is CdRomDrive);
                }
            }
        }

        public void ClearHDDs()
        {
            lock (diskDrives)
            {
                for (int i = 0; i < diskDrives.Length; i++)
                {
                    if (diskDrives[i] is HardDrive)
                        diskDrives[i] = null;
                }
            }
        }

        public void ClearCDROM()
        {
            lock (diskDrives)
            {
                for (int i = 0; i < diskDrives.Length; i++)
                {
                    if (diskDrives[i] is CdRomDrive)
                        diskDrives[i] = null;
                }
            }
        }

        public void AddHDD(ATADrive newDrive)
        {
            int slot = GetFirstEmptySlot(0, 1, 2, 3);
            if (slot < 0)
            {
                Helpers.Logger("Error: no free ATA slot available for HDD");
                return;
            }

            diskDrives[slot] = newDrive;
            masterSelected[slot / 2] = true;

            try
            {
                newDrive.Reset();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ATA] AddHDD Reset exception: " + ex.Message);
            }

            Debug.WriteLine($"[ATA] AddHDD attached to slot {slot}. Type: {newDrive.GetType().Name}");
        }

        public void AddCDROM(ATADrive newDrive)
        {
            int slot = GetFirstEmptySlot(2, 3, 1, 0);
            if (slot < 0)
            {
                Helpers.Logger("Error: no free ATA slot available for CD-ROM");
                return;
            }

            diskDrives[slot] = newDrive;
            masterSelected[slot / 2] = true;

            try
            {
                newDrive.Reset();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ATA] AddCDROM Reset exception: " + ex.Message);
            }

            Debug.WriteLine($"[ATA] AddCDROM attached to slot {slot}. Type: {newDrive.GetType().Name}");
        }

        public void Reset(int controller)
        {
            int baseIndex = controller * 2;
            bool foundDrive = false;

            for (int i = 0; i < 2; i++)
            {
                ATADrive drive = diskDrives[baseIndex + i];
                if (drive != null)
                {
                    drive.Reset();
                    foundDrive = true;
                }
            }

            if (!foundDrive)
                Helpers.Logger("Error: ATA 'Reset' called with no drives attached");
        }

        public void RunCommand(int controller, byte command)
        {
            ATADrive drive = GetSelectedDrive(controller);
            Debug.WriteLine($"[ATA] RunCommand(controller={controller}, command=0x{command:X2}) selectedDrive={drive?.GetType().Name ?? "<none>"}");

            if (drive == null)
            {
                Helpers.Logger("Error: ATA 'RunCommand' called with no selected drive");
                return;
            }

            TrackIo(drive);
            drive.RunCommand(command);
        }

        public int[] PortsUsed => portsUsed;

        public uint Read(ushort addr, int size)
        {
            int controller = GetControllerIndex(addr);
            ATADrive drive = GetSelectedDrive(controller);

            // Alternate Status register (0x3F6 = primary, 0x376 = secondary).
            // Must be checked before the command-block switch because both share
            // the same low nibble (0x06) as the Drive/Head register (0x1F6/0x176).
            if (addr == 0x3f6 || addr == 0x376)
            {
                TrackIo(drive);
                return drive != null ? (uint)(byte)drive.Status : 0xFFu;
            }

            switch (addr & 0x0f)
            {
                case 0x0:
                    TrackIo(drive);
                    return drive?.SectorBuffer ?? 0;
                case 0x1:
                    return (byte)(drive?.Error ?? 0);
                case 0x2:
                    TrackIo(drive);
                    return drive?.SectorCount ?? 0;
                case 0x3:
                    TrackIo(drive);
                    return drive?.SectorNumber ?? 0;
                case 0x4:
                    TrackIo(drive);
                    return drive?.CylinderLow ?? 0;
                case 0x5:
                    TrackIo(drive);
                    return drive?.CylinderHigh ?? 0;
                case 0x6:
                    return drive?.DriveHead ?? 0;
                case 0x7:
                    TrackIo(drive);
                    return drive != null ? (uint)(byte)drive.Status : 0xFFu;
                default:
                    break;
            }

            return 0;
        }

        public void Write(ushort addr, uint value, int size)
        {
            int controller = GetControllerIndex(addr);
            ATADrive drive = GetSelectedDrive(controller);

            // Device Control register (0x3F6 = primary, 0x376 = secondary).
            // Must be checked before the command-block switch because both share
            // the same low nibble (0x06) as the Drive/Head register (0x1F6/0x176).
            if (addr == 0x3f6 || addr == 0x376)
            {
                HandleDeviceControlWrite(controller, (byte)value);
                return;
            }

            switch (addr & 0x0f)
            {
                case 0x0:
                    TrackIo(drive);
                    if (drive != null)
                        drive.SectorBuffer = (ushort)value;
                    return;
                case 0x1:
                    return;
                case 0x2:
                    TrackIo(drive);
                    if (drive != null)
                        drive.SectorCount = (byte)value;
                    return;
                case 0x3:
                    TrackIo(drive);
                    if (drive != null)
                        drive.SectorNumber = (byte)value;
                    return;
                case 0x4:
                    TrackIo(drive);
                    if (drive != null)
                        drive.CylinderLow = (byte)value;
                    return;
                case 0x5:
                    TrackIo(drive);
                    if (drive != null)
                        drive.CylinderHigh = (byte)value;
                    return;
                case 0x6:
                    masterSelected[controller] = (value & 0x10) == 0;
                    drive = GetSelectedDrive(controller);
                    TrackIo(drive);
                    if (drive != null)
                        drive.DriveHead = (byte)value;
                    return;
                case 0x7:
                    Debug.WriteLine($"[ATA] Port write 0x{addr:X3} value=0x{value:X2}");
                    RunCommand(controller, (byte)value);
                    return;
                default:
                    break;
            }
        }

        private static int GetControllerIndex(ushort addr)
        {
            if ((addr >= 0x170 && addr <= 0x177) || addr == 0x376)
                return 1;

            return 0;
        }

        private int GetSelectedDriveIndex(int controller)
        {
            int baseIndex = controller * 2;
            return baseIndex + (masterSelected[controller] ? 0 : 1);
        }

        private ATADrive GetSelectedDrive(int controller)
        {
            int index = GetSelectedDriveIndex(controller);
            if (index < 0 || index >= diskDrives.Length)
                return null;

            return diskDrives[index];
        }

        private int GetFirstEmptySlot(params int[] preferredOrder)
        {
            foreach (int slot in preferredOrder)
            {
                if (slot >= 0 && slot < diskDrives.Length && diskDrives[slot] == null)
                    return slot;
            }

            return -1;
        }

        private void TrackIo(ATADrive drive)
        {
            if (drive is HardDrive)
                SystemConfig.IO_HDDCall();
            else if (drive is CdRomDrive)
                SystemConfig.IO_CDCall();
        }

        private void HandleDeviceControlWrite(int controller, byte value)
        {
            if ((value & 0x4) == 0x4)
            {
                if ((deviceControl[controller] & 0x4) != 0x4)
                    Reset(controller);
            }
            else if ((deviceControl[controller] & 0x4) == 0x4)
            {
                int baseIndex = controller * 2;
                for (int i = 0; i < 2; i++)
                {
                    ATADrive drive = diskDrives[baseIndex + i];
                    if (drive != null)
                    {
                        drive.Status &= ~DeviceStatus.Busy;
                        drive.Status |= DeviceStatus.Ready | DeviceStatus.SeekComplete;
                        // ATA spec: after SRST is deasserted the master must report
                        // Diagnostic Passed (0x01) in the Error register.
                        drive.Error = DeviceError.DiagnosticPassed;
                    }
                }
            }

            deviceControl[controller] = value;
        }
    }
}
