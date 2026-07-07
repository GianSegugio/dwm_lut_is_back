# DwmLut Is Back Documentation

## Architecture Overview

DwmLut is composed of a C++ core (`lutdwm`) and a C# management interface (`DwmLutGUI`).

> **Build target:** this build is tuned for and verified against **`dwmcore.dll` 10.0.26100.8246** and **10.0.26100.8655** (Windows 11 25H2, OS Builds 26200.8246 and 26200.8655). 
> The per-monitor identification and DWM offsets below are specific to those binaries. Legacy Windows-version detection remains in the code, but correct per-monitor mapping is **not** guaranteed on other builds, see **Known Limitations** for more details.

### 1. The Core Engine (`lutdwm`)
The core engine is a dynamic link library (`lutdwm.dll`) designed for injection into `dwm.exe` (Desktop Window Manager).

#### Key Responsibilities:
- **Direct3D Hooking**: Locates unexported DWM functions by **AOB (array-of-bytes) signature scanning** and installs inline hooks via MinHook; reaches the composed backbuffer through **vtable traversal** of the overlay swapchain object.
- **LUT Application**: Uses a custom pixel shader to apply 3D LUT data via tetrahedral interpolation.
- **Dithering**: Implements blue-noise dithering for SDR display modes to maintain bit-depth integrity.
- **Multi-GPU / Multi-Monitor**: Isolates rendering resources per graphics adapter and per output, and validates every bound resource against the presenting device to keep hybrid iGPU/dGPU setups stable.
- **Windows Version Handling**: Contains version-detection scaffolding for Windows 10 / Windows 11 (23H2 / 24H2 / 25H2); the active per-monitor coordinate logic is tuned to the **25H2 (26100.8246 / 8655)** layout.

#### Core Files:
- `lutdwm/dllmain.cpp`: Main entry point, hooking logic, resource management, and shaders.
- `lutdwm/framework.h`: Framework/library includes (explicitly includes the C++ runtime headers the core relies on — `<map> <mutex> <atomic> <sstream> <iomanip> <stdexcept> <wrl/client.h>`).
- `lutdwm/noise.h`: Blue noise texture data for dithering.
- `lutdwm/pch.h`: Precompiled headers.
- `lutdwm/OverlaysEnabledThunk.asm`: Register-preserving thunk for the 25H2 `OverlaysEnabled` hook.

### 2. The GUI Manager (`DwmLutGUI`)
A WPF application used to configure and monitor the LUT application status.

#### Key Features:
- **Per-Monitor Calibration**: Detects all connected monitors and allows assigning a different `.cube` file to each. LUTs are keyed by the monitor's **desktop position** (e.g. `-1280_0.cube`); the "#" column shows a stable, globally-unique display index (independent of per-adapter source IDs).
- **Display naming**: Monitor names come from each display's EDID. Internal laptop panels commonly ship an EDID with no product-name descriptor, so they are labelled **"Internal Display"** (detected from the connection type) rather than shown blank; other displays with no EDID name fall back to `???`.
- **UAC Bypass Autostart**: Uses Windows Task Scheduler to launch with highest privileges on system logon.
- **DLL Injection**: Automates the `CreateRemoteThread` injection process into `dwm.exe`.
- **Minimized Operation**: Runs in the system tray to keep LUTs active without cluttering the taskbar.
- **HDR awareness**: Detects each display's current HDR (advanced-color) state, shows it in the monitor list's **Mode** column, and warns if you assign a LUT that can't apply in that mode (e.g. an SDR LUT to an HDR display). A LUT is applied only when its type matches the display's mode (see *HDR / SDR LUT selection*).

#### Project Layout:
- `DwmLutGUI/MainWindow.xaml`: Main user interface.
- `DwmLutGUI/MonitorData.cs`: Per-monitor model (identity, LUT paths, display index).
- `DwmLutGUI/Injector.cs`: Handles process discovery and DLL injection.
- `DwmLutGUI/MainViewModel.cs`: Core application logic, monitor enumeration, and config persistence.
- `DwmLutGUI/HdrInfo.cs`: Queries Windows for each display's HDR (advanced-color) state.

---

## Technical Deep Dive: Windows 11 25H2 Support

Windows 11 Build 26200 (25H2) significantly refactored DWM's internal structures. This build addresses those changes for `dwmcore.dll` 10.0.26100.8246 and 10.0.26100.8655 as follows.

