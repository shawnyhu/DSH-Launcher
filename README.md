# DSH Launcher

[![Windows CI](https://github.com/shawnyhu/DSH-Launcher/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/shawnyhu/DSH-Launcher/actions/workflows/windows-ci.yml)
[![GitHub Releases](https://img.shields.io/badge/releases-Windows%20%2B%20macOS-blue)](https://github.com/shawnyhu/DSH-Launcher/releases)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

DSH Launcher 是 DeepSeek Harness（`@deepseek-ai/dsh`）的桌面安装、启动和管理工具。

| 平台 | 状态 | Release 标签 |
|---|---|---|
| Windows 10/11 x64 | `0.1.9` 可用 | `win-v*` |
| macOS | 开发准备阶段 | `mac-v*` |

Windows 与 macOS 使用同一个 GitHub 仓库，但各自维护版本号、Release、安装包和更新通道。完整跨平台规格见 [`项目说明.md`](项目说明.md)。

## Windows 功能

- 系统托盘常驻，双击打开 DSH Web UI。
- 启动、停止和重启 Launcher 管理的 DSH 进程。
- 使用 DSH 官方黑白鲸鱼图标。
- 灰色表示停止，黑白表示空闲，绿色表示运行任务，黄色闪烁表示等待回答或审批。
- 提问、权限审批和对话结束时发送 Windows 通知。
- 管理多个 DSH 安装实例。
- 全局 npm 安装作为默认方式。
- 在用户选择的独立目录中安装多个指定版本。
- 更新、重装和卸载所选 DSH 实例。
- 更新 DSH 和 Launcher 时显示独立进度窗口。
- 按 DSH 官方 GitHub Release 发布时间选择最新的可安装版本。
- 管理多个 DSH_HOME，记录 Launcher 观察到的最后写入版本。
- 配置端口、工作目录、浏览器启动行为和开机自启。
- 从 GitHub Releases 检查并安装 Launcher 轻量更新。
- Launcher 不读取、保存或转发 API Key。

## Windows 一键安装

运行 `DSHLauncher-Windows-Setup-0.1.9-x64.exe`。

安装器会请求管理员权限，并默认把 Launcher 安装到 `C:\Program Files\DSH Launcher`。安装器会检查 Node.js；缺少兼容版本时安装 Node.js 24 LTS。随后默认全局安装最新的 `@deepseek-ai/dsh`，也允许改为 Launcher 管理的独立目录。安装过程中会单独选择 DSH_HOME，默认路径为 `%USERPROFILE%\.dsh`，可以手动修改。

全局安装和独立安装都使用 npm 官方程序包。DSH_HOME 与程序安装位置相互独立。安装完成后请从开始菜单或桌面快捷方式启动 Launcher。

## 只更新 Windows Launcher

已经安装过 DSH Launcher 时，可以运行 `DSHLauncher-Windows-Update-0.1.9-x64.exe`。

`0.1.7` 和 `0.1.8` 的下载器会在关闭临时文件之前执行重命名，因此托盘自动更新会在下载完成后报“文件正在被另一个进程使用”。这一次必须从 [`win-v0.1.9` Release](https://github.com/shawnyhu/DSH-Launcher/releases/tag/win-v0.1.9) 手动下载并运行轻量更新包。安装 `0.1.9` 后，后续托盘自动更新恢复正常。

轻量更新包只替换 Launcher 主程序，不安装 Node.js、不修改 DSH 程序包，也不更改 DSH_HOME 和现有配置。更新完成后请手动重新启动 Launcher；启动成功后会删除 `%TEMP%\DSHLauncher` 中已经下载的更新包。

Launcher 更新包下载时显示已下载大小、总大小和百分比；GitHub 未提供总大小时自动使用动态进度条。完成写入后会先关闭临时文件句柄，再执行重命名和启动更新器。

Windows `0.1.8` 是更新通道桥接版本：

- `0.1.7` 及更早客户端仍通过仓库 Latest 找到 `v0.1.8`。
- `0.1.8` 开始读取 GitHub Release 列表，同时校验 Windows 标签、语义版本和 x64 更新资产。
- GitHub REST API 限流或不可用时自动使用官方 Releases Atom Feed。
- Windows `0.1.9` 起使用 `win-v*`，并忽略所有 `mac-v*` Release。
- macOS Launcher 使用同样的筛选流程，只接受 `mac-v*` 和对应 Mac 架构资产。

更新仓库可以在“配置 → 启动设置”中修改，默认是 [`shawnyhu/DSH-Launcher`](https://github.com/shawnyhu/DSH-Launcher)。

## GitHub 双平台 Release

GitHub 的一个 Release 只能对应一个 Git tag。两个平台保持独立版本号，因此在同一仓库中使用两个 Release 通道：

```text
win-v0.1.9   → Windows Release
mac-v0.1.0   → macOS Release
```

仓库级 Latest 只有一个，自动更新不使用它判断平台最新版。两个 Launcher 都先检查平台标签，再比较版本和选择匹配架构的更新资产。`v0.1.8` 仅用于把旧 Windows 客户端迁移到该规则。

## DSH_HOME

默认路径：

```text
%USERPROFILE%\.dsh
```

配置窗口可以添加已有目录或创建新目录。切换 DSH 版本时，Launcher 会显示数据目录最后由哪个版本写入。

Launcher 通过文件系统变更事件记录最后写入版本，并排除 `.credentials.yaml`。在 Launcher 外启动 DSH、手动复制目录或文件监控事件丢失时，该记录可能不完整。

不同版本共用一个 DSH_HOME 可能存在数据格式兼容风险。Launcher 会提示风险，但无法保证上游版本之间兼容。请勿同时运行多个写入同一个 DSH_HOME 的 DSH 实例。

## 多版本管理

npm 全局范围只能保留一个 DSH 版本。其他版本安装到不同的独立目录，例如：

```text
D:\DSH Versions\0.1.0-rc.7
D:\DSH Versions\0.1.0-rc.8
```

在“配置 → DSH 版本 → 安装版本”中可以查询 npm 上所有版本、搜索版本号、选择安装范围和路径。

托盘菜单“检查并更新当前 DSH”操作当前运行配置选择的安装实例。DSH 更新期间会显示停止、安装、保存和重启阶段；npm 执行期间使用动态进度条。配置窗口中的更新按钮操作列表里选中的实例。Launcher 按 DSH 官方 GitHub Release 的发布时间选择 npm 中已经存在的最新包；GitHub Releases API 限流时使用官方 Atom Feed，两者都不可用时按 npm 发布时间回退，并且不会把较晚发布的当前版本降级。

## 数据和卸载

卸载或重装 DSH 程序包不会删除 DSH_HOME。

从 Launcher 中移除 DSH_HOME 只会移除路径记录。卸载 DSH Launcher 时也保留：

```text
%USERPROFILE%\.dsh
%LOCALAPPDATA%\DSHLauncher
```

如需删除这些数据，请先自行备份。

## 从源码构建 Windows 版本

要求：

- Windows 10/11 x64
- PowerShell 5.1 或更高版本
- Inno Setup 6（仅构建安装包需要）
- 网络连接（首次下载 SDK、Node MSI 和框架引用）

运行：

```powershell
.\scripts\Build.ps1
```

构建脚本会准备 .NET 10 SDK、发布自包含单文件程序、执行 `--check` 自检、下载并校验 Node.js 24.19.0 MSI，然后生成完整安装包和 Launcher 轻量更新包。

输出：

```text
artifacts\app\DshLauncher.exe
artifacts\installer\DSHLauncher-Windows-Setup-0.1.9-x64.exe
artifacts\updater\DSHLauncher-Windows-Update-0.1.9-x64.exe
```

只发布应用：

```powershell
.\scripts\Build.ps1 -SkipInstaller
```

## 日志

```text
%LOCALAPPDATA%\DSHLauncher\logs\launcher.log
```

日志不记录 API Key。
