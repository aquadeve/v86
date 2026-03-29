using Windows.UI;

namespace x86Emulator.Devices
{
    public class VGA : IDevice, INeedsMMIO
    {
        private enum SequenceRegister
        {
            Reset,
            ClockingMode,
            MapMask,
            CharacterMap,
            SequencerMemoryMode
        }

        private readonly int[] portsUsed = {
                                               0x3b4, 0x3b5, 0x3ba, 0x3c0, 0x3c1, 0x3c2, 0x3c4, 0x3c5, 0x3c7, 0x3c8, 0x3c9,
                                               0x3ca, 0x3cc, 0x3ce, 0x3cf, 0x3d4, 0x3d5, 0x3da
                                           };

        private readonly MemoryMapRegion[] memoryMap = {
                                                           new MemoryMapRegion { Base = 0xa0000, Length = 0x1ffff },
                                                           new MemoryMapRegion { Base = 0xb0000, Length = 0x7fff },
                                                           new MemoryMapRegion { Base = 0xb8000, Length = 0xffff }
                                                       };
        private readonly byte[] sequencer;
        private readonly Color[] dacPalette;
        private readonly byte[] dacColour;
        private readonly byte[] attributeControl;
        private readonly byte[] crtControl;
        private readonly byte[] gcRegisters;

        private byte miscOutputRegister;
        private byte featureControl;
        private SequenceRegister sequencerAddress;
        private byte dacAddress;
        private byte attributeControlAddress;
        private byte crtControlAddress;
        private byte gcAddress;
        private byte currColor;
        private bool attributeControlFlipFlop;

        public int[] PortsUsed
        {
            get { return portsUsed; }
        }

        public MemoryMapRegion[] MemoryMap
        {
            get { return memoryMap; }
        }

        /// <summary>
        /// True when the VGA sequencer is in Chain-4 mode (bit 3 of SequencerMemoryMode register).
        /// Chain-4 enables the 256-colour packed-pixel linear framebuffer used by Mode 13h and
        /// most DOS games.
        /// </summary>
        public bool IsChain4Mode => (sequencer[4] & 0x08) != 0;

        /// <summary>
        /// True when the Graphics Controller Miscellaneous register (index 6) indicates that
        /// the active memory map begins at A000:0000 (the standard graphics-mode aperture).
        /// </summary>
        public bool IsGraphicsMode => (gcRegisters[6] & 0x01) != 0;

        public VGA()
        {
            sequencer = new byte[5];
            dacPalette = new Color[256];
            dacColour = new byte[3];
            dacAddress = 0;
            attributeControlFlipFlop = false;
            attributeControl = new byte[0x15];
            crtControl = new byte[0x19];
            gcRegisters = new byte[9];
        }

        /// <summary>Returns the text-mode colour for the given attribute nibble (palette-mapped).</summary>
        public Color GetColour(int index)
        {
            return dacPalette[attributeControl[index]];
        }

        /// <summary>
        /// Returns the DAC colour for the given palette index directly.
        /// Used when rendering packed-pixel graphics modes (e.g. Mode 13h) where each
        /// framebuffer byte is a direct index into the 256-entry DAC palette.
        /// </summary>
        public Color GetDACColor(int index)
        {
            return dacPalette[index & 0xFF];
        }

        public uint Read(ushort addr, int size)
        {
            uint ret = 0;

            switch (addr)
            {
                case 0x3da:
                    attributeControlFlipFlop = false;
                    ret = 0;
                    break;
                case 0x3ba:
                case 0x3c2:
                    ret = 0;
                    break;
                case 0x3ca:
                    ret = featureControl;
                    break;
                case 0x3cc:
                    ret = miscOutputRegister;
                    break;
                case 0x3c5:
                    if ((int)sequencerAddress < sequencer.Length)
                    {
                        ret = sequencer[(int)sequencerAddress];
                    }
                    break;
                case 0x3d5:
                    ret = crtControl[crtControlAddress];
                    break;
                case 0x3c1:
                    ret = attributeControl[attributeControlAddress];
                    break;
                case 0x3c9:
                    ret = dacColour[currColor];
                    break;
                case 0x3c0:
                    break;
                case 0x3cf:
                    if (gcAddress < gcRegisters.Length)
                        ret = gcRegisters[gcAddress];
                    break;
                default:
                    System.Diagnostics.Debugger.Break();
                    break;
            }
            return ret;
        }

        public void Write(ushort addr, uint value, int size)
        {
            switch (addr)
            {
                case 0x3b4:
                case 0x3d4:
                    crtControlAddress = (byte)value;
                    break;
                case 0x3b5:
                case 0x3d5:
                    crtControl[crtControlAddress] = (byte)value;
                    break;
                case 0x3ba:
                case 0x3da:
                    featureControl = (byte)value;
                    break;
                case 0x3c2:
                    miscOutputRegister = (byte)value;
                    break;
                case 0x3c4:
                    sequencerAddress = (SequenceRegister)value;
                    break;
                case 0x3c5:
                    sequencer[(int)sequencerAddress] = (byte)value;
                    break;
                case 0x3c0:
                    if (attributeControlFlipFlop)
                    {
                        attributeControl[attributeControlAddress] = (byte)value;
                        attributeControlFlipFlop = false;
                    }
                    else
                    {
                        attributeControlAddress = (byte)value;
                        attributeControlFlipFlop = true;
                    }
                    break;
                case 0x3c8:
                    dacAddress = (byte)value;
                    currColor = 0;
                    break;
                case 0x3c9:
                    dacColour[currColor] = (byte)((value & 0x3f) << 2);
                    if (++currColor == 3)
                    {
                        currColor = 0;
                        dacPalette[dacAddress] = Color.FromArgb(255, dacColour[0], dacColour[1], dacColour[2]);
                        dacAddress++;
                    }
                    break;
                case 0x3c7:
                    dacAddress = (byte)value;
                    break;
                case 0x3ce:
                    gcAddress = (byte)value;
                    break;
                case 0x3cf:
                    if (gcAddress < gcRegisters.Length)
                        gcRegisters[gcAddress] = (byte)value;
                    break;
                default:
                    System.Diagnostics.Debugger.Break();
                    break;
            }
        }
    }
}