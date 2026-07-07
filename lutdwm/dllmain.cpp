
#include "pch.h"

#include <io.h>
#include <map>
#include <mutex>
#include <string>
#include <sstream>
#include <iostream>
#include <iomanip>
#pragma comment (lib, "d3d11.lib")
#pragma comment (lib, "d3dcompiler.lib")
#pragma comment (lib, "dxgi.lib")
#pragma comment (lib, "uuid.lib")
#pragma comment (lib, "dxguid.lib")

#pragma intrinsic(_ReturnAddress)

#define DITHER_GAMMA 2.2
#define LUT_FOLDER "%SYSTEMROOT%\\Temp\\luts"

#define RELEASE_IF_NOT_NULL(x) { if (x != NULL) { x->Release(); } }
#define _STRINGIFY(x) #x
#define STRINGIFY(x) _STRINGIFY(x)
// dwmcore.dll version helper: pack (build, revision) into one comparable value.
#define DWM_VER(build, rev) ((((unsigned long long)(build)) << 32) | (unsigned int)(rev))
#pragma comment(lib, "version.lib")
#define LOG_FILE_PATH R"(C:\DWMLOG\dwm.log)"
#define MAX_LOG_FILE_SIZE 20 * 1024 * 1024
#ifdef _DEBUG
#define DEBUG_MODE true
#else
#define DEBUG_MODE false
#endif

#if DEBUG_MODE == true
#define __LOG_ONLY_ONCE(x, y) if (static bool first_log_##y = true) { log_to_file(x); first_log_##y = false; }
#define _LOG_ONLY_ONCE(x, y) __LOG_ONLY_ONCE(x, y)
#define LOG_ONLY_ONCE(x) _LOG_ONLY_ONCE(x, __COUNTER__)
#define MESSAGE_BOX_DBG(x, y) MessageBoxA(NULL, x, "DEBUG HOOK DWM", y);

#define EXECUTE_WITH_LOG(winapi_func_hr) \
	do { \
		HRESULT hr = (winapi_func_hr); \
		if (FAILED(hr)) \
		{ \
			std::stringstream ss; \
			ss << "ERROR AT LINE: " << __LINE__ << " HR: " << hr << " - DETAILS: "; \
			LPSTR error_message = nullptr; \
			FormatMessageA(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, \
				NULL, hr, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT), (LPSTR)&error_message, 0, NULL); \
			ss << error_message; \
			log_to_file(ss.str().c_str()); \
			LocalFree(error_message); \
			throw std::exception(ss.str().c_str()); \
		} \
	} while (false);

#define EXECUTE_D3DCOMPILE_WITH_LOG(winapi_func_hr, error_interface) \
	do { \
		HRESULT hr = (winapi_func_hr); \
		if (FAILED(hr)) \
		{ \
			std::stringstream ss; \
			ss << "ERROR AT LINE: " << __LINE__ << " HR: " << hr << " - DETAILS: "; \
			LPSTR error_message = nullptr; \
			FormatMessageA(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, \
				NULL, hr, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT), (LPSTR)&error_message, 0, NULL); \
			ss << error_message << " - DX COMPILE ERROR: " << (char*)error_interface->GetBufferPointer(); \
			error_interface->Release(); \
			log_to_file(ss.str().c_str()); \
			LocalFree(error_message); \
			throw std::exception(ss.str().c_str()); \
		} \
	} while (false);

#define LOG_ADDRESS(prefix_message, address) \
	{ \
		std::stringstream ss; \
		ss << prefix_message << " 0x" << std::setw(sizeof(address) * 2) << std::setfill('0') << std::hex << (UINT_PTR)address; \
		log_to_file(ss.str().c_str()); \
	}

#else
#define LOG_ONLY_ONCE(x)
#define MESSAGE_BOX_DBG(x, y)
#define EXECUTE_WITH_LOG(winapi_func_hr) winapi_func_hr;
#define EXECUTE_D3DCOMPILE_WITH_LOG(winapi_func_hr, error_interface) winapi_func_hr;
#define LOG_ADDRESS(prefix_message, address)
#endif

#if DEBUG_MODE == true
void log_to_file(const char* log_buf)
{
	FILE* pFile = fopen(LOG_FILE_PATH, "a");
	if (pFile == NULL)
	{
		return;
	}
	fseek(pFile, 0, SEEK_END);
	long size = ftell(pFile);
	if (size > MAX_LOG_FILE_SIZE)
	{
		if (_chsize(_fileno(pFile), 0) == -1)
		{
			fclose(pFile);
			return;
		}
	}
	fseek(pFile, 0, SEEK_END);
	fprintf(pFile, "%s\n", log_buf);
	fclose(pFile);
}
#endif

using Microsoft::WRL::ComPtr;

// Always-compiled, crash-proof diagnostic log (SYSTEM-writable). Called only on rare events.
static void diag_log(const char* msg)
{
	return;   // logging disabled — remove this line to re-enable dwm_diag.log
	__try
	{
		FILE* f = fopen(R"(C:\Windows\Temp\dwm_diag.log)", "a");
		if (!f) return;
		fprintf(f, "%s\n", msg);
		fclose(f);
	}
	__except (EXCEPTION_EXECUTE_HANDLER) {}
}

// Once set, every hook body returns immediately -> DWM composites normally, never crashes.
static std::atomic<bool> g_hookInert{false};

#define HR_OR_THROW(expr) do { HRESULT _hr_ = (expr); if (FAILED(_hr_)) throw std::runtime_error(#expr); } while (0)

// True adapter identity. Survives device-pointer changes; distinguishes iGPU vs dGPU nodes.
static bool GetDeviceLuidKey(ID3D11Device* dev, unsigned long long* outKey)
{
	if (!dev) return false;
	ComPtr<IDXGIDevice> dxgiDev;
	if (FAILED(dev->QueryInterface(IID_PPV_ARGS(&dxgiDev)))) return false;
	ComPtr<IDXGIAdapter> adapter;
	if (FAILED(dxgiDev->GetAdapter(&adapter))) return false;
	DXGI_ADAPTER_DESC desc;
	if (FAILED(adapter->GetDesc(&desc))) return false;
	*outKey = ((unsigned long long)(unsigned int)desc.AdapterLuid.HighPart << 32) |
	          (unsigned int)desc.AdapterLuid.LowPart;
	return true;
}

static bool ResourceOnDevice(ID3D11DeviceChild* res, ID3D11Device* dev)
{
	if (!res || !dev) return false;
	ComPtr<ID3D11Device> d;
	res->GetDevice(&d);
	return d.Get() == dev;
}

struct RtvCacheEntry {
	ID3D11Texture2D* texture;
	ID3D11RenderTargetView* rtv;
};

// Immutable, per DEVICE INSTANCE (resources are bound to the instance, so the instance is the
// correct key; LUID is recorded for diagnostics). Built ONCE on the worker thread.
struct AdapterAssets {
	unsigned long long luidKey = 0;
	ComPtr<ID3D11Device> device;                 // we hold a ref -> pointer can't be freed+reused (no ABA)
	ComPtr<ID3D11DeviceContext> context;         // immediate context; guarded by ctxMutex
	ComPtr<ID3D11VertexShader> vs;
	ComPtr<ID3D11PixelShader> ps;
	ComPtr<ID3D11InputLayout> inputLayout;
	ComPtr<ID3D11SamplerState> sampler;
	ComPtr<ID3D11SamplerState> noiseSampler;
	ComPtr<ID3D11ShaderResourceView> noiseSrv;
	ComPtr<ID3D11ShaderResourceView> lutSrv[64];
	int lutCount = 0;
	std::atomic<bool> ready{false};              // published true only after full successful build
	std::mutex ctxMutex;
};

// Per-output scratch, keyed by the stable COverlayContext (self). Rebuilt only when its owner
// device instance changes (primary flip) or the surface grows/changes format -> NOT per frame.
struct OutputRes {
	ComPtr<ID3D11Device> ownerDevice;
	ComPtr<ID3D11Buffer> vertexBuffer;
	ComPtr<ID3D11Buffer> constantBuffer;
	ComPtr<ID3D11Texture2D> scratch[2];
	ComPtr<ID3D11ShaderResourceView> scratchSrv[2];
	D3D11_TEXTURE2D_DESC scratchDesc[2] = {};
	struct RtvEntry { ID3D11Texture2D* key = nullptr; ComPtr<ID3D11RenderTargetView> rtv; };
	RtvEntry rtvCache[8];
	int rtvCount = 0;
	int lastLutSize[2] = {-1, -1};
	int sightings = 0;                            // transient-plane debounce
	std::atomic<bool> ready{false};
};

static std::map<ID3D11Device*, AdapterAssets*> g_adapters;
static std::mutex g_adaptersMutex;
static std::map<void* /*self*/, OutputRes*> g_outputs;
static std::mutex g_outputsMutex;

#if DEBUG_MODE == true
void print_error(const char* prefix_message)
{
	DWORD errorCode = GetLastError();
	LPSTR errorMessage = nullptr;
	FormatMessageA(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
	               nullptr, errorCode, MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT), (LPSTR)&errorMessage, 0, nullptr);

	char message_buf[100];
	sprintf(message_buf, "%s: %s - error code: %u", prefix_message, errorMessage, errorCode);
	log_to_file(message_buf);
	return;
}
#endif

unsigned int lut_index(const unsigned int b, const unsigned int g, const unsigned int r, const unsigned int c,
                       const unsigned int lut_size)
{
	return lut_size * lut_size * 4 * b + lut_size * 4 * g + 4 * r + c;
}

#define LUT_ACCESS_INDEX(lut, b, g, r, c, lut_size) (*((float*)(lut) + lut_index(b, g, r, c, lut_size)))

void* get_relative_address(void* instruction_address, int offset, int instruction_size)
{
	int relative_offset = *(int*)((unsigned char*)instruction_address + offset);

	return (unsigned char*)instruction_address + instruction_size + relative_offset;
}

const unsigned char COverlayContext_Present_bytes[] = {
	0x48, 0x89, 0x5c, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x57, 0x48, 0x83, 0xec, 0x40, 0x48, 0x8b, 0xb1, 0x20,
	0x2c, 0x00, 0x00, 0x45, 0x8b, 0xd0, 0x48, 0x8b, 0xfa, 0x48, 0x8b, 0xd9, 0x48, 0x85, 0xf6, 0x0f, 0x85
};
const int IOverlaySwapChain_IDXGISwapChain_offset = -0x118;

const unsigned char COverlayContext_IsCandidateDirectFlipCompatible_bytes[] = {
	0x48, 0x89, 0x7c, 0x24, 0x20, 0x55, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8b, 0xec, 0x48, 0x83,
	0xec, 0x40
};
const unsigned char COverlayContext_OverlaysEnabled_bytes[] = {
	0x75, 0x04, 0x32, 0xc0, 0xc3, 0xcc, 0x83, 0x79, 0x30, 0x01, 0x0f, 0x97, 0xc0, 0xc3
};

const int COverlayContext_DeviceClipBox_offset = -0x120;

const int IOverlaySwapChain_HardwareProtected_offset = -0xbc;

const unsigned char COverlayContext_Present_bytes_w11[] = {
	0x40, 0x53, 0x55, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57, 0x48, 0x81, 0xEC, 0x88, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x05,
	'?', '?', '?', '?', 0x48, 0x33, 0xC4, 0x48, 0x89, 0x44, 0x24, 0x78, 0x48
};
const int IOverlaySwapChain_IDXGISwapChain_offset_w11 = 0xE0;

const unsigned char COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11[] = {
	0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC,
	0x68, 0x48,
};

const unsigned char COverlayContext_OverlaysEnabled_bytes_w11[] = {
	0x83, 0x3D, '?', '?', '?', '?', '?', 0x75, 0x04
};

int COverlayContext_DeviceClipBox_offset_w11 = 0x466C;

