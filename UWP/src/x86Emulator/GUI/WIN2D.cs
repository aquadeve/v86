using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;
using x86Emulator.Configuration;
using x86Emulator.Devices;
using x86Emulator.UWP.Native;

namespace x86Emulator.GUI.WIN2D
{
    public class WIN2D : UI
    {
        private byte[] Memory;
        private static int Width = 640;
        private static int Height = 400;

        // Hardware-accelerated GPU passthrough renderer.  Streams VGA framebuffer
        // data directly into a persistent D3D11 DYNAMIC texture via Map/Unmap,
        // avoiding the per-frame CanvasBitmap allocation used by the legacy path.
        private GpuPassthrough gpuPassthrough;

        /// <summary>
        /// Exposes the <see cref="GpuPassthrough"/> instance so that the VirtIO GPU
        /// device can use the same D3D11 texture upload path.
        /// </summary>
        public GpuPassthrough GpuPassthrough => gpuPassthrough;

        // When a VirtioGPU frame is available, this overrides the VGA frame.
        private CanvasBitmap virtioGPUFrame;

        /// <summary>
        /// Called by <see cref="VirtioGPU"/> when a new frame has been flushed.
        /// The next render cycle will display this frame instead of the VGA output.
        /// </summary>
        public void SetVirtioGPUFrame(CanvasBitmap frame)
        {
            virtioGPUFrame = frame;
        }

        public static bool InterpolationLinear = false;
        public static bool FitScreen = false;
        public static bool DumpFrames = false;
        public static RowDefinition PanelRow;
        private static Color fillColor = Colors.Black;
        public static Color FillColor
        {
            get
            {
                return fillColor;
            }
            set
            {
                fillColor = value;
                FillDisplay(fillColor);
            }
        }

        #region Render Manager
        private static CanvasAnimatedControl renderPanel;
        public CanvasAnimatedControl RenderPanel
        {
            get => renderPanel;
            set
            {
                if (renderPanel == value)
                {
                    return;
                }

                Dispose();

                if (renderPanel != null)
                {
                    renderPanel.Update -= RenderPanelUpdate;
                    renderPanel.Draw -= RenderPanelDraw;
                    renderPanel.GameLoopStopped -= RenderPanelLoopStopping;
                }

                renderPanel = value;
                if (renderPanel != null)
                {
                    RenderPanel.ClearColor = fillColor;
                    renderPanel.Update += RenderPanelUpdate;
                    renderPanel.Draw += RenderPanelDraw;
                    renderPanel.GameLoopStopped += RenderPanelLoopStopping;
                }
            }
        }

        private const uint RenderTargetMinSize = 1024;
        public CanvasBitmap RenderTarget { get; set; } = null;
        private Rect RenderTargetViewport = new Rect();

        //This may be different from viewport's width/haight.
        public float RenderTargetAspectRatio { get; set; } = 1.0f;

        private GameGeometry currentGeometry;
        public GameGeometry CurrentGeometry
        {
            get => currentGeometry;
            set
            {
                currentGeometry = value;
                RenderTargetAspectRatio = currentGeometry.AspectRatio;
                if (RenderTargetAspectRatio < 0.1f)
                {
                    RenderTargetAspectRatio = (float)(currentGeometry.BaseWidth) / currentGeometry.BaseHeight;
                }
            }
        }
        public Rotations CurrentRotation { get; set; }

        public void Dispose()
        {
            try
            {
                RenderTarget?.Dispose();
                RenderTarget = null;
            }
            catch (Exception e)
            {

            }

            try
            {
                gpuPassthrough?.Dispose();
            }
            catch (Exception)
            {
            }
        }

