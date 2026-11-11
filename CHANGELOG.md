# Changelog

All notable changes to OpacityBar. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.1.0] - 2026-11-11

The first public release, under the new name **OpacityBar** (previously Opacbar).

### Added
- New installer: one window, one **Install** button. It updates an existing copy in place, needs no admin
  rights, and can start OpacityBar with Windows. Uninstall from Settings > Apps.
- **Normal** style: the standard Windows taskbar, one click away.
- **Blur amount** slider for a real, adjustable blur behind the taskbar, with an optional tint.
- Gear button for settings: start with Windows, light/dark theme, reset, quit.
- New OpacityBar logo: black on light themes, white on dark themes (tray icon and installer follow Windows).
- [Privacy policy](PRIVACY.md) listing every file and registry entry OpacityBar touches, plus a code of
  conduct and a note on the OpacityBar name and logo.

### Changed
- Simpler window: no on/off switch or tabs. Pick Normal to get the stock taskbar back.
- The tray icon follows the taskbar's light/dark theme and dims while Normal is selected.
- Settings move to `%AppData%\OpacityBar`; settings from Opacbar 1.0 are carried over automatically.
- Explorer restarts needed after a helper update now happen automatically.
- No logs unless you start OpacityBar with `--diagnostics`.

### Fixed
- Blur rendering as a solid color.
- Taskbar disappearing after an automatic Explorer restart.
- Settings window sized wrong on high-DPI screens.

### Removed
- Floating "pill" taskbar and per-situation styles (one look now applies everywhere).

## 1.0.0

- First test version, as Opacbar (development builds only, never published as a release).

[Unreleased]: https://github.com/mollerstudio/OpacityBar/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/mollerstudio/OpacityBar/releases/tag/v1.1.0
