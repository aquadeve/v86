#pragma once

#include "IntPtr.h"
#include "D3D11Interop.h"

using namespace Microsoft::Graphics::Canvas;
using namespace Microsoft::WRL;
using namespace Platform;

namespace x86Emulator
{
	namespace UWP
	{
		namespace Native
		{
			/// <summary>
			/// Hardware-accelerated GPU passthrough renderer for Xbox One.
			///
			/// Maintains a single persistent D3D11 DYNAMIC texture whose resolution
			/// matches the current VGA video mode.  Each frame the emulator writes
			/// BGRA pixel data directly into GPU-accessible memory via
			/// ID3D11DeviceContext::Map / Unmap (D3D11_MAP_WRITE_DISCARD), which
			/// avoids the per-frame GPU texture allocation that
			/// CanvasBitmap::CreateFromColors would otherwise incur.
			///
			/// On Xbox One the application must be registered as a "Game" (not "App")
			/// to obtain the elevated GPU priority that the hardware compositor
			/// requires for low-latency hardware-accelerated frame delivery.
			/// </summary>
			public ref class GpuPassthroughRenderer sealed
			{
			private:
				ComPtr<ID3D11Device>        d3dDevice;
				ComPtr<ID3D11DeviceContext> d3dContext;
				ComPtr<ID3D11Texture2D>     frameTexture;
				CanvasBitmap^               outputBitmap;
				CanvasDevice^               renderDevice;
				uint32                      texWidth;
				uint32                      texHeight;

				bool EnsureTexture(uint32 width, uint32 height);

			public:
				/// <summary>
				/// Creates the renderer and captures the D3D11 device that backs the
				/// supplied Win2D canvas device.  Call once when the canvas device is
				/// first available (e.g. inside CanvasAnimatedControl.CreateResources).
				/// </summary>
				GpuPassthroughRenderer(CanvasDevice^ canvasDevice);
				virtual ~GpuPassthroughRenderer();

				/// <summary>
				/// Uploads a raw BGRA (B8G8R8A8_UNORM) frame to the GPU texture via
				/// a single Map / Unmap call.
				/// <para>
				/// <paramref name="data"/> must contain at least
				/// <c>width * height * 4</c> bytes in row-major, bottom-up order.
				/// </para>
				/// Returns <c>true</c> on success; <c>false</c> if the texture could
				/// not be mapped (e.g. device lost).
				/// </summary>
				bool UploadBgraFrame(const Platform::Array<uint8>^ data, uint32 width, uint32 height);

				/// <summary>
				/// The Win2D CanvasBitmap backed by the most-recently uploaded GPU
				/// texture.  Assign this to your render target and draw it via Win2D.
				/// The reference is stable between frames of the same resolution;
				/// it is replaced only when the video-mode resolution changes.
				/// </summary>
				property CanvasBitmap^ OutputBitmap
				{
					CanvasBitmap^ get() { return outputBitmap; }
				}
			};
		}
	}
}
