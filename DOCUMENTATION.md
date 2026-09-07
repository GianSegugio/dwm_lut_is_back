# DwmLut Is Back Documentation

## Architecture Overview

DwmLut is composed of a C++ core (`lutdwm`) and a C# management interface (`DwmLutGUI`).

> **Build target:** this build is tuned for and verified against the **`dwmcore.dll`** builds listed in [SUPPORTED_VERSIONS.txt](SUPPORTED_VERSIONS.txt). 
> The per-monitor identification and DWM offsets below are specific to those binaries. Legacy Windows-version detection remains in the code, but correct per-monitor mapping is **not** guaranteed on older Windows versions, see **Known Limitations** for more details.

### 1. The Core Engine (`lutdwm`)
The core engine is a dynamic link library (`lutdwm.dll`) designed for injection into `dwm.exe` (Desktop Window Manager).

#### Key Responsibilities:
- **Direct3D Hooking**: Locates unexported DWM functions by **AOB (array-of-bytes) signature scanning** and installs inline hooks via MinHook; reaches the composed backbuffer through **vtable traversal** of the overlay swapchain object.
- **LUT Application**: Uses a custom pixel shader to apply 3D LUT data via tetrahedral interpolation.
- **Dithering**: Implements blue-noise dithering for SDR display modes to maintain bit-depth integrity.
- **Multi-GPU / Multi-Monitor**: Isolates rendering resources per graphics adapter and per output, and validates every bound resource against the presenting device to keep hybrid iGPU/dGPU setups stable.
- **Windows Version Handling**: Contains version-detection scaffolding for Windows 10 / Windows 11 (21H2 / 22H2 / 23H2 / 24H2 / 25H2 / 26H2); the active per-monitor coordinate logic is tuned to the 25H2 and 26H2-preview layouts of the builds listed in [SUPPORTED_VERSIONS.txt](SUPPORTED_VERSIONS.txt). The 21H2 tier is hardware-validated.

#### Core Files:
- `lutdwm/dllmain.cpp`: Main entry point, hooking logic, resource management, and shaders.
- `lutdwm/framework.h`: Framework/library includes (explicitly includes the C++ runtime headers the core relies on — `<map> <mutex> <atomic> <sstream> <iomanip> <stdexcept> <wrl/client.h>`).
- `lutdwm/noise.h`: Blue noise texture data for dithering.
- `lutdwm/pch.h`: Precompiled headers.
- `lutdwm/OverlaysEnabledThunk.asm`: Register-preserving thunk for the 25H2 `OverlaysEnabled` hook.

### 2. The GUI Manager (`DwmLutGUI`)
A WPF application used to configure and monitor the LUT application status.

#### Key Features:
- **Per-Monitor Calibration**: Detects all connected monitors and allows assigning a different `.cube` file to each. LUTs are keyed by the monitor's **desktop position** (e.g. `-1280_0.cube`); the "#" row shows a stable, globally-unique display index (independent of per-adapter source IDs).
- **Comprehensive monitor table**: Properties are rows and each monitor is a column, so the labels read in full and a machine's handful of displays sit side by side. Every column is self-contained — its own SDR and HDR LUT pickers with **Browse / Next / Clear** — so there is no "selected monitor" mode.
- **Display naming**: Monitor names come from each display's EDID. Internal laptop panels commonly ship an EDID with no product-name descriptor, so they are labelled **"Internal Display"** (detected from the connection type) rather than shown blank; other displays with no EDID name fall back to `???`.
- **UAC Bypass Autostart**: Uses Windows Task Scheduler to launch with highest privileges on system logon, so no UAC prompt appears each time. Offered once on first run and reversible thereafter from the **Autostart** label and **Turn on / Turn off** button beside the hotkey selector; the displayed state is read from the actual scheduled task rather than remembered, so it stays correct even if the task is removed outside the app.
- **DLL Injection**: Automates the `CreateRemoteThread` injection process into `dwm.exe`.
- **Minimized Operation**: Runs in the system tray to keep LUTs active without cluttering the taskbar.
- **Advanced-colour awareness**: Detects each display's current advanced-colour mode — **SDR**, **WCG** or **HDR** — shows it in the monitor list's **Mode** row, and warns if you assign a LUT that can't apply in that mode (e.g. an SDR LUT to an HDR display). A LUT is applied only when its type matches the display's mode (see *HDR / SDR LUT selection*). WCG appears when Windows' Auto Color Management puts an ordinary SDR display into advanced colour; it is not HDR, though it uses the same FP16 composition path and therefore the same LUT slot.
- **SDR-in-HDR gamma fix**: Optional **On / Off** control, with a target-gamma dropdown (2.2 / 2.4 / 2.6, default 2.4), that patches DWM's SDR→scRGB conversion shaders so SDR content is mapped into the HDR space with a pure power curve instead of the piecewise sRGB curve Windows uses, which otherwise lifts near-blacks and washes SDR content out in HDR mode. Native HDR content is unaffected, and the fix is independent of the LUT (see *SDR-in-HDR gamma fix* below).