const int IOverlaySwapChain_HardwareProtected_offset_w11 = -0x144;

const unsigned char COverlayContext_Present_bytes_w11_24h2[] = {
	0x4C, 0x8B, 0xDC, 0x56, 0x41, 0x56
};

const int IOverlaySwapChain_IDXGISwapChain_offset_w11_24h2 = 0x108;

const unsigned char COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11_24h2[] = {
	0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, '?', 0x48, 0x89, 0x68, '?', 0x48, 0x89, 0x70, '?', 0x48, 0x89, 0x78, '?', 0x41, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x33, 0xDB
};

const unsigned char COverlayContext_OverlaysEnabled_bytes_relative_w11_24h2[] = {
	0xE8, '?', '?', '?', '?', 0x84, 0xC0, 0xB8, 0x04, 0x00, 0x00, 0x00
};

int COverlayContext_DeviceClipBox_offset_w11_24h2 = 0x53E8;

const int IOverlaySwapChain_HardwareProtected_offset_w11_24h2 = 0x64;

// ---- Per-dwmcore-binary profiles -----------------------------------------------------------
// A profile binds a dwmcore version to (a) a shared AOB *signature set* and (b) the build-specific
// *offsets*. These move INDEPENDENTLY: e.g. 26100.8246 and 26100.8655 share the exact same signatures
// but have different device-vector addresses. So the signatures live once in a DwmSignatures set that
// profiles point at, and each profile carries only its own offsets. Adding a new build:
//   * offsets moved only (the common case)     -> add one DwmProfile row pointing at an existing set
//   * a signature byte-pattern actually moved   -> copy a DwmSignatures set, edit the bytes that moved,
//                                                  and point the new row at it (no new global arrays)
// The 24H2 / 23H2 / legacy paths do NOT use this and are unchanged.

// Max length of any single AOB signature. The compiler enforces this (too many initializers = error),
// so if a future signature is longer, just bump this.
static const int DWM_SIG_MAX = 64;

// One dwmcore "signature generation": the four AOB patterns stored INLINE, so a new generation is just a
// new initializer below -- no separate global arrays to add. '?' (0x3F) is the wildcard byte understood
// by aob_match_inverse. Each ...Len is the real pattern length: the arrays are zero-padded to
// DWM_SIG_MAX and a signature may legitimately contain 0x00, so the length is given explicitly.
struct DwmSignatures
{
	unsigned char present[DWM_SIG_MAX];           size_t presentLen;
	unsigned char overlaysEnabled[DWM_SIG_MAX];   size_t overlaysEnabledLen;
	unsigned char isCandidateDf[DWM_SIG_MAX];     size_t isCandidateDfLen;
	unsigned char processDeviceLost[DWM_SIG_MAX]; size_t processDeviceLostLen; // for the device-lost hook
};

struct DwmProfile
{
	unsigned long long minVersion;                     // applies when dwmcore version >= this
	DwmSignatures sigs;                                // AOB signatures (embedded by value)
	int clipBoxOffset;                                 // per-monitor desktop origin (float RECT)
	// Device-lost gating: read DWM's internal device vector (CDeviceManager) to release our resources
	// ONLY when a device is actually flagged for deletion, so the every-frame ProcessDeviceLost hook
	// doesn't rebuild resources every frame. All are dwmcore-build-specific.
	unsigned int deviceVecFirstRva;   // .data addr of the device vector's _Myfirst (begin) pointer
	unsigned int deviceVecLastRva;    // .data addr of the device vector's _Mylast (end) pointer
	int deviceInfoStride;             // bytes per DeviceInfo element in that vector
	int deviceLostFlagOffset;         // offset in the device object; nonzero => flagged lost/about-to-erase
	// Fullscreen-overlay suppression: on this build, hook COverlayContext::OverlaysEnabled (forcing it
	// false for LUT contexts) to keep the LUT applied over fullscreen apps. It is installed via the
	// register-preserving asm thunk (OverlaysEnabled_thunk), because DWM's IsDFlipOnMPO relies on r8
	// surviving the call and a plain detour crashes it. Verified on 26100.8246 / 8655. A future build
	// that needs a different mechanism can set this false. (Not RE-derived data -- the thunk itself is
	// build-independent -- but a per-build switch so the decision lives with the rest of the profile.)
	bool overlaysEnabledThunk;
};

// Newest-first: SelectDwmProfile() returns the first row whose minVersion <= the running dwmcore.
// Each row is fully self-contained: version, the inline AOB signatures, then the offsets
// (clipBoxOffset, deviceVecFirstRva, deviceVecLastRva, deviceInfoStride, deviceLostFlagOffset).
// NOTE: signatures are embedded by value, so builds that share the same patterns repeat those bytes.
static const DwmProfile g_dwmProfiles[] = {
	// --- add newer dwmcore builds ABOVE (most-recent first) ---

	// Windows 11 25H2 - dwmcore 10.0.26100.8655   (signatures identical to 8246; device-vector globals moved)
	{
		DWM_VER(26100, 8655),
		{   // AOB signatures (inline)
			// COverlayContext::Present
			{ 0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D, 0x6C,
			  0x24, 0xF9, 0x48, 0x81, 0xEC, 0xF8, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x05,
			  '?', '?', '?', '?', 0x48, 0x33, 0xC4, 0x48, 0x89, 0x45, 0xEF, 0x4C, 0x8B, 0x65, '?', 0x48, 0x8B, 0xD9 }, 46,
			// COverlayContext::OverlaysEnabled
			{ 0x83, 0x3D, '?', '?', '?', '?', 0x05, 0x74, 0x09, 0x83, 0x79, 0x28, 0x01, 0x0F, 0x97, 0xC0, 0xC3 }, 17,
			// COverlayContext::IsCandidateDirectFlipCompatible
			{ 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48,
			  0x89, 0x78, 0x20, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x33, 0xDB }, 27,
			// CDeviceManager::ProcessDeviceLost (prologue ends in a build-specific lea rcx,[rip+rel32], wildcarded)
			{ 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x68, 0x18, 0x48, 0x89, 0x48, 0x08, 0x56,
			  0x57, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x40, 0x0F, 0x57, 0xC0, 0x48, 0x8D, 0x0D, '?', '?', '?', '?' }, 33,
		},
		0x7658, 0x3FAB78, 0x3FAB80, 0x10, 0x458,  // clipBox, vecFirst, vecLast, stride, flag
		true                                       // overlaysEnabledThunk (hook OverlaysEnabled via asm thunk)
	},

	// Windows 11 25H2 - dwmcore 10.0.26100.8246
	{
		DWM_VER(26100, 8246),
		{   // AOB signatures (inline)
			// COverlayContext::Present
			{ 0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57, 0x48, 0x8D, 0x6C,
			  0x24, 0xF9, 0x48, 0x81, 0xEC, 0xF8, 0x00, 0x00, 0x00, 0x48, 0x8B, 0x05,
			  '?', '?', '?', '?', 0x48, 0x33, 0xC4, 0x48, 0x89, 0x45, 0xEF, 0x4C, 0x8B, 0x65, '?', 0x48, 0x8B, 0xD9 }, 46,
			// COverlayContext::OverlaysEnabled
			{ 0x83, 0x3D, '?', '?', '?', '?', 0x05, 0x74, 0x09, 0x83, 0x79, 0x28, 0x01, 0x0F, 0x97, 0xC0, 0xC3 }, 17,
			// COverlayContext::IsCandidateDirectFlipCompatible
			{ 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48,
			  0x89, 0x78, 0x20, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x33, 0xDB }, 27,
			// CDeviceManager::ProcessDeviceLost (prologue ends in a build-specific lea rcx,[rip+rel32], wildcarded)
			{ 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x68, 0x18, 0x48, 0x89, 0x48, 0x08, 0x56,
			  0x57, 0x41, 0x56, 0x48, 0x83, 0xEC, 0x40, 0x0F, 0x57, 0xC0, 0x48, 0x8D, 0x0D, '?', '?', '?', '?' }, 33,
		},
		0x7658, 0x3FDA88, 0x3FDA90, 0x10, 0x458,  // clipBox, vecFirst, vecLast, stride, flag
		true                                       // overlaysEnabledThunk (hook OverlaysEnabled via asm thunk)
	},
};

static const DwmProfile* g_activeDwmProfile = NULL;
static unsigned char* g_dwmDeviceVecFirst = NULL; // (dwmcore base + profile->deviceVecFirstRva), or NULL
static unsigned char* g_dwmDeviceVecLast  = NULL;

static const DwmProfile* SelectDwmProfile(unsigned long long ver)
{
	const size_t n = sizeof(g_dwmProfiles) / sizeof(g_dwmProfiles[0]);
	if (n == 0) return NULL;
	if (ver == 0) return &g_dwmProfiles[0]; // version capture failed: assume the newest known profile
	for (size_t i = 0; i < n; i++)
		if (ver >= g_dwmProfiles[i].minVersion)
			return &g_dwmProfiles[i];
	return NULL;
}

bool isWindows11 = false;
bool isWindows11_23h2 = false;
bool isWindows11_24h2 = false;
bool isWindows11_25h2 = false;
unsigned long long g_dwmcoreVersion = 0; // (build<<32)|revision of the loaded dwmcore.dll

static int* g_pOverlayTestMode = NULL;

bool aob_match_inverse(const void* buf1, const void* mask, const int buf_len)
{
	for (int i = 0; i < buf_len; ++i)
	{
		if (((unsigned char*)buf1)[i] != ((unsigned char*)mask)[i] && ((unsigned char*)mask)[i] != '?')
		{
			return true;
		}
	}
	return false;
}

char shaders[] = R"(
    struct VS_INPUT {
	float2 pos : POSITION;
	float2 tex : TEXCOORD;
};

struct VS_OUTPUT {
	float4 pos : SV_POSITION;
	float2 tex : TEXCOORD;
};

Texture2D backBufferTex : register(t0);
Texture3D lutTex : register(t1);
SamplerState smp : register(s0);

Texture2D noiseTex : register(t2);
SamplerState noiseSmp : register(s1);

cbuffer ConstantBuffer : register(b0) {
    int lutSize;
    bool hdr;
};

static float3x3 scrgb_to_bt2100 = {
2939026994.L / 585553224375.L, 9255011753.L / 3513319346250.L,   173911579.L / 501902763750.L,
  76515593.L / 138420033750.L, 6109575001.L / 830520202500.L,    75493061.L / 830520202500.L,
  12225392.L / 93230009375.L, 1772384008.L / 2517210253125.L, 18035212433.L / 2517210253125.L,
};

static float3x3 bt2100_to_scrgb = {
 348196442125.L / 1677558947.L, -123225331250.L / 1677558947.L,  -15276242500.L / 1677558947.L,
-579752563250.L / 37238079773.L, 5273377093000.L / 37238079773.L,  -38864558125.L / 37238079773.L,
 -12183628000.L / 5369968309.L, -472592308000.L / 37589778163.L, 5256599974375.L / 37589778163.L,
};

static float m1 = 1305 / 8192.;
static float m2 = 2523 / 32.;
static float c1 = 107 / 128.;
static float c2 = 2413 / 128.;
static float c3 = 2392 / 128.;

float3 SampleLut(float3 index) {
	float3 tex = (index + 0.5) / lutSize;
	return lutTex.Sample(smp, tex).rgb;
}

// Adapted from https://doi.org/10.2312/egp.20211031
void barycentricWeight(float3 r, out float4 bary, out int3 vert2, out int3 vert3) {
	vert2 = int3(0, 0, 0); vert3 = int3(1, 1, 1);
	int3 c = r.xyz >= r.yzx;
	bool c_xy = c.x; bool c_yz = c.y; bool c_zx = c.z;
	bool c_yx = !c.x; bool c_zy = !c.y; bool c_xz = !c.z;
	bool cond;  float3 s = float3(0, 0, 0);
#define ORDER(X, Y, Z)                   \
            cond = c_ ## X ## Y && c_ ## Y ## Z; \
            s = cond ? r.X ## Y ## Z : s;        \
            vert2.X = cond ? 1 : vert2.X;        \
            vert3.Z = cond ? 0 : vert3.Z;
	ORDER(x, y, z)   ORDER(x, z, y)   ORDER(z, x, y)
		ORDER(z, y, x)   ORDER(y, z, x)   ORDER(y, x, z)
		bary = float4(1 - s.x, s.z, s.x - s.y, s.y - s.z);
}