### Per-Monitor Identification
Each overlay composition context reports **local, origin-`(0,0)` coordinates at native resolution** — not its global desktop position — so monitors cannot be told apart by the context's clip rectangle directly. 
This build reads each monitor's true **desktop origin from `self + 0x7658`** (two floats: `left, top`). Verified behavior: the primary monitor reads `(0, 0)`; a monitor positioned at desktop `x = -1280` reads `left = -1280`. That origin is matched against the position-named `.cube` files, so each context applies its own LUT. A strict 1:1 `context ↔ origin` ownership guard prevents any residual cross-assignment. (The monitor's native resolution is also available at `self + 0x4A24` as an alternate identifier.)

On **24H2 / 23H2 / Windows 10**, the origin is read from that version's own clip-box offset instead — `*(void**)self + 0x53E8` (24H2), `+ 0x466C` (23H2 / Windows 11), or `- 0x120` (Windows 10) — through the same SEH-guarded read. These older paths carry the original ed1ii offsets forward and are supported but unverified on current hardware.

### Backbuffer Acquisition
DWM no longer exposes an `IDXGISwapChain` through standard patterns. The engine obtains each monitor's composed surface from the **`IOverlaySwapChain`** object by calling `vt[24]`, then `vt[19]` on the returned object, then `QueryInterface(ID3D11Texture2D)` to reach the backbuffer texture. If this cannot resolve a surface, that monitor's frame is skipped. (An earlier brute-force scan that probed arbitrary offsets for a swapchain pointer was removed — it could invoke unintended methods on non-swapchain pointers and destabilize composition.)

### MPO / DirectFlip Suppression
To ensure the LUT is applied even during DirectFlip or MPO (Multi-Plane Overlays), the engine forces the `OverlayTestMode` global to `5`. The global is resolved via the `COverlayContext::OverlaysEnabled` signature. `COverlayContext::IsCandidateDirectFlipCompatible` is also hooked; note its function prologue is ambiguous on this build (two matches), so the correct instance is disambiguated by its member offset.
Several finer-grained suppression functions (`CWindowContext`/`CCompSwapChain`/`CCompVisual` candidates) are **inlined** on this build and therefore not hookable as standalone functions; forcing `OverlayTestMode = 5` covers MPO suppression globally in their place.
On 25H2, `COverlayContext::OverlaysEnabled` is hooked through a small **register-preserving assembly thunk** rather than a direct C++ detour. DWM's `COverlayContext::OverlayPlaneInfo::IsDFlipOnMPO` dereferences `r8` after calling `OverlaysEnabled` and relies on it surviving the call (interprocedural register allocation, since the real callee only touches `rcx`/`al`); a plain detour clobbers `r8` and crashes DWM during fullscreen-overlay evaluation. The thunk (`OverlaysEnabled_thunk`) saves/restores `rcx`/`rdx`/`r8`–`r11` around the hook. Its use is selected per build by the `overlaysEnabledThunk` `DwmProfile` flag; older builds (24H2 / 23H2) use the C++ hook directly.

### Multi-GPU Resource Model
Rendering resources are split into two levels:
- **Per adapter (`ID3D11Device`)** — immutable assets (shaders, samplers, blue-noise texture, the 3D LUT textures), each allocated on that adapter's device.
- **Per output** — mutable scratch (backbuffer-copy texture, render-target-view cache, constant buffer, last-applied state).

Before any draw, both the backbuffer and the LUT shader-resource-view are validated to belong to the **presenting** device; a mismatch skips the frame, so a resource created on the iGPU can never be bound into the dGPU's context (or vice-versa). Each adapter's immediate context is serialized so concurrent presents from multiple outputs cannot corrupt shared device state. Assets are created synchronously and cached on first use; a device whose assets fail to build is skipped rather than crashing.
Multiple adapter devices are held **simultaneously** — a hybrid laptop composites on more than one GPU at once — so the engine never treats the appearance of a second device as a reason to evict the first. (Doing so previously caused a per-frame evict/rebuild thrash whenever an external monitor was attached, seen as reduced animation smoothness.)

### Version Profiles
Each supported `dwmcore.dll` build is one **`DwmProfile`** row in `g_dwmProfiles[]`, and every row is self-contained: the dwmcore version, the four AOB signatures (embedded inline as fixed-size byte arrays, `'?'` = wildcard), and the build-specific offsets (clip-box + device-vector). At load, the engine reads the running `dwmcore.dll`'s file version and selects the newest row whose minimum version it satisfies (newest-first; a version-read failure falls back to the newest row; an unmatched build is skipped). Two builds are currently profiled — **26100.8246** and **26100.8655** — which share identical signatures but differ in their device-vector addresses. Supporting a future build is a single new row. (The 24H2 / 23H2 / legacy paths are not part of this table and are unchanged.)

