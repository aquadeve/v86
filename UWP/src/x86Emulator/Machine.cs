using System;

using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using x86Emulator.Devices;

using x86Emulator.GUI;
using x86Emulator.GUI.WIN2D;
using System.Diagnostics;
using Windows.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Storage;
using Windows.ApplicationModel;
using System.Threading.Tasks;
using x86Emulator.Configuration;
using x86Emulator.ATADevice;



namespace x86Emulator
{
    public delegate void InteruptHandler();

    class IOPort
    {
        public ushort port;
        public IOEntry entry;
    }
    public class Machine
    {
        private static string BIOSImagePath = "BIOS-bochs-latest";
        private static string VGABIOSImagePath = "VGABIOS-lgpl-latest";

        private static StorageFile BIOSImageFile;
        private static StorageFile VGABIOSImageFile;

        public static IntPtr BIOSImagePathPtr;
        public static IntPtr VGABIOSImagePathPtr;

        private StorageFolder InstallLocation = Package.Current.InstalledLocation;

        private readonly Dictionary<uint, uint> breakpoints = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, uint> tempBreakpoints = new Dictionary<uint, uint>();
        public static UI gui;
        private IDevice[] devices;
        private readonly PIC8259 picDevice; // interrupt controller
        private readonly VGA vgaDevice;
        private readonly DMAController dmaController; // Direct Memory Access
        private readonly PCIBus pciBus;       // PCI configuration space controller
        private readonly UART16550 uart0;     // COM1 serial port
        private readonly UART16550 uart1;     // COM2 serial port
        private readonly SB16 sb16Device;     // Sound Blaster 16 audio
        private readonly NE2000 ne2kDevice;   // NE2000 Ethernet
        private VirtioGPU virtioGPU;          // VirtIO GPU (D3D11-backed)
        private readonly VirtioConsole virtioConsole; // VirtIO serial console
        private readonly VirtioNet virtioNet;         // VirtIO network adapter
        private readonly ATA ataDevice;

        private List<IOPort> ioPorts;
        private KeyboardDevice keyboard;
        public MouseDevice mouse;
        private bool isStepping;

        public Floppy FloppyDrives { get; private set; }
        public CPU.CPU CPU { get; private set; }

        public bool Running;
        private IOEntry GetIOEntry(ushort port)
        {
            IOEntry entry = new IOEntry();
            foreach (var ioPort in ioPorts)
            {
                if (ioPort.port == port)
                {
                    entry = ioPort.entry;
                    break;
                }
            }
            return entry;
        }
        public Machine(CanvasAnimatedControl uiForm)
        {

            BIOSImageFile = SystemConfig.MachineResources.bios.BOCHS.resourcesFile;
            VGABIOSImageFile = SystemConfig.MachineResources.bios.VGA.resourcesFile;

            systemIsLoading = true;
            picDevice = new PIC8259();
            vgaDevice = new VGA();
            FloppyDrives = new Floppy();
            dmaController = new DMAController();
            keyboard = new KeyboardDevice();
            mouse = new MouseDevice();
            ataDevice = new ATA();
            pciBus = new PCIBus(picDevice);
            uart0  = new UART16550(0x3F8); // COM1 IRQ4
            uart1  = new UART16550(0x2F8); // COM2 IRQ3
            sb16Device = new SB16();
            ne2kDevice = new NE2000();
            virtioConsole = new VirtioConsole();
            virtioNet     = new VirtioNet();
            // VirtioGPU is created later in CompleteLoading after GpuPassthrough is available
            // Attach HDD and CD-ROM to ATA controller
            try
            {

                // Load HDD image from Resources (or wherever it's defined)
                if (!string.IsNullOrEmpty(Resources.HddImagePath) && File.Exists(Resources.HddImagePath))
                {
                    ataDevice.AddHDD(new HardDrive(Resources.HddImagePath));
                    // 0 = Primary Master
                    Debug.WriteLine($"HDD attached: {Resources.HddImagePath}");
                }

                // Load CD-ROM image from Resources
                if (!string.IsNullOrEmpty(Resources.CdRomImagePath) && File.Exists(Resources.CdRomImagePath))
                {
                    var cdrom = new CdRomDrive(Resources.CdRomImagePath);
                    ataDevice.AddCDROM(cdrom);
                    Debug.WriteLine($"[CDROM] Attached: {Resources.CdRomImagePath}");
                }
                else
                {
                    Debug.WriteLine("[CDROM] No ISO image found or invalid path.");
                }


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error attaching ATA devices: {ex.Message}");
            }

            PrepareMachine(uiForm);
        }

        public async Task PrepareResources()
        {
            await SyncHDDs();
            await SyncCDROMs();
            await SyncFloppies();
        }

        public async Task SyncHDDs()
        {
            ataDevice.ClearHDDs();
            //Append HDDs
            foreach (var hdd in SystemConfig.MachineResources.hdds.HDD)
            {
                if (hdd.isValid())
                {
                    ATADrive newHDD = new HardDrive();
                    await newHDD.LoadImage(hdd.resourcesFile);
                    ataDevice.AddHDD(newHDD);
                }
            }
        }