float3 LutTransformTetrahedral(float3 rgb) {
	float3 lutIndex = rgb * (lutSize - 1);
	float4 bary; int3 vert2; int3 vert3;
	barycentricWeight(frac(lutIndex), bary, vert2, vert3);

	float3 base = floor(lutIndex);
	return bary.x * SampleLut(base) +
		bary.y * SampleLut(base + 1) +
		bary.z * SampleLut(base + vert2) +
		bary.w * SampleLut(base + vert3);
}

float3 pq_eotf(float3 e) {
	return pow(max((pow(e, 1 / m2) - c1), 0) / (c2 - c3 * pow(e, 1 / m2)), 1 / m1);
}

float3 pq_inv_eotf(float3 y) {
	return pow((c1 + c2 * pow(y, m1)) / (1 + c3 * pow(y, m1)), m2);
}

float3 OrderedDither(float3 rgb, float2 pos) {
	float3 low = floor(rgb * 255) / 255;
	float3 high = low + 1.0 / 255;

	float3 rgb_linear = pow(rgb,)" STRINGIFY(DITHER_GAMMA) R"();
	float3 low_linear = pow(low,)" STRINGIFY(DITHER_GAMMA) R"();
	float3 high_linear = pow(high,)" STRINGIFY(DITHER_GAMMA) R"();

	float noise = noiseTex.Sample(noiseSmp, pos / )" STRINGIFY(NOISE_SIZE) R"().x;
	float3 threshold = lerp(low_linear, high_linear, noise);

	return lerp(low, high, rgb_linear > threshold);
}

VS_OUTPUT VS(VS_INPUT input) {
	VS_OUTPUT output;
	output.pos = float4(input.pos, 0, 1);
	output.tex = input.tex;
	return output;
}

float4 PS(VS_OUTPUT input) : SV_TARGET{
	float3 sample = backBufferTex.Sample(smp, input.tex).rgb;

	if (hdr) {
		float3 hdr10_sample = pq_inv_eotf(saturate(mul(scrgb_to_bt2100, sample)));

		float3 hdr10_res = LutTransformTetrahedral(hdr10_sample);

		float3 scrgb_res = mul(bt2100_to_scrgb, pq_eotf(hdr10_res));

		return float4(scrgb_res, 1);
	}
	else {
		float3 res = LutTransformTetrahedral(sample);

		res = OrderedDither(res, input.pos.xy);

		return float4(res, 1);
	}
}
)";

struct lutData
{
	int left;
	int top;
	int size;
	bool isHdr;
	
	float* rawLut;
};

int numLuts;

lutData* luts;

bool ParseLUT(lutData* lut, char* filename)
{
	FILE* file = fopen(filename, "r");
	if (file == NULL) return false;

	char line[256];
	int lutSize;

	while (1)
	{
		if (!fgets(line, sizeof(line), file))
		{
			fclose(file);
			return false;
		}
		if (sscanf(line, "LUT_3D_SIZE%d", &lutSize) == 1)
		{
			break;
		}
	}

	float* rawLut = (float*)malloc(lutSize * lutSize * lutSize * 4 * sizeof(float));

	for (int b = 0; b < lutSize; b++)
	{
		for (int g = 0; g < lutSize; g++)
		{
			for (int r = 0; r < lutSize; r++)
			{
				while (1)
				{
					if (!fgets(line, sizeof(line), file))
					{
						fclose(file);

						return false;
					}
					
					if (line[0] == '#' || line[0] == '\n' || line[0] == '\r')
						continue;
					
					if ((line[0] >= '0' && line[0] <= '9') || line[0] == '-' || line[0] == '.')
					{
						float red, green, blue;

						if (sscanf(line, "%f %f %f", &red, &green, &blue) != 3)
						{
							fclose(file);

							return false;
						}
						LUT_ACCESS_INDEX(rawLut, b, g, r, 0, lutSize) = red;
						LUT_ACCESS_INDEX(rawLut, b, g, r, 1, lutSize) = green;
						LUT_ACCESS_INDEX(rawLut, b, g, r, 2, lutSize) = blue;
						LUT_ACCESS_INDEX(rawLut, b, g, r, 3, lutSize) = 1;

						break;
					}
				}
			}
		}
	}
	fclose(file);
	lut->size = lutSize;
	lut->rawLut = rawLut;
	return true;
}

bool AddLUTs(char* folder)
{
	WIN32_FIND_DATAA findData;

	char path[MAX_PATH];
	strcpy(path, folder);
	strcat(path, "\\*");
	HANDLE hFind = FindFirstFileA(path, &findData);
	if (hFind == INVALID_HANDLE_VALUE) return false;
	do
	{
		if (!(findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
		{
			char filePath[MAX_PATH];
			char* fileName = findData.cFileName;

			strcpy(filePath, folder);
			strcat(filePath, "\\");
			strcat(filePath, fileName);

			{
				lutData* _tmp = (lutData*)realloc(luts, (size_t)(numLuts + 1) * sizeof(*luts));
				if (!_tmp)
				{
					LOG_ONLY_ONCE("realloc failed while loading LUTs")
					FindClose(hFind);
					return false;
				}
				luts = _tmp;
			}
			lutData* lut = &luts[numLuts];
			if (sscanf(findData.cFileName, "%d_%d", &lut->left, &lut->top) == 2)
			{
				lut->isHdr = strstr(fileName, "hdr") != NULL;
				if (!ParseLUT(lut, filePath))
				{
					LOG_ONLY_ONCE("LUT could not be parsed")
					FindClose(hFind);
					return false;
				}
				numLuts++;
			}
		}
	}
	while (FindNextFileA(hFind, &findData) != 0);
	FindClose(hFind);
	return true;
}

int numLutTargets;
void** lutTargets;

bool IsLUTActive(void* target)
{
	for (int i = 0; i < numLutTargets; i++)
	{
		if (lutTargets[i] == target)
		{
			return true;
		}
	}
	return false;
}

void SetLUTActive(void* target)
{
	if (!IsLUTActive(target))
	{
		void** _tmp = (void**)realloc(lutTargets, (size_t)(numLutTargets + 1) * sizeof(*lutTargets));
		if (_tmp)
		{
			lutTargets = _tmp;
			lutTargets[numLutTargets++] = target;
		}
	}
}

void UnsetLUTActive(void* target)
{
	for (int i = 0; i < numLutTargets; i++)
	{
		if (lutTargets[i] == target)
		{
			lutTargets[i] = lutTargets[--numLutTargets];
			void** _tmp = (void**)realloc(lutTargets, (size_t)numLutTargets * sizeof(*lutTargets));
			if (_tmp || numLutTargets == 0) lutTargets = _tmp; // shrink; keep old block if realloc fails for nonzero size
			return;
		}
	}
}

// SEH-guarded reader for the composition object's coordinate fields (used to recover each
// monitor's desktop origin from self+0x7658). RectKind selects int vs float interpretation.
enum RectKind { RK_INT = 0, RK_FLOAT = 1 };
static std::mutex g_clipMutex;

static bool ReadRect(void* self, int baseSel, int off, RectKind kind, int out[4])
{
    __try
    {
        unsigned char* base = (unsigned char*)self;
        if (baseSel == 1)
        {
            void* a = *(void**)self;
            if (!a) return false;
            base = (unsigned char*)a;
        }
        if (kind == RK_INT)
        {
            int* r = (int*)(base + off);
            out[0] = r[0]; out[1] = r[1]; out[2] = r[2]; out[3] = r[3];
        }
        else
        {
            float* r = (float*)(base + off);
            out[0] = (int)r[0]; out[1] = (int)r[1]; out[2] = (int)r[2]; out[3] = (int)r[3];
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return false;
    }
}

// Strict 1:1 context<->monitor-origin ownership. A context that resolves to an origin already
// owned by a DIFFERENT context is rejected (returns NULL -> untouched passthrough) rather than
// being handed another monitor's LUT. Self-heals on layout change. Guarded by g_clipMutex.
static std::map<unsigned long long, void*> g_positionOwner;

static bool ClaimPosition(int left, int top, void* ctx)
{
	unsigned long long key = ((unsigned long long)(unsigned int)left << 32) | (unsigned int)top;
	std::lock_guard<std::mutex> lk(g_clipMutex);
	// drop stale claims this ctx previously held for other origins (monitor moved)
	for (auto it = g_positionOwner.begin(); it != g_positionOwner.end();)
	{
		if (it->second == ctx && it->first != key) it = g_positionOwner.erase(it);
		else ++it;
	}
	auto it = g_positionOwner.find(key);
	if (it == g_positionOwner.end()) { g_positionOwner[key] = ctx; return true; }
	return it->second == ctx;
}

lutData* GetLUTDataFromCOverlayContext(void* context, bool hdr, int* out_index)
{
	if (out_index) *out_index = -1;
	if (!context) return NULL;

	if (g_pOverlayTestMode != NULL)
		*g_pOverlayTestMode = 5;

	// Resolve the monitor's desktop origin. The DeviceClipBox location and interpretation differ per
	// Windows build, so each branch reads a different offset via the SEH-guarded ReadRect (returns false
	// on a bad read). 25H2 uses the verified 26100.8246 layout; the older branches mirror the ed1ii base.
	int left = 0, top = 0;
	int r[4];
	bool gotCoords = false;

	if (isWindows11_25h2) // Windows 11 25H2 and all newer generations (26H2, 27H2, ...)
	{
		// Clip-box offset comes from the dwmcore-version profile chosen at startup (see g_dwmProfiles).
		// If no profile matched (unknown/older 25H2 binary) skip rather than fall to a legacy offset.
		// A rare version-capture failure falls back to the newest profile (see SelectDwmProfile).
		if (g_activeDwmProfile == NULL)
			return NULL;
		if (ReadRect(context, 0, g_activeDwmProfile->clipBoxOffset, RK_FLOAT, r))
		{
			left = r[0]; top = r[1];
			gotCoords = true;
		}
	}
	else if (isWindows11_24h2)
	{
		// 24H2: DeviceClipBox at *(void**)self + 0x53E8 (float RECT).
		if (ReadRect(context, 1, COverlayContext_DeviceClipBox_offset_w11_24h2, RK_FLOAT, r))
		{
			left = r[0]; top = r[1];
			if (left == 0 && top == 0 && (r[2] != 0 || r[3] != 0)) { left = r[2]; top = r[3]; }
			gotCoords = true;
		}
	}
	else if (isWindows11_23h2 || isWindows11)
	{
		// 23H2 / Windows 11: DeviceClipBox at *(void**)self + 0x466C (float RECT).
		if (ReadRect(context, 1, COverlayContext_DeviceClipBox_offset_w11, RK_FLOAT, r))
		{
			left = r[0]; top = r[1];
			if (left == 0 && top == 0 && (r[2] != 0 || r[3] != 0)) { left = r[2]; top = r[3]; }
			gotCoords = true;
		}
	}
	else
	{
		// Windows 10 / older: DeviceClipBox at *(void**)self - 0x120 (float RECT).
		if (ReadRect(context, 1, COverlayContext_DeviceClipBox_offset, RK_FLOAT, r))
		{
			left = r[0]; top = r[1];
			gotCoords = true;
		}
	}

	// If the origin can't be read, skip this frame rather than guess (avoids applying a wrong LUT).
	if (!gotCoords)
		return NULL;

	// Defensive 1:1 ownership: a context resolving to an origin already owned by another is skipped.
	if (!ClaimPosition(left, top, context))
		return NULL;

	// Exact match only: an HDR context takes an HDR LUT, an SDR context takes an SDR LUT. If the only LUT
	// for this monitor is the wrong type for its current mode, apply nothing (correct-or-nothing) rather
	// than run a LUT through the mismatched shader path, which produces wrong colors in either direction.
	for (int i = 0; i < numLuts; i++)
		if (luts[i].left == left && luts[i].top == top && luts[i].isHdr == hdr)
		{
			if (out_index) *out_index = i;
			return &luts[i];
		}

	return NULL;
}

// ---- relocated worker/builder block (moved below shaders/lutData/luts so its dependencies resolve) ----
// ---- builders run ONLY on the worker; throwing leaves ready=false -> frames safely skipped ----
static void BuildAdapterAssets(AdapterAssets* a, ID3D11Device* dev)
{
	a->device = dev;
	dev->GetImmediateContext(&a->context);

	ID3DBlob* vsBlob = nullptr; ID3DBlob* err = nullptr;
	HR_OR_THROW(D3DCompile(shaders, sizeof shaders, NULL, NULL, NULL, "VS", "vs_5_0", 0, 0, &vsBlob, &err));
	HR_OR_THROW(dev->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), NULL, &a->vs));
	D3D11_INPUT_ELEMENT_DESC ied[] = {
		{"POSITION", 0, DXGI_FORMAT_R32G32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0},
		{"TEXCOORD", 0, DXGI_FORMAT_R32G32_FLOAT, 0, D3D11_APPEND_ALIGNED_ELEMENT, D3D11_INPUT_PER_VERTEX_DATA, 0}
	};
	HR_OR_THROW(dev->CreateInputLayout(ied, ARRAYSIZE(ied), vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), &a->inputLayout));
	vsBlob->Release();

	ID3DBlob* psBlob = nullptr;
	HR_OR_THROW(D3DCompile(shaders, sizeof shaders, NULL, NULL, NULL, "PS", "ps_5_0", 0, 0, &psBlob, &err));
	HR_OR_THROW(dev->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), NULL, &a->ps));
	psBlob->Release();

	D3D11_SAMPLER_DESC sd = {};
	sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
	sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
	sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
	HR_OR_THROW(dev->CreateSamplerState(&sd, &a->sampler));

	a->lutCount = 0;
	for (int i = 0; i < numLuts && i < 64; i++)
	{
		lutData* lut = &luts[i];
		if (!lut->rawLut) { a->lutCount = i + 1; continue; }
		D3D11_TEXTURE3D_DESC td = {};
		td.Width = lut->size; td.Height = lut->size; td.Depth = lut->size;
		td.MipLevels = 1; td.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
		td.Usage = D3D11_USAGE_IMMUTABLE; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
		D3D11_SUBRESOURCE_DATA init;
		init.pSysMem = lut->rawLut;
		init.SysMemPitch = lut->size * 4 * sizeof(float);
		init.SysMemSlicePitch = lut->size * lut->size * 4 * sizeof(float);
		ComPtr<ID3D11Texture3D> tex;
		HR_OR_THROW(dev->CreateTexture3D(&td, &init, &tex));
		HR_OR_THROW(dev->CreateShaderResourceView(tex.Get(), NULL, &a->lutSrv[i]));
		a->lutCount = i + 1;
	}

	D3D11_SAMPLER_DESC nsd = {};
	nsd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
	nsd.AddressU = nsd.AddressV = nsd.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
	nsd.ComparisonFunc = D3D11_COMPARISON_NEVER;
	HR_OR_THROW(dev->CreateSamplerState(&nsd, &a->noiseSampler));

	D3D11_TEXTURE2D_DESC ntd = {};
	ntd.Width = NOISE_SIZE; ntd.Height = NOISE_SIZE; ntd.MipLevels = 1; ntd.ArraySize = 1;
	ntd.Format = DXGI_FORMAT_R32_FLOAT; ntd.SampleDesc.Count = 1;
	ntd.Usage = D3D11_USAGE_IMMUTABLE; ntd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	float noise[NOISE_SIZE][NOISE_SIZE];
	for (int i = 0; i < NOISE_SIZE; i++) for (int j = 0; j < NOISE_SIZE; j++) noise[i][j] = (noiseBytes[i][j] + 0.5f) / 256;
	D3D11_SUBRESOURCE_DATA ninit; ninit.pSysMem = noise; ninit.SysMemPitch = sizeof(noise[0]);
	ComPtr<ID3D11Texture2D> ntex;
	HR_OR_THROW(dev->CreateTexture2D(&ntd, &ninit, &ntex));
	HR_OR_THROW(dev->CreateShaderResourceView(ntex.Get(), NULL, &a->noiseSrv));

	a->ready.store(true);
	{ char b[96]; sprintf(b, "adapter assets built luid=0x%llX luts=%d", a->luidKey, a->lutCount); diag_log(b); }
}