        public Matrix3x2 transformMatrix;
        public float aspectRatio = 1;
        public Size destinationSize;
        public void Render(CanvasDrawingSession drawingSession, ICanvasAnimatedControl canvas)
        {
            try
            {
                var canvasSize = canvas.Size;

                UpdateRenderTargetSize(drawingSession);

                drawingSession.Antialiasing = CanvasAntialiasing.Antialiased;
                drawingSession.TextAntialiasing = CanvasTextAntialiasing.Auto;

                var viewportWidth = RenderTargetViewport.Width;
                var viewportHeight = RenderTargetViewport.Height;
                aspectRatio = RenderTargetAspectRatio;
                if (RenderTarget == null || viewportWidth <= 0 || viewportHeight <= 0)
                    return;

                var rotAngle = 0.0;
                switch (CurrentRotation)
                {
                    case Rotations.CCW90:
                        rotAngle = -0.5 * Math.PI;
                        aspectRatio = 1.0f / aspectRatio;
                        break;
                    case Rotations.CCW180:
                        rotAngle = -Math.PI;
                        break;
                    case Rotations.CCW270:
                        rotAngle = -1.5 * Math.PI;
                        aspectRatio = 1.0f / aspectRatio;
                        break;
                }

                destinationSize = ComputeBestFittingSize(canvasSize, aspectRatio);
                var scaleMatrix = Matrix3x2.CreateScale((float)(destinationSize.Width), (float)(destinationSize.Height));
                var rotMatrix = Matrix3x2.CreateRotation((float)rotAngle);
                var transMatrix = Matrix3x2.CreateTranslation((float)(0.5 * canvasSize.Width), (float)(0.5f * canvasSize.Height));
                transformMatrix = rotMatrix * scaleMatrix * transMatrix;

                drawingSession.Transform = transformMatrix;
                var interpolation = InterpolationLinear ? CanvasImageInterpolation.Linear : CanvasImageInterpolation.NearestNeighbor;
                drawingSession.DrawImage(RenderTarget, new Rect(-0.5, -0.5, 1, 1), RenderTargetViewport, 1.0f, interpolation);
                drawingSession.Transform = Matrix3x2.Identity;
            }
            catch (Exception e)
            {

            }
        }
        int trueWidth = 0;
        int trueHeight = 0;
        StorageFolder dumpFramesFolder;