        public async Task SyncCDROMs()
        {
            ataDevice.ClearCDROM();

            // Grab the CD-ROM resource from your config
            var cdromResource = SystemConfig.MachineResources.cdrom.CDROM[0].resourcesFile;

            if (cdromResource != null)
            {
                var cdrom = new CdRomDrive();
                await cdrom.LoadImage(cdromResource);
                ataDevice.AddCDROM(cdrom);

                System.Diagnostics.Debug.WriteLine($"[CDROM] ISO loaded: {cdromResource.Path}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[CDROM] No ISO selected or configured.");
            }
        }

        public async Task SyncFloppies()
        {
            //Append Floppies
            if (SystemConfig.MachineResources.floppies.Floppy[0].isValid())
            {
                await FloppyDrives.MountImage(SystemConfig.MachineResources.floppies.Floppy[0].resourcesFile, FloppyType.PrimaryFloppy);
            }
            else
            {
                FloppyDrives.UnMountImage(FloppyType.PrimaryFloppy);
            }

            if (SystemConfig.MachineResources.floppies.Floppy.Count() > 1 && SystemConfig.MachineResources.floppies.Floppy[1].isValid())
            {
                await FloppyDrives.MountImage(SystemConfig.MachineResources.floppies.Floppy[1].resourcesFile, FloppyType.SecondaryFloppy);
            }
            else
            {
                FloppyDrives.UnMountImage(FloppyType.SecondaryFloppy);
            }
        }

        public async void PrepareMachine(CanvasAnimatedControl uiForm)
        {
            await PrepareResources();

            CompleteLoading(uiForm);

        }
        public bool systemIsLoading = false;
        private async void CompleteLoading(CanvasAnimatedControl uiForm)
        {
            gui = new WIN2D(uiForm, vgaDevice);

            gui.KeyDown += new EventHandler<UIntEventArgs>(GUIKeyDown);
            gui.KeyUp += new EventHandler<UIntEventArgs>(GUIKeyUp);

            RunGUICycle();
            gui.Init();

            // Create VirtIO GPU once the GPU passthrough (D3D11) is available
            virtioGPU = new VirtioGPU(gui.GpuPassthrough);
            virtioGPU.FrameUpdated += (s, e) => gui.SetVirtioGPUFrame(virtioGPU.CurrentFrame);

            devices = new IDevice[]
            {
                FloppyDrives, new CMOS(ataDevice), new Misc(), new PIT8253(),
                picDevice, keyboard, mouse, dmaController, vgaDevice, ataDevice,
                pciBus, uart0, uart1, sb16Device, ne2kDevice, virtioGPU,
                virtioConsole, virtioNet
            };

            CPU = new CPU.CPU();

            picDevice.Interrupt += PicDeviceInterrupt;

            await SetupSystem();

            CPU.IORead += CPUIORead;
            CPU.IOWrite += CPUIOWrite;
            systemIsLoading = false;
        }

        public void ResetScreen()
        {
            gui?.ResetScreen();
        }

        bool MachineShutdown = false;
        private async void RunGUICycle()
        {
            MachineShutdown = false;
            await Task.Run(async () =>
            {
                while (!MachineShutdown)
                {
                    if (Running)
                    {
                        await gui.Cycle();
                    }
                    else
                    {
                        await Task.Delay(500);
                    }
                }
            });
        }


        void GUIKeyUp(object sender, UIntEventArgs e)
        {
            keyboard.KeyUp(e.Number);
        }

        void GUIKeyDown(object sender, UIntEventArgs e)
        {
            keyboard.KeyPress(e.Number);
        }

        void PicDeviceInterrupt(object sender, InterruptEventArgs e)
        {
            if (CPU.IF)
            {
                uint currentAddr = (uint)(CPU.GetSelectorBase(x86Disasm.SegmentRegister.CS) + CPU.EIP);
                picDevice.AckInterrupt(e.IRQ);
                CPU.ExecuteInterrupt(e.Vector);
                if (isStepping)
                {
                    tempBreakpoints.Add(currentAddr, currentAddr);
                    Running = true;
                }
            }
        }

        void DMARaised(object sender, ByteArrayEventArgs e)
        {
            var device = sender as INeedsDMA;

            if (device == null)
                return;

            // SB16 DMA is a READ transfer: audio data flows from guest memory
            // to the device (then to the audio output), not the other way around.
            if (device is SB16 sb16)
            {
                var audioData = dmaController.ReadTransfer(sb16.DMAChannel, e.ByteArray.Length);
                sb16.OnDmaData(audioData);
                return;
            }

            dmaController.DoTransfer(device.DMAChannel, e.ByteArray);
        }

        void IRQRaised(object sender, EventArgs e)
        {
            var device = sender as INeedsIRQ;

            if (device == null)
                return;
            picDevice.RequestInterrupt((byte)device.IRQNumber);
        }

        private void SetupIOEntry(ushort port, ReadCallback read, WriteCallback write)
        {
            var entry = new IOEntry { Read = read, Write = write };
            ioPorts.Add(new IOPort()
            {
                port = port,
                entry = entry
            });
        }