static void BuildOutputRes(OutputRes* o, ID3D11Device* dev, int index, UINT w, UINT h, DXGI_FORMAT fmt)
{
	bool deviceChanged = (o->ownerDevice.Get() != dev);
	if (deviceChanged)
	{
		for (int i = 0; i < o->rtvCount; i++) { o->rtvCache[i].rtv.Reset(); o->rtvCache[i].key = nullptr; }
		o->rtvCount = 0;
		for (int i = 0; i < 2; i++) { o->scratch[i].Reset(); o->scratchSrv[i].Reset(); o->scratchDesc[i] = {}; o->lastLutSize[i] = -1; }
		o->ownerDevice = dev;

		D3D11_BUFFER_DESC vb = {};
		vb.ByteWidth = 4 * sizeof(float) * 4; vb.Usage = D3D11_USAGE_DYNAMIC;
		vb.BindFlags = D3D11_BIND_VERTEX_BUFFER; vb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
		o->vertexBuffer.Reset();
		HR_OR_THROW(dev->CreateBuffer(&vb, NULL, &o->vertexBuffer));

		D3D11_BUFFER_DESC cb = {};
		cb.ByteWidth = 16; cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
		cb.Usage = D3D11_USAGE_DYNAMIC; cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
		o->constantBuffer.Reset();
		HR_OR_THROW(dev->CreateBuffer(&cb, NULL, &o->constantBuffer));
	}

	if (index >= 0 && index < 2)
	{
		UINT nw = max(w, o->scratchDesc[index].Width);
		UINT nh = max(h, o->scratchDesc[index].Height);
		D3D11_TEXTURE2D_DESC td = {};
		td.Width = nw; td.Height = nh; td.MipLevels = 1; td.ArraySize = 1;
		td.Format = fmt; td.SampleDesc.Count = 1;
		td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
		ComPtr<ID3D11Texture2D> tex; ComPtr<ID3D11ShaderResourceView> srv;
		HR_OR_THROW(dev->CreateTexture2D(&td, NULL, &tex));
		HR_OR_THROW(dev->CreateShaderResourceView(tex.Get(), NULL, &srv));
		o->scratch[index] = tex; o->scratchSrv[index] = srv; o->scratchDesc[index] = td;
	}
	o->ready.store(true);
}

// ---- Synchronous, double-guarded builders (replaces the deferred-worker path) ----------------
// The async worker left some adapters permanently stuck in its queue. Per-adapter build is
// one-time and cheap, so we build inline and cache. Two guards: the C++ layer turns an
// HR_OR_THROW failure into a logged false; the SEH layer contains any access violation from a
// bad device (raw-pointer function -> __try is legal). A failed adapter is cached as not-ready
// so it is skipped, not retried every frame.
static bool BuildAdapterAssets_Cpp(AdapterAssets* a, ID3D11Device* dev)
{
	try { BuildAdapterAssets(a, dev); return true; }
	catch (const std::exception& e) { char b[256]; sprintf(b, "adapter build threw: %s", e.what()); diag_log(b); return false; }
	catch (...) { diag_log("adapter build threw: unknown"); return false; }
}
static bool BuildAdapterAssets_Guarded(AdapterAssets* a, ID3D11Device* dev)
{
	__try { return BuildAdapterAssets_Cpp(a, dev); }
	__except (EXCEPTION_EXECUTE_HANDLER) { diag_log("SEH during adapter build -> skip adapter"); return false; }
}
static bool BuildOutputRes_Cpp(OutputRes* o, ID3D11Device* dev, int index, UINT w, UINT h, DXGI_FORMAT fmt)
{
	try { BuildOutputRes(o, dev, index, w, h, fmt); return true; }
	catch (const std::exception& e) { char b[256]; sprintf(b, "output build threw: %s", e.what()); diag_log(b); return false; }
	catch (...) { diag_log("output build threw: unknown"); return false; }
}
static bool BuildOutputRes_Guarded(OutputRes* o, ID3D11Device* dev, int index, UINT w, UINT h, DXGI_FORMAT fmt)
{
	__try { return BuildOutputRes_Cpp(o, dev, index, w, h, fmt); }
	__except (EXCEPTION_EXECUTE_HANDLER) { diag_log("SEH during output build -> skip frame"); return false; }
}

static AdapterAssets* FindOrRequestAdapter(ID3D11Device* dev)
{
	std::lock_guard<std::mutex> lk(g_adaptersMutex);
	auto it = g_adapters.find(dev);
	if (it != g_adapters.end()) return it->second->ready.load() ? it->second : nullptr;

	// A device we haven't seen is cached ALONGSIDE any existing ones. On a hybrid / multi-GPU system
	// DWM legitimately composites on more than one device at once (one per adapter), so we must NOT
	// treat "a different device appeared" as a device swap and evict the others -- doing that caused a
	// per-frame evict/rebuild thrash as compositing alternated between adapters (huge wasted shader/
	// texture rebuilds -> stutter, and dropped LUTs on the adapter not being serviced that frame).
	// Genuine device teardown is handled separately by the ProcessDeviceLost hook (EvictAllAssets on
	// DWM's own device-lost flag), which is the only correct signal that a device is actually going away.
	AdapterAssets* a = new AdapterAssets();
	GetDeviceLuidKey(dev, &a->luidKey);
	{ char b[96]; sprintf(b, "new device instance %p luid=0x%llX -> building", (void*)dev, a->luidKey); diag_log(b); }
	g_adapters[dev] = a;                         // cache before building so a failure isn't retried every frame
	BuildAdapterAssets_Guarded(a, dev);          // synchronous; sets a->ready and logs "adapter assets built"
	return a->ready.load() ? a : nullptr;
}

// Returns a ready OutputRes valid on `dev` and covering (index,w,h,fmt); else requests build and returns null.
static OutputRes* FindOrRequestOutput(void* self, ID3D11Device* dev, int index, UINT w, UINT h, DXGI_FORMAT fmt)
{
	std::lock_guard<std::mutex> lk(g_outputsMutex);
	OutputRes* o;
	auto it = g_outputs.find(self);
	if (it == g_outputs.end()) { o = new OutputRes(); g_outputs[self] = o; }
	else o = it->second;

	bool ok = o->ready.load() && o->ownerDevice.Get() == dev &&
	          o->scratch[index] && o->scratchDesc[index].Width >= w &&
	          o->scratchDesc[index].Height >= h && o->scratchDesc[index].Format == fmt;
	if (ok) return o;

	// Build synchronously on the present thread (create-once, cached). No deferral, no queue stall.
	if (!BuildOutputRes_Guarded(o, dev, index, w, h, fmt)) { o->ready.store(false); return nullptr; }
	return o->ready.load() ? o : nullptr;
}

