# Security policy

OpacityBar loads a small helper DLL into `explorer.exe` (see [How it works](README.md#how-it-works)), so we
take security reports seriously.

## Supported versions

Only the [latest release](https://github.com/mollerstudio/OpacityBar/releases/latest) gets fixes.

## Reporting a vulnerability

Please **don't** open a public issue. Report it privately through
[GitHub security advisories](https://github.com/mollerstudio/OpacityBar/security/advisories/new).
Include the steps to reproduce and the impact you see. You'll get a reply within a week.

## Verifying downloads

Every release lists SHA-256 checksums in `SHA256SUMS.txt`. Releases are built by GitHub Actions from the tagged
commit. The workflow log shows exactly what was built.
