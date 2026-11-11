# Branding assets

Everything here is generated from the logo PDF (`OpacityBar-logo.pdf`: page 1 black, page 2 white):

```powershell
.\tools\make-assets.ps1 -Pdf assets\OpacityBar-logo.pdf
.\build.ps1
```

| File | Used for |
| :--- | :--- |
| `OpacityBar.ico` | App and installer icon (Explorer, Start menu, Settings > Apps). Black logo. |
| `tray-black.ico` | Tray icon on a **light** taskbar, and the fallback everywhere |
| `tray-white.ico` | Tray icon on a **dark** taskbar |
| `logo-black.png` | Installer logo on the light theme, and the fallback |
| `logo-white.png` | Installer logo on the dark theme |

Rule: the black logo is used on light themes, the white one on dark themes, and the black one whenever a
single icon has to work everywhere. The script also writes `docs/images/logo-*.png` for the README.