void UninitializeStuff()
{
	{
		std::lock_guard<std::mutex> lk(g_outputsMutex);
		for (auto& p : g_outputs) delete p.second;  // ComPtr members auto-release
		g_outputs.clear();
	}
	{
		std::lock_guard<std::mutex> lk(g_adaptersMutex);
		for (auto& p : g_adapters) delete p.second;
		g_adapters.clear();
	}
	{
		std::lock_guard<std::mutex> lk(g_clipMutex);
		g_positionOwner.clear();
	}
	for (int i = 0; i < numLuts; i++)
		if (luts[i].rawLut) { free(luts[i].rawLut); luts[i].rawLut = NULL; }
	free(luts);
	free(lutTargets);
}

bool RenderLUT(void* self, ID3D11Texture2D* backBuffer, struct tagRECT* rects, int numRects,
               AdapterAssets* a, ID3D11Device* dev)
{
	if (!a || !dev || !backBuffer) return false;
	if (!ResourceOnDevice(backBuffer, dev)) return false; // cross-instance/adapter guard

	D3D11_TEXTURE2D_DESC bbDesc;
	backBuffer->GetDesc(&bbDesc);

	int index = -1;
	if (bbDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM || bbDesc.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
	    bbDesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || bbDesc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
	    bbDesc.Format == DXGI_FORMAT_R10G10B10A2_UNORM)
		index = 0;
	else if (bbDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT)
	{
		index = 1;
	}
	if (index == -1) return false;

	int lutIdx = 0; lutData* lut;
	if (!(lut = GetLUTDataFromCOverlayContext(self, index == 1, &lutIdx))) return false;
	if (lutIdx < 0 || lutIdx >= a->lutCount) return false;
	ID3D11ShaderResourceView* lutSrv = a->lutSrv[lutIdx].Get();
	if (!lutSrv || !ResourceOnDevice(lutSrv, dev)) return false; // explicit: LUT texture belongs to this device

	OutputRes* o = FindOrRequestOutput(self, dev, index, bbDesc.Width, bbDesc.Height, bbDesc.Format);
	if (!o) return false; // building/not-ready -> skip this frame (NO allocation on render thread)

	std::lock_guard<std::mutex> ctxLock(a->ctxMutex);

	// Re-validate everything under the lock; if anything is stale, bail (no inline build).
	if (o->ownerDevice.Get() != dev || !o->scratch[index] || !o->scratchSrv[index] ||
	    !o->vertexBuffer || !o->constantBuffer) return false;
	if (!ResourceOnDevice(o->scratch[index].Get(), dev)) return false;

	// RTV cache (tiny view objects; cached per backbuffer, evict-oldest). Not the leak source.
	ID3D11RenderTargetView* rtv = nullptr;
	for (int i = 0; i < o->rtvCount; i++)
		if (o->rtvCache[i].key == backBuffer) { rtv = o->rtvCache[i].rtv.Get(); break; }
	if (!rtv)
	{
		ComPtr<ID3D11RenderTargetView> newRtv;
		if (FAILED(dev->CreateRenderTargetView(backBuffer, NULL, &newRtv))) return false;
		if (o->rtvCount >= 8)
		{
			for (int i = 0; i < 7; i++) o->rtvCache[i] = o->rtvCache[i + 1];
			o->rtvCount--;
		}
		o->rtvCache[o->rtvCount].key = backBuffer;
		o->rtvCache[o->rtvCount].rtv = newRtv;
		rtv = newRtv.Get();
		o->rtvCount++;
	}

	ID3D11DeviceContext* ctx = a->context.Get();
	const D3D11_VIEWPORT vp(0, 0, (float)bbDesc.Width, (float)bbDesc.Height, 0.0f, 1.0f);
	ctx->RSSetViewports(1, &vp);
	ctx->OMSetRenderTargets(1, &rtv, NULL);
	ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
	ctx->IASetInputLayout(a->inputLayout.Get());
	ctx->VSSetShader(a->vs.Get(), NULL, 0);
	ctx->PSSetShader(a->ps.Get(), NULL, 0);
	ID3D11SamplerState* smps[2] = { a->sampler.Get(), a->noiseSampler.Get() };
	ctx->PSSetSamplers(0, 1, &smps[0]);
	ctx->PSSetSamplers(1, 1, &smps[1]);
	ID3D11ShaderResourceView* srv0 = o->scratchSrv[index].Get();
	ID3D11ShaderResourceView* srv2 = a->noiseSrv.Get();
	ctx->PSSetShaderResources(0, 1, &srv0);
	ctx->PSSetShaderResources(1, 1, &lutSrv);
	ctx->PSSetShaderResources(2, 1, &srv2);

	if (o->lastLutSize[index] != lut->size)
	{
		int cdata[4] = { lut->size, index == 1, 0, 0 };
		D3D11_MAPPED_SUBRESOURCE mr;
		if (FAILED(ctx->Map(o->constantBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mr))) return false;
		memcpy(mr.pData, cdata, sizeof(cdata));
		ctx->Unmap(o->constantBuffer.Get(), 0);
		o->lastLutSize[index] = lut->size;
	}
	ID3D11Buffer* cbuf = o->constantBuffer.Get();
	ctx->PSSetConstantBuffers(0, 1, &cbuf);

	D3D11_BOX box; box.left = 0; box.top = 0; box.front = 0;
	box.right = bbDesc.Width; box.bottom = bbDesc.Height; box.back = 1;
	ctx->CopySubresourceRegion(o->scratch[index].Get(), 0, 0, 0, 0, backBuffer, 0, &box);

	UINT stride = 4 * sizeof(float), offset = 0;
	for (int i = 0; i < numRects; i++)
	{
		tagRECT* rect = &rects[i];
		float bw = (float)bbDesc.Width, bh = (float)bbDesc.Height;
		float l = rect->left / bw * 2 - 1, t = rect->top / bh * -2 + 1;
		float r = rect->right / bw * 2 - 1, b = rect->bottom / bh * -2 + 1;
		float tw = (float)o->scratchDesc[index].Width, th = (float)o->scratchDesc[index].Height;
		float tl = rect->left / tw, tt = rect->top / th, tr = rect->right / tw, tb = rect->bottom / th;
		float vtx[] = { l, b, tl, tb,  l, t, tl, tt,  r, b, tr, tb,  r, t, tr, tt };
		D3D11_MAPPED_SUBRESOURCE mr;
		if (FAILED(ctx->Map(o->vertexBuffer.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mr))) return false;
		memcpy(mr.pData, vtx, sizeof(vtx));
		ctx->Unmap(o->vertexBuffer.Get(), 0);
		ID3D11Buffer* vb = o->vertexBuffer.Get();
		ctx->IASetVertexBuffers(0, 1, &vb, &stride, &offset);
		ctx->Draw(4, 0);
	}
	return true;
}

// Raw-pointer SEH boundary. This function owns NO C++ objects that require unwinding, so __try is
// legal here (fixes C2712: ApplyLUT/ApplyLUTDirect own ComPtr locals and therefore cannot host __try).
// On any SEH we latch the hook inert, so no later frame re-enters -- a lock held during a hardware
// fault can never deadlock a subsequent present; DWM simply composites without the LUT this session.
static bool RenderLUT_Guarded(void* self, ID3D11Texture2D* backBuffer, struct tagRECT* rects, int numRects,
                              AdapterAssets* a, ID3D11Device* dev)
{
	__try
	{
		return RenderLUT(self, backBuffer, rects, numRects, a, dev);
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		diag_log("SEH inside RenderLUT -> latching hook inert");
		g_hookInert.store(true);
		return false;
	}
}

// Untrusted-swapchain-safe device fetch. The Present-hook fallback scans DWM object memory for a
// candidate IDXGISwapChain*, so the pointer may not be a COM object at all (the dump showed a candidate
// whose first qword -- the "vtable" -- was the value 2, giving call [2+0x38] -> AV). Validate the vtable
// is readable, then make the virtual GetDevice call under SEH. Raw pointers only -> __try is legal.
static bool SafeGetDeviceFromSwapChain(IDXGISwapChain* sc, ID3D11Device** outDev)
{
	*outDev = NULL;
	if (!sc || IsBadReadPtr(sc, sizeof(void*))) return false;
	__try
	{
		void* vtbl = *(void**)sc;
		if (!vtbl || IsBadReadPtr(vtbl, sizeof(void*) * 8)) return false; // reject non-code "vtables" such as 2
		HRESULT hr = sc->GetDevice(IID_ID3D11Device, (void**)outDev);
		if (FAILED(hr) || !*outDev) { *outDev = NULL; return false; }
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		*outDev = NULL;
		return false;
	}
}

bool ApplyLUT(void* self, IDXGISwapChain* swapChain, struct tagRECT* rects, int numRects)
{
	if (g_hookInert.load() || !swapChain) return false;

	// Do NOT call swapChain->GetDevice() directly: the hook may hand us a non-COM candidate pointer.
	ID3D11Device* devRaw = NULL;
	if (!SafeGetDeviceFromSwapChain(swapChain, &devRaw)) return false; // bad/foreign/non-COM -> skip safely
	ComPtr<ID3D11Device> dev;
	dev.Attach(devRaw); // take ownership of the ref GetDevice added

	AdapterAssets* a = FindOrRequestAdapter(dev.Get());
	if (!a) return false; // adapter assets unavailable (build failed) -> skip cleanly

	ComPtr<ID3D11Texture2D> backBuffer;
	if (FAILED(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) || !backBuffer) return false;

	bool result = RenderLUT_Guarded(self, backBuffer.Get(), rects, numRects, a, dev.Get());
	return result;
}

bool ApplyLUTDirect(void* self, ID3D11Texture2D* backBuffer, struct tagRECT* rects, int numRects)
{
	if (g_hookInert.load() || !backBuffer) return false;
	ComPtr<ID3D11Device> dev;
	backBuffer->GetDevice(&dev);
	if (!dev) return false;

	AdapterAssets* a = FindOrRequestAdapter(dev.Get());
	if (!a) return false;

	bool result = RenderLUT_Guarded(self, backBuffer, rects, numRects, a, dev.Get());
	return result;
}

typedef struct rectVec
{
	struct tagRECT* start;
	struct tagRECT* end;
	struct tagRECT* cap;
} rectVec;

typedef long (COverlayContext_Present_t)(void*, void*, unsigned int, rectVec*, unsigned int, bool);
typedef long long (COverlayContext_Present_24h2_t)(void*, void*, unsigned int, rectVec*, int, void*, bool);

static ID3D11Texture2D* GetBackBuffer_25H2(void* overlaySwapChain)
{
	__try
	{
		if (!overlaySwapChain) return NULL;

		void** vt = *(void***)overlaySwapChain;
		if (!vt) return NULL;

		typedef void* (__fastcall *VirtFunc)(void*);

		VirtFunc func1 = (VirtFunc)vt[24];
		if (!func1) return NULL;

		void* r1 = func1(overlaySwapChain);
		if (!r1) return NULL;

		void** vt2 = *(void***)r1;
		if (!vt2) return NULL;

		VirtFunc func2 = (VirtFunc)vt2[19];
		if (!func2) return NULL;

		void* r2 = func2(r1);
		if (!r2) return NULL;

		ID3D11Texture2D* tex = NULL;
		HRESULT hr = ((IUnknown*)r2)->QueryInterface(IID_ID3D11Texture2D, (void**)&tex);
		if (FAILED(hr) || !tex) return NULL;

		// Reject implausible surfaces (wrong vtable index on a serviced build); caller then
		// falls back to the dynamic swap-chain-offset path rather than corrupting a stray texture.
		D3D11_TEXTURE2D_DESC d; tex->GetDesc(&d);
		if (d.Width < 64 || d.Height < 64 || d.Width > 16384 || d.Height > 16384 ||
		    !(d.BindFlags & D3D11_BIND_RENDER_TARGET))
		{
			tex->Release();
			return NULL;
		}

		LOG_ONLY_ONCE("25H2: Got texture via overlaySwapChain->vt[24]()->vt2[19]()->QI")
		return tex;
	}
	__except (EXCEPTION_EXECUTE_HANDLER)
	{
		return NULL;
	}
}

