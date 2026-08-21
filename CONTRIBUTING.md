# Contributing

Thank you for helping improve DSH Launcher.

## Repository layout

- `src/DshLauncher/`, `installer/`, and `scripts/`: Windows application and packaging.
- `macos/`: macOS application and packaging when development begins.
- `项目说明.md`: shared product requirements, update-channel contract, and macOS implementation specification.

## Windows development setup

- Windows 10 or later, x64
- PowerShell 7
- .NET 10 SDK
- Node.js 22.19+ or 24+
- Inno Setup 6 for installer builds

Run a full build:

```powershell
.\scripts\Build.ps1
```

## macOS development setup

Follow `项目说明.md`. Keep all Mac source and packaging files under `macos/`. The target stack is Swift 6 with SwiftUI and AppKit. Mac changes must not rewrite the existing Windows project, tags, or Release assets.

## Pull requests

1. Create a focused branch. Mac branches should use the `mac/` prefix.
2. State whether the change affects Windows, macOS, or the shared update protocol.
3. Keep DSH_HOME, API credentials, signing certificates, update private keys, and notarization credentials out of the repository.
4. Describe user-visible behavior and verification steps.
5. Update README.md and the relevant CHANGELOG when behavior changes.
6. Do not commit artifacts, local SDKs, Node installers, logs, settings, DerivedData, or signing files.
7. The affected platform build must finish with zero warnings and zero errors.

Use a separate DSH_HOME and a non-default port for protocol tests.

## Release channels

- Windows bridge: `v0.1.8` only.
- Future Windows releases: `win-v*`.
- macOS releases: `mac-v*`.

A platform updater must reject Release tags and assets belonging to the other platform.

By contributing, you agree that your contribution is licensed under MIT.
