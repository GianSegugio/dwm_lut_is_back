# DwmLut Is Back Changelog

## Note on environment tuning

Since the switch to the Windows 11 Germanium Platform, DWM internals got updated and [lauralex/dwm_lut](https://github.com/lauralex/dwm_lut) was not working anymore. As for [ed1ii/dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed) it was developed to bring support up to 25H2 (Canary), but newer 25H2 builds broke DwmLut again.  
This version of DwmLut is tuned for `dwmcore.dll` **10.0.26100.8246** and **10.0.26100.8655** (Windows 11 25H2, builds 26200.8246 and 26200.8655), ImageBase `0x180000000`. All signatures/offsets are valid for those 25H2 binaries, thus the tool is not guaranteed to work on older 25H2 builds for which LUT application is skipped entirely as a safety measure. Support for 24H2 (direct composition), and 23H2 (build 22631) has been kept, but no evaluation has been conducted for such legacy versions.

---
---
## v1.0.0

### C# UI — `DwmLutGUI`

#### `MonitorData.cs`
- **Identity fields could be `null`** → **never null.** The constructors stored `Name = name` (and the config-only constructor set no `Name` at all), so `Name`/`DevicePath`/`Position` could be `null`. Now all identity fields are coalesced to safe defaults (`""` / `"???"`).
- **No stable display number** → **added `DisplayIndex`.** A globally-unique 1-based ordinal assigned during enumeration (see the "#" column below).
- **`HdrLutPath` setter had no null guard** (unlike `SdrLutPath`) → **guard added**, and null values are no longer added to the LUT collections.

#### `MainViewModel.cs`
- **`SaveConfig` crashed on null attribute values** → **can no longer crash.** It built
  `new XAttribute("path", x.DevicePath)` / `new XAttribute("name", x.Name)` directly; a null value threw `ArgumentNullException` on every LUT select/clear. Now every attribute value is null-coalesced, null LUT entries are filtered, and the whole method is wrapped so a failed write is non-fatal.
- **Monitors keyed for the "#" column by per-adapter `SourceId`** → **by unique `DisplayIndex`.** `DisplaySource.SourceId` is per-GPU and collides across adapters (producing `1, 1, 2`). Monitor tracking/matching is keyed on `DevicePath`, with a synthesized stable key when a device path is absent.
- **The internal laptop panel showed `???` in the monitor list's Name column.** The name comes from `DisplayTarget.FriendlyName`, which is populated from the monitor's EDID product-name descriptor — something internal panels frequently omit, so the value was empty and fell back to `???`. When the name is empty, enumeration now inspects `OutputTechnology` and, for an internal/embedded panel (reported as `Internal`, or `…Embedded` on eDP laptops), labels it **"Internal Display"** instead. This mirrors what Windows Settings shows for a display with no EDID name. Other empty-name cases (e.g. a rare external monitor with no EDID name) still show `???`, since the display is genuinely unidentified there.
- **A monitor's LUT could vanish from the config after a display change** → **fixed (save-suppression).** `MonitorData`'s constructor sets `SdrLutPath`/`HdrLutPath` through setters that raise `StaticPropertyChanged` → `SaveConfig`. Because monitors are constructed *during* `UpdateMonitors` (which clears and rebuilds the list), `SaveConfig` ran on a **half-built** list and wrote `config.xml` without the monitors not yet re-added, dropping whichever display was enumerated last (it then matched nothing on the next enumeration). `SaveConfig` is now suppressed while `UpdateMonitors` rebuilds; user-driven saves are unaffected.

#### `MainWindow.xaml`
- **"#" column bound to `SourceId`** → **bound to `DisplayIndex`.** `Binding="{Binding SourceId}"` → `Binding="{Binding DisplayIndex}"`.

#### `MainWindow.xaml.cs`
- **"Disable and exit" disabled the LUT but did not exit** (it hid to tray) → **fixed.** The `Closing` handler hides-to-tray unless `_isExiting` is set, and this handler called `Close()` without setting it. It now sets `_isExiting = true` first. The same omission in the constructor's init-crash `catch` was fixed too, so an init failure exits cleanly instead of hiding a broken instance.

---

### C++ injector — `lutdwm/dllmain.cpp`

#### Per-monitor identification (the wrong-LUT-per-monitor fix)
- **Was:** the 25H2 path read the composition context's clip box at `*(void**)context + 0x4D0`.
  On this build that region is **all zeros**, so every monitor resolved to origin `(0,0)` and only the primary monitor's LUT was ever matched.
- **Is:** the per-monitor desktop origin is read from **`self + 0x7658`** (floats → `left, top`).
  Each context now resolves to its own origin (verified: primary `(0,0)`; a monitor at desktop x = −1280 reads `left = −1280`), so each monitor matches its own position-named LUT. A strict 1:1 `context ↔ origin` ownership guard (`ClaimPosition`) prevents any residual cross-assignment.
- **Older Windows** (24H2 / 23H2 / Win10) use their own clip-box offsets — `*(void**)self + 0x53E8` (24H2), `+ 0x466C` (23H2/Win11), `− 0x120` (Win10) — carried forward from the ed1ii base through the same SEH-guarded read. These paths are supported but unverified on current hardware.

#### Backbuffer acquisition (the DWM-crash fix)
- **Was:** the Present hook tried `GetBackBuffer_25H2`, and on failure fell back to a **brute-force swapchain scan** over `overlaySwapChain + [0x80, 0x240)`, calling `IDXGISwapChain::GetDevice` on each candidate. Candidates that were not real swapchains (or not COM objects at all) invoked unintended vtable methods, destabilizing DWM composition — a GPU-side fault that crashed DWM on every frame.
- **Is:** the scan is **removed**. `GetBackBuffer_25H2` (the `vt[24] → vt2[19] → QueryInterface(ID3D11Texture2D)` traversal) is retained and, on this hardware, resolves each monitor's composed surface. If it ever returns NULL, that monitor's frame is **skipped**, not scanned. `GetBackBuffer_25H2` also gained desc-sanity validation (reject implausible surfaces).

#### Resource / device model
- **Was:** a single per-device `struct DeviceContext` map (`g_deviceContexts`, keyed by `ID3D11Device*`) holding all resources; created synchronously with no validation.
- **Is:** a two-level model — **`AdapterAssets`** per device (immutable, `ComPtr`-owned: shaders, samplers, noise, LUT 3D textures) + **`OutputRes`** per output (scratch, RTV cache, constant buffer, last-state). Built synchronously and cached, under a **double guard** (C++ `try/catch` + raw-pointer SEH), so a bad adapter is skipped instead of crashing.
- **Constant-buffer state (`lastLutSize` / `lastIsHdr`) was a function-`static`** shared across all devices → **now stored per `OutputRes`.** The shared static caused a second GPU to sample the LUT with the wrong size/HDR flag (posterization / wrong colors on multi-GPU).

#### Cross-adapter safety
- **Added `ResourceOnDevice()`** — before every bind, both the backbuffer and the LUT SRV are verified to belong to the presenting device; a mismatch skips the frame (no cross-adapter binding).
- **Added `SafeGetDeviceFromSwapChain()`** — validates a swapchain's vtable is readable and calls `GetDevice` under SEH, so an untrusted pointer fails safely.
- **Added per-adapter immediate-context serialization** (`ctxMutex`) — prevents concurrent use of one device's non-thread-safe `ID3D11DeviceContext` when multiple outputs present simultaneously.

#### Signature resolution (26100.8246)
- **`COverlayContext::IsCandidateDirectFlipCompatible`: was "first match wins"** → **disambiguated.**
  The shared function prologue matches two functions (`0x14818`, member `0x1B0`; and `0x5E7D4`, member
  `0x4BF8`); the correct `COverlayContext` instance (`0x5E7D4`) is now selected by its large member
  offset. The old behavior hooked the wrong function.
- **Removed a fragile `48 8D 05` heuristic** that could lock onto a stray `lea` past the tiny
  `OverlaysEnabled` function.
- `CWindowContext::IsCandidateDirectFlipCompatible`, `CCompSwapChain::IsCandidateDirectFlipCompatible`,
  and `CCompVisual::IsCandidateForPromotion` are **inlined on this build (zero standalone matches)** and
  left unhooked; their `if (...orig != NULL)` guards make this harmless, and forcing
  `OverlayTestMode = 5` covers MPO suppression globally.

#### Per-dwmcore-binary profile
- **Was:** the 25H2 signatures/offsets were loose globals referenced directly by the scan and `GetLUTData`.
- **Is:** a **`DwmProfile`** table (`g_dwmProfiles[]`) — one self-contained entry per dwmcore build, carrying the AOB signatures (`Present`, `OverlaysEnabled`, `IsCandidateDirectFlipCompatible`, `ProcessDeviceLost`) **and** the clip-box + device-vector offsets together, keyed by `DWM_VER(build, rev)`.
  `g_dwmcoreVersion` is captured at `DllMain` from the loaded DLL's file version; `SelectDwmProfile()` picks the newest entry with `minVersion <= version` (newest-first; `ver == 0` falls back to the newest known).
  Adding a future build is one prepended entry. The 24H2 / 23H2 / legacy paths are untouched.

#### Fullscreen device-lost crash (the DWM-leak-checker fix)
- **Was:** a display-mode change while a LUT was active (e.g. 3D Pinball entering native-resolution fullscreen) **crashed DWM** — `int 3` in `CD3DResourceLeakChecker` as DWM tore down an internal `CD3DDevice` that still had our LUT resources on it. (`CD3DDevice` is DWM's internal wrapper, not the `ID3D11Device` we cache — which is why our device kept reporting `S_OK` and naive eviction never fired.)
- **Is:** `CDeviceManager::ProcessDeviceLost` (RVA `0xEF370`) is hooked; at its entry, **all** LUT assets are released **only when DWM is actually about to erase a device** — determined by reading DWM's own device vector (`AnyDwmDeviceLost()`: `.data` `_Myfirst`/`_Mylast`, `0x10` stride, lost-flag at `device+0x458`, SEH-guarded). 
  Because `ProcessDeviceLost` runs every frame, the release is **gated**, not unconditional. Assets rebuild on the recovered device next frame, so the LUT returns on fullscreen exit. The same release also **clears the context↔origin ownership map** (`g_positionOwner`); otherwise a recreated overlay context resolving to an origin still "owned" by a now-destroyed context would be skipped, leaving that monitor without its LUT after the mode change. The LUT is still not *guaranteed* during exclusive/mode-changed fullscreen — only the crash is fixed and recovery is clean.

#### Multi-GPU device coexistence
- **Was:** `FindOrRequestAdapter` treated any not-yet-seen `ID3D11Device` as a device *replacement* and evicted every other device's LUT assets.
- **Is:** devices **coexist**. On a hybrid multi-GPU machine DWM composites on more than one device at once (one per adapter), so treating "a different device appeared" as a swap caused a **per-frame evict/rebuild thrash** — compositor stutter whenever an external monitor was attached (~150 rebuilds/session alternating between two adapter LUIDs). The eviction was removed; genuine teardown is handled by the `ProcessDeviceLost` gated release above.

#### Fail-safe & diagnostics
- **`g_hookInert` kill-switch** — on an unrecoverable render fault, all hooks return immediately and DWM
  composites normally instead of crash-looping (set by the `RenderLUT` SEH boundary and at `DLL_PROCESS_DETACH`).
- **`RenderLUT_Guarded()`** — a raw-pointer SEH boundary around the render path (required because the
  render path holds `ComPtr` locals and cannot host `__try` directly); latches inert on any fault.
- **`GetDeviceLuidKey()`** — resolves each device's true adapter LUID.
- **`diag_log()`** — cold-path logging to `C:\Windows\Temp\dwm_diag.log` (dwmcore version at startup, adapter build, device-lost eviction, errors; never per frame). Currently disabled in the code (hard-coded `return;` at function begin), see **`lutdwm/dllmain.cpp`** if restore is needed.

---

### Reverse-engineering reference — verified against 26100.8246

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
**26100.8655 delta:** same structure, shifted RVAs — `Present` `0x231800`, `OverlaysEnabled` `0x1A2BE8`, `IsCandidateDirectFlipCompatible` `0xB1414` (member `0x4BF8`), `ProcessDeviceLost` `0xDCF80`, and device vector `_Myfirst`/`_Mylast` `0x3FAB78`/`0x3FAB80`. Signature bytes, clip-box `0x7658`, stride `0x10`, and lost-flag `0x458` are unchanged.

---

*Last Updated: 06 July 2026*