COverlayContext_Present_t* COverlayContext_Present_orig = NULL;
COverlayContext_Present_t* COverlayContext_Present_real_orig = NULL;

COverlayContext_Present_24h2_t* COverlayContext_Present_orig_24h2 = NULL;
COverlayContext_Present_24h2_t* COverlayContext_Present_real_orig_24h2 = NULL;

long long COverlayContext_Present_hook_24h2(void* self, void* overlaySwapChain, unsigned int a3, rectVec* rectVec,
	int a5, void* a6, bool a7)
{
	if (_ReturnAddress() < (void*)COverlayContext_Present_real_orig_24h2 || isWindows11_24h2 || isWindows11_25h2)
	{
			LOG_ONLY_ONCE("I am inside COverlayContext::Present hook inside the main if condition")
			std::stringstream overlay_swapchain_message;
			overlay_swapchain_message << "OverlaySwapChain address: 0x" << std::hex << overlaySwapChain
				<< " -- windows 11 25h2: " << isWindows11_25h2
				<< " -- windows 11 24h2: " << isWindows11_24h2
				<< " -- " << "windows 11: " << isWindows11;
			LOG_ONLY_ONCE(overlay_swapchain_message.str().c_str())

			if (isWindows11_25h2)
			{

				bool success = false;

				ID3D11Texture2D* backBuffer = GetBackBuffer_25H2(overlaySwapChain);
				if (backBuffer)
				{
					if (ApplyLUTDirect(self, backBuffer, rectVec->start, (int)(rectVec->end - rectVec->start)))
					{
						SetLUTActive(self);
						success = true;
					}
					backBuffer->Release();
				}

					// NOTE: swapchain-offset scan removed. It called GetDevice on arbitrary non-swapchain
					// pointers, which destabilized DWM composition (confirmed crash source on 26100.8246).
					// If GetBackBuffer_25H2 fails, we skip the frame for this monitor instead of scanning.

				if (!success)
				{
					UnsetLUTActive(self);
				}
			}
			else
			{

			bool hwProtected = false;
			if (isWindows11_24h2)
				hwProtected = *((bool*)overlaySwapChain + IOverlaySwapChain_HardwareProtected_offset_w11_24h2);
			else if (isWindows11)
				hwProtected = *((bool*)overlaySwapChain + IOverlaySwapChain_HardwareProtected_offset_w11);
			else
				hwProtected = *((bool*)overlaySwapChain + IOverlaySwapChain_HardwareProtected_offset);

			if (hwProtected)
			{
				LOG_ONLY_ONCE("Hardware protected - unsetting LUT active")
				UnsetLUTActive(self);
			}
			else
			{
				IDXGISwapChain* swapChain = NULL;

				if (isWindows11_24h2)
				{
					LOG_ONLY_ONCE("Gathering IDXGISwapChain pointer")
					swapChain = *(IDXGISwapChain**)((unsigned char*)overlaySwapChain +
						IOverlaySwapChain_IDXGISwapChain_offset_w11_24h2);
				}
				else if (isWindows11)
				{
					LOG_ONLY_ONCE("Gathering IDXGISwapChain pointer")
					int sub_from_legacy_swapchain = *(int*)((unsigned char*)overlaySwapChain - 4);
					void* real_overlay_swap_chain = (unsigned char*)overlaySwapChain - sub_from_legacy_swapchain -
						0x1b0;
					swapChain = *(IDXGISwapChain**)((unsigned char*)real_overlay_swap_chain +
						IOverlaySwapChain_IDXGISwapChain_offset_w11);
				}
				else
				{
					swapChain = *(IDXGISwapChain**)((unsigned char*)overlaySwapChain +
						IOverlaySwapChain_IDXGISwapChain_offset);
				}

				if (swapChain != NULL && ApplyLUT(self, swapChain, rectVec->start, (int)(rectVec->end - rectVec->start)))
				{
					LOG_ONLY_ONCE("Setting LUTactive")
					SetLUTActive(self);
				}
				else
				{
					LOG_ONLY_ONCE("Un-setting LUTactive")
					UnsetLUTActive(self);
				}
			}
			}
	}

	return COverlayContext_Present_orig_24h2(self, overlaySwapChain, a3, rectVec, a5, a6, a7);
}

long COverlayContext_Present_hook(void* self, void* overlaySwapChain, unsigned int a3, rectVec* rectVec,
                                  unsigned int a5, bool a6)
{
	if (_ReturnAddress() < (void*)COverlayContext_Present_real_orig)
	{
		LOG_ONLY_ONCE("I am inside COverlayContext::Present hook inside the main if condition")

		bool hwProtected = false;
		if (isWindows11)
			hwProtected = *((bool*)overlaySwapChain + IOverlaySwapChain_HardwareProtected_offset_w11);
		else
			hwProtected = *((bool*)overlaySwapChain + IOverlaySwapChain_HardwareProtected_offset);

		if (hwProtected)
		{
			LOG_ONLY_ONCE("Hardware protected - unsetting LUT active")
			UnsetLUTActive(self);
		}
		else
		{
			IDXGISwapChain* swapChain;

			if (isWindows11)
			{
				LOG_ONLY_ONCE("Gathering IDXGISwapChain pointer")
				int sub_from_legacy_swapchain = *(int*)((unsigned char*)overlaySwapChain - 4);
				void* real_overlay_swap_chain = (unsigned char*)overlaySwapChain - sub_from_legacy_swapchain -
					0x1b0;
				swapChain = *(IDXGISwapChain**)((unsigned char*)real_overlay_swap_chain +
					IOverlaySwapChain_IDXGISwapChain_offset_w11);
			}
			else
			{
				swapChain = *(IDXGISwapChain**)((unsigned char*)overlaySwapChain +
					IOverlaySwapChain_IDXGISwapChain_offset);
			}

			if (ApplyLUT(self, swapChain, rectVec->start, (int)(rectVec->end - rectVec->start)))
			{
				LOG_ONLY_ONCE("Setting LUTactive")
				SetLUTActive(self);
			}
			else
			{
				LOG_ONLY_ONCE("Un-setting LUTactive")
				UnsetLUTActive(self);
			}
		}
	}

	return COverlayContext_Present_orig(self, overlaySwapChain, a3, rectVec, a5, a6);
}

typedef bool (CWindowContext_IsCandidateDirectFlipCompatible_t)(void*, void*, bool);
CWindowContext_IsCandidateDirectFlipCompatible_t* CWindowContext_IsCandidateDirectFlipCompatible_orig = NULL;

bool CWindowContext_IsCandidateDirectFlipCompatible_hook(void* self, void* a2, bool a3)
{
	if (numLuts > 0)
	{
		return false;
	}
	return CWindowContext_IsCandidateDirectFlipCompatible_orig(self, a2, a3);
}

typedef bool (CCompSwapChain_IsCandidateDirectFlipCompatible_t)(void*, void*, bool);
CCompSwapChain_IsCandidateDirectFlipCompatible_t* CCompSwapChain_IsCandidateDirectFlipCompatible_orig = NULL;

bool CCompSwapChain_IsCandidateDirectFlipCompatible_hook(void* self, void* a2, bool a3)
{
	if (numLuts > 0)
	{
		return false;
	}
	return CCompSwapChain_IsCandidateDirectFlipCompatible_orig(self, a2, a3);
}

typedef bool (CCompVisual_IsCandidateForPromotion_t)(void*, void*, void*);
CCompVisual_IsCandidateForPromotion_t* CCompVisual_IsCandidateForPromotion_orig = NULL;

bool CCompVisual_IsCandidateForPromotion_hook(void* self, void* a2, void* a3)
{
	if (numLuts > 0)
	{
		return false;
	}
	return CCompVisual_IsCandidateForPromotion_orig(self, a2, a3);
}

typedef bool (CCompSwapChain_IsCandidateIndependentFlipCompatible_t)(void*);
CCompSwapChain_IsCandidateIndependentFlipCompatible_t* CCompSwapChain_IsCandidateIndependentFlipCompatible_orig = NULL;

bool CCompSwapChain_IsCandidateIndependentFlipCompatible_hook(void* self)
{
	if (numLuts > 0)
	{
		return false;
	}
	return CCompSwapChain_IsCandidateIndependentFlipCompatible_orig(self);
}

typedef bool (COverlayContext_IsCandidateDirectFlipCompatible_t)(void*, void*, void*, void*, int, unsigned int, bool, bool);

typedef bool (COverlayContext_IsCandidateDirectFlipCompatible_24h2_t)(void*, void*, void*, void*, unsigned int, bool);

COverlayContext_IsCandidateDirectFlipCompatible_t* COverlayContext_IsCandidateDirectFlipCompatible_orig;
COverlayContext_IsCandidateDirectFlipCompatible_24h2_t* COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2;

bool COverlayContext_IsCandidateDirectFlipCompatible_hook_24h2(void* self, void* a2, void* a3, void* a4, unsigned int a5,
	bool a6)
{
	if (IsLUTActive(self))
	{
		return false;
	}
	return COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2(self, a2, a3, a4, a5, a6);
}

bool COverlayContext_IsCandidateDirectFlipCompatible_hook(void* self, void* a2, void* a3, void* a4, int a5,
                                                          unsigned int a6, bool a7, bool a8)
{
	if (IsLUTActive(self))
	{
		return false;
	}
	return COverlayContext_IsCandidateDirectFlipCompatible_orig(self, a2, a3, a4, a5, a6, a7, a8);
}

// COverlayContext::OverlaysEnabled. Forcing this to return false for LUT-active contexts keeps the LUT
// applied over composited fullscreen surfaces (OverlayTestMode=5 alone does NOT hold on the fullscreen
// overlay-config path). On 25H2 the detour is a register-preserving assembly thunk (OverlaysEnabled_thunk,
// in OverlaysEnabledThunk.asm), because DWM's IsDFlipOnMPO dereferences r8 after the call and a plain C++
// detour clobbers it (INVALID_POINTER_READ crash); the thunk saves/restores rcx/rdx/r8-r11 around this
// hook. NOTE: this does NOT make IndependentFlip'd fullscreen games take the LUT -- that is a DWM
// composite-vs-flip decision outside this hook's reach. Older
// builds (24H2 / 23H2, unverified) call this C++ hook directly, unchanged.
typedef bool (COverlayContext_OverlaysEnabled_t)(void*);

COverlayContext_OverlaysEnabled_t* COverlayContext_OverlaysEnabled_orig  = NULL;

// Defined in OverlaysEnabledThunk.asm. IntelliSense does not parse .asm, so it reports "definition not
// found"; give it a stub under __INTELLISENSE__ (which the real compiler never sees -- it links the asm).
#ifdef __INTELLISENSE__
extern "C" bool OverlaysEnabled_thunk(void* self) { return false; }
#else
extern "C" bool OverlaysEnabled_thunk(void* self);  // register-preserving detour, OverlaysEnabledThunk.asm
#endif

// extern "C" so the asm thunk can call it by an unmangled name.
extern "C" bool COverlayContext_OverlaysEnabled_hook(void* self)
{
	if (IsLUTActive(self))
	{
		return false;
	}
	return COverlayContext_OverlaysEnabled_orig(self);
}

// Device-lost hook: release our resources on any lost device before DWM destroys it.
typedef void (CDeviceManager_ProcessDeviceLost_t)(void*);
CDeviceManager_ProcessDeviceLost_t* CDeviceManager_ProcessDeviceLost_orig = NULL;

