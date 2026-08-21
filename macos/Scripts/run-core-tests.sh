#!/bin/zsh
set -euo pipefail

SCRIPT_DIR=${0:A:h}
MACOS_DIR=${SCRIPT_DIR:h}
export CLANG_MODULE_CACHE_PATH="${MACOS_DIR}/.build/clang-module-cache"
export SWIFTPM_MODULECACHE_OVERRIDE="${MACOS_DIR}/.build/swift-module-cache"
cd "${MACOS_DIR}"
swift run --disable-sandbox DSHLauncherCoreSelfCheck
