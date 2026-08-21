#!/bin/zsh
set -euo pipefail

SCRIPT_DIR=${0:A:h}
MACOS_DIR=${SCRIPT_DIR:h}
REPO_ROOT=${MACOS_DIR:h}
VERSION=${VERSION:-0.2.0}
ARCH=${ARCH:-arm64}
BUILD_ROOT=${REPO_ROOT}/artifacts/macos
APP_NAME="DSH Launcher"
APP_BUNDLE=${BUILD_ROOT}/${APP_NAME}.app
CONTENTS=${APP_BUNDLE}/Contents
EXECUTABLE=${CONTENTS}/MacOS/${APP_NAME}
PACKAGE=${BUILD_ROOT}/DSHLauncher-Mac-Setup-${VERSION}-${ARCH}.pkg
UPDATE_PACKAGE=${BUILD_ROOT}/DSHLauncher-Mac-Update-${VERSION}-${ARCH}.pkg
MODULE_CACHE=${MACOS_DIR}/.build/release-module-cache

mkdir -p "${CONTENTS}/MacOS" "${CONTENTS}/Resources" "${MODULE_CACHE}"

SOURCES=(
  "${MACOS_DIR}/DSHLauncher/App/DSHLauncherApp.swift"
  "${MACOS_DIR}/DSHLauncher/App/AppDelegate.swift"
  "${MACOS_DIR}/DSHLauncher/App/AppModel.swift"
  "${MACOS_DIR}/DSHLauncher/App/MenuBarController.swift"
  "${MACOS_DIR}/DSHLauncher/App/ConfigurationWindowController.swift"
  "${MACOS_DIR}/DSHLauncher/Models/LauncherSettings.swift"
  "${MACOS_DIR}/DSHLauncher/Infrastructure/AppPaths.swift"
  "${MACOS_DIR}/DSHLauncher/Infrastructure/AppLogger.swift"
  "${MACOS_DIR}/DSHLauncher/Infrastructure/SettingsStore.swift"
  "${MACOS_DIR}/DSHLauncher/Services/CommandRunner.swift"
  "${MACOS_DIR}/DSHLauncher/Services/NodeDiscoveryService.swift"
  "${MACOS_DIR}/DSHLauncher/Services/NpmService.swift"
  "${MACOS_DIR}/DSHLauncher/Services/PortService.swift"
  "${MACOS_DIR}/DSHLauncher/Services/DSHRuntimeService.swift"
  "${MACOS_DIR}/DSHLauncher/Services/DSHEventMonitor.swift"
  "${MACOS_DIR}/DSHLauncher/Services/DSHHomeWatcher.swift"
  "${MACOS_DIR}/DSHLauncher/Services/LoginItemService.swift"
  "${MACOS_DIR}/DSHLauncher/Services/NotificationService.swift"
  "${MACOS_DIR}/DSHLauncher/Services/LauncherUpdateService.swift"
  "${MACOS_DIR}/DSHLauncher/Views/ConfigurationView.swift"
)

xcrun swiftc \
  -parse-as-library \
  -swift-version 6 \
  -O \
  -whole-module-optimization \
  -target "${ARCH}-apple-macos13.0" \
  -module-name DSHLauncher \
  -module-cache-path "${MODULE_CACHE}" \
  "${SOURCES[@]}" \
  -o "${EXECUTABLE}"

cp "${MACOS_DIR}/DSHLauncher/Resources/Info.plist" "${CONTENTS}/Info.plist"
plutil -replace CFBundleExecutable -string "${APP_NAME}" "${CONTENTS}/Info.plist"
plutil -replace CFBundleIdentifier -string "com.shawnyhu.dshlauncher" "${CONTENTS}/Info.plist"
plutil -replace CFBundleName -string "${APP_NAME}" "${CONTENTS}/Info.plist"
plutil -replace CFBundleShortVersionString -string "${VERSION}" "${CONTENTS}/Info.plist"
plutil -replace CFBundleVersion -string "${BUILD_NUMBER:-1}" "${CONTENTS}/Info.plist"
plutil -replace LSMinimumSystemVersion -string "13.0" "${CONTENTS}/Info.plist"
cp "${MACOS_DIR}/DSHLauncher/Resources/Whale.svg" "${CONTENTS}/Resources/Whale.svg"
cp "${MACOS_DIR}/DSHLauncher/Resources/UpdatePublicKeys.json" "${CONTENTS}/Resources/UpdatePublicKeys.json"

codesign --force --options runtime --timestamp=none --sign "${APP_SIGN_IDENTITY:--}" "${APP_BUNDLE}"
codesign --verify --deep --strict --verbose=2 "${APP_BUNDLE}"

STAGING=${BUILD_ROOT}/pkg-root
mkdir -p "${STAGING}/Applications"
ditto "${APP_BUNDLE}" "${STAGING}/Applications/${APP_NAME}.app"
PKG_ARGS=(--root "${STAGING}" --identifier com.shawnyhu.dshlauncher --version "${VERSION}" --install-location /)
COMPONENT_DIR=$(mktemp -d "${TMPDIR:-/tmp}/dsh-launcher-components.XXXXXX")
trap 'rm -rf "${COMPONENT_DIR}"' EXIT
COMPONENT_PLIST=${COMPONENT_DIR}/components.plist
pkgbuild --analyze --root "${STAGING}" "${COMPONENT_PLIST}"
/usr/libexec/PlistBuddy -c "Set :0:BundleIsRelocatable false" "${COMPONENT_PLIST}"
PKG_ARGS+=(--component-plist "${COMPONENT_PLIST}")
if [[ -n "${INSTALLER_SIGN_IDENTITY:-}" ]]; then
  PKG_ARGS+=(--sign "${INSTALLER_SIGN_IDENTITY}")
fi
pkgbuild "${PKG_ARGS[@]}" "${PACKAGE}"
cp "${PACKAGE}" "${UPDATE_PACKAGE}"

(cd "${BUILD_ROOT}" && shasum -a 256 "${PACKAGE:t}" "${UPDATE_PACKAGE:t}" > checksums-macos.txt)
SIGNING_KEY=${UPDATE_SIGNING_KEY:-/private/tmp/dsh-launcher-dev-update-key.raw}
if [[ -f "${SIGNING_KEY}" ]]; then
  env CLANG_MODULE_CACHE_PATH="${MODULE_CACHE}" xcrun swift \
    "${MACOS_DIR}/Scripts/make-update-manifest.swift" \
    "${UPDATE_PACKAGE}" "${VERSION}" "${ARCH}" \
    "${UPDATE_KEY_ID:-development-2026-08}" "${SIGNING_KEY}" \
    "${APPLE_TEAM_ID:-ADHOC}" "${BUILD_ROOT}"
  env CLANG_MODULE_CACHE_PATH="${MODULE_CACHE}" xcrun swift \
    "${MACOS_DIR}/Scripts/verify-update-manifest.swift" \
    "${BUILD_ROOT}/update-manifest-macos-${ARCH}.json" \
    "${BUILD_ROOT}/update-manifest-macos-${ARCH}.sig" \
    "${MACOS_DIR}/DSHLauncher/Resources/UpdatePublicKeys.json" \
    "${UPDATE_PACKAGE}"
fi
echo "Built ${APP_BUNDLE}"
echo "Built ${PACKAGE}"