// True if DWM has a device flagged lost (about to be erased). We read DWM's internal device vector
// exactly as CDeviceManager::ProcessDeviceLost does. SEH-guarded: a torn/racy read just returns false.
static bool AnyDwmDeviceLost()
{
	if (g_dwmDeviceVecFirst == NULL || g_dwmDeviceVecLast == NULL || g_activeDwmProfile == NULL) return false;
	const int stride  = g_activeDwmProfile->deviceInfoStride;
	const int flagOff = g_activeDwmProfile->deviceLostFlagOffset;
	if (stride <= 0) return false;
	__try
	{
		unsigned char* it  = *(unsigned char**)g_dwmDeviceVecFirst;
		unsigned char* end = *(unsigned char**)g_dwmDeviceVecLast;
		for (int guard = 0; it != NULL && it < end && guard < 256; it += stride, guard++)
		{
			unsigned char* dev = *(unsigned char**)it;             // DeviceInfo.device at +0
			if (dev != NULL && *(volatile int*)(dev + flagOff) != 0) return true;
		}
	}
	__except (EXCEPTION_EXECUTE_HANDLER) { return false; }
	return false;
}

// Release ALL cached LUT assets on every tracked device. Called right before DWM erases a lost device
// so its CD3DResourceLeakChecker finds nothing outstanding. Assets rebuild lazily next frame.
static void EvictAllAssets()
{
	{
		std::lock_guard<std::mutex> lo(g_outputsMutex);
		for (auto& p : g_outputs) delete p.second;   // ComPtr members release GPU resources
		g_outputs.clear();
	}
	{
		std::lock_guard<std::mutex> lk(g_adaptersMutex);
		for (auto& p : g_adapters) delete p.second;
		g_adapters.clear();
	}
	{
		// Also drop the 1:1 context<->origin ownership claims. The overlay contexts are being
		// destroyed and recreated by DWM across this device-lost, so their old pointers are stale;
		// if we keep them, a recreated context resolving to a still-"owned" origin is skipped and
		// never gets its LUT (observed as the LUT not reapplying on a monitor after a fullscreen
		// mode change). Cleared here, the new contexts re-claim their origins freshly next frame.
		std::lock_guard<std::mutex> lk(g_clipMutex);
		g_positionOwner.clear();
	}
	diag_log("dwm device-lost imminent -> released all LUT assets (rebuild after recovery)");
}

