# Opacbar

A small, open-source taskbar customizer for Windows 11 (and 10). An alternative to TranslucentTB with a monochrome UI and a color picker for your taskbar.

Opacbar is a single portable `.exe` plus a tiny helper DLL. No installer, no admin rights, no network access.

---

## Features

- **Taskbar looks:** Clear, Color, or Blur. Color: pick any color with the built-in picker (saturation/value square, hue strip, hex, quick and recent swatches) and an opacity. Blur is Windows' own frosted taskbar material. One look applies everywhere.
- **True transparency on Windows 11:** only the taskbar's background is recolored, so icons, text and the clock stay fully opaque. A completely clear (alpha 0) background is possible.
- **Floating pills:** in development.
- **Tray icon:** left-click opens settings, right-click opens a quick menu.
- **Emergency quit:** `Ctrl+Shift+Alt+Q` quits Opacbar and restores the stock taskbar.
- Restores the normal Windows taskbar when it exits.

---

## Privacy and how it works

Opacbar has nothing to hide, and the full source is in this repository.

- **No admin.** Runs as a normal user (`asInvoker`).
- **No network.** No telemetry, analytics, update checks or downloads.
- **Local config only.** Settings are a JSON file (`opacbar.config.json` next to the exe, or in `%AppData%\Opacbar`). "Start with Windows" adds a value under `HKCU\...\CurrentVersion\Run` only if you turn it on.

**The helper DLL.** On Windows 11 the taskbar is drawn with XAML inside `explorer.exe`, and its background can't be changed from outside. To recolor it, Opacbar loads a small open-source helper, [`native/OpacbarTAP.cpp`](native/OpacbarTAP.cpp), into Explorer through Microsoft's official **XAML Diagnostics API** (`InitializeXamlDiagnosticsEx`). Visual Studio's Live Visual Tree and TranslucentTB use the same API. Because this loads code into another process, some antivirus tools may flag it.

The helper:

- finds the taskbar's background rectangles (`BackgroundFill` / `BackgroundStroke` inside `TaskbarBackground`) and changes **only their fill color**;
- takes a small text document of style rules from Opacbar through an invisible message-only window (`OpacbarTAP.Sink`); see [`native/PROTOCOL.md`](native/PROTOCOL.md);
- puts the original Windows brush back when asked to restore;
- does **not** use the network, write files or the registry, hook the keyboard or mouse, or read anything Explorer displays.

The top of the source file explains this in plain English. The helper is a single, heavily commented C++ file of plain Win32 and COM, so you can audit it in one sitting.

Opacbar copies the helper to `%LocalAppData%\Opacbar\helper` before loading it, so the original next to the exe is never locked. The helper stays loaded until Explorer restarts. Restarting Explorer or signing out removes it completely.

Blur leaves Windows' own taskbar material untouched; it is the only blur that renders reliably on the Windows 11 taskbar. Clear hides that layer, Color replaces its brush, and switching back restores the original exactly. On Windows 10 (build below 22000) Opacbar uses the classic DWM accent policy instead and needs no helper. If the helper is missing, Opacbar tells you and the taskbar keeps its stock look.

---

## Download

Get `Opacbar-Windows-Portable.zip` from [Releases](../../releases) (or the latest **Actions** run). It contains:

| File | Purpose |
| :--- | :--- |
| `Opacbar.exe` | The app (.NET Framework 4.8, built into Windows) |
| `OpacbarTAP.x64.dll` | Taskbar helper for x64 PCs |
| `OpacbarTAP.arm64.dll` | Taskbar helper for ARM64 PCs |
| `SHA256SUMS.txt` | Checksums for the three binaries |

Keep the DLLs in the same folder as `Opacbar.exe`.

---

## Building

### Opacbar.exe

No Visual Studio needed. Windows ships the .NET Framework C# compiler (`csc.exe`):

```powershell
.\build.ps1        # or double-click build.bat
```

Output: `dist\Opacbar.exe`.

### Helper DLL

GitHub Actions ([`.github/workflows/build.yml`](.github/workflows/build.yml)) builds both DLLs with MSVC on every push. To build locally, install [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022) with the C++ workload (and the ARM64 build tools for ARM64). Then, from a *Developer PowerShell* for the target architecture, run:

```powershell
cl /nologo /LD /O2 /EHs-c- /D_HAS_EXCEPTIONS=0 /GS /guard:cf /W4 /DUNICODE /D_UNICODE `
   native\OpacbarTAP.cpp /Fe:dist\OpacbarTAP.x64.dll `
   /link /DEF:native\OpacbarTAP.def /GUARD:CF ole32.lib oleaut32.lib user32.lib
```

For ARM64, use an `amd64_arm64` (or native arm64) developer prompt and name the output `OpacbarTAP.arm64.dll`.

---

## Project layout

```text
src/                  C# WinForms app (tray, settings UI, taskbar engine, helper loader)
native/OpacbarTAP.cpp Explorer helper (XAML Diagnostics TAP), single file
native/OpacbarTAP.def DLL exports
build.ps1 / build.bat Builds Opacbar.exe with the built-in csc
.github/workflows/    CI: exe + x64/ARM64 helper, checksums, portable zip, releases
```

---

## License

[MIT](LICENSE)