        private uint CPUIORead(ushort addr, int size)
        {
            IOEntry entry = GetIOEntry(addr);
            var ret = (ushort)0xffff;
            if(entry.Read != null)
            {
                ret = (ushort)entry.Read(addr, size);
            }
            if (CPU.Logging)
                Helpers.Logger(String.Format("IO Read Port {0:X}, Value {1:X}", addr, ret));

            return ret;
        }

        private void CPUIOWrite(ushort addr, uint value, int size)
        {
            IOEntry entry = GetIOEntry(addr);
            if (entry.Write != null)
            {
                entry.Write(addr, value, size);
            }
            if (CPU.Logging)
                Helpers.Logger(String.Format("IO Write Port {0:X}, Value {1:X}", addr, value));
        }

        private async Task<Stream> GetFileStream(string file)
        {
            var biosFile = (StorageFile)await InstallLocation.TryGetItemAsync(file);
            var biosRandomStream = await biosFile.OpenReadAsync();
            var biosStream = biosRandomStream.AsStream();
            return biosStream;
        }
        private async Task<Stream> GetFileStream(StorageFile file)
        {
            var biosRandomStream = await file.OpenReadAsync();
            var biosStream = biosRandomStream.AsStream();
            return biosStream;
        }

        bool BIOSLoaded = false;
        private async Task LoadBIOS()
        {
            if (!BIOSLoaded)
            {
                var biosStream = BIOSImageFile != null ? await GetFileStream(BIOSImageFile) : await GetFileStream(BIOSImagePath);
                var buffer = streamToByteArray(biosStream);
                uint startAddr = (uint)(0xfffff - buffer.Length) + 1;
                Memory.BlockWrite(startAddr, buffer, buffer.Length);
                biosStream.Dispose();
                BIOSLoaded = true;
            }
        }

        bool VGALoaded = false;
        private async Task LoadVGABios()
        {
            if (!VGALoaded)
            {
                var biosStream = VGABIOSImageFile != null ? await GetFileStream(VGABIOSImageFile) : await GetFileStream(VGABIOSImagePath);
                var buffer = streamToByteArray(biosStream);
                Memory.BlockWrite(0xc0000, buffer, buffer.Length);
                biosStream.Dispose();
                VGALoaded = true;
            }
        }
        public byte[] streamToByteArray(Stream input)
        {
            MemoryStream ms = new MemoryStream();
            input.CopyTo(ms);
            return ms.ToArray();
        }
        private async Task SetupSystem()
        {
            ioPorts = new List<IOPort>();

            await LoadBIOS();
            await LoadVGABios();

            foreach (IDevice device in devices)
            {
                INeedsIRQ irqDevice = device as INeedsIRQ;
                INeedsDMA dmaDevice = device as INeedsDMA;

                if (irqDevice != null)
                    irqDevice.IRQ += IRQRaised;

                if (dmaDevice != null)
                    dmaDevice.DMA += DMARaised;

                foreach (int port in device.PortsUsed)
                    SetupIOEntry((ushort)port, device.Read, device.Write);
            }

            CPU.CS = 0xf000;
            CPU.IP = 0xfff0;
        }

        public async Task Restart()
        {
            Running = false;
            CPU.Reset();
            await SetupSystem();
        }

        public void SetBreakpoint(uint addr)
        {
            if (breakpoints.ContainsKey(addr))
                return;

            breakpoints.Add(addr, addr);
        }

        public void ClearBreakpoint(uint addr)
        {
            if (!breakpoints.ContainsKey(addr))
                return;

            breakpoints.Remove(addr);
        }

        public bool CheckBreakpoint()
        {
            uint cpuAddr = CPU.CurrentAddr;

            return breakpoints.ContainsKey(cpuAddr) || tempBreakpoints.ContainsKey(cpuAddr);
        }

        public void Start()
        {
            int addr = (int)((CPU.CS << 4) + CPU.IP);

            CPU.Fetch(true);
        }

        public void Stop()
        {
            foreach (IDevice device in devices)
            {
                IShutdown shutdown = device as IShutdown;

                if (shutdown != null)
                    shutdown.Shutdown();
            }
            MachineShutdown = true;
        }

        public void ClearTempBreakpoints()
        {
            tempBreakpoints.Clear();
        }

        public void StepOver()
        {
            uint currentAddr = (uint)(CPU.GetSelectorBase(x86Disasm.SegmentRegister.CS) + CPU.EIP + CPU.OpLen);

            tempBreakpoints.Add(currentAddr, currentAddr);
            Running = true;
        }

        public void RunCycle()
        {
            RunCycle(false, false);
        }

        public void RunCycle(bool logging, bool stepping)
        {
            isStepping = stepping;
            CPU.Cycle(logging);
            CPU.Fetch(logging);
            picDevice.RunController();
            keyboard.Cycle();
            mouse.Cycle();
        }
    }

    public struct IOEntry
    {
        public ReadCallback Read;
        public WriteCallback Write;
    }
}
