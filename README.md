# Opacbar

A small, open-source taskbar customizer for Windows 11 (and 10). An alternative to TranslucentTB with a monochrome UI and a color picker for your taskbar.

Opacbar is a small tray app plus a tiny helper DLL. Install it per user (no admin rights) or run it portable. No network access.

---

## Features

- **Taskbar looks:** Clear, Color, or Blur. Color: pick any color with the built-in picker (saturation/value square, hue strip, hex, quick and recent swatches) and an opacity. Blur: an adjustable blur of the desktop behind the taskbar, tinted with a color. One look applies everywhere.
- **True transparency on Windows 11:** only the taskbar's background is recolored, so icons, text and the clock stay fully opaque. A completely clear (alpha 0) background is possible.
- **Tray icon:** left-click opens settings, right-click opens a quick menu.
- **Emergency quit:** `Ctrl+Shift+Alt+Q` quits Opacbar and restores the stock taskbar.
- Restores the normal Windows taskbar when it exits.

---

## Privacy and how it works

Opacbar has nothing to hide, and the full source is in this repository.

- **No admin.** Runs as a normal user (`asInvoker`).
- **No network.** No telemetry, analytics, update checks or downloads.
- **Local config only.** Settings are a JSON file, `%AppData%\Opacbar\settings.json` (or `settings.json` next to the exe when a file named `portable` is there). "Start with Windows" adds the value `Opacbar` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` only if you turn it on.
- **No logs by default.** Start Opacbar with `--diagnostics` (or put a file named `diagnostics` next to the exe) to write a small local log, `%LocalAppData%\Opacbar\opacbar.log`, and show a "Save taskbar map..." button for bug reports.

**The helper DLL.** On Windows 11 the taskbar is drawn with XAML inside `explorer.exe`, and its background can't be changed from outside. To recolor it, Opacbar loads a small open-source helper, [`native/OpacbarTAP.cpp`](native/OpacbarTAP.cpp), into Explorer through Microsoft's official **XAML Diagnostics API** (`InitializeXamlDiagnosticsEx`). Visual Studio's Live Visual Tree and TranslucentTB use the same API. Because this loads code into another process, some antivirus tools may flag it.

The helper:

- finds the taskbar's background rectangles (`BackgroundFill` / `BackgroundStroke` inside `TaskbarBackground`) and changes **only their fill or opacity** (or draws the blur behind them);
- takes a small text document of style rules from Opacbar through an invisible message-only window (`OpacbarTAP.Sink`); see [`native/PROTOCOL.md`](native/PROTOCOL.md);
- puts the original Windows brush back when asked to restore;
- does **not** use the network, write files or the registry, hook the keyboard or mouse, or read anything Explorer displays.

The top of the source file explains this in plain English. The helper is a single, heavily commented C++ file of plain Win32 and COM, so you can audit it in one sitting.

Opacbar copies the helper to `%LocalAppData%\Opacbar\helper` before loading it, so the original next to the exe is never locked. The helper stays loaded until Explorer restarts. Restarting Explorer or signing out removes it completely.

Clear hides Windows' background layer, Color replaces its brush, Blur draws an adjustable blur in its place, and turning Opacbar off restores the original exactly. Each helper injection runs in a short-lived, invisible `Opacbar.exe --inject` process that exits within a few seconds (Windows allows one such call per process). On Windows 10 (build below 22000) Opacbar uses the classic DWM accent policy instead and needs no helper. If the helper is missing, Opacbar tells you and the taskbar keeps its stock look.

---

## Download

From [Releases](../../releases) (or the latest **Actions** run):

**Installer (recommended):** `OpacbarSetup.exe`. Installs for your user only, without admin rights, to `%LocalAppData%\Programs\Opacbar`, adds a Start menu entry and, if you leave the box ticked, starts Opacbar when Windows starts. Upgrading or uninstalling closes a running Opacbar first and restores the taskbar. Uninstall from Settings > Apps.

**Portable:** `Opacbar-Windows-Portable.zip`. Unzip anywhere and run `Opacbar.exe`. Add an empty file named `portable` next to it to keep the settings in that folder too. "Start with Windows" in the app works the same way.

| File | Purpose |
| :--- | :--- |
| `Opacbar.exe` | The app (.NET Framework 4.8, built into Windows) |
| `OpacbarTAP.x64.dll` | Taskbar helper for x64 PCs |
| `OpacbarTAP.arm64.dll` | Taskbar helper for ARM64 PCs |
| `SHA256SUMS.txt` | Checksums |

Keep the DLLs in the same folder as `Opacbar.exe`.

### Command line

| Switch | Effect |
| :--- | :--- |
| `--minimized` | Start in the tray without opening anything (used by "Start with Windows") |
| `--quit` | Ask a running Opacbar to quit and restore the taskbar |
| `--diagnostics` | Write the local diagnostics log and show "Save taskbar map..." |

---

## Building

### Opacbar.exe

No Visual Studio needed. Windows ships the .NET Framework C# compiler (`csc.exe`):

```powershell
.\build.ps1        # or double-click build.bat
```

Output: `dist\Opacbar.exe`. If [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed and both helper DLLs are in `dist\`, the script also builds `dist\OpacbarSetup.exe` from [`installer/Opacbar.iss`](installer/Opacbar.iss). `-NoInstaller` skips that; `-OutExe <path>` builds the exe elsewhere (handy while `dist\Opacbar.exe` is running).

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
installer/Opacbar.iss Inno Setup script (per-user installer)
build.ps1 / build.bat Builds Opacbar.exe with the built-in csc (and the installer)
.github/workflows/    CI: exe + x64/ARM64 helper, installer, checksums, portable zip, releases
```

---

## License

[MIT](LICENSE)

