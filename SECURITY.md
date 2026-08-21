# Security Policy

## Supported versions

Only the newest Release in each platform channel is supported:

- Windows: `win-v*` (`v0.1.8` is the one-time bridge)
- macOS: `mac-v*`

## Reporting a vulnerability

Do not open a public issue for a vulnerability that could expose credentials, execute unintended commands, bypass update tag/signature validation, or modify a user's DSH_HOME. Use GitHub's private security advisory feature for this repository.

DSH Launcher must never read, store, log, or transmit API keys. Reports should exclude real credentials, private DSH_HOME contents, conversation data, signing certificates, and private update keys.
