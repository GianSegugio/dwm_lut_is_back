# **DwmLut Is Back**

## A fork of ed1ii's [dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed) adjusted for **Windows 11 25H2 build >= 26200.8246**

> [!WARNING]
> **This fork has been developed to add 25H2 support:** it has been validated on Windows 11 25H2 builds 26200.8246, 26200.8655 and 26200.8875 fresh installs, with multiple SDR and HDR monitors, plus Windows 11 21H2 (22000.1880) and a 26H2 preview (26300). While there is some support for Windows versions older than 25H2 (read [documentation](DOCUMENTATION.md) for details), the tool is not guaranteed to work on 25H2 builds prior 26200.8246 for which LUT application is skipped entirely as a safety measure. Furthermore keep in mind that any future Windows 11 update may introduce DWM changes that break the current tool configuration.

> [!CAUTION]
> This software injects a DLL into `dwm.exe` and hooks undocumented Windows internals.
> 
> **Note for games:**
> Anti-cheat software may detect or reject modification of the Windows graphics pipeline, and some games prohibit color filters that can improve visibility. Disable and close DwmLut before launching competitive or anti-cheat-protected games.

## Dependencies
- **Visual C++ runtime (recommended):** [AIO Redistributable](https://www.techpowerup.com/download/visual-c-redistributable-runtime-package-all-in-one/)

## About
This tool applies 3D LUTs to the Windows desktop by hooking into DWM. It works in both SDR and HDR modes, and uses tetrahedral interpolation on the LUT data. In SDR, blue-noise dithering is applied to the output to reduce banding.

Right now it should work on 20H2, 21H1, 21H2, 22H2, 23H2, 24H2, 25H2 builds >= 26200.8246 and 26H2 preview builds, but any future Windows 11 update may introduce DWM changes and I'll try to update it whenever a new version breaks it. 
**Legacy Windows support:**
- For 20H2 or 21H1 or 21H2 builds try [ledoge/dwm_lut](https://github.com/ledoge/dwm_lut)
- For 22H2 or 23H2 builds try [lauralex/dwm_lut](https://github.com/lauralex/dwm_lut)
- For 23H2 or 24H2 or 25H2 (Canary) builds try [ed1ii/dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed)

## Key Features

- **Windows 11 Compatible**: Full support for **25H2 (tested on 26200.8246, 26200.8655 and 26200.8875; newer 25H2 builds apply the latest profile)** plus a **26H2 preview** profile (26300), and tested support for **21H2** (22000.1880). Older Windows versions are supported but untested (read [documentation](DOCUMENTATION.md) for details).
- **Multi-Monitor & Multi-GPU support**: Reliable LUT application across multiple displays and GPUs. Proper discrete GPU and integrated GPU handling with multi-GPU isolation (rendering resources are allocated and validated per graphics adapter and per output).
- **Crash-resilient across device teardown:** Fullscreen apps that switch resolution (e.g. classic DirectDraw games) and monitors being connected or disconnected both make DWM destroy and recreate its graphics devices. DWM's resource-leak checker crashes the compositor if anything is still holding a device it destroys, so this fork hooks the exact point at which a device is removed and releases its LUT resources there — correctly timed regardless of compositing rate or power state — then rebuilds them automatically afterwards.
- **Version-keyed build profiles:** Every supported `dwmcore.dll` build's signatures and offsets live in one self-contained table entry keyed by version, so adapting to a future Windows update is a single localized change.
- **Fail-safe by design:** A process-wide kill-switch and structured-exception boundaries around the render path keep any failure contained; DWM composites normally instead of crash-looping.
- **MPO / DirectFlip Management**: Automated `OverlayTestMode` handling for *windowed -> borderless -> direct-fullscreen-composition* transitions, plus a **composition-blocker overlay** that (only when a fullscreen app is detected on a monitor with an applicable LUT) forces that display to composite so the LUT applies to fullscreen/borderless apps that would otherwise IndependentFlip. Per-monitor, DPI-correct, and removed as soon as fullscreen ends. In summary, LUTs apply to composited surfaces (windowed apps, most fullscreen video, and legacy fullscreen games). **Fullscreen or borderless games that DWM promotes to IndependentFlip (direct scanout) bypass composition and will not show the LUT**; this is a DWM limitation with no compositor-side hook on 25H2. For those, an in-game overlay such as [ImGui](https://github.com/ocornut/imgui) is the right approach.
- **SDR-in-HDR gamma fix**: In HDR mode Windows maps SDR content with the piecewise sRGB curve rather than the pure gamma curve content is actually graded against, which lifts blacks and washes it out — with no Windows setting to change it. An optional **On / Off** control patches DWM's own SDR-to-HDR conversion shaders to a pure gamma curve of your choice (2.2 / 2.4 / 2.6, default 2.4), correcting SDR content while leaving **native HDR content untouched**. It is independent of the LUT, so correctly-mapped SDR and a calibrated HDR display work at the same time. Toggling it restarts DWM (a brief black flash) and changes nothing on disk.
- **Improved UI**: A comprehensive monitor table — properties are labelled rows, each monitor is a self-contained column — with per-monitor SDR/HDR LUT dropdowns and Browse / Next / Clear actions, a live per-monitor Status (inactive / active windowed / active fullscreen), a per-display Mode (SDR / WCG / HDR), a global Apply/Disable hotkey, an autostart toggle, and a tooltip on every control explaining what it does.
- **Enhanced .cube Parser**: Support for DisplayCAL generated LUTs, including negative values and floating-point data.

## Usage
Use [DisplayCAL](https://displaycal.net/) or similar to generate .cube LUT files of any size, run `DwmLutGUI.exe`, assign them to monitors and then click Apply. Note that LUTs cannot be applied to monitors that are in "Duplicate" mode.

For [ColourSpace](https://lightillusion.com/colourspace.html) users with HT license level, 65^3 eeColor LUT .txt files are also supported.

HDR LUTs must use BT.2020 + SMPTE ST 2084 values as input and output.

If you use HDR and want SDR → HDR mapped content to follow the pure gamma curve it was authored for, instead of the piecewise sRGB curve Windows uses by default, turn the **scRGB piecewise → scRGB 2.x (HDR gamma fix)** on and pick the target gamma (2.2 / 2.4 / 2.6) from the dropdown beside it. It corrects how Windows maps SDR content into the HDR space and leaves native HDR content alone, so it can be used together with a calibrated HDR LUT. It restarts DWM each time it is switched, so set it once rather than toggling it repeatedly.

Minimizing the GUI will make it disappear from the taskbar, and you can use the context menu of the tray icon to quickly apply or disable all LUTs. For automation, you can start the exe with any (sensible) combination of `-apply`, `-disable`, `-minimize` and `-exit` as arguments (exact and case-sensitive: `-apply`, not `-Apply`). If DwmLut is already running — as it will be with autostart enabled — the arguments are handed to that instance and the second launch exits silently, so scripts and scheduled tasks keep working without a second copy or a dialog to dismiss.

## Compiling
Install [vcpkg](https://vcpkg.io/en/getting-started.html) for C++ dependency management:

- Create and switch to your desired install folder (e.g. _%LOCALAPPDATA%\vcpkg_)
- `git clone https://github.com/Microsoft/vcpkg.git .`
- `.\bootstrap-vcpkg.bat`
- `vcpkg integrate install`

Just open the projects in Visual Studio and compile a x64 Release build.

> The injector links the C++ runtime **statically** (`/MT`, set in `lutdwm.vcxproj`), so `lutdwm.dll` doesn't depend on the target machine's `msvcp140.dll` version. This is deliberate: with a dynamic CRT (`/MD`), a DLL built with the VS2022 17.10+ toolset crashes in `std::mutex` on any machine whose runtime `msvcp140.dll` predates 14.40 (the `constexpr`-mutex ABI change). Leave the Runtime Library on `/MT`. (The Visual C++ Redistributable under **Dependencies** is still worth installing as a general safety net; static linking is what actually protects the injector.)

## Changelog
See [changelog](CHANGELOG.md) for new features and differences from ed1ii's [dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed).

## Documentation
See [documentation](DOCUMENTATION.md) for technical info and known limitations.

## License

This project is licensed under the **GNU General Public License v3.0** — see [LICENSE](LICENSE) for the full text. The licence is inherited from [ledoge/dwm_lut](https://github.com/ledoge/dwm_lut), which this fork descends from.

Third-party components keep their own terms, reproduced in full in [LICENSE-THIRD-PARTY](LICENSE-THIRD-PARTY):

| Component | Used for | License |
| --- | --- | --- |
| [MinHook](https://github.com/TsudaKageyu/minhook) | Hooking the DWM functions the injector intercepts | BSD 2-Clause |
| Hacker Disassembler Engine 32 / 64 C | Instruction-length decoding, required by MinHook | BSD 2-Clause |
| [WindowsDisplayAPI](https://github.com/falahati/WindowsDisplayAPI) | Monitor and display-config enumeration in the GUI | LGPL-3.0 |
| [DXBCChecksum](https://github.com/GPUOpen-Archive/common-src-ShaderUtils/tree/master/DX10) | Recomputing DXBC shader checksums for the SDR-in-HDR gamma fix | MIT (AMD), incl. the RSA MD5 notice |

## Credits
- **Original Author**: [ledoge](https://github.com/ledoge)
- **Maintenance**: [lauralex](https://github.com/lauralex) and [ed1ii](https://github.com/ed1ii)

---

*Last Updated: 5 August 2026*