#### Project Layout:
- `DwmLutGUI/MainWindow.xaml`: Main user interface.
- `DwmLutGUI/MonitorData.cs`: Per-monitor model (identity, LUT paths, display index).
- `DwmLutGUI/Injector.cs`: Handles process discovery and DLL injection.
- `DwmLutGUI/MainViewModel.cs`: Core application logic, monitor enumeration, and config persistence.
- `DwmLutGUI/HdrInfo.cs`: Queries Windows for each display's advanced-colour mode (SDR / WCG / HDR).
- `DwmLutGUI/SingleInstance.cs`: Forwards a second launch's command-line arguments to the instance already running.
- `DwmLutGUI/EotfPatcher.cs`: Applies the SDR-in-HDR gamma fix by patching DWM's shader bytecode in memory.
- `DwmLutGUI/GuiDiag.cs`: Optional GUI-side diagnostic log (default off).

---

## Technical Deep Dive: Windows 11 25H2 Support

Windows 11 build 26200 (25H2) significantly refactored DWM's internal structures. This build addresses those changes for `dwmcore.dll` 10.0.26100.8246, 10.0.26100.8655 and 10.0.26100.8875 as follows.

### Per-Monitor Identification
Each overlay composition context reports **local, origin-`(0,0)` coordinates at native resolution** — not its global desktop position — so monitors cannot be told apart by the context's clip rectangle directly. 
This build reads each monitor's true **desktop origin from `self + 0x7658`** (two floats: `left, top`). Verified behavior: the primary monitor reads `(0, 0)`; a monitor positioned at desktop `x = -1280` reads `left = -1280`. That origin is matched against the position-named `.cube` files, so each context applies its own LUT. A strict 1:1 `context ↔ origin` ownership guard prevents any residual cross-assignment. (The monitor's native resolution is also available at `self + 0x4A24` as an alternate identifier.)