### HDR / SDR LUT selection
DWM composites an HDR display into an FP16 (scRGB) backbuffer and an SDR display into an 8/10-bit backbuffer, and the shader applies the LUT through an HDR (PQ/BT.2100) or SDR path accordingly — so a LUT is only valid for the mode it was calibrated in. The engine matches **exactly**: an HDR context takes the HDR LUT, an SDR context takes the SDR LUT. If the only LUT assigned to a display is the wrong type for its current mode, **no LUT is applied** rather than a mismatched one (which, run through the other path, would produce wrong colors). Use an HDR LUT (a `.cube` with `hdr` in the filename, calibrated in HDR) for a display in HDR mode, and an SDR LUT for SDR mode.

### Fullscreen Device-Lost Handling
Recent DWM builds run a **resource-leak checker** that deliberately breaks (`int 3`, crashing DWM) if it destroys one of its internal D3D devices while resources are still alive on it. 
Because the engine's LUT resources live on DWM's device, a real display-mode change (for example an old DirectDraw game entering a native-resolution fullscreen) would tear down a device that still held them and crash DWM. 
The engine hooks DWM's device-lost handler (`CDeviceManager::ProcessDeviceLost`) and, at its entry, releases **all** LUT resources **only when DWM is actually about to remove a device** — a decision it makes by reading DWM's own internal device list and checking each device's "lost" flag (a bounds-checked, exception-guarded read). 
The handler runs every frame, so this release is gated rather than unconditional. The resources rebuild on the recovered device on the next frame, so the LUT returns automatically when the app leaves fullscreen. The same release also **resets the 1:1 context-to-origin ownership map**: DWM destroys and recreates its overlay contexts across a mode change, and a recreated context resolving to an origin still "owned" by a now-destroyed one would otherwise be skipped and left without its LUT. (The LUT is still not *guaranteed* while an exclusive/mode-changed fullscreen is up — only the crash is prevented and the recovery afterward is clean.)

### Fail-Safe Design
- A process-wide **kill-switch** makes every hook return immediately once tripped, so DWM composites normally instead of crash-looping (tripped by the render-path exception boundary and on DLL detach).
- The render path runs behind a structured-exception boundary; any access violation is contained and latches the kill-switch rather than propagating into DWM.
- Assets are validated per device before every bind, and any adapter/output whose resources fail to build is skipped rather than crashing.

---

## Reverse-engineering reference (verified against 26100.8246)

| Symbol | Location |
|---|---|
| `COverlayContext::Present` | RVA `0x232A20` (unique) |
| `COverlayContext::OverlaysEnabled` | RVA `0x18893C` (unique) |
| `OverlayTestMode` global | RVA `0x3FE1C4` (`.data`), forced to `5` |
| `COverlayContext::IsCandidateDirectFlipCompatible` | RVA `0x5E7D4` (member `0x4BF8`) — not `0x14818` |
| `IOverlaySwapChain` vtable | `.rdata` RVA `0x30CB48` (slot 24 = backbuffer-array accessor) |
| Per-monitor desktop origin | `self + 0x7658` (float `left, top`) |
| Per-monitor native resolution | `self + 0x4A24` (`0,0,W,H`) — alternate identifier |
| `CDeviceManager::ProcessDeviceLost` | RVA `0x0EF370` (unique 33-byte prologue; hooked for the device-lost fix) |
| DWM device vector (`CDeviceManager`) | `.data` `_Myfirst`/`_Mylast` RVA `0x3FDA88`/`0x3FDA90`; `DeviceInfo` stride `0x10`; lost-flag `device+0x458` |

***26100.8655 delta:** same structure, shifted RVAs — `Present` `0x231800`, `OverlaysEnabled` `0x1A2BE8`, `IsCandidateDirectFlipCompatible` `0xB1414` (member `0x4BF8`), `ProcessDeviceLost` `0xDCF80`, and device vector `_Myfirst`/`_Mylast` `0x3FAB78`/`0x3FAB80`. Signature bytes, clip-box `0x7658`, stride `0x10`, and lost-flag `0x458` are unchanged.*

## Verified dwmcore values

