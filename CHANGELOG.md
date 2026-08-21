# Changelog

## 0.1.6 - 2026-08-21

- Select the newest installable DSH package by official GitHub Release publish time, with an Atom Feed fallback for API rate limits.
- Fall back to npm publish time and prevent updates from downgrading a newer installed package.
- Delete downloaded Launcher update packages on the next normal application start.

## 0.1.5 - 2026-08-21

- Do not automatically start Launcher after a full installation or lightweight update.
- Require a normal user-initiated launch after setup so DSH inherits the standard desktop session.

## 0.1.4 - 2026-08-21

- Detect elevated GUI launches before creating the tray or starting DSH.
- Relaunch through the interactive Windows Shell at medium integrity.
- Log Launcher version, process ID, and elevation state at startup.

## 0.1.3 - 2026-08-21

- Start Launcher as the original non-elevated user after setup and updates.
- Avoid GitHub REST API rate limits by using the official latest-release link.
- Show the Launcher version in the tray menu.
- Include the relevant DSH stderr line when startup fails.
- Preserve custom Launcher installation paths during lightweight updates.

## 0.1.2 - 2026-08-21

- Allow build self-checks on clean machines before DSH is installed.
- Validate the GitHub Releases build and publishing pipeline.

## 0.1.1 - 2026-08-21

- Fix configuration window construction at narrow widths.
- Use a vertical, responsive layout for DSH and DSH_HOME management.
- Prevent DSH and Launcher from opening duplicate browser tabs.
- Add a lightweight Launcher-only updater package.
- Add GitHub Releases based Launcher update checks.

## 0.1.0 - 2026-08-21

- Initial public preview.
