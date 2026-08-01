# DwmLut Is Back Changelog

## Note on environment tuning

Since the switch to the Windows 11 Germanium Platform, DWM internals got updated and [lauralex/dwm_lut](https://github.com/lauralex/dwm_lut) was not working anymore. As for [ed1ii/dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed) it was developed to bring support up to 25H2 (Canary), but newer 25H2 builds broke DwmLut again.  
This version of DwmLut is tuned for `dwmcore.dll` **10.0.26100.8246**, **10.0.26100.8655**, **10.0.26100.8875** (Windows 11 25H2, builds 26200.8246 / 8655 / 8875) and **10.0.26100.8935** (Windows 11 26H2 preview, OS build 26300), ImageBase `0x180000000`. All signatures/offsets are valid for those binaries, thus the tool is not guaranteed to work on older 25H2 builds for which LUT application is skipped entirely as a safety measure. Windows 11 21H2 (22000) is now hardware-validated as well; the remaining legacy versions have been kept but not evaluated.

---
---

## v1.2.0

### New feature — SDR-in-HDR gamma fix (`DwmLutGUI/EotfPatcher.cs`)

Windows composites SDR content into the HDR (scRGB) space using the **piecewise sRGB** transfer function. Practically all SDR content is authored on gamma-2.2 displays, so in HDR mode near-blacks are lifted and SDR content looks washed out, with no Windows setting to change it. v1.2.0 adds an optional fix, exposed as a target-gamma dropdown plus **On / Off** buttons next to the LUT Apply / Disable buttons.

- **What it does.** Rewrites the sRGB constants inside DWM's own SDR→scRGB conversion shaders so the piecewise curve collapses to a pure power law: breakpoint `0.04045` → `0`, offset `0.055` → `0`, `1/1.055` → `1`, exponent `2.4` → the selected gamma. Removing the breakpoint means the linear toe near black can never be taken, which is where sRGB and a pure gamma curve actually differ; the offset and scale collapse `((V + 0.055)/1.055)^2.4` to plain `V^gamma`. The shaders are located by their **DXBC checksum**, not by offset, and every copy is patched (dwmcore ships duplicates of some of them, so the site count is build-dependent: 4 on 26100.8246, 6 on 22000.1880).
- **Selectable target gamma.** A dropdown left of the On/Off buttons offers **2.2** (PC/sRGB nominal), **2.4** (BT.1886, dark room — also ledoge's own default) and **2.6** (DCI), defaulting to **2.4**. Changing it while the fix is live deliberately does nothing to the running DWM: the value is baked into pixel shaders that already exist, so it only takes effect on the next Off → On cycle.
- **Why it is not part of the LUT.** The LUT runs in `COverlayContext::Present`, i.e. *after* composition, where SDR- and HDR-originated pixels are already blended and indistinguishable — any correction there would hit native HDR content too. These shaders run *before* composition and only on SDR content. The two corrections are orthogonal and compose cleanly: this is a **content-domain** fix, the LUT is a **display-domain** fix, so correctly-mapped SDR and calibrated native HDR can be had at the same time.
- **Why DWM is restarted.** DWM builds its pixel shader objects from these blobs during startup; once those objects exist, patching the bytecode has no effect. Switching the fix on — and equally switching it off — therefore requires a fresh DWM (a brief black flash). Patching is done **from the GUI, with the target process suspended**, immediately after the restart and before the shaders are created, which removes any race. Writes go to DWM's private copy of the mapped image: `dwmcore.dll` on disk is never modified, and the patch disappears on any DWM restart or reboot.
- **Safety.** Before patching anything, the checksum implementation is verified against several unmodified DXBC blobs in the loaded image; if it does not reproduce their stored checksums exactly, nothing is patched. Both the shader blobs and the container blobs they are nested in have their checksums recomputed after the edit.
- **State is read back, not remembered.** Whether the fix is live is determined by comparing DWM's mapped `dwmcore.dll` against the copy on disk, so the button state stays correct across GUI restarts and reflects a DWM patched by any means.
- **Restart pacing.** Restarting DWM tears down and rebuilds the whole display pipeline; doing it repeatedly in quick succession was observed to leave a multi-monitor setup in a bad state (a display dropping out, scaling and HDR reset). Consecutive restarts are therefore spaced by a minimum interval, during which the On/Off buttons are disabled and the row label shows a short countdown, so repeat clicks cannot queue into a burst.
- **Independent of the LUT.** Turning the fix on or off leaves the LUT exactly as it was — applied or not — and a normal LUT Apply / Disable never restarts DWM.
- **Third-party code.** The DXBC checksum routine is a C# port of AMD's `CalculateDXBCChecksum` (GPUOpen `common-src-ShaderUtils`, MIT), itself derived from the RSA Data Security, Inc. MD5 Message Digest Algorithm. Both notices were added to `LICENSE-THIRD-PARTY`.
- **Validated** on Windows 11 25H2 (26100.8246) and 21H2 (22000.1880), single and dual monitor, with the LUT both active and inactive.

### C++ injector — `lutdwm/dllmain.cpp`

#### Windows 11 26H2 preview — `dwmcore.dll` 10.0.26100.8935 support
- **New profile row.** 8935 is newer than the last profiled build, so without a matching row `SelectDwmProfile` would fall back to the 8875 offsets. A dedicated `DwmProfile` row for `DWM_VER(26100, 8935)` now sits at the top of `g_dwmProfiles[]`.
- **RE delta (8935 vs 8875).** All four AOB signatures are byte-for-byte identical and still match uniquely — `Present` `0x22F5B0`, `OverlaysEnabled` `0x1CDF08`, `IsCandidateDirectFlipCompatible` `0x15C094`, `ProcessDeviceLost` `0xB92D0`. Two things moved: the device-vector globals `_Myfirst`/`_Mylast` → `0x3FAD38`/`0x3FAD40`, and — for the first time since 8246 — the per-monitor **`DeviceClipBox` moved to `self + 0x7648`**. `0x7658` still exists on 8935 but now holds an *int* monitor-**local** box that always begins at `(0,0)`; read as a float it yields `(0,0)` for every context, which on a multi-monitor setup makes every display collide on one origin so only the primary receives its LUT. Stride `0x10` and lost-flag `0x458` are unchanged.
- **Verification.** Confirmed on a live 3-monitor 26H2-preview VM: at `self + 0x7648` the displays read `(0,0)`, `(-1200,-22)` and `(3840,-22)` and each matched its own LUT. **This is a preview build**; its layout may shift again before 26H2 ships.

#### Windows 11 21H2 tier — now hardware-validated
- The 21H2 tier (builds 22000–22620) was added in v1.1.1 from ledoge's values and had never been exercised on real hardware. It is now validated on 10.0.22000.1880 across single-monitor, USB-C and HDMI hot-plug, and a three-display layout with an HDR primary at `(0,0)` plus SDR displays at `(-300,-2160)` and `(-1500,-2176)` — confirming the direct-read float clip box at `self + 0x462C`, including negative coordinates.

### GUI — `DwmLutGUI`

#### New monitor table
- **Was:** a `DataGrid` with one row per monitor and ten columns, plus a separate panel at the top that browsed and cleared the LUT of whichever row was *selected*.
- **Is:** the table is transposed — properties are rows with full-width labels on the left, each monitor is a column. Since a machine has few monitors and many properties, this reads far better and the labels are no longer truncated. Each column is **self-contained**: name, index, connector, position, mode, status, and its own SDR and HDR pickers with **Browse / Next / Clear** buttons. The notion of a "selected monitor" and the entire top panel are gone, which removes the "which monitor am I editing?" ambiguity; the Apply/Disable hotkey selector moved to the bottom bar. Alternating rows are tinted slightly for readability, and columns scroll horizontally if more monitors are connected than fit.
- **Browse also registers the file.** Picking a LUT now adds it to that monitor's list (so **Next** can cycle to it) and takes effect immediately when a LUT is already applied.

#### Display-topology robustness
- `QueryDisplayConfig` legitimately fails with `ERROR_NOT_SUPPORTED` while the display topology is in flux — exactly the state a DWM restart, a monitor hot-plug or a mode change produces. The monitor refresh now queries the topology **before** clearing anything and retries briefly, so a transient failure no longer surfaces as an unhandled exception, and never leaves the GUI with an empty monitor list. Display-change events caused by our own DWM restart are ignored while the operation is in progress, so they cannot trigger a re-inject mid-restart. Restarting DWM is also scoped to the current session, so other logged-in users' desktops are untouched.

#### Diagnostics — `DwmLutGUI/GuiDiag.cs`
- Added a GUI-side diagnostic log (default **off**, `GuiDiag.Enabled`) writing to `C:\Windows\Temp\dwmlut_gui.log`, next to the injector's `dwm_diag.log`. When enabled it records the full gamma-fix sequence — DWM restart timing, suspend/resume status, every shader site patched, region writes, and the live-vs-disk status comparison — plus any unhandled exception.

---
---
## v1.1.2

### C++ injector — `lutdwm/dllmain.cpp`

#### Windows 11 25H2 — `dwmcore.dll` 10.0.26100.8875 support
- **New profile row.** 26100.8875 is newer than the last profiled build (8655); without a matching row `SelectDwmProfile` would fall back to the 8655 offsets, whose device-vector globals are stale on 8875, so the per-frame `ProcessDeviceLost` crash-resilience hook would walk the wrong `.data` region. A dedicated `DwmProfile` row for `DWM_VER(26100, 8875)` now sits at the top of `g_dwmProfiles[]` (newest-first, so 8875 matches exactly and any build between 8655 and 8875 still falls to 8655).
- **RE delta (8875 vs 8655 / 8246):** all four AOB signatures — `Present` `0x231530`, `OverlaysEnabled` `0xA048`, `IsCandidateDirectFlipCompatible` `0x6E1F4`, `ProcessDeviceLost` `0xB3780` — are byte-for-byte identical and still match uniquely, so every hook resolves and installs unchanged. Only the device-vector globals moved: `_Myfirst`/`_Mylast` → `.data` `0x3FCC98`/`0x3FCCA0` (from `0x3FAB78`/`0x3FAB80`). The per-monitor `DeviceClipBox` is **unchanged at `self + 0x7658`**, as are `DeviceInfo` stride `0x10` and lost-flag `0x458`. `OverlayTestMode` and the `Present` security cookie stay RIP-relative (resolved at runtime, no profile fields).
- **Verification.** Validated on a live 8875 machine: LUTs apply correctly single and dual monitor (each monitor's desktop origin reads correctly at `0x7658`), and fullscreen enter/exit is stable. The device-vector RVAs are static-analysis-derived — the `ProcessDeviceLost` hook runs cleanly across fullscreen mode changes, but that path is SEH-guarded, so a genuine device reset (driver update / TDR with the LUT active) is still the definitive test.

#### Thread-safe LUT-target list — `dllmain.cpp`
- The active-context list (`lutTargets` / `numLutTargets`) was read and `realloc`-ed from the Present hooks with no synchronization. DWM composites on more than one thread on a multi-GPU system, so two threads could `realloc` the same block at once — a data race that can corrupt the heap. A dedicated `std::mutex` now guards every read, every `realloc`, and the teardown `free`; it is a leaf lock (held only inside those three functions), so it cannot deadlock against the existing adapter / output / clip mutexes. Behavior is unchanged on the common single-composition-thread path.

#### Diagnostics — `dllmain.cpp`
- Added a compile-time `DIAG_MONITOR_MATCH` switch (default **off**). When set to `1` it logs loaded LUTs, each overlay context's origin / claim / match outcome, and a clip-box-shaped-`RECT` scan of every context to `C:\Windows\Temp\dwm_diag.log` (via the existing `diag_log`) — the tooling that pinned the 8875 clip-box offset against a live two-monitor layout. It compiles out entirely when off.

### Build — `lutdwm/lutdwm.vcxproj`

#### Static C++ runtime (`/MT`) — fixes an `std::mutex` crash on machines with an older VC++ runtime
- **Symptom.** On some target machines DWM crashed the instant a LUT was applied — `msvcp140!mtx_do_lock` reading a null pointer, reached through the Present hook while locking one of the injector's global `std::mutex` objects.
- **Root cause.** The project carried no `<RuntimeLibrary>` element, so it inherited the MSBuild default of `MultiThreadedDLL` (`/MD`): the C++ runtime was linked *dynamically* against whatever `msvcp140.dll` is present on the target. Built with a VS2022 17.10+ toolset, the compiler emits the new `constexpr` `std::mutex` layout (its internal pointer left null), which an older runtime's `mtx_do_lock` still dereferences → access violation. Every `std::mutex` lock was a landmine on any machine whose VC++ redistributable predates 14.40.
- **Fix.** Every configuration now links the CRT statically — `MultiThreaded` (`/MT`) for Release, `MultiThreadedDebug` (`/MTd`) for Debug. The runtime and the matching `std::mutex` implementation are embedded in `lutdwm.dll`, so there is **no dependency on the target's `msvcp140.dll` / VC++ redistributable at all** — the correct model for an injected DLL, and consistent with the `x64-windows-static` vcpkg triplet the project already referenced.

---
---

## v1.1.1

### C++ injector — `lutdwm/dllmain.cpp`

#### Dedicated Windows 11 21H2 support tier (build 22000–22620)
- **Was:** builds in the 21H2 range were folded into the 22H2/23H2 code path, so they used the wrong `DeviceClipBox` / `IDXGISwapChain` / `HardwareProtected` offsets **and** the wrong addressing — 22H2/23H2 dereferences the context for the clip box (`*(void**)self + 0x466C`) and resolves the swapchain pointer through the `sub_from_legacy_swapchain` indirection. On 21H2 that mismatch misidentifies monitors and/or fails to apply the LUT.
- **Is:** 21H2 now has its own tier carrying [ledoge/dwm_lut](https://github.com/ledoge/dwm_lut)'s original 21H2 signatures and offsets: `DeviceClipBox` read **directly** from the context at `self + 0x462C` (float RECT; `+8` when UBR ≥ 706), `IDXGISwapChain` at the **plain** offset `-0x148` (no indirection), `HardwareProtected` at `-0xEC`. It is selected via `VerifyVersionInfo` (build ≥ 22000 and < 22621) and kept fully separate from the 22H2/23H2 path; any 21H2 build sets both the 21H2 flag *and* the generic Windows-11 flag, so a hypothetical un-specialized code path falls back to 22H2/23H2 behavior rather than to Windows 10.
- The 21H2 `OverlaysEnabled` hook uses the plain C++ detour (the register-preserving thunk is a 25H2-only need), and — matching ledoge — `OverlayTestMode` is left untouched on 21H2 (ledoge never forced it and worked on 21H2; the 21H2 `OverlaysEnabled` signature is the function body, not the `OverlayTestMode` compare instruction the 22H2/23H2 path derives that global from).
- Reviewed the other legacy tiers against ledoge/lauralex while adding this one: the 22H2/23H2 offsets (lauralex's) were already correct and are unchanged; the Windows 10 tier needed a correction (see **Windows 10 clip-box read corrected (20H2 / 21H1)**).
- Inherits the existing safety layer unchanged: the SEH-guarded clip-box read (a bad offset skips the frame instead of crashing DWM), the strict 1:1 context↔origin ownership guard, the process-wide kill switch, and the SEH render boundary. The 25H2-only `ProcessDeviceLost` crash-resilience hook is **not** part of any legacy tier (it needs per-build reverse engineering of each `dwmcore.dll`, which isn't available for these builds).
- **Not evaluated on hardware.** Like the other sub-25H2 tiers, the 21H2 path is unverified on a real 21H2 machine.

#### Windows 10 clip-box read corrected (20H2 / 21H1)
- **Was:** the Windows 10 monitor-origin read used the 22H2/23H2 pattern — a **dereferenced float** RECT (`*(void**)self`, read as `RK_FLOAT`) — with only the offset swapped to `-0x120`. ledoge, the only proven Windows 10 implementation, reads `self - 0x120` **directly** (no dereference) as an **int** RECT. Reading integer coordinates as float, through an extra indirection, yields a garbage origin, so Windows 10 monitor identification could not work.
- **Is:** the Windows 10 branch now matches ledoge exactly — direct read, int RECT. The Windows 10 signatures, base offsets (`IDXGISwapChain -0x118`, `HardwareProtected -0xBC`), swapchain access and install adjustments already matched ledoge and are unchanged.
- **Not evaluated on hardware.** Corrected against ledoge's source but unverified on a real 20H2 / 21H1 machine.

---
---

## v1.1.0

### C# UI — `DwmLutGUI`

#### Fullscreen-game LUT via a composition-blocker overlay
- **New:** while LUTs are active, a per-monitor, click-through, almost-transparent (non-zero-alpha) topmost overlay is placed on a display **only when a fullscreen app is covering it**, which forces DWM to composite that surface instead of promoting it to IndependentFlip / a hardware overlay plane. So the LUT applies to fullscreen/borderless apps that would otherwise bypass composition. The overlay is created only when fullscreen is detected on an applicable-LUT monitor and removed the moment it isn't (a ~400 ms watcher, `WindowInteropHelper` / `EnumWindows` based, foreground-independent).
- Overlays are positioned in **true physical pixels** (`EnumDisplayMonitors` + `SetWindowPos` under a Per-Monitor-v2 DPI context evaluated off the UI thread), so coverage is exact on mixed-DPI multi-monitor layouts. Exclusive-fullscreen apps still bypass DWM entirely and cannot be reached (documented limit).

#### DPI-correct Apply-time repaint
- **Was:** `RedrawScreens` spanned a single window across `Screen.AllScreens` bounds, which are expressed in the primary monitor's DPI units and therefore wrong on mixed-DPI setups, so a monitor could keep its pre-LUT pixels until dirtied by the cursor/a window ("wipe-in").
- **Is:** it now flashes a brief repaint per monitor sized in **true physical pixels**, forcing a full
  re-composite on every display regardless of scaling.

#### New "Status" column
- Each monitor row shows a live status: `Inactive` / `Active, windowed mode` / `Active, fullscreen mode`, updated by the same watcher.

#### In-cell LUT ComboBoxes + per-row actions
- The **SDR LUT** / **HDR LUT** cells are now always-visible ComboBoxes listing that monitor's LUTs (filename shown; empty when the list is empty or none is selected). Selecting a LUT applies and persists it as before.
- New **"SDR/HDR LUT actions"** columns with per-row **`NXT`** (cycle to the next LUT in that monitor's list; nothing with fewer than two) and **`DEL`** (remove the selected LUT from the list; if it was applied, unapply and stay disabled). Both operate on the ComboBox's current selection, and the ComboBox follows programmatic changes.

#### Apply/Disable hotkey
- The global toggle key (labeled **"Apply/Disable hotkey"**) now toggles Apply/Disable instead of cycling per-monitor LUTs: it disables if active, otherwise applies. The handler reads the bound key (null-safe; `None` = no hotkey) and the key dropdown now displays its current value correctly.

#### `MainViewModel.cs`
- **HDR LUT list now persists.** `SaveConfig` wrote `<sdr_luts>` but not `<hdr_luts>` (the loader already read it), so per-monitor HDR LUT lists were lost across restarts. The `<hdr_luts>` block is now written.
- **Missing LUTs are pruned on load.** LUT files that no longer exist (deleted, or on a disconnected drive whose letter doesn't resolve) are dropped from each monitor's list, and a current selection whose file is missing is cleared.

---
---

## v1.0.3

### C++ injector — `lutdwm/dllmain.cpp`

#### Fullscreen `OverlaysEnabled` hook restored via a register-preserving thunk (25H2)
- **Was (v1.0.2):** the `COverlayContext::OverlaysEnabled` hook is removed on 25H2 as a first mitigation for the `IsDFlipOnMPO` crash. But that hook — forcing `OverlaysEnabled` to return `false` for LUT-active contexts — is what keeps DWM **compositing** (rather than direct-flipping) those surfaces, so removing it also stopped the LUT from applying over composited fullscreen surfaces (e.g. fullscreen browser video).
- **Is (v1.0.3):** the hook is installed again on 25H2, but through a **register-preserving assembly thunk** (`OverlaysEnabled_thunk`, in the new `OverlaysEnabledThunk.asm`). DWM's `IsDFlipOnMPO` dereferences `r8` after calling `OverlaysEnabled` and relies on it surviving the call (interprocedural register allocation, since the real, tiny callee only touches `rcx`/`al`); a plain C++ detour clobbers `r8` and faults. The thunk saves/restores `rcx`/`rdx`/`r8`–`r11` around the hook, so the crash stays fixed **and** the LUT applies again over composited fullscreen surfaces. Whether to use the thunk is a per-build flag `overlaysEnabledThunk` in `DwmProfile` (set on 26100.8246 / 8655); older builds (24H2 / 23H2) keep the plain C++ hook, unchanged.
- **Build:** `lutdwm.vcxproj` gains the MASM build customization (`masm.props` / `masm.targets`) and the `.asm` as a `<MASM>` item, so it assembles with no manual Visual Studio setup (x64 only).
- **Documented limitation:** this does **not** make all IndependentFlip'd fullscreen/borderless games take the LUT. A LUT only applies while DWM composites a surface; a flip-model game swapchain promoted to IndependentFlip (direct scanout) bypasses composition, and the decision has no hookable entry point on 25H2. See **Known Limitations** in DOCUMENTATION.md.

---
---

## v1.0.2

### C++ injector — `lutdwm/dllmain.cpp`

#### Fullscreen-overlay DWM crash (25H2)
- **Was:** on 25H2, taking a video fullscreen (e.g. a windowed browser video on an external display in a multi-GPU setup) could crash DWM a couple seconds later — `INVALID_POINTER_READ` in `COverlayContext::OverlayPlaneInfo::IsDFlipOnMPO`, reached from `InitCheckCandidatesList` / `ComputeOverlayConfiguration` on the composition thread. Root cause: we hooked `COverlayContext::OverlaysEnabled`, but `IsDFlipOnMPO` calls it and then relies on `r8` surviving the call (the compiler did interprocedural register allocation, since the real, tiny `OverlaysEnabled` only touches `rcx`/`al`). A C++ detour clobbers `r8`, so the following `cmp [r8+0x168]` read a bad address.
- **Is:** on 25H2 (`>= 26200.8246`) the `OverlaysEnabled` hook is **removed** as a first, minimal mitigation for the crash. Its address is still resolved (to locate the `OverlayTestMode` global) and `OverlayTestMode = 5` is still forced. The hook is **kept on older builds** (24H2 / 23H2), which are unverified, so their behavior is untouched.

---
---

## v1.0.1

### C# UI — `DwmLutGUI`

#### `MonitorData.cs`
- **HDR awareness.** Added `IsHdr` and a derived `HdrStatus` ("HDR"/"SDR").

#### `MainViewModel.cs`
- **HDR awareness.** Each enumeration queries HDR states once and tags every monitor (matched by device path); refreshes on display-settings changes (e.g. toggling HDR in Windows).

#### `HdrInfo.cs`
- **HDR awareness.** Queries `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` (`GET_ADVANCED_COLOR_INFO`, `advancedColorEnabled` bit) and returns a device-path -> HDR-enabled map. Fully exception-guarded; any failure reports nothing (treated as SDR).

#### `MainWindow.xaml`
- **HDR awareness.** New **"Mode"** column in the monitor list showing HDR / SDR.

#### `MainWindow.xaml.cs`
- **HDR awareness.** Assigning a LUT that can't apply in the display's current mode now shows a warning (an SDR LUT on an HDR display, or an HDR LUT on an SDR display).

---

### C++ injector — `lutdwm/dllmain.cpp`

#### HDR / SDR LUT selection
- **Was:** for the primary HDR context, if no LUT of the matching type existed, the code fell back to a LUT of the *opposite* type (SDR<->HDR pairing). Since the shader's HDR math follows the backbuffer format, not the LUT, a mismatched LUT ran through the wrong pipeline (e.g. an SDR-calibrated LUT fed PQ/BT.2100 input) and produced wrong colors.
- **Is:** `GetLUTDataFromCOverlayContext` is **exact-match only** — an HDR context takes an HDR LUT, an SDR context takes an SDR LUT, and if the only LUT assigned is the wrong type for the display's current mode, **no LUT is applied** (correct-or-nothing) instead of a mis-calibrated one. The bidirectional pairing fallback and the now-dead `g_primaryHdrContext` global were removed.

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

***26100.8655 delta:** same structure, shifted RVAs — `Present` `0x231800`, `OverlaysEnabled` `0x1A2BE8`, `IsCandidateDirectFlipCompatible` `0xB1414` (member `0x4BF8`), `ProcessDeviceLost` `0xDCF80`, and device vector `_Myfirst`/`_Mylast` `0x3FAB78`/`0x3FAB80`. Signature bytes, clip-box `0x7658`, stride `0x10`, and lost-flag `0x458` are unchanged.*

---

*Last Updated: 31 July 2026*
