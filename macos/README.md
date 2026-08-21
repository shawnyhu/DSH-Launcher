# DSH Launcher for macOS

macOS 版是原生 SwiftUI/AppKit 菜单栏应用，首版支持 Apple Silicon 与 macOS 13 及以上版本。它与 Windows 版共用产品版本号，但使用完全独立的源码、运行目录、构建产物、CI、Release 标签和自动更新资产。

## 用户体验

- 以菜单栏应用运行，不占用 Dock。
- 原生侧边栏配置窗口，可管理 DSH 版本、`DSH_HOME` 与启动设置。
- 发现 Homebrew、nvm、fnm、Volta 和系统 Node.js，要求 `^22.19.0 || >=24.0.0`。
- 支持 npm 全局安装和 Launcher 托管的多版本安装。
- 启动、停止、重启 DSH，并通过 RPC 就绪检测确认服务可用。
- 只停止 Launcher 自己创建的进程；不会误杀占用端口的外部 DSH。
- 使用 macOS 通知处理提问、审批和任务完成事件。
- Launcher 更新只接受 `mac-v*`、当前 CPU 架构资产、Ed25519 签名清单和匹配的 SHA-256。

## 平台隔离

macOS 代码只位于 `macos/`，不会移动或改写 `src/`、`installer/`、`scripts/` 等 Windows 目录。运行时数据也不复用 Windows 路径：

```text
~/Library/Application Support/DSHLauncher/   配置与托管 DSH 版本
~/Library/Caches/DSHLauncher/                npm 缓存与更新下载
~/Library/Logs/DSHLauncher/                  Launcher 日志
~/.dsh                                      默认 DSH_HOME（用户数据，不随 Launcher 卸载）
```

构建输出固定在 `artifacts/macos/`。macOS CI 仅监听 `macos/**`，macOS Release 工作流仅接受 `mac-v*`；Windows 工作流仍只接受 `win-v*`。两个平台的自动更新器都拒绝另一平台的标签和资产。

## 本地构建与测试

要求：Apple Silicon Mac、macOS 13+、Command Line Tools 或 Xcode、兼容的 Node.js/npm。

```bash
macos/Scripts/run-core-tests.sh
DSH_LAUNCHER_LIVE_CHECK=1 macos/Scripts/run-core-tests.sh
VERSION=0.2.0 ARCH=arm64 macos/Scripts/build-release.sh
open "artifacts/macos/DSH Launcher.app"
```

产物：

```text
artifacts/macos/DSH Launcher.app
artifacts/macos/DSHLauncher-Mac-Setup-0.2.0-arm64.pkg
artifacts/macos/DSHLauncher-Mac-Update-0.2.0-arm64.pkg
artifacts/macos/checksums-macos.txt
artifacts/macos/update-manifest-macos-arm64.json
artifacts/macos/update-manifest-macos-arm64.sig
```

未配置证书时，脚本使用 Hardened Runtime 的 ad-hoc 签名，适合本机开发验证。公开 Release 必须提供 Developer ID Application、Developer ID Installer、Apple 公证凭据和离线保存的更新清单私钥；私钥、证书和 keychain 不得提交到仓库或暴露给 CI 日志。

完整 Xcode 环境可进一步运行：

```bash
xcodebuild -project macos/DSHLauncher.xcodeproj \
  -scheme DSHLauncher \
  -destination 'platform=macOS' \
  CODE_SIGNING_ALLOWED=NO test
```

## 正式发布约束

- 标签必须是 `mac-v<semver>`，且与工程 `MARKETING_VERSION` 一致。
- Apple Silicon 资产名必须以 `-arm64.pkg` 结尾。
- `.app` 必须使用 Developer ID Application 签名并启用 Hardened Runtime。
- `.pkg` 必须使用 Developer ID Installer 签名，提交 Apple 公证并成功 staple。
- 更新清单必须使用仓库内公钥对应的离线 Ed25519 私钥签名。
- 不得把 macOS 资产附加到 `win-v*` Release，也不得把 Windows 资产附加到 `mac-v*` Release。

`.github/workflows/macos-release.yml` 需要单独配置 macOS 证书、证书密码、临时 keychain 密码、Application/Installer 身份、Apple ID、Team ID、App 专用密码，以及与 `UpdatePublicKeys.json` 匹配的 Ed25519 私钥和 key ID。工作流会签名、提交公证、staple、重新计算更新哈希并只发布 Mac 资产；任一步失败都不会创建可被 Launcher 接受的更新清单。

DSH Launcher 是社区项目，并非 DeepSeek 官方产品。鲸鱼资源仅用于表达本机 DSH 运行状态。
