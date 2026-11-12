# Privacy policy

*Last updated: 11 November 2026 · Applies to OpacityBar 1.1.0 and later*

**Short version: OpacityBar collects nothing, sends nothing and has no network code.** It changes how your
taskbar looks. That's all it does.

OpacityBar is made by [moller.studio](https://moller.studio). This policy covers the OpacityBar app, its
installer and its helper DLL. It does not cover GitHub, where the downloads are hosted (see
[GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)).

## What OpacityBar does not do

- No internet access: no telemetry, analytics, crash reports, update checks, ads or accounts.
  The source contains no networking code, which you can [check yourself](#check-it-yourself).
- No personal data: it doesn't read your files, documents, browsing, or what's on your screen.
- No keylogging or input hooks. It registers one hotkey (Ctrl+Shift+Alt+Q, emergency quit).
- No admin rights, services, drivers or scheduled tasks.

## Everything OpacityBar touches on your PC

This is the complete list. Everything is under your own user account (HKCU and your profile folders).

**Files and folders**

| Location | What | When |
| :--- | :--- | :--- |
| `%LocalAppData%\Programs\OpacityBar\` | The app and its two helper DLLs | Installer only |
| Start menu `OpacityBar.lnk` | Shortcut | Installer only |
| `%AppData%\OpacityBar\settings.json` | Your style, color, blur and theme choices | Always |
| `%LocalAppData%\OpacityBar\opacitybar.log` | Technical log, never sent anywhere | Only with `--diagnostics` |
| Desktop `opacitybar-taskbar-map.txt` | Taskbar structure for bug reports | Only when you click "Save taskbar map..." in diagnostics mode |

**Registry (HKCU, your user only)**

| Key | What | When |
| :--- | :--- | :--- |
| `Software\Microsoft\Windows\CurrentVersion\Run` → `OpacityBar` | Start with Windows | Only while that option is on |
| `Software\Microsoft\Windows\CurrentVersion\Uninstall\OpacityBar` | The entry in Settings > Apps | Installer only |
| `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` → `SystemUsesLightTheme` | "Windows mode" (light/dark for the taskbar, Start and notification panels), switched so taskbar icons stay readable on your bar color. "App mode" is never touched. | When the bar is light on dark mode, or dark on light mode |
| `Software\OpacityBar` → `SavedSystemUsesLightTheme` | Your own Windows mode, remembered so it can be put back for Normal, on quit and on uninstall | Only while OpacityBar has switched it |

OpacityBar also **reads** (never changes) Windows' light/dark and transparency settings, and the current
wallpaper image, so it knows how light your bar looks. The wallpaper is only measured for
brightness on your PC and never stored or sent anywhere. In diagnostics mode it reads the Windows build number.

**Inside Explorer (Windows 11 only)**

The helper DLL is loaded into `explorer.exe` through Microsoft's XAML Diagnostics API. It changes only the
taskbar's background layer (`BackgroundFill` / `BackgroundStroke`) and restores the original when you pick
Normal or quit. It reads no content and writes no files or registry values. When Explorer restarts or you sign
out, it's gone.

## Removing everything

Uninstall from **Settings > Apps > Installed apps > OpacityBar**. This quits the app, restores the taskbar,
and deletes the files, folders and registry entries above, settings included. Explorer keeps the helper DLL
in use until it restarts, and Windows can't delete a file in use. So the uninstaller switches the helper off
and adds a one-time cleanup (`HKCU\...\RunOnce\OpacityBarCleanup`) that removes the rest of the install folder
at your next sign-in. Windows deletes that entry after running it.

The portable version creates `settings.json` (in `%AppData%\OpacityBar`, or next to the exe in portable
mode) and the startup entry only if you turn it on. To remove it, quit it, sign out or restart (so Explorer
releases the helper), and delete its folder.

## Check it yourself

All the code is public in this repository, and each release is built from it by GitHub Actions
(the build log is public too). The parts that touch your system are:

- [`src/Lifecycle.cs`](src/Lifecycle.cs): install, uninstall, startup entry
- [`src/AppConfig.cs`](src/AppConfig.cs): the settings file
- [`src/TapInjector.cs`](src/TapInjector.cs): loading the helper
- [`native/OpacityBarTAP.cpp`](native/OpacityBarTAP.cpp): the helper inside Explorer

## Changes and contact

Any change to this policy will show up in this file and in the [changelog](CHANGELOG.md). Questions?
Open an [issue](https://github.com/mollerstudio/OpacityBar/issues) or get in touch through
[moller.studio](https://moller.studio).
