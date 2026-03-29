using Microsoft.Graphics.Canvas;
using System;
using Windows.UI;
using x86Emulator.UWP.Native;

namespace x86Emulator.Devices
{
    /// <summary>
    /// Emulated graphics card that passes VGA framebuffer rendering through to
    /// the host system's GPU via Direct3D 11 hardware acceleration.
    ///
    /// Instead of the legacy path (build a <c>Color[]</c> array on the CPU, then
    /// call <c>CanvasBitmap.CreateFromColors</c> to allocate a new GPU texture
    /// every frame), this class maintains a single persistent D3D11
    /// <c>D3D11_USAGE_DYNAMIC</c> texture and streams each frame into it with a
    /// single <c>Map(D3D11_MAP_WRITE_DISCARD) / Unmap</c> pair.  This removes the
    /// per-frame GPU texture allocation and halves the number of heap objects
    /// created, giving measurably lower frame times especially at high resolutions.
    ///
    /// <para>
    /// <b>Xbox One note:</b> on Xbox One the UWP application must be registered
    /// as a <em>Game</em> (not <em>App</em>) in its package manifest so that the
    /// OS grants it the elevated GPU priority required for hardware-compositor
    /// frame delivery.  The manifest change is tracked separately.
    /// </para>
    /// </summary>
    public sealed class GpuPassthrough : IDisposable
    {
        private GpuPassthroughRenderer renderer;
        private readonly VGA vgaDevice;

        // Reusable BGRA scratch buffer allocated once and grown on demand.
        // Using a persistent buffer avoids a large per-frame heap allocation.
        private byte[] bgraBuffer;

        /// <summary>
        /// <c>true</c> once <see cref="Initialize"/> has completed successfully.
        /// </summary>
        public bool IsInitialized => renderer != null;

        /// <summary>
        /// Creates a new <see cref="GpuPassthrough"/> bound to <paramref name="vga"/>.
        /// Call <see cref="Initialize"/> before uploading any frames.
        /// </summary>
        public GpuPassthrough(VGA vga)
        {
            vgaDevice = vga ?? throw new ArgumentNullException(nameof(vga));
        }

        /// <summary>
        /// Initialises the underlying D3D11 hardware renderer from the Win2D
        /// canvas device.  Typically called once from
        /// <c>CanvasAnimatedControl.CreateResources</c> or the first time the
        /// render panel device is available.
        /// </summary>
        public void Initialize(CanvasDevice canvasDevice)
        {
            if (canvasDevice == null)
                throw new ArgumentNullException(nameof(canvasDevice));

            renderer?.Dispose();
            renderer = new GpuPassthroughRenderer(canvasDevice);
        }

        // ------------------------------------------------------------------
        // Per-mode upload helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Converts a Mode 13h linear packed-pixel framebuffer (one byte per
        /// pixel, DAC-palette indexed, 320×200) to BGRA and uploads it to the
        /// GPU texture via a single Map / Unmap call.
        /// </summary>
        /// <returns>
        /// The <see cref="CanvasBitmap"/> backed by the updated GPU texture, or
        /// <c>null</c> if the renderer is not yet initialised or the upload
        /// failed.
        /// </returns>
        public CanvasBitmap UploadMode13hFrame(byte[] frameBuffer, int width, int height)
        {
            if (renderer == null) return null;

            int pixelCount = width * height;
            EnsureBgraBuffer(pixelCount * 4);

            for (int i = 0; i < pixelCount; i++)
            {
                Color c = vgaDevice.GetDACColor(frameBuffer[i]);
                int dst = i * 4;
                bgraBuffer[dst]     = c.B;
                bgraBuffer[dst + 1] = c.G;
                bgraBuffer[dst + 2] = c.R;
                bgraBuffer[dst + 3] = 0xFF;
            }

            return renderer.UploadBgraFrame(bgraBuffer, (uint)width, (uint)height)
                ? renderer.OutputBitmap
                : null;
        }

        /// <summary>
        /// Converts a Mode 12h (640×480, 16-colour, packed-bit planar) buffer to
        /// BGRA and uploads it to the GPU texture.
        /// Each byte contains the bitmask for 8 horizontally adjacent pixels;
        /// bit 7 = leftmost pixel, bit 0 = rightmost.
        /// </summary>
        public CanvasBitmap UploadMode12hFrame(byte[] rawBuffer, int width, int height)
        {
            if (renderer == null) return null;

            int totalBytes = (width / 8) * height;
            EnsureBgraBuffer(width * height * 4);

            Color fgColor = vgaDevice.GetDACColor(15); // bright white
            Color bgColor = vgaDevice.GetDACColor(0);  // black

            for (int byteIdx = 0; byteIdx < totalBytes; byteIdx++)
            {
                byte b = rawBuffer[byteIdx];
                int pixelBase = byteIdx * 8;
                for (int bit = 7; bit >= 0; bit--)
                {
                    Color c = ((b >> bit) & 1) != 0 ? fgColor : bgColor;
                    int dst = (pixelBase + (7 - bit)) * 4;
                    bgraBuffer[dst]     = c.B;
                    bgraBuffer[dst + 1] = c.G;
                    bgraBuffer[dst + 2] = c.R;
                    bgraBuffer[dst + 3] = 0xFF;
                }
            }

            return renderer.UploadBgraFrame(bgraBuffer, (uint)width, (uint)height)
                ? renderer.OutputBitmap
                : null;
        }

        /// <summary>
        /// Converts a pre-built <see cref="Color"/> array (e.g. from text-mode
        /// rendering) to BGRA and uploads it to the GPU texture.
        /// </summary>
        public CanvasBitmap UploadColorFrame(Color[] pixels, int width, int height)
        {
            if (renderer == null) return null;

            int pixelCount = width * height;
            EnsureBgraBuffer(pixelCount * 4);

            for (int i = 0; i < pixelCount; i++)
            {
                Color c = pixels[i];
                int dst = i * 4;
                bgraBuffer[dst]     = c.B;
                bgraBuffer[dst + 1] = c.G;
                bgraBuffer[dst + 2] = c.R;
                bgraBuffer[dst + 3] = 0xFF;
            }

            return renderer.UploadBgraFrame(bgraBuffer, (uint)width, (uint)height)
                ? renderer.OutputBitmap
                : null;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private void EnsureBgraBuffer(int neededBytes)
        {
            if (bgraBuffer == null || bgraBuffer.Length < neededBytes)
                bgraBuffer = new byte[neededBytes];
        }

        public void Dispose()
        {
            renderer?.Dispose();
            renderer = null;
        }
    }
}
