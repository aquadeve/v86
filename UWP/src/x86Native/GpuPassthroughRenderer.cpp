#include "pch.h"
#include "GpuPassthroughRenderer.h"

using namespace x86Emulator::UWP::Native;
using namespace Platform;

// ---------------------------------------------------------------------------
// Construction / destruction
// ---------------------------------------------------------------------------

GpuPassthroughRenderer::GpuPassthroughRenderer(CanvasDevice^ canvasDevice)
	: texWidth(0), texHeight(0), outputBitmap(nullptr), renderDevice(canvasDevice)
{
	// Extract the underlying ID3D11Device from the Win2D canvas device.
	// CanvasDevice implements IDirect3DDevice, so GetDXGIInterface resolves it
	// to the concrete D3D11 object.
	__abi_ThrowIfFailed(GetDXGIInterface(canvasDevice, d3dDevice.GetAddressOf()));
	d3dDevice->GetImmediateContext(&d3dContext);
}

GpuPassthroughRenderer::~GpuPassthroughRenderer()
{
	outputBitmap = nullptr;
	renderDevice = nullptr;
	frameTexture.Reset();
	d3dContext.Reset();
	d3dDevice.Reset();
}

// ---------------------------------------------------------------------------
// Private helpers
// ---------------------------------------------------------------------------

bool GpuPassthroughRenderer::EnsureTexture(uint32 width, uint32 height)
{
	// Re-use the existing texture if the resolution has not changed.
	if (texWidth == width && texHeight == height && frameTexture != nullptr)
		return true;

	// Release any previously allocated GPU resources.
	outputBitmap = nullptr;
	frameTexture.Reset();

	// Create a D3D11 DYNAMIC texture that the CPU can map for writing.
	// DXGI_FORMAT_B8G8R8A8_UNORM matches the BGRA byte layout that the
	// emulator produces, so no per-pixel format conversion is needed.
	D3D11_TEXTURE2D_DESC desc = {};
	desc.Width            = width;
	desc.Height           = height;
	desc.MipLevels        = 1;
	desc.ArraySize        = 1;
	desc.Format           = DXGI_FORMAT_B8G8R8A8_UNORM;
	desc.SampleDesc.Count = 1;
	desc.Usage            = D3D11_USAGE_DYNAMIC;
	desc.BindFlags        = D3D11_BIND_SHADER_RESOURCE;
	desc.CPUAccessFlags   = D3D11_CPU_ACCESS_WRITE;

	HRESULT hr = d3dDevice->CreateTexture2D(&desc, nullptr, frameTexture.GetAddressOf());
	if (FAILED(hr))
		return false;

	// Wrap the texture as a Win2D CanvasBitmap so that the rest of the
	// rendering pipeline can draw it using the existing Win2D drawImage path.
	ComPtr<IDXGISurface> dxgiSurface;
	hr = frameTexture.As(&dxgiSurface);
	if (FAILED(hr))
		return false;

	auto winRTSurface = CreateDirect3DSurface(dxgiSurface.Get());
	outputBitmap = CanvasBitmap::CreateFromDirect3D11Surface(renderDevice, winRTSurface);

	texWidth  = width;
	texHeight = height;
	return true;
}

// ---------------------------------------------------------------------------
// Public API
// ---------------------------------------------------------------------------

bool GpuPassthroughRenderer::UploadBgraFrame(
	const Platform::Array<uint8>^ data, uint32 width, uint32 height)
{
	if (data == nullptr || data->Length < width * height * 4u)
		return false;

	if (!EnsureTexture(width, height))
		return false;

	// Map the texture for CPU write access, discarding the previous frame's
	// contents.  D3D11_MAP_WRITE_DISCARD causes the runtime to return a
	// pointer to a fresh backing buffer, leaving any in-flight GPU read of
	// the old contents unaffected.
	D3D11_MAPPED_SUBRESOURCE mapped = {};
	HRESULT hr = d3dContext->Map(
		frameTexture.Get(), 0,
		D3D11_MAP_WRITE_DISCARD, 0,
		&mapped);
	if (FAILED(hr))
		return false;

	const uint8* src      = data->Data;
	uint8*       dst      = reinterpret_cast<uint8*>(mapped.pData);
	uint32       srcPitch = width * 4u;

	// Copy row-by-row to account for the GPU texture's row pitch, which may
	// be wider than the image pitch due to hardware alignment requirements.
	for (uint32 row = 0; row < height; ++row)
	{
		memcpy(dst + row * mapped.RowPitch,
		       src + row * srcPitch,
		       srcPitch);
	}

	d3dContext->Unmap(frameTexture.Get(), 0);
	return true;
}
