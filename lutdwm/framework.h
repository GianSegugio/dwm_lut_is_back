#pragma once


#define WIN32_LEAN_AND_MEAN

#include <windows.h>
#include <MinHook.h>

#if defined(_M_X64)
#if defined(_DEBUG)
#pragma comment(lib, "vcpkg_installed\\x64-windows-static\\x64-windows-static\\debug\\lib\\minhook.x64d.lib")
#else
#pragma comment(lib, "vcpkg_installed\\x64-windows-static\\x64-windows-static\\lib\\minhook.x64.lib")
#endif
#endif

#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
#include <psapi.h>
#include <intrin.h>

// ---- C++ runtime facilities used by dllmain.cpp ----
#include <cstdio>
#include <vector>
#include <map>
#include <mutex>
#include <atomic>
#include <sstream>       // std::stringstream (logging macros)
#include <iomanip>       // std::setw / std::setfill / std::hex
#include <stdexcept>     // std::runtime_error / std::exception
#include <wrl/client.h>  // Microsoft::WRL::ComPtr

#include "noise.h"
