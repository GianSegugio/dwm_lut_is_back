# **DwmLut Is Back**

## A fork of ed1ii's [dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed) adjusted for **Windows 11 25H2 build >= 26200.8246**

> [!WARNING]
> **This fork has been developed to add 25H2 support:** it has been validated on a Windows 11 25H2 build 26200.8246 and 26200.8655 fresh installs, with multiple SDR monitors. While there is some support for 24H2 and 23H2 (read [documentation](DOCUMENTATION.md) for details), the tool is not guaranteed to work on 25H2 builds prior 26200.8246 for which LUT application is skipped entirely as a safety measure. Furthermore keep in mind that any future Windows 11 update may introduce DWM changes that break the current tool configuration.

> [!CAUTION]
> This software injects a DLL into `dwm.exe` and hooks undocumented Windows internals.
> 
> **Note for games:**
> Anti-cheat software may detect or reject modification of the Windows graphics pipeline, and some games prohibit color filters that can improve visibility. Disable and close DwmLut before launching competitive or anti-cheat-protected games.

## Dependencies
- **Visual C++ runtime:** [AIO Redistributable](https://www.techpowerup.com/download/visual-c-redistributable-runtime-package-all-in-one/)

## About
This tool applies 3D LUTs to the Windows desktop by hooking into DWM. It works in both SDR and HDR modes, and uses tetrahedral interpolation on the LUT data. In SDR, blue-noise dithering is applied to the output to reduce banding.

Right now it should work on 23H2 (build 22631), 24H2 and 25H2 builds >= 26200.8246, but any future Windows 11 update may introduce DWM changes and I'll try to update it whenever a new version breaks it. 
**Legacy Windows support:**
- For 20H2 or 21H1 builds try [ledoge/dwm_lut](https://github.com/ledoge/dwm_lut)
- For 22H2 builds try [lauralex/dwm_lut](https://github.com/lauralex/dwm_lut)
- For 23H2 or 24H2 or 25H2 (Canary) builds try [ed1ii/dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed)

## Key Features

- **Windows 11 Compatible**: Full support for **25H2 (tested on 26200.8246 and 26200.8655; newer 25H2 builds apply the latest profile)**, support (not tested) for **24H2 (direct composition)** and **23H2 (build 22631)**.
- **Multi-Monitor & Multi-GPU support**: Reliable LUT application across multiple displays and GPUs. Proper discrete GPU and integrated GPU handling with multi-GPU isolation (rendering resources are allocated and validated per graphics adapter and per output).
- **Crash-resilient across display-mode changes:** Fullscreen apps that switch resolution (e.g. classic DirectDraw games) force DWM to tear down and recreate its graphics device. This fork releases its LUT resources at the exact moment DWM does so, so DWM doesn't crash, and the LUT is restored automatically on exit.
- **Version-keyed build profiles:** Every supported `dwmcore.dll` build's signatures and offsets live in one self-contained table entry keyed by version, so adapting to a future Windows update is a single localized change.
- **Fail-safe by design:** A process-wide kill-switch and structured-exception boundaries around the render path keep any failure contained; DWM composites normally instead of crash-looping.
- **MPO / DirectFlip Management**: Automated `OverlayTestMode` handling so *windowed → borderless → direct-fullscreen-composition* transitions keep working, with correct per-adapter/per-output resource allocation/deallocation. LUTs apply to composited surfaces (windowed apps, most fullscreen video, and legacy fullscreen games). **Fullscreen or borderless games that DWM promotes to IndependentFlip (direct scanout) bypass composition and will not show the LUT**; this is a DWM limitation with no compositor-side hook on 25H2. For those, an in-game overlay such as [ImGui](https://github.com/ocornut/imgui) is the right approach.
- **Enhanced .cube Parser**: Support for DisplayCAL generated LUTs, including negative values and floating-point data.

## Usage
Use [DisplayCAL](https://displaycal.net/) or similar to generate .cube LUT files of any size, run `DwmLutGUI.exe`, assign them to monitors and then click Apply. Note that LUTs cannot be applied to monitors that are in "Duplicate" mode.

For [ColourSpace](https://lightillusion.com/colourspace.html) users with HT license level, 65^3 eeColor LUT .txt files are also supported.

HDR LUTs must use BT.2020 + SMPTE ST 2084 values as input and output.

Minimizing the GUI will make it disappear from the taskbar, and you can use the context menu of the tray icon to quickly apply or disable all LUTs. For automation, you can start the exe with any (sensible) combination of `-apply`,  `-disable`, `-minimize` and `-exit` as arguments.

## Compiling
Install [vcpkg](https://vcpkg.io/en/getting-started.html) for C++ dependency management:

- Create and switch to your desired install folder (e.g. _%LOCALAPPDATA%\vcpkg_)
- `git clone https://github.com/Microsoft/vcpkg.git .`
- `.\bootstrap-vcpkg.bat`
- `vcpkg integrate install`

Just open the projects in Visual Studio and compile a x64 Release build.

## Changelog
See [changelog](CHANGELOG.md) for new features and differences from ed1ii's [dwm_lut_fixed](https://github.com/ed1ii/dwm_lut_fixed).

## Documentation
See [documentation](DOCUMENTATION.md) for technical info and known limitations.

## Credits
- **Original Author**: [ledoge](https://github.com/ledoge)
- **Maintenance**: [lauralex](https://github.com/lauralex) and [ed1ii](https://github.com/ed1ii)

---

*Last Updated: 07 July 2026*
