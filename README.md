# OPACBAR (Opac)

**Modern Minimal Monochrome Taskbar Customizer & Floating Pill Studio for Windows 10 & 11.**

Opacbar is an open-source, zero-telemetry, zero-permission standalone replacement for TranslucentTB—engineered with a strict **black-and-white monochrome UI**, **floating pill geometry with adjustable corner radius**, and **100% transparent code** with zero sketchy permissions.

---

## Why Opacbar?

Many taskbar utilities require bloated store frameworks, background services, invasive permissions, or complex config file editing. **Opacbar** was built from the ground up around three principles:

1. **Full Privacy & Zero Sketchy Permissions**
   - **Zero network calls**: No `HttpClient`, no sockets, no analytics, no auto-updater phoning home.
   - **Standard user privileges (`asInvoker`)**: Never asks for Administrator UAC elevation.
   - **No DLL injection**: Uses standard user-mode Win32 & DWM window attributes (`SetWindowCompositionAttribute`, `SetWindowRgn`, `DwmSetWindowAttribute`) and cleanly restores your default Windows taskbar the instant you exit.
2. **Floating Pill & Custom Corner Geometry**
   - Turn your taskbar into a **floating pill dock** or keep it full-width.
   - Adjust **Corner Radius** in real time from `0px` (sharp square) to `8px` (soft), `16px` (rounded), or `26px–32px` (full pill).
   - Adjust **Horizontal Edge Inset** (pill width) and **Vertical Floating Gap** with live sliders.
   - Optionally auto-expand the floating pill back to a full-width bar when any window is maximized.
3. **Effortless Visual UI/UX (No Config Coding Required)**
   - **Left-click** the system tray icon to open **Opacbar Studio**—a modern minimal monochrome control panel with an interactive **Live Taskbar Preview Canvas**.
   - **Right-click** the system tray icon for one-click access to modes (`Clear`, `Acrylic`, `Blur`, `Tinted`, `Solid`, `Normal`) and corner presets (`Square`, `Soft`, `Rounded`, `Full Pill`).
   - Strictly monochromatic black-and-white aesthetic (`#0A0A0A` / `#FAFAFA`), using only semantic status dots:
     - **Green (`#22C55E`)**: Active / Verified Safe
     - **Yellow (`#EAB308`)**: Paused
     - **Red (`#EF4444`)**: Error / Alert

---

## Features at a Glance

| Feature | TranslucentTB | **Opacbar** |
| :--- | :---: | :---: |
| **Clear / Transparent Taskbar** | Yes | **Yes** |
| **Acrylic & Frosted Blur Glass** | Yes | **Yes** |
| **Monochrome Opacity & Shade Sliders** | Color Picker | **Precision Monochrome Luminance & Opacity Sliders** |
| **Dynamic States (Maximized / Start / Battery Saver)** | Yes | **Yes (Visual Rule Editor)** |
| **Floating Pill Taskbar** | No | **Yes (Adjustable Horizontal & Vertical Margins)** |
| **Adjustable Corner Radius (Square ↔ Rounded ↔ Pill)** | No | **Yes (0px to 32px Slider + Quick Presets)** |
| **Interactive Live Preview Canvas** | No | **Yes** |
| **Zero Network / Zero Telemetry / Single `.exe`** | Store Package | **100% Standalone ~45 KB `.exe`** |

---

## Building on GitHub (Automated CI/CD)

This repository includes a ready-to-run GitHub Actions workflow at [`.github/workflows/build.yml`](.github/workflows/build.yml):

1. Push this repository to GitHub.
2. On every push to `main`/`master` (or when you trigger **Actions → Build Opacbar Standalone Executable**), GitHub Actions automatically compiles `Opacbar.exe` and packages `Opacbar-Windows-Portable.zip` with SHA-256 checksums.
3. Download the compiled `Opacbar-Windows-Standalone` artifact directly from the **Actions** tab, or push a version tag (e.g., `git tag v1.0.0 && git push --tags`) to automatically publish a **GitHub Release** with `Opacbar.exe` attached.

---

## Building Locally on Any Windows PC (Zero Dependencies)

You do **not** need to install Visual Studio or heavy SDKs to build Opacbar on your PC. Every Windows 10 and Windows 11 installation includes the built-in .NET Framework compiler (`csc.exe`).

Double-click **`build.bat`** or run in PowerShell:

```powershell
.\build.ps1
```

This outputs the standalone executable to:

```text
dist\Opacbar.exe
```

If you prefer using the `.NET SDK` (`dotnet`) or Visual Studio, [`Opacbar.csproj`](Opacbar.csproj) is also included:

```powershell
dotnet build Opacbar.csproj -c Release -o dist
```

---

## Project Structure

```text
├── .github/workflows/
│   └── build.yml            # Automated GitHub Actions build & release pipeline
├── src/
│   ├── Program.cs           # Entry point, single-instance mutex & system tray menu
│   ├── SettingsForm.cs      # Monochrome Opacbar Studio UI & 4-tab visual editor
│   ├── ThemeAndControls.cs  # Custom monochrome switches, sliders, segmented controls & live preview
│   ├── TaskbarEngine.cs     # Multi-monitor Win32/DWM transparency, acrylic & pill region engine
│   ├── AppConfig.cs         # Portable JSON persistence & Windows startup registry helper
│   ├── NativeMethods.cs     # Audited P/Invoke signatures (user32, dwmapi, gdi32)
│   ├── AssemblyInfo.cs      # Binary metadata
│   └── app.manifest         # Standard user privilege (asInvoker) & PerMonitorV2 DPI awareness
├── Opacbar.csproj           # SDK-style C# project file
├── build.ps1                # Zero-dependency PowerShell build script
├── build.bat                # One-click Windows batch build launcher
└── LICENSE                  # MIT License
```

---

## License

Released under the [MIT License](LICENSE).
