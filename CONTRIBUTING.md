# Contributing

Thank you for helping improve DSH Launcher.

## Development setup

- Windows 10 or later, x64
- PowerShell 7
- .NET 10 SDK
- Node.js 22.19+ or 24+
- Inno Setup 6 for installer builds

Run a full build:

```powershell
.\scripts\Build.ps1
```

The build must finish with zero warnings and zero errors. Use a separate
DSH_HOME and non-default port for protocol tests.

## Pull requests

1. Create a focused branch.
2. Keep DSH_HOME and API credentials out of the repository.
3. Describe user-visible behavior and verification steps.
4. Update README.md and CHANGELOG.md when behavior changes.
5. Do not commit artifacts, local SDKs, Node MSI files, logs, or settings.

By contributing, you agree that your contribution is licensed under MIT.