On **24H2 / 23H2 / 22H2 / 21H2 / Windows 10**, the origin is read from that version's own clip-box offset instead — `*(void**)self + 0x53E8` (24H2), `*(void**)self + 0x466C` (23H2 / 22H2 / Windows 11), `self + 0x462C` read **directly** (21H2 — no extra dereference, ledoge's original scheme), or `self - 0x120` read **directly** as an int RECT (Windows 10 — also ledoge's scheme, no extra dereference) — all through the same SEH-guarded read. The 24H2 offsets are ed1ii's, the 22H2/23H2 offsets are lauralex's, and the 21H2 and Windows 10 offsets (with their direct-read addressing) are ledoge's; the 21H2 path is hardware-validated (22000.1880, including a three-display layout with negative coordinates); the remaining legacy paths are supported but unverified on current hardware.

### Backbuffer Acquisition
DWM no longer exposes an `IDXGISwapChain` through standard patterns. The engine obtains each monitor's composed surface from the **`IOverlaySwapChain`** object by calling `vt[24]`, then `vt[19]` on the returned object, then `QueryInterface(ID3D11Texture2D)` to reach the backbuffer texture. If this cannot resolve a surface, that monitor's frame is skipped. (An earlier brute-force scan that probed arbitrary offsets for a swapchain pointer was removed — it could invoke unintended methods on non-swapchain pointers and destabilize composition.)

### MPO / DirectFlip Suppression
To ensure the LUT is applied even during DirectFlip or MPO (Multi-Plane Overlays), the engine forces the `OverlayTestMode` global to `5`. The global is resolved via the `COverlayContext::OverlaysEnabled` signature. `COverlayContext::IsCandidateDirectFlipCompatible` is also hooked; note its function prologue is ambiguous on this build (two matches), so the correct instance is disambiguated by its member offset.
Several finer-grained suppression functions (`CWindowContext`/`CCompSwapChain`/`CCompVisual` candidates) are **inlined** on this build and therefore not hookable as standalone functions; forcing `OverlayTestMode = 5` covers MPO suppression globally in their place.
On 25H2, `COverlayContext::OverlaysEnabled` is hooked through a small **register-preserving assembly thunk** rather than a direct C++ detour. DWM's `COverlayContext::OverlayPlaneInfo::IsDFlipOnMPO` dereferences `r8` after calling `OverlaysEnabled` and relies on it surviving the call (interprocedural register allocation, since the real callee only touches `rcx`/`al`); a plain detour clobbers `r8` and crashes DWM during fullscreen-overlay evaluation. The thunk (`OverlaysEnabled_thunk`) saves/restores `rcx`/`rdx`/`r8`–`r11` around the hook. Its use is selected per build by the `overlaysEnabledThunk` `DwmProfile` flag; older Windows versions use the C++ hook directly.

### Fullscreen LUT via a composition-blocker overlay
A LUT is only applied while DWM composites a surface. Fullscreen/borderless apps are often promoted to IndependentFlip (direct scanout) or a hardware overlay plane, bypassing composition. The GUI counters this from user space: while LUTs are active it watches (~400 ms poll) for a fullscreen app covering an applicable-LUT monitor and, when found, places a full-monitor, click-through, no-activate, almost-invisible (1/255-alpha) topmost window over it. That is "something composited on top", which disqualifies the output from IndependentFlip / overlay promotion and forces composition, so the LUT applies. 
The overlay exists only while fullscreen is detected and is removed as soon as it isn't. Overlays are positioned in true physical pixels (`EnumDisplayMonitors` + `SetWindowPos` under a Per-Monitor-v2 DPI context enumerated off the UI thread) so coverage is exact on mixed-DPI layouts.
Costs and limits: forcing composition disables IndependentFlip's efficiency for that surface (some added latency/power); the ~1/255-alpha overlay tints the covered monitor by ~0.4% (sub-perceptible but non-zero, before the LUT); a topmost overlay over a game may be flagged by anti-cheat; and **exclusive-fullscreen** apps bypass DWM (and the overlay) entirely and cannot be reached, use an in-game overlay (e.g. ImGui) for those.


### Multi-GPU Resource Model
Rendering resources are split into two levels:
- **Per adapter (`ID3D11Device`)** — immutable assets (shaders, samplers, blue-noise texture, the 3D LUT textures), each allocated on that adapter's device.
- **Per output** — mutable scratch (backbuffer-copy texture, render-target-view cache, constant buffer, last-applied state).

Before any draw, both the backbuffer and the LUT shader-resource-view are validated to belong to the **presenting** device; a mismatch skips the frame, so a resource created on the iGPU can never be bound into the dGPU's context (or vice-versa). Each adapter's immediate context is serialized so concurrent presents from multiple outputs cannot corrupt shared device state. Assets are created synchronously and cached on first use; a device whose assets fail to build is skipped rather than crashing.
Multiple adapter devices are held **simultaneously** — a hybrid laptop composites on more than one GPU at once — so the engine never treats the appearance of a second device as a reason to evict the first. (Doing so previously caused a per-frame evict/rebuild thrash whenever an external monitor was attached, seen as reduced animation smoothness.)

### Version Profiles
Each supported `dwmcore.dll` build is one **`DwmProfile`** row in `g_dwmProfiles[]`, and every row is self-contained: the dwmcore version, the four AOB signatures (embedded inline as fixed-size byte arrays, `'?'` = wildcard), and the build-specific offsets (clip-box + device-vector). At load, the engine reads the running `dwmcore.dll`'s file version and selects the newest row whose minimum version it satisfies (newest-first; a version-read failure falls back to the newest row; an unmatched build is skipped). The profiled builds are listed in [SUPPORTED_VERSIONS.txt](SUPPORTED_VERSIONS.txt); they all share identical signatures but differ in their device-vector addresses, and, from 8935 onwards, in the clip-box offset too (and 9278 additionally moved the device-lock offset and inlined the device erase — see *Device teardown*). Supporting a future build is a single new row (the 24H2 / 23H2 / legacy paths are not part of this table and are unchanged).

### HDR / SDR LUT selection
DWM composites an HDR display into an FP16 (scRGB) backbuffer and an SDR display into an 8/10-bit backbuffer, and the shader applies the LUT through an HDR (PQ/BT.2100) or SDR path accordingly — so a LUT is only valid for the mode it was calibrated in. The engine matches **exactly**: an HDR context takes the HDR LUT, an SDR context takes the SDR LUT. If the only LUT assigned to a display is the wrong type for its current mode, **no LUT is applied** rather than a mismatched one (which, run through the other path, would produce wrong colors). Use an HDR LUT (a `.cube` with `hdr` in the filename, calibrated in HDR) for a display in HDR mode, and an SDR LUT for SDR mode. A display in **WCG** mode (Auto Color Management on an SDR display) composites in FP16 like an HDR one, so it takes the **HDR** LUT slot — but the surface is not PQ-encoded the way true HDR10 is, so a LUT authored for an HDR10 display will not be correct there.

### Device teardown (fullscreen mode changes and monitor hot-plug)
Recent DWM builds run a **resource-leak checker** on every internal D3D device they destroy: it performs the device's final `Release` and deliberately breaks (`int 3`, crashing DWM) if the refcount does not come back zero.
Because the engine's LUT resources live on DWM's device — and in D3D11 every child object keeps its device alive — anything of ours still outstanding when DWM destroys a device turns a routine event into a `dwm.exe` crash.

DWM removes a device inside `CDeviceManager::DeleteUnusedDevices`, which erases on **either** of two tests: a per-device "lost" flag, or an idle path (the device's own refcount back to 1, no outstanding work, and past a grace deadline). The idle path is by far the common one, and it is not predictable from outside: its deadline is expressed in DWM's own composition units, so a wall-clock or frame-count estimate of "about to be erased" drifts against it whenever the compositing rate changes.

The engine therefore does not predict it. It hooks the **device-removal function** itself — the frame directly above `CD3DDevice::Release`, reached only when a device is actually being removed. That function is located from the `call rel32` at `eraseCallOffset` inside `DeleteUnusedDevices`, so no additional per-build address is stored. Crucially it is *not* `DeleteUnusedDevices` itself that is hooked: that runs every composited frame and usually removes nothing, so releasing there would destroy and rebuild every LUT asset each frame (a shader-compile/upload storm that shows up as heavy lag while dragging windows).

Which function that is depends on the build, selected per profile by a `TeardownPath` value:
- **`EraseHook`** (26100.8246 / 8655 / 8875 / 8935 / 9168): a standalone `std::vector<DeviceInfo>::erase` is the sole device-removal call; the engine hooks it directly. At its entry the device is still alive and its final `Release` has not run, so releasing there is correctly timed by construction.
- **`DeleteUnusedDevicesEntry`** (26100.9278): the `vector<DeviceInfo>::erase` is inlined into `DeleteUnusedDevices`, so there is no standalone function to hook. The inlined loop still calls a per-`DeviceInfo` **destroy helper** once per removed device, and `eraseCallOffset` points at that call instead — every call site of the helper is a device being destroyed, so it fires only on a genuine removal, exactly like the standalone erase. Adding a future build with a different removal shape means adding one `TeardownPath` enumerator and its handling.

The release drops every LUT asset and also **resets the 1:1 context-to-origin ownership map**: DWM destroys and recreates its overlay contexts across a topology change, and a recreated context resolving to an origin still "owned" by a destroyed one would otherwise be skipped and left without its LUT. Assets rebuild on the next frame, so the LUT returns automatically once the new configuration settles.

Three supporting measures close the remaining ways a reference could survive:
- **Render state is unbound after every draw.** D3D's immediate context holds references to whatever is bound to it, so releasing our own handles is not enough while our render target, shaders, samplers and SRVs are still bound.
- **Adapters with no applicable LUT are never touched.** Building assets takes a strong reference to that adapter's device; a display with no LUT must leave no footprint on its adapter, or unplugging it destroys a device we had no reason to hold.
- **Detach releases before unhooking.** The DLL sets its kill switch, drains in-flight hook bodies, releases every D3D reference, and only then removes its hooks — so there is no window in which a reference is held with no hook able to evict it.

(The LUT is still not *guaranteed* while an exclusive/mode-changed fullscreen is up — only the crash is prevented and the recovery afterward is clean.)

### Fail-Safe Design
- A process-wide **kill-switch** makes every hook return immediately once tripped, so DWM composites normally instead of crash-looping (tripped by the render-path exception boundary and on DLL detach).
- The render path runs behind a structured-exception boundary; any access violation is contained and latches the kill-switch rather than propagating into DWM.
- Assets are validated per device before every bind, and any adapter/output whose resources fail to build is skipped rather than crashing.

### SDR-in-HDR gamma fix
Windows maps SDR content into the HDR (scRGB) composition space using the **piecewise sRGB** transfer function. Virtually all SDR content is authored on gamma-2.2 displays, so the mismatch lifts near-blacks and flattens shadows in HDR mode, and Windows exposes no control over it.

sRGB is a **piecewise** transfer function: a short linear segment below `V = 0.04045` (there so the inverse curve doesn't need unbounded precision near black) and a power segment `((V + 0.055)/1.055)^2.4` above it. That toe is what makes its effective gamma flatter than 2.2 in the shadows, which is precisely where content graded on a pure-gamma display looks lifted.

This is corrected by rewriting the four sRGB constants inside DWM's own SDR→scRGB conversion shaders, collapsing the two pieces into one: breakpoint `0.04045` → `0` (nothing is ever ≤ 0, so the linear toe can never be selected), offset `0.055` → `0` and `1/1.055` → `1` (the power segment becomes plain `V^n`), and exponent `2.4` → the selected gamma. The target is chosen in the GUI from **2.2 / 2.4 / 2.6**, defaulting to **2.4** (BT.1886, the dark-room viewing standard, and ledoge's own default; 2.2 is the nominal sRGB authoring target and 2.6 is DCI); note that at 2.4 the exponent constant is left unchanged, because sRGB's own exponent is already 2.4 — the correction there comes entirely from removing the toe. The shaders are identified by their **DXBC checksum** rather than by offset — the same four hashes match unchanged from 21H2 (22000) through the 26H2 preview (26100.8935) — and every copy is patched, since dwmcore ships duplicates of some of them (4 sites on 26100.8246, 6 on 22000.1880).

**Why this cannot be done in the LUT shader.** The LUT runs in `COverlayContext::Present`, after composition, where SDR- and HDR-originated pixels are already blended into one surface; any transform there necessarily hits native HDR content too. These conversion shaders run before composition and only on SDR content, so patching them leaves HDR alone. The two are orthogonal — a **content-domain** correction and a **display-domain** correction — and compose cleanly.

**Timing.** DWM creates its pixel shader objects from these blobs at startup; patching the bytecode afterwards has no effect on shaders that already exist. Enabling or disabling the fix therefore restarts DWM, and the patch is applied from the GUI with the fresh process **suspended**, before it can build its shaders. For the same reason, changing the target gamma while the fix is already on does nothing until it is switched off and on again.

**Guards.** The checksum implementation is verified against unmodified blobs in the loaded image before anything is written; a mismatch aborts the patch. Shader and container checksums are both recomputed. Live state is read back out of DWM rather than remembered. Consecutive DWM restarts are rate-limited, because repeatedly rebuilding the display pipeline in quick succession can leave a multi-monitor configuration in a bad state.

---

## Reverse-engineering reference (verified against 26100.8246)

| Symbol                                             | Location                                                                                                                                                                 |
| -------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `COverlayContext::Present`                         | RVA `0x232A20` (unique)                                                                                                                                                  |
| `COverlayContext::OverlaysEnabled`                 | RVA `0x18893C` (unique)                                                                                                                                                  |
| `OverlayTestMode` global                           | RVA `0x3FE1C4` (`.data`), forced to `5`                                                                                                                                  |
| `COverlayContext::IsCandidateDirectFlipCompatible` | RVA `0x5E7D4` (member `0x4BF8`) — not `0x14818`                                                                                                                          |
| `IOverlaySwapChain` vtable                         | `.rdata` RVA `0x30CB48` (slot 24 = backbuffer-array accessor)                                                                                                            |
| Per-monitor desktop origin                         | `self + 0x7658` (float `left, top`)                                                                                                                                      |
| Per-monitor native resolution                      | `self + 0x4A24` (`0,0,W,H`) — alternate identifier                                                                                                                       |
| `CDeviceManager::ProcessDeviceLost`                | RVA `0x0EF370` (unique 33-byte prologue; no longer hooked)                                                                                                               |
| `CDeviceManager::DeleteUnusedDevices`              | RVA `0x0EF470` (unique 38-byte prologue; erases on lost-flag **or** idle test)                                                                                           |
| `std::vector<DeviceInfo>::erase`                   | RVA `0x0EFD14` — **hooked** on `EraseHook` builds; reached only from `DeleteUnusedDevices+0x47`, the sole device-removal path                                            |
| `DeleteUnusedDevices` device-lock `lea`            | `EraseHook` builds: `+0x0A`. On 26100.9278 the prologue grew and it moved to `+0x18` (stored per profile as `deviceLockLeaOffset`)                                       |
| per-`DeviceInfo` destroy helper (26100.9278)       | RVA `0x243CF4` — **hooked** on `DeleteUnusedDevicesEntry` builds; reached from `DeleteUnusedDevices+0x9E` (the inlined erase loop). Every call site is a device teardown |
| Device-manager `CRITICAL_SECTION`                  | `.data` RVA `0x3FDA60`, entered by `DeleteUnusedDevices+0x11`; guards the device vector                                                                                  |
| DWM device vector (`CDeviceManager`)               | `.data` `_Myfirst`/`_Mylast` RVA `0x3FDA88`/`0x3FDA90`; `DeviceInfo` stride `0x10`; lost-flag `device+0x458`                                                             |

***26100.8655 delta:** same structure, shifted RVAs — `Present` `0x231800`, `OverlaysEnabled` `0x1A2BE8`, `IsCandidateDirectFlipCompatible` `0xB1414` (member `0x4BF8`), `ProcessDeviceLost` `0xDCF80`, and device vector `_Myfirst`/`_Mylast` `0x3FAB78`/`0x3FAB80`. Signature bytes, clip-box `0x7658`, stride `0x10`, and lost-flag `0x458` are unchanged.*
***26100.8875 delta:** signature bytes unchanged (all four match uniquely) — `Present` `0x231530`, `OverlaysEnabled` `0xA048`, `IsCandidateDirectFlipCompatible` `0x6E1F4`, `ProcessDeviceLost` `0xB3780`. Device vector `_Myfirst`/`_Mylast` moved to `0x3FCC98`/`0x3FCCA0`; clip-box `0x7658`, stride `0x10`, and lost-flag `0x458` unchanged.*
***26100.8935 delta (26H2 preview, OS build 26300):** signature bytes unchanged (all four match uniquely) — `Present` `0x22F5B0`, `OverlaysEnabled` `0x1CDF08`, `IsCandidateDirectFlipCompatible` `0x15C094`, `ProcessDeviceLost` `0xB92D0`. Device vector `_Myfirst`/`_Mylast` moved to `0x3FAD38`/`0x3FAD40`, and the `DeviceClipBox` moved for the first time since 8246: **`self + 0x7648`**. `0x7658` still exists but now holds an int monitor-**local** box that always starts at `(0,0)`, so reading the old offset makes every context report the same origin and only the primary display receives a LUT. Stride `0x10` and lost-flag `0x458` unchanged. Verified on a live 3-monitor 26H2-preview VM. Preview build — these offsets may shift before 26H2 ships.*

## Verified dwmcore values

| Field                                      | 8246                          | 8655                          | 8875                   | 8935 (26H2 preview)    | changed?   |
| ------------------------------------------ | ----------------------------- | ----------------------------- | ---------------------- | ---------------------- | ---------- |
| `minVersion`                               | `DWM_VER(26100, 8246)`        | `DWM_VER(26100, 8655)`        | `DWM_VER(26100, 8875)` | `DWM_VER(26100, 8935)` | **yes**    |
| `Present` sig                              | @ RVA 0x232A20                | @ RVA 0x231800                | @ RVA 0x231530         | @ RVA 0x22F5B0         | same bytes |
| `OverlaysEnabled` sig                      | @ RVA 0x18893C                | @ RVA 0x1A2BE8                | @ RVA 0xA048           | @ RVA 0x1CDF08         | same bytes |
| `IsCandidateDF` sig                        | @ RVA 0x5E7D4 (member 0x4BF8) | @ RVA 0xB1414 (member 0x4BF8) | @ RVA 0x6E1F4          | @ RVA 0x15C094         | same bytes |
| `ProcessDeviceLost` sig                    | @ RVA 0xEF370                 | @ RVA 0xDCF80                 | @ RVA 0xB3780          | @ RVA 0xB92D0          | same bytes |
| `clipBoxOffset`                            | `0x7658`                      | `0x7658`                      | `0x7658`               | **`0x7648`**           | **yes**    |
| `deviceVecFirstRva` (_Myfirst)             | `0x3FDA88`                    | `0x3FAB78`                    | `0x3FCC98`             | **`0x3FAD38`**         | **yes**    |
| `deviceVecLastRva` (_Mylast)               | `0x3FDA90`                    | `0x3FAB80`                    | `0x3FCCA0`             | **`0x3FAD40`**         | **yes**    |
| `deviceInfoStride`                         | `0x10`                        | `0x10`                        | `0x10`                 | `0x10`                 | same       |
| `deviceLostFlagOffset`                     | `0x458`                       | `0x458`                       | `0x458`                | `0x458`                | same       |
| `DeleteUnusedDevices` sig                  | @ RVA 0xEF470                 | @ RVA 0xDD080                 | @ RVA 0xB3880          | @ RVA 0xB93D0          | same bytes |
| `vector<DeviceInfo>::erase`                | @ RVA 0xEFD14                 | @ RVA 0xDD924                 | @ RVA 0xB4124          | @ RVA 0xB9C74          | derived    |
| device `CRITICAL_SECTION`                  | `0x3FDA60`                    | `0x3FAB50`                    | `0x3FCC70`             | `0x3FAD10`             | derived    |
| `eraseCallOffset` / `deviceRefCountOffset` | `0x47` / `0x08`               | `0x47` / `0x08`               | `0x47` / `0x08`        | `0x47` / `0x08`        | same       |

Additional 25H2 profiles (only the fields that differ from 8935 are interesting):

| Field                          | 9168                       | 9278                                |
| ------------------------------ | -------------------------- | ----------------------------------- |
| `minVersion`                   | `DWM_VER(26100, 9168)`     | `DWM_VER(26100, 9278)`              |
| `clipBoxOffset`                | `0x7648`                   | `0x7648` (verified 2-monitor)       |
| `deviceVecFirstRva` (_Myfirst) | `0x3FAD58`                 | `0x40EDC8`                          |
| `deviceVecLastRva` (_Mylast)   | `0x3FAD60`                 | `0x40EDD0`                          |
| `deviceInfoStride`             | `0x10`                     | `0x10`                              |
| `deviceLostFlagOffset`         | `0x458`                    | `0x458`                             |
| `deviceRefCountOffset`         | `0x08`                     | `0x08`                              |
| `teardownPath`                 | `EraseHook`                | `DeleteUnusedDevicesEntry`          |
| `deviceLockLeaOffset`          | `0x0A`                     | `0x18`                              |
| `eraseCallOffset`              | `0x47` (→ `vector::erase`) | `0x9E` (→ per-`DeviceInfo` destroy) |

On 9278 the device-vector globals, the erase target and the `CRITICAL_SECTION` are all still derived at load; `vector<DeviceInfo>::erase` has no standalone address there because it is inlined.

---

## Command line and single instance

`-apply`, `-disable`, `-minimize` and `-exit` may be combined. `-apply` / `-disable` are mutually exclusive and act first; `-exit` is evaluated next and quits regardless of the other flags; `-minimize` folds the window to the tray. Matching is exact and case-sensitive — `-apply` works, `-Apply` does not.

Only one instance may run, because it owns the injected DLL and the tray icon. A second launch **with** arguments does not start a second copy: it forwards them over a session-scoped named pipe to the instance already running, which acts on them, and then exits silently — so the flags above keep working while the tool sits in the tray, and a script is never left waiting on a dialog. A second launch with **no** arguments shows a "Single instance guard" notice instead. A forwarded `-apply` deliberately does not raise the window.

## Known Limitations

- **Binary-version lock:** Signatures and offsets are valid for `dwmcore.dll` 10.0.26100.8246, 10.0.26100.8655, 10.0.26100.8875 (Windows 11 25H2, builds 26200.8246 / 8655 / 8875) and 10.0.26100.8935 (Windows 11 26H2 preview, build 26300), ImageBase `0x180000000`, thus the tool is not guaranteed to work on older 25H2 builds for which LUT application is skipped entirely as a safety measure. The 8935 profile is derived from a **preview** build and may need revisiting when 26H2 ships. 
  Support for older Windows versions (20H2, 21H1, 22H2, 23H2, 24H2) has been kept but not evaluated; 21H2 (22000) is hardware-validated.
- **Static C++ runtime (build requirement):** the injector links the C++ runtime statically (`/MT`), so `lutdwm.dll` carries no dependency on the target's `msvcp140.dll` / VC++ redistributable. A dynamic-CRT (`/MD`) build made with the VS2022 17.10+ toolset crashes in `msvcp140!mtx_do_lock` — a null dereference inside `std::mutex::lock`, on the first mutex taken in the LUT path — on any machine whose VC++ runtime predates 14.40, because that toolset's `constexpr` `std::mutex` layout is incompatible with the older runtime. This is unrelated to the dwmcore version; static linking removes it outright.
- **SDR-in-HDR gamma fix restarts DWM:** Because DWM builds its pixel shaders at startup, switching the fix on or off requires a fresh DWM — a brief black flash, and any per-session compositor state is rebuilt. Consecutive toggles are therefore rate-limited (the buttons are disabled with a short countdown between them): restarting the display pipeline several times in quick succession has been observed to leave a multi-monitor setup in a bad state, with a display dropping out and scaling/HDR reset until reconnected or rebooted. Set the fix once rather than toggling it repeatedly. The patch is memory-only — it never modifies `dwmcore.dll` on disk and is gone after any DWM restart or reboot — so a normal LUT Apply / Disable, which does *not* restart DWM, will not carry it over.
- **A WCG display needs its own LUT, not an HDR10 one:** when Auto Color Management puts an SDR display into WCG (advanced color), Windows composites it into the same FP16 (scRGB) surface as HDR, so the LUT is taken from the **HDR** slot and the shader applies it in the PQ / BT.2100 domain. The panel is still a wide-gamut display at SDR luminance, though, not an HDR10 one — its peak luminance and response are different — so a LUT authored for, or measured on, a genuine HDR10 display will not be correct there. A LUT for a WCG display has to be measured on that display while it is in WCG mode. Turning "Automatically manage color for apps" off returns the display to plain SDR and to the SDR LUT slot.
- **Autostart needs the scheduled task to be creatable:** autostart is a Task Scheduler entry running with highest privileges (a plain `Run` registry entry would prompt for UAC at every logon, since the app requires administrator rights). If policy or an error prevents `schtasks` from registering it, the failure is reported and the setting stays off rather than silently appearing to have worked. The task carries a 15-second delay, because logon fires before the display topology has settled and applying immediately can run against monitors that are not yet enumerated.
- **Device-teardown safety depends on one unguarded offset:** the release that keeps `dwm.exe` alive across a device removal is a hook on whichever function actually removes a device (see *Device teardown*): `std::vector<DeviceInfo>::erase` on `EraseHook` builds, or the per-`DeviceInfo` destroy helper on `DeleteUnusedDevicesEntry` builds. Either way its address is derived from the `call rel32` at `eraseCallOffset` inside `CDeviceManager::DeleteUnusedDevices` (`0x47` on the older builds, `0x9E` on 26100.9278). That function's AOB signature does not cover the call site, so a future build could match the signature while having moved the call. This is checked at load — the byte must be `0xE8` — and a mismatch is logged as `FAILED to resolve device-removal function - device teardown is UNPROTECTED`, which is the line to look for if monitor hot-plug starts crashing DWM again after a Windows update. The fix in that case is one number (`eraseCallOffset`) in the profile.
- **Crash proof, update vulnerable:** When a Windows update breaks the tool, the symptom points to the cause:
  - *Nothing happens at all* → a `COverlayContext` **signature** moved (most likely `Present`), or the running dwmcore has **no matching profile**.
  - *Wrong monitor / wrong colors, or only the primary display gets its LUT* → the clip-box **offset** moved (per build: `0x7658` on 8246 / 8655 / 8875, `0x7648` on 8935), or `GetBackBuffer_25H2`'s `vt[24]`/`vt2[19]` indices moved. Note that dwmcore also carries a monitor-**local** clip box a few fields away that always reads `(0,0)`; picking that one by mistake makes every display collide on one origin, so only the primary is color-managed. The `DIAG_MONITOR_MATCH` build switch dumps every clip-box-shaped RECT per context and is the reliable way to tell them apart on a multi-monitor layout.
  - *Flicker / LUT dropping out on a surface* → `OverlayTestMode` / the overlay hooks moved.
  - *DWM crashes on a monitor being connected/disconnected, or on a fullscreen mode change* → the device-teardown release is not firing. Check the diagnostic log for `FAILED to resolve device-removal function - device teardown is UNPROTECTED`: that means the `CDeviceManager::DeleteUnusedDevices` signature still matched but the `call rel32` at `eraseCallOffset` moved, so `eraseCallOffset` needs updating (`0x47` on `EraseHook` builds, `0x9E` on 26100.9278). If instead the log shows `FAILED to find CDeviceManager::DeleteUnusedDevices (signature miss)`, the signature itself moved. The crash is always the same `CD3DResourceLeakChecker` `int 3` in `dwmcore`, reached via `DeleteUnusedDevices` → the removal function → `CD3DDevice::Release`. (The device-vector offsets and `deviceLostFlagOffset` are read only by the `DIAG_MONITOR_MATCH` build and cannot cause this.)
  - *DWM crashes when a video/app goes fullscreen (overlay path)* → a hooked overlay function is being relied upon by DWM to preserve a volatile register across the call. On 25H2 `OverlaysEnabled` is left unhooked for this reason; if a similar crash appears with another overlay hook (`IsCandidateDirectFlipCompatible` family) in the stack, it needs the same treatment.
  Adding support for a new build is a **single prepended `g_dwmProfiles[]` entry**, but obtaining the values is a reverse-engineering pass (disassembly + live capture).
- **Exclusive / mode-changed fullscreen is not *guaranteed* to be color-managed:** (e.g. old DirectDraw games switching to a native-resolution fullscreen): such surfaces bypass DWM composition, so the LUT is not reliably reachable (matches ledoge's original limitation). This **no longer crashes DWM** and **recovers its LUTs cleanly on exit**, even on a multi-GPU / multi-monitor setup. The LUT may remain applied through such a fullscreen DWM bypass, now that resources stay stable across the transition, but that is not guaranteed. A LUT is applied only while DWM **composites** a surface. When a fullscreen or borderless game presents a flip-model swapchain that DWM promotes to **IndependentFlip** (direct scanout), the frames bypass composition entirely, so no LUT can be applied. This is a DWM decision, made per frame from swapchain state, occlusion, the mouse cursor, and MPO capability, with **no hookable entry point on 25H2** — the relevant `CCompSwapChain` / `CWindowContext` flip-candidate checks are unreachable there. Exclusive-fullscreen apps bypass DWM outright and likewise cannot be reached. Windowed and *composited* fullscreen surfaces (most fullscreen browser video, and legacy fullscreen games that DWM still composites) do get the LUT.
- **Occasional partial repaint right after Apply:** On multi-monitor (especially mixed-DPI/rotated) setups, a region of a display may briefly keep its pre-LUT pixels immediately after Apply, until something redraws that area (moving the mouse or a window over it refreshes it instantly). This is cosmetic and self-healing, the LUT is applied; DWM just hasn't re-composited that region yet. It does not affect color correctness once the surface refreshes.

---

*Last Updated: 7 September 2026*