        /// <summary>
        /// Updates the render target with new pixel data.
        ///
        /// Fast path: when the GPU passthrough renderer is initialised the
        /// <paramref name="data"/> array is converted to BGRA and streamed
        /// directly into the persistent D3D11 DYNAMIC texture via Map / Unmap,
        /// avoiding a per-frame GPU texture allocation.
        ///
        /// Legacy fallback: when the GPU passthrough is not yet ready, or when
        /// frame dumping is active, the original <c>CanvasBitmap.CreateFromColors</c>
        /// path is used so that frame saves continue to work.
        /// </summary>
        public async Task UpdateOutput(Color[] data)
        {
            try
            {
                RenderTargetViewport.Width = Width;
                RenderTargetViewport.Height = Height;

                // --- Hardware-accelerated path (GPU passthrough) ---
                if (gpuPassthrough.IsInitialized && !DumpFrames)
                {
                    var bitmap = gpuPassthrough.UploadColorFrame(data, (int)Width, (int)Height);
                    if (bitmap != null)
                    {
                        RenderTarget = bitmap;
                        return;
                    }
                    // Fall through to the legacy path if the upload failed.
                }

                // --- Legacy path (allocates a new CanvasBitmap each frame) ---
                RenderTarget = CanvasBitmap.CreateFromColors(renderPanel, data, (int)Width, (int)Height, 96, CanvasAlphaMode.Ignore);
                if (DumpFrames)
                {
                    await SaveCurrentFrameAsync();
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Saves the current <see cref="RenderTarget"/> bitmap to the DumpFrames
        /// folder as a PNG file.
        /// </summary>
        private async Task SaveCurrentFrameAsync()
        {
            try
            {
                if (dumpFramesFolder == null)
                {
                    var root = await ApplicationData.Current.TemporaryFolder.CreateFolderAsync("DumpFrames", CreationCollisionOption.OpenIfExists);
                    var time = DateTime.Now.ToString().Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace(" ", "_");
                    dumpFramesFolder = await root.CreateFolderAsync(time, CreationCollisionOption.ReplaceExisting);
                }

                StorageFile tempFile = await dumpFramesFolder.CreateFileAsync("x86Emulator.png", CreationCollisionOption.GenerateUniqueName);
                using (var saveStream = (await tempFile.OpenStreamForWriteAsync()).AsRandomAccessStream())
                {
                    await RenderTarget.SaveAsync(saveStream, CanvasBitmapFileFormat.Png);
                }
            }
            catch (Exception)
            {
            }
        }

        private void RenderPanelDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            Render(args.DrawingSession, sender);
        }
        private void RenderPanelUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {

        }
        private void RenderPanelLoopStopping(ICanvasAnimatedControl sender, object args)
        {
            callResizeTimer();
        }

        private void UpdateRenderTargetSize(CanvasDrawingSession drawingSession)
        {
            if (RenderTarget != null)
            {
                try
                {
                    var currentSize = RenderTarget.Size;
                    if (currentSize.Width >= CurrentGeometry.MaxWidth && currentSize.Height >= CurrentGeometry.MaxHeight)
                    {
                        return;
                    }
                }
                catch
                {
                    return;
                }
            }
            try
            {
                var size = Math.Max(Math.Max(CurrentGeometry.MaxWidth, CurrentGeometry.MaxHeight), RenderTargetMinSize);
                size = ClosestGreaterPowerTwo(size);

                RenderTarget?.Dispose();
                RenderTarget = BitmapMap.CreateMappableBitmap(drawingSession, size, size);
            }
            catch (Exception e)
            {

            }
        }

        private static Size ComputeBestFittingSize(Size viewportSize, float aspectRatio)
        {
            try
            {
                var candidateWidth = Math.Floor(viewportSize.Height * aspectRatio);
                var size = new Size(candidateWidth, viewportSize.Height);
                if (viewportSize.Width < candidateWidth)
                {
                    var height = viewportSize.Width / aspectRatio;
                    size = new Size(viewportSize.Width, height);
                }

                return size;
            }
            catch (Exception e)
            {
                return viewportSize;
            }
        }

        private static uint ClosestGreaterPowerTwo(uint value)
        {
            uint output = 1;
            while (output < value)
            {
                output *= 2;
            }

            return output;
        }

        #endregion

        #region Main
        public WIN2D(CanvasAnimatedControl panel, VGA device) : base(device)
        {
            dumpFramesFolder = null;
            RenderPanel = panel;
            Memory = new byte[Width * Height * 4]; // BGRA

            // Create the GPU passthrough device bound to the emulated VGA card.
            // The underlying D3D11 renderer is initialised lazily in Init() once
            // the Win2D device is guaranteed to be available.
            gpuPassthrough = new GpuPassthrough(device);

            CurrentGeometry = new GameGeometry()
            {
                BaseHeight = (uint)Height,
                MaxHeight = (uint)Height,
                BaseWidth = (uint)Width,
                MaxWidth = (uint)Width,
                AspectRatio = 1.6f
            };
            callResizeTimer(true);

            ResetScreen();
        }


        public override void Init()
        {
            if (renderPanel != null)
            {
                RenderPanel.ClearColor = fillColor;

                // Initialise the hardware GPU passthrough renderer using the
                // Win2D canvas device that backs the render panel.
                if (!gpuPassthrough.IsInitialized)
                {
                    try
                    {
                        gpuPassthrough.Initialize(renderPanel.Device);
                    }
                    catch (Exception)
                    {
                        // Fall back silently to the legacy CanvasBitmap path.
                    }
                }
            }
        }

        public override void ResetScreen()
        {
            RenderPanel.ClearColor = fillColor;
        }

        private static void FillDisplay(Color color)
        {
            if (renderPanel != null)
            {
                renderPanel.ClearColor = color;
            }
        }

        public override async Task Cycle()
        {
            // VirtIO GPU takes priority over legacy VGA when active
            if (virtioGPUFrame != null)
            {
                RenderTarget = virtioGPUFrame;
                return;
            }

            if (vgaDevice.IsChain4Mode)
                await CycleMode13h();
            else if (vgaDevice.IsGraphicsMode)
                await CycleMode12h();
            else
                await CycleTextMode();
        }

        /// <summary>
        /// Text mode rendering (80×25 characters, 16 colours via VGA attribute controller).
        /// Reads the font glyph data from 0xA0000 and the character/attribute buffer from 0xB8000.
        /// </summary>
        private async Task CycleTextMode()
        {
            const int textWidth = 640;
            const int textHeight = 400;

            if (Width != textWidth || Height != textHeight)
            {
                Width = textWidth;
                Height = textHeight;
                CurrentGeometry = new GameGeometry()
                {
                    BaseHeight = (uint)textHeight,
                    MaxHeight = (uint)textHeight,
                    BaseWidth = (uint)textWidth,
                    MaxWidth = (uint)textWidth,
                    AspectRatio = 1.6f
                };
            }

            var fontBuffer = new byte[0x2000];
            var displayBuffer = new byte[0xfa0];
            Color[] data = new Color[textWidth * textHeight];

            x86Emulator.Memory.BlockRead(0xa0000, fontBuffer, fontBuffer.Length);
            x86Emulator.Memory.BlockRead(0xb8000, displayBuffer, displayBuffer.Length);

            for (var i = 0; i < displayBuffer.Length; i += 2)
            {
                int currChar = displayBuffer[i];
                int fontOffset = currChar * 32;
                byte attribute = displayBuffer[i + 1];
                int y = i / 160 * 16;

                Color foreColour = vgaDevice.GetColour(attribute & 0xf);
                Color backColour = vgaDevice.GetColour((attribute >> 4) & 0xf);

                for (var f = fontOffset; f < fontOffset + 16; f++)
                {
                    int x = ((i % 160) / 2) * 8;

                    for (var j = 7; j >= 0; j--)
                    {
                        if (((fontBuffer[f] >> j) & 0x1) != 0)
                            data[y * textWidth + x] = foreColour;
                        else
                            data[y * textWidth + x] = backColour;
                        x++;
                    }
                    y++;
                }
            }

            await UpdateOutput(data);
        }

        /// <summary>
        /// Mode 13h rendering: 320×200, 256 colours, linear packed-pixel framebuffer at 0xA0000.
        /// Each byte in the framebuffer is a direct index into the 256-entry DAC palette.
        /// This is the standard mode used by DOS games (DOOM, Quake, etc.).
        ///
        /// Fast path: when the GPU passthrough renderer is active the palette lookup
        /// and BGRA conversion are performed inside <see cref="GpuPassthrough"/> and
        /// the result is streamed directly to a persistent D3D11 texture, avoiding
        /// the per-frame <c>Color[]</c> allocation and <c>CanvasBitmap.CreateFromColors</c>
        /// call used by the legacy path.
        /// </summary>
        private async Task CycleMode13h()
        {
            const int gfxWidth = 320;
            const int gfxHeight = 200;

            if (Width != gfxWidth || Height != gfxHeight)
            {
                Width = gfxWidth;
                Height = gfxHeight;
                CurrentGeometry = new GameGeometry()
                {
                    BaseHeight = (uint)gfxHeight,
                    MaxHeight = (uint)gfxHeight,
                    BaseWidth = (uint)gfxWidth,
                    MaxWidth = (uint)gfxWidth,
                    AspectRatio = 1.6f
                };
            }

            var frameBuffer = new byte[gfxWidth * gfxHeight];
            x86Emulator.Memory.BlockRead(0xa0000, frameBuffer, frameBuffer.Length);

            // --- Hardware-accelerated GPU passthrough path ---
            if (gpuPassthrough.IsInitialized && !DumpFrames)
            {
                try
                {
                    var bitmap = gpuPassthrough.UploadMode13hFrame(frameBuffer, gfxWidth, gfxHeight);
                    if (bitmap != null)
                    {
                        RenderTargetViewport.Width = gfxWidth;
                        RenderTargetViewport.Height = gfxHeight;
                        RenderTarget = bitmap;
                        return;
                    }
                }
                catch (Exception)
                {
                    // Fall through to the legacy path on error.
                }
            }

            // --- Legacy path ---
            Color[] data = new Color[gfxWidth * gfxHeight];
            for (int i = 0; i < frameBuffer.Length; i++)
                data[i] = vgaDevice.GetDACColor(frameBuffer[i]);

            await UpdateOutput(data);
        }

        /// <summary>
        /// Mode 12h rendering: 640×480, 16 colours, 4-plane memory layout at 0xA0000.
        /// Since this emulator does not implement VGA plane-select multiplexing at the memory
        /// level, each byte at 0xA0000 is treated as a packed bitmask for one plane.
        /// Bit 7 = leftmost pixel, bit 0 = rightmost.  Each set bit is rendered with palette
        /// colour 15 (bright white in default EGA/VGA palette); clear bits use colour 0 (black).
        /// This is a best-effort approximation that works for simple graphics and text-over-graphics
        /// scenarios without requiring full planar emulation.
        ///
        /// Fast path: when the GPU passthrough renderer is active the conversion is performed
        /// inside <see cref="GpuPassthrough"/> and uploaded directly to the GPU texture.
        /// </summary>
        private async Task CycleMode12h()
        {
            const int gfxWidth = 640;
            const int gfxHeight = 480;
            const int bytesPerRow = gfxWidth / 8;        // 80 bytes per scanline
            const int totalBytes = bytesPerRow * gfxHeight; // 38400 bytes – fits in 64 KB VGA window

            if (Width != gfxWidth || Height != gfxHeight)
            {
                Width = gfxWidth;
                Height = gfxHeight;
                CurrentGeometry = new GameGeometry()
                {
                    BaseHeight = (uint)gfxHeight,
                    MaxHeight = (uint)gfxHeight,
                    BaseWidth = (uint)gfxWidth,
                    MaxWidth = (uint)gfxWidth,
                    AspectRatio = (float)gfxWidth / gfxHeight
                };
            }

            var rawBuffer = new byte[totalBytes];
            x86Emulator.Memory.BlockRead(0xa0000, rawBuffer, totalBytes);

            // --- Hardware-accelerated GPU passthrough path ---
            if (gpuPassthrough.IsInitialized && !DumpFrames)
            {
                try
                {
                    var bitmap = gpuPassthrough.UploadMode12hFrame(rawBuffer, gfxWidth, gfxHeight);
                    if (bitmap != null)
                    {
                        RenderTargetViewport.Width = gfxWidth;
                        RenderTargetViewport.Height = gfxHeight;
                        RenderTarget = bitmap;
                        return;
                    }
                }
                catch (Exception)
                {
                    // Fall through to the legacy path on error.
                }
            }

            // --- Legacy path ---
            Color fgColor = vgaDevice.GetDACColor(15); // bright white
            Color bgColor = vgaDevice.GetDACColor(0);  // black

            Color[] data = new Color[gfxWidth * gfxHeight];
            for (int byteIdx = 0; byteIdx < totalBytes; byteIdx++)
            {
                byte b = rawBuffer[byteIdx];
                int pixelBase = byteIdx * 8;
                // Bit 7 = leftmost pixel (pixel offset 0), bit 0 = rightmost (pixel offset 7)
                for (int bit = 7; bit >= 0; bit--)
                {
                    data[pixelBase + (7 - bit)] = ((b >> bit) & 1) != 0 ? fgColor : bgColor;
                }
            }

            await UpdateOutput(data);
        }

        #endregion

        #region Canvas Resolver
        public static int[] ASR = new int[] { 4, 3 };
        private Timer ResolveTimer;
        bool timerState = false;
        private void callResizeTimer(bool startState = false)
        {
            try
            {
                ResolveTimer?.Dispose();
                timerState = false;
                if (startState)
                {
                    ResolveTimer = new Timer(async delegate
                    {
                        if (!timerState)
                        {
                            await ResolveCanvasSize();
                        }
                    }, null, 0, 1100);
                }
            }
            catch (Exception e)
            {

            }
        }
        private async Task ResolveCanvasSize()
        {
            try
            {
                timerState = true;

                await Task.Delay(300);
                await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
                {
                    try
                    {
                        var width = 0d;
                        var height = 0d;
                        var currentHeight = 0d;
                        var currentWidth = 0d;

                        if (FitScreen)
                        {
                            if (renderPanel.VerticalAlignment != VerticalAlignment.Center)
                            {
                                renderPanel.VerticalAlignment = VerticalAlignment.Center;
                            }
                            width = (double)Width;
                            height = (double)Height;
                            currentHeight = PanelRow.ActualHeight;
                            currentWidth = Window.Current.CoreWindow.Bounds.Width;
                        }
                        else
                        {
                            if (renderPanel.VerticalAlignment != VerticalAlignment.Top)
                            {
                                renderPanel.VerticalAlignment = VerticalAlignment.Top;
                            }
                            width = (double)Width;
                            height = (double)Height;

                            currentHeight = PanelRow.ActualHeight;
                            currentWidth = Window.Current.CoreWindow.Bounds.Width;
                        }

                        if (width > 0 && height > 0)
                        {
                            try
                            {
                                double aspectRatio_X = ASR[0];
                                double aspectRatio_Y = ASR[1];

                                double targetHeight = height;
                                if (aspectRatio_X == 0 && aspectRatio_Y == 0)
                                {
                                    //get core aspect
                                    targetHeight = Convert.ToDouble(width) / currentGeometry.AspectRatio;
                                }
                                else
                                {
                                    targetHeight = Convert.ToDouble(width) / (aspectRatio_X / aspectRatio_Y);
                                }
                                height = targetHeight;
                            }
                            catch (Exception ex)
                            {

                            }

                            float ratioX = (float)currentWidth / (float)width;
                            float ratioY = (float)currentHeight / (float)height;
                            float ratio = Math.Min(ratioX, ratioY);

                            float sourceRatio = (float)width / (float)height;

                            // New width and height based on aspect ratio
                            int newWidth = (int)(width * ratio);
                            int newHeight = (int)(height * ratio);

                            if (renderPanel.Height != newHeight)
                            {
                                renderPanel.Height = newHeight;
                            }
                            if (renderPanel.Width != newWidth)
                            {
                                renderPanel.Width = newWidth;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            renderPanel.VerticalAlignment = VerticalAlignment.Stretch;
                            renderPanel.Width = Double.NaN;
                            renderPanel.Height = Double.NaN;
                        }
                        catch (Exception ecx)
                        {

                        }
                    }

                    timerState = false;
                });
            }
            catch (Exception ex)
            {

            }
        }
        #endregion
    }
}