| Field                          | 8246                          | 8655                          | changed?   |
| ------------------------------ | ----------------------------- | ----------------------------- | ---------- |
| `minVersion`                   | `DWM_VER(26100, 8246)`        | `DWM_VER(26100, 8655)`        | **yes**    |
| `Present` sig                  | @ RVA 0x232A20                | @ RVA 0x231800                | same bytes |
| `OverlaysEnabled` sig          | @ RVA 0x18893C                | @ RVA 0x1A2BE8                | same bytes |
| `IsCandidateDF` sig            | @ RVA 0x5E7D4 (member 0x4BF8) | @ RVA 0xB1414 (member 0x4BF8) | same bytes |
| `ProcessDeviceLost` sig        | @ RVA 0xEF370                 | @ RVA 0xDCF80                 | same bytes |
| `clipBoxOffset`                | `0x7658`                      | `0x7658`                      | same bytes |
| `deviceVecFirstRva` (_Myfirst) | `0x3FDA88`                    | **`0x3FAB78`**                | **yes**    |
| `deviceVecLastRva` (_Mylast)   | `0x3FDA90`                    | **`0x3FAB80`**                | **yes**    |
| `deviceInfoStride`             | `0x10`                        | `0x10`                        | same       |
| `deviceLostFlagOffset`         | `0x458`                       | `0x458`                       | same       |

---

## Known Limitations

- **Binary-version lock:** Signatures and offsets are valid for `dwmcore.dll` 10.0.26100.8246 and 10.0.26100.8655 (Windows 11 25H2, builds 26200.8246 and 26200.8655), ImageBase `0x180000000`, thus the tool is not guaranteed to work on older 25H2 builds for which LUT application is skipped entirely as a safety measure. 
  Support for 24H2 (direct composition), and 23H2 (build 22631) has been kept, but no evaluation has been conducted for such legacy versions.
- **Crash proof, update vulnerable:** When a Windows update breaks the tool, the symptom points to the cause:
  - *Nothing happens at all* → a `COverlayContext` **signature** moved (most likely `Present`), or the running dwmcore has **no matching profile**.
  - *Wrong monitor / wrong colors* → the clip-box **offset** (`0x7658`) or `GetBackBuffer_25H2`'s `vt[24]`/`vt2[19]` indices moved.
  - *Flicker / LUT dropping out on a surface* → `OverlayTestMode` / the overlay hooks moved.
  - *DWM crashes again on a fullscreen mode change* → the `ProcessDeviceLost` signature or a device-vector offset moved (per build; `0x3FDA88`/`0x3FDA90` on 8246, `0x3FAB78`/`0x3FAB80` on 8655; stride `0x10`, flag `0x458`).
  - *DWM crashes when a video/app goes fullscreen (overlay path)* → a hooked overlay function is being relied upon by DWM to preserve a volatile register across the call. On 25H2 `OverlaysEnabled` is left unhooked for this reason; if a similar crash appears with another overlay hook (`IsCandidateDirectFlipCompatible` family) in the stack, it needs the same treatment.
  Adding support for a new build is a **single prepended `g_dwmProfiles[]` entry**, but obtaining the values is a reverse-engineering pass (disassembly + live capture).
- **Exclusive / mode-changed fullscreen is not *guaranteed* to be color-managed:** (e.g. old DirectDraw games switching to a native-resolution fullscreen): such surfaces bypass DWM composition, so the LUT is not reliably reachable (matches ledoge's original limitation). This **no longer crashes DWM** and **recovers its LUTs cleanly on exit**, even on a multi-GPU / multi-monitor setup. The LUT may remain applied through such a fullscreen DWM bypass, now that resources stay stable across the transition, but that is not guaranteed. A LUT is applied only while DWM **composites** a surface. When a fullscreen or borderless game presents a flip-model swapchain that DWM promotes to **IndependentFlip** (direct scanout), the frames bypass composition entirely, so no LUT can be applied. This is a DWM decision, made per frame from swapchain state, occlusion, the mouse cursor, and MPO capability, with **no hookable entry point on 25H2** — the relevant `CCompSwapChain` / `CWindowContext` flip-candidate checks are unreachable there. Exclusive-fullscreen apps bypass DWM outright and likewise cannot be reached. Windowed and *composited* fullscreen surfaces (most fullscreen browser video, and legacy fullscreen games that DWM still composites) do get the LUT.

---

*Last Updated: 07 July 2026*
