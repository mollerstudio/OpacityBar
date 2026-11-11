# Contributing to OpacityBar

Thanks for helping! Bug reports, ideas and pull requests are all welcome. Please follow the
[code of conduct](CODE_OF_CONDUCT.md). By contributing, you agree that your contribution is licensed under the
project's [MIT License](LICENSE).

Two rules keep OpacityBar trustworthy, so changes that break them won't be merged:

- **No network access** of any kind (no telemetry, update checks or downloads).
- **Nothing beyond the taskbar's look.** Every new file, registry value or system change must be listed in
  [PRIVACY.md](PRIVACY.md) in the same pull request.

## Reporting a bug

Use [New issue > Bug report](https://github.com/mollerstudio/OpacityBar/issues/new/choose) and include your
Windows version (`winver`), the OpacityBar version (shown in the settings footer) and what you expected to see.
A diagnostics log helps a lot: quit OpacityBar, start it with `OpacityBar.exe --diagnostics`, reproduce the
problem, then attach `%LocalAppData%\OpacityBar\opacitybar.log`. Use **Save taskbar map...** in settings
if the problem is about how the taskbar looks.

## Building

| Part | Tooling |
| :--- | :--- |
| `OpacityBar.exe`, `OpacityBarSetup.exe` | `.\build.ps1`, using the .NET Framework `csc.exe` built into Windows |
| `OpacityBarTAP.<arch>.dll` | MSVC + Windows SDK (C++20, C++/WinRT). See the `Compile helper DLLs` step in [build.yml](.github/workflows/build.yml) |

Without MSVC you can still work on the app: put helper DLLs from a release into `dist\`, then run `.\build.ps1`.
`.\build.ps1 -OutExe <path>` builds just the app somewhere else, which is handy while `dist\OpacityBar.exe` is running.

## Code style

- C# 5 (what the built-in compiler supports): no `?.`, `$""`, `nameof` or expression-bodied members.
- All UI is owner-drawn from the tokens in [STYLEGUIDE.md](STYLEGUIDE.md): monochrome, with color only for
  success, warning and error.
- The helper stays a single, commented C++ file. Any change to how the app and helper talk updates
  [native/PROTOCOL.md](native/PROTOCOL.md) and bumps the protocol version on both sides.
- Keep it light: no network access, no admin rights, no background polling that isn't needed.

## Releasing (maintainers)

1. Bump `Version` in [`src/AppInfo.cs`](src/AppInfo.cs).
2. Move the `Unreleased` notes in [CHANGELOG.md](CHANGELOG.md) under a new `## [x.y.z] - date` heading.
3. Commit, then tag and push: `git tag v1.2.3 && git push origin v1.2.3`.

The workflow checks that the tag matches `AppInfo.Version`, builds everything, and publishes a GitHub Release
with `OpacityBar-x.y.z-Setup.exe`, `OpacityBar-x.y.z-Portable.zip` and `SHA256SUMS.txt`, using the changelog
section as the release notes.