void CDeviceManager_ProcessDeviceLost_hook(void* self)
{
	// DWM calls this EVERY frame, so we must not release unconditionally (that would rebuild shaders/
	// textures every frame). Release only when DWM is actually about to erase a device flagged lost --
	// that's the moment our resources on it would trip the leak checker.
	if (AnyDwmDeviceLost())
		EvictAllAssets();
	CDeviceManager_ProcessDeviceLost_orig(self);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD fdwReason, LPVOID lpReserved)
{
	switch (fdwReason)
	{
	case DLL_PROCESS_ATTACH:
		{
			HMODULE dwmcore = GetModuleHandle(L"dwmcore.dll");
			MODULEINFO moduleInfo;
			GetModuleInformation(GetCurrentProcess(), dwmcore, &moduleInfo, sizeof moduleInfo);

			OSVERSIONINFOEX versionInfo;
			ZeroMemory(&versionInfo, sizeof OSVERSIONINFOEX);
			versionInfo.dwOSVersionInfoSize = sizeof OSVERSIONINFOEX;
			versionInfo.dwBuildNumber = 22000;

			OSVERSIONINFOEX versionInfo24h2;
			ZeroMemory(&versionInfo24h2, sizeof OSVERSIONINFOEX);
			versionInfo24h2.dwOSVersionInfoSize = sizeof OSVERSIONINFOEX;
			versionInfo24h2.dwBuildNumber = 26100;

			OSVERSIONINFOEX versionInfo25h2;
			ZeroMemory(&versionInfo25h2, sizeof OSVERSIONINFOEX);
			versionInfo25h2.dwOSVersionInfoSize = sizeof OSVERSIONINFOEX;
			versionInfo25h2.dwBuildNumber = 26200;

			ULONGLONG dwlConditionMask = 0;
			VER_SET_CONDITION(dwlConditionMask, VER_BUILDNUMBER, VER_GREATER_EQUAL);

			OSVERSIONINFOEX versionInfo23h2;
			ZeroMemory(&versionInfo23h2, sizeof OSVERSIONINFOEX);
			versionInfo23h2.dwOSVersionInfoSize = sizeof OSVERSIONINFOEX;
			versionInfo23h2.dwBuildNumber = 22621;

			if (VerifyVersionInfo(&versionInfo25h2, VER_BUILDNUMBER, dwlConditionMask))
			{
				isWindows11_25h2 = true;
			}
			else if (VerifyVersionInfo(&versionInfo24h2, VER_BUILDNUMBER, dwlConditionMask))
			{
				isWindows11_24h2 = true;
			}
			else if (VerifyVersionInfo(&versionInfo23h2, VER_BUILDNUMBER, dwlConditionMask))
			{
				isWindows11_23h2 = true;
				isWindows11 = true;
			}
			else if (VerifyVersionInfo(&versionInfo, VER_BUILDNUMBER, dwlConditionMask))
			{
				isWindows11 = true;
			}
			else
			{
				isWindows11 = false;
			}

			// Capture the loaded dwmcore.dll file version so the 25H2+ path can select the correct
			// clip-box offset per binary build. g_dwmcoreVersion = (build<<32)|revision.
			{
				wchar_t dwmcorePath[MAX_PATH];
				if (dwmcore && GetModuleFileNameW(dwmcore, dwmcorePath, MAX_PATH))
				{
					DWORD verHandle = 0;
					DWORD verSize = GetFileVersionInfoSizeW(dwmcorePath, &verHandle);
					if (verSize)
					{
						std::vector<BYTE> verData(verSize);
						if (GetFileVersionInfoW(dwmcorePath, 0, verSize, verData.data()))
						{
							VS_FIXEDFILEINFO* ffi = NULL;
							UINT ffiLen = 0;
							if (VerQueryValueW(verData.data(), L"\\", (void**)&ffi, &ffiLen) && ffi && ffiLen)
							{
								unsigned int build = HIWORD(ffi->dwFileVersionLS);
								unsigned int revision = LOWORD(ffi->dwFileVersionLS);
								g_dwmcoreVersion = DWM_VER(build, revision);
								char vb[128];
								sprintf(vb, "dwmcore.dll version %u.%u.%u.%u",
									HIWORD(ffi->dwFileVersionMS), LOWORD(ffi->dwFileVersionMS), build, revision);
								diag_log(vb);
							}
						}
					}
				}
			}

			g_activeDwmProfile = SelectDwmProfile(g_dwmcoreVersion);
			if (isWindows11_25h2 && g_activeDwmProfile == NULL)
				diag_log("WARNING: 25H2+ but no matching dwmcore profile -> LUTs will not be applied");
			if (g_activeDwmProfile != NULL && g_activeDwmProfile->deviceVecFirstRva != 0 && dwmcore != NULL)
			{
				g_dwmDeviceVecFirst = (unsigned char*)dwmcore + g_activeDwmProfile->deviceVecFirstRva;
				g_dwmDeviceVecLast  = (unsigned char*)dwmcore + g_activeDwmProfile->deviceVecLastRva;
			}

			if (isWindows11_25h2 && g_activeDwmProfile != NULL)
			{
				// (a) Present + OverlaysEnabled (from the active dwmcore profile) and the OverlayTestMode global.
				for (size_t i = 0; i <= moduleInfo.SizeOfImage - g_activeDwmProfile->sigs.overlaysEnabledLen; i++)
				{
					unsigned char* address = (unsigned char*)dwmcore + i;

					if (!COverlayContext_Present_orig_24h2 &&
						g_activeDwmProfile->sigs.presentLen <= moduleInfo.SizeOfImage - i &&
						!aob_match_inverse(address, g_activeDwmProfile->sigs.present,
							(int)g_activeDwmProfile->sigs.presentLen))
					{
						COverlayContext_Present_orig_24h2 = (COverlayContext_Present_24h2_t*)address;
						COverlayContext_Present_real_orig_24h2 = COverlayContext_Present_orig_24h2;
					}
					else if (!COverlayContext_OverlaysEnabled_orig &&
						g_activeDwmProfile->sigs.overlaysEnabledLen <= moduleInfo.SizeOfImage - i &&
						!aob_match_inverse(address, g_activeDwmProfile->sigs.overlaysEnabled,
							(int)g_activeDwmProfile->sigs.overlaysEnabledLen))
					{
						COverlayContext_OverlaysEnabled_orig = (COverlayContext_OverlaysEnabled_t*)address;

						// cmp dword ptr [rip+disp32], 5   -> OverlayTestMode global. Verified RVA 0x3FE1C4 (.data).
						int rip_offset = *(int*)(address + 2);
						g_pOverlayTestMode = (int*)(address + 7 + rip_offset);
					}

					if (COverlayContext_Present_orig_24h2 && COverlayContext_OverlaysEnabled_orig)
						break;
				}

				// (b) COverlayContext::IsCandidateDirectFlipCompatible.
				// The 25h2 prologue matches TWO functions on this build (0x14818 uses member [rcx+0x1B0];
				// 0x5E7D4 uses [rcx+0x4BF8]). The COverlayContext instance is the one with the large member
				// offset. Taking the first match (previous behaviour) hooked the wrong function, leaving
				// DirectFlip un-suppressed -> surfaces bypass the composed/LUT path at random.
				{
					const unsigned char* sig = g_activeDwmProfile->sigs.isCandidateDf;
					const size_t siglen = g_activeDwmProfile->sigs.isCandidateDfLen;
					unsigned char* best = NULL; unsigned int bestDisp = 0;
					for (size_t i = 0; i + siglen <= moduleInfo.SizeOfImage; i++)
					{
						unsigned char* a = (unsigned char*)dwmcore + i;
						if (aob_match_inverse(a, sig, (int)siglen)) continue;
						for (int k = 0; k < 0x40; k++)
						{
							// cmp dword ptr [rcx+disp32], ebx  == 39 99 <disp32>
							if (a[k] == 0x39 && a[k + 1] == 0x99)
							{
								unsigned int disp = *(unsigned int*)(a + k + 2);
								if (disp > bestDisp) { bestDisp = disp; best = a; }
								break;
							}
						}
					}
					COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2 =
						(COverlayContext_IsCandidateDirectFlipCompatible_24h2_t*)best;
				}

				// (c) CDeviceManager::ProcessDeviceLost - hooked to release our resources before DWM tears
				// down a lost device (avoids its resource-leak-checker int 3 on fullscreen mode changes).
				if (g_activeDwmProfile->sigs.processDeviceLost)
				{
					for (size_t i = 0; i + g_activeDwmProfile->sigs.processDeviceLostLen <= moduleInfo.SizeOfImage; i++)
					{
						unsigned char* a = (unsigned char*)dwmcore + i;
						if (!aob_match_inverse(a, g_activeDwmProfile->sigs.processDeviceLost,
							(int)g_activeDwmProfile->sigs.processDeviceLostLen))
						{
							CDeviceManager_ProcessDeviceLost_orig = (CDeviceManager_ProcessDeviceLost_t*)a;
							break;
						}
					}
				}

				// NOTE: CWindowContext / CCompSwapChain / CCompVisual candidates are INLINED on 26100.8246
				// (0 standalone matches) and left unhooked; MPO/overlay/flip promotion is suppressed via forced
				// OverlayTestMode=5 plus the COverlayContext DirectFlip/OverlaysEnabled hooks.
			}
			else if (isWindows11_24h2)
			{
				for (size_t i = 0; i <= moduleInfo.SizeOfImage - sizeof COverlayContext_OverlaysEnabled_bytes_relative_w11_24h2; i++)
				{
					unsigned char* address = (unsigned char*)dwmcore + i;
					if (!COverlayContext_Present_orig && sizeof COverlayContext_Present_bytes_w11_24h2 <= moduleInfo.
						SizeOfImage - i && !aob_match_inverse(address, COverlayContext_Present_bytes_w11_24h2,
							sizeof COverlayContext_Present_bytes_w11_24h2))
					{
						COverlayContext_Present_orig_24h2 = (COverlayContext_Present_24h2_t*)address;
						COverlayContext_Present_real_orig_24h2 = COverlayContext_Present_orig_24h2;
					}
					else if (!COverlayContext_IsCandidateDirectFlipCompatible_orig && sizeof
						COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11_24h2 <= moduleInfo.SizeOfImage - i && !
						aob_match_inverse(
							address, COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11_24h2,
							sizeof COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11_24h2))
					{
						COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2 = (
							COverlayContext_IsCandidateDirectFlipCompatible_24h2_t*)address;
					}
					else if (!COverlayContext_OverlaysEnabled_orig && sizeof COverlayContext_OverlaysEnabled_bytes_relative_w11_24h2
						<= moduleInfo.SizeOfImage - i && !aob_match_inverse(
							address, COverlayContext_OverlaysEnabled_bytes_relative_w11_24h2,
							sizeof COverlayContext_OverlaysEnabled_bytes_relative_w11_24h2))
					{

						COverlayContext_OverlaysEnabled_orig = (COverlayContext_OverlaysEnabled_t*)get_relative_address(address, 1, 5);
					}
					if (COverlayContext_Present_orig && COverlayContext_IsCandidateDirectFlipCompatible_orig &&
						COverlayContext_OverlaysEnabled_orig)
					{
						break;
					}
				}
			}
			else if (isWindows11)
			{
				for (size_t i = 0; i <= moduleInfo.SizeOfImage - sizeof COverlayContext_OverlaysEnabled_bytes_w11; i++)
				{
					unsigned char* address = (unsigned char*)dwmcore + i;
					if (!COverlayContext_Present_orig && sizeof COverlayContext_Present_bytes_w11 <= moduleInfo.
						SizeOfImage - i && !aob_match_inverse(address, COverlayContext_Present_bytes_w11,
						                                      sizeof COverlayContext_Present_bytes_w11))
					{
						COverlayContext_Present_orig = (COverlayContext_Present_t*)address;
						COverlayContext_Present_real_orig = COverlayContext_Present_orig;
					}
					else if (!COverlayContext_IsCandidateDirectFlipCompatible_orig && sizeof
						COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11 <= moduleInfo.SizeOfImage - i && !
						aob_match_inverse(
							address, COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11,
							sizeof COverlayContext_IsCandidateDirectFlipCompatible_bytes_w11))
					{
						COverlayContext_IsCandidateDirectFlipCompatible_orig = (
							COverlayContext_IsCandidateDirectFlipCompatible_t*)address;
					}
					else if (!COverlayContext_OverlaysEnabled_orig && sizeof COverlayContext_OverlaysEnabled_bytes_w11
						<= moduleInfo.SizeOfImage - i && !aob_match_inverse(
							address, COverlayContext_OverlaysEnabled_bytes_w11,
							sizeof COverlayContext_OverlaysEnabled_bytes_w11))
					{
						COverlayContext_OverlaysEnabled_orig = (COverlayContext_OverlaysEnabled_t*)address;

						
						int rip_offset = *(int*)(address + 2);
						g_pOverlayTestMode = (int*)(address + 7 + rip_offset);
					}
					if (COverlayContext_Present_orig && COverlayContext_IsCandidateDirectFlipCompatible_orig &&
						COverlayContext_OverlaysEnabled_orig)
					{
						break;
					}
				}

				DWORD rev;
				DWORD revSize = sizeof(rev);
				RegGetValueA(HKEY_LOCAL_MACHINE, "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", "UBR", RRF_RT_DWORD,
				             NULL, &rev, &revSize);
			}
			else
			{
				for (size_t i = 0; i <= moduleInfo.SizeOfImage - sizeof(COverlayContext_Present_bytes); i++)
				{
					unsigned char* address = (unsigned char*)dwmcore + i;
					if (!COverlayContext_Present_orig && !memcmp(address, COverlayContext_Present_bytes,
					                                             sizeof(COverlayContext_Present_bytes)))
					{
						COverlayContext_Present_orig = (COverlayContext_Present_t*)address;
						COverlayContext_Present_real_orig = COverlayContext_Present_orig;
					}
					else if (!COverlayContext_IsCandidateDirectFlipCompatible_orig && !memcmp(
						address, COverlayContext_IsCandidateDirectFlipCompatible_bytes,
						sizeof(COverlayContext_IsCandidateDirectFlipCompatible_bytes)))
					{
						static int found = 0;
						found++;
						if (found == 2)
						{
							COverlayContext_IsCandidateDirectFlipCompatible_orig = (
								COverlayContext_IsCandidateDirectFlipCompatible_t*)(address - 0xa);
						}
					}
					else if (!COverlayContext_OverlaysEnabled_orig && !memcmp(
						address, COverlayContext_OverlaysEnabled_bytes, sizeof(COverlayContext_OverlaysEnabled_bytes)))
					{
						COverlayContext_OverlaysEnabled_orig = (COverlayContext_OverlaysEnabled_t*)(address - 0x7);
						
						
						int rip_offset = *(int*)(address - 5);
						g_pOverlayTestMode = (int*)(address + rip_offset);
					}
					if (COverlayContext_Present_orig && COverlayContext_IsCandidateDirectFlipCompatible_orig &&
						COverlayContext_OverlaysEnabled_orig)
					{
						break;
					}
				}
			}

			char lutFolderPath[MAX_PATH];
			ExpandEnvironmentStringsA(LUT_FOLDER, lutFolderPath, sizeof(lutFolderPath));
			if (!AddLUTs(lutFolderPath))
			{
				return FALSE;
			}
			if ((COverlayContext_Present_orig && COverlayContext_IsCandidateDirectFlipCompatible_orig &&
				COverlayContext_OverlaysEnabled_orig) ||
				(COverlayContext_Present_orig_24h2 && COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2 && COverlayContext_OverlaysEnabled_orig) && numLuts != 0)

			{
				MH_Initialize();
				if (!isWindows11_24h2 && !isWindows11_25h2)
					MH_CreateHook((PVOID)COverlayContext_Present_orig, (PVOID)COverlayContext_Present_hook,
								  (PVOID*)&COverlayContext_Present_orig);
				else
					MH_CreateHook((PVOID)COverlayContext_Present_orig_24h2, (PVOID)COverlayContext_Present_hook_24h2,
						(PVOID*)&COverlayContext_Present_orig_24h2);

				if (!isWindows11_24h2 && !isWindows11_25h2)
					MH_CreateHook((PVOID)COverlayContext_IsCandidateDirectFlipCompatible_orig,
								  (PVOID)COverlayContext_IsCandidateDirectFlipCompatible_hook,
								  (PVOID*)&COverlayContext_IsCandidateDirectFlipCompatible_orig);
				else
					MH_CreateHook((PVOID)COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2,
						(PVOID)COverlayContext_IsCandidateDirectFlipCompatible_hook_24h2,
						(PVOID*)&COverlayContext_IsCandidateDirectFlipCompatible_orig_24h2);

				if (CWindowContext_IsCandidateDirectFlipCompatible_orig)
				{
					MH_CreateHook((PVOID)CWindowContext_IsCandidateDirectFlipCompatible_orig,
						(PVOID)CWindowContext_IsCandidateDirectFlipCompatible_hook,
						(PVOID*)&CWindowContext_IsCandidateDirectFlipCompatible_orig);
					LOG_ONLY_ONCE("Hooked CWindowContext::IsCandidateDirectFlipCompatible")
				}
				else {
					LOG_ONLY_ONCE("FAILED to find CWindowContext::IsCandidateDirectFlipCompatible")
				}

				if (CCompSwapChain_IsCandidateIndependentFlipCompatible_orig)
				{
					MH_CreateHook((PVOID)CCompSwapChain_IsCandidateIndependentFlipCompatible_orig,
						(PVOID)CCompSwapChain_IsCandidateIndependentFlipCompatible_hook,
						(PVOID*)&CCompSwapChain_IsCandidateIndependentFlipCompatible_orig);
					LOG_ONLY_ONCE("Hooked CCompSwapChain::IsCandidateIndependentFlipCompatible")
				}
				else {
					LOG_ONLY_ONCE("FAILED to find CCompSwapChain::IsCandidateIndependentFlipCompatible")
				}

				if (CCompSwapChain_IsCandidateDirectFlipCompatible_orig)
				{
					MH_CreateHook((PVOID)CCompSwapChain_IsCandidateDirectFlipCompatible_orig,
						(PVOID)CCompSwapChain_IsCandidateDirectFlipCompatible_hook,
						(PVOID*)&CCompSwapChain_IsCandidateDirectFlipCompatible_orig);
					LOG_ONLY_ONCE("Hooked CCompSwapChain::IsCandidateDirectFlipCompatible")
				}
				else {
					LOG_ONLY_ONCE("FAILED to find CCompSwapChain::IsCandidateDirectFlipCompatible")
				}

				if (CCompVisual_IsCandidateForPromotion_orig)
				{
					MH_CreateHook((PVOID)CCompVisual_IsCandidateForPromotion_orig,
						(PVOID)CCompVisual_IsCandidateForPromotion_hook,
						(PVOID*)&CCompVisual_IsCandidateForPromotion_orig);
					LOG_ONLY_ONCE("Hooked CCompVisual::IsCandidateForPromotion")
				}
				else {
					LOG_ONLY_ONCE("FAILED to find CCompVisual::IsCandidateForPromotion")
				}

				if (g_pOverlayTestMode != NULL)
				{
					*g_pOverlayTestMode = 5;
					LOG_ONLY_ONCE("SUCCESS: Forced OverlayTestMode to 5")
				}
				else {
					LOG_ONLY_ONCE("FAILED to find g_pOverlayTestMode")
				}

				// OverlaysEnabled suppression (forces false for LUT contexts -> keeps the LUT over
				// fullscreen apps). The active profile decides whether to use the register-preserving asm
				// thunk (needed on 25H2, where a plain C++ detour clobbers r8 and crashes DWM's
				// IsDFlipOnMPO -- see the note by the typedef). Builds with no profile / the flag unset
				// (24H2 / 23H2, unverified) use the plain C++ hook, unchanged.
				if (COverlayContext_OverlaysEnabled_orig)
				{
					if (g_activeDwmProfile != NULL && g_activeDwmProfile->overlaysEnabledThunk)
						MH_CreateHook((PVOID)COverlayContext_OverlaysEnabled_orig, (PVOID)OverlaysEnabled_thunk,
						              (PVOID*)&COverlayContext_OverlaysEnabled_orig);
					else
						MH_CreateHook((PVOID)COverlayContext_OverlaysEnabled_orig, (PVOID)COverlayContext_OverlaysEnabled_hook,
						              (PVOID*)&COverlayContext_OverlaysEnabled_orig);
				}
				if (CDeviceManager_ProcessDeviceLost_orig)
					MH_CreateHook((PVOID)CDeviceManager_ProcessDeviceLost_orig, (PVOID)CDeviceManager_ProcessDeviceLost_hook,
					              (PVOID*)&CDeviceManager_ProcessDeviceLost_orig);
				MH_EnableHook(MH_ALL_HOOKS);
				// (worker thread removed: adapter/output assets are now built synchronously and cached)
				LOG_ONLY_ONCE("DWM HOOK DLL INITIALIZATION. START LOGGING")

				if (g_pOverlayTestMode != NULL)
				{
					*g_pOverlayTestMode = 5;
					LOG_ONLY_ONCE("Set OverlayTestMode global to 5 in DWM memory")
				}

				break;
			}
			return FALSE;
		}
	case DLL_PROCESS_DETACH:
		g_hookInert.store(true);
		if (g_pOverlayTestMode != NULL) *g_pOverlayTestMode = 0;
		MH_DisableHook(MH_ALL_HOOKS);
		Sleep(50);
		MH_Uninitialize();
		Sleep(100);
		UninitializeStuff();
		break;
	default:
		break;
	}
	return TRUE;
}
