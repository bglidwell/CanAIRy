# CanAIRy

**CanAIRy — Wireless Display Casting** is a native Windows tray application for
casting an entire desktop or an individual app window to an Apple TV on the
local network. It includes Apple TV discovery, PIN pairing, remembered
receivers, real app-window previews, and automatic reconnection.

[![Build](https://github.com/bglidwell/CanAIRy/actions/workflows/build.yml/badge.svg)](https://github.com/bglidwell/CanAIRy/actions/workflows/build.yml)

## Install

Versioned releases provide two Windows x64 downloads:

- `CanAIRy-<version>-win-x64-setup.exe` — per-user installer with Start menu and
  optional desktop shortcuts. It supports silent installation and does not
  require administrator privileges.
- `CanAIRy-<version>-win-x64-portable.zip` — self-contained portable folder.

Until the first release is published, build the app locally with:

```powershell
dotnet build app\CanAIRy\CanAIRy.csproj -c Release
```

## Releases

Pushing a semantic version tag builds and publishes a GitHub Release:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The **Release** workflow can also be run manually to build and download an
installer artifact without publishing a GitHub Release.

Optional Authenticode signing is enabled when the repository contains the
`WINDOWS_CERTIFICATE_BASE64` and `WINDOWS_CERTIFICATE_PASSWORD` Actions secrets.

## WinGet

The proposed package identifier is `BobbyGlidwell.CanAIRy`. The initial package
must be submitted to Microsoft's community repository after the first stable
GitHub release. Later version submissions can be automated with the included
**Submit WinGet update** workflow. See [distribution/WINGET.md](distribution/WINGET.md).

## Third-party components

The AirPlay sender is derived from `omarroth/doubletake`, and FFmpeg is included
for Windows screen capture and H.264 encoding. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the license texts under
`sender/`.
