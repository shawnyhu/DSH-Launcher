import AppKit
import SwiftUI

private enum ConfigurationSection: String, CaseIterable, Identifiable {
    case versions = "DSH 版本"
    case homes = "DSH_HOME"
    case startup = "启动设置"
    var id: Self { self }
    var icon: String {
        switch self {
        case .versions: return "shippingbox"
        case .homes: return "folder"
        case .startup: return "gearshape"
        }
    }
}

struct ConfigurationView: View {
    @ObservedObject var model: AppModel
    @State private var section: ConfigurationSection? = .versions
    @State private var showingInstaller = false

    var body: some View {
        NavigationSplitView {
            List(ConfigurationSection.allCases, selection: $section) { item in
                Label(item.rawValue, systemImage: item.icon).tag(item)
            }
            .navigationSplitViewColumnWidth(min: 170, ideal: 190, max: 230)
        } detail: {
            Group {
                switch section ?? .versions {
                case .versions: versionsView
                case .homes: homesView
                case .startup: startupView
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .padding(22)
        }
        .safeAreaInset(edge: .bottom) { footer }
        .sheet(isPresented: $showingInstaller) { InstallVersionView(model: model) }
        .alert("DSH Launcher", isPresented: Binding(
            get: { model.presentedError != nil },
            set: { if !$0 { model.presentedError = nil } }
        )) { Button("好") { model.presentedError = nil } } message: {
            Text(model.presentedError ?? "")
        }
        .frame(minWidth: 680, minHeight: 520)
    }

    private var versionsView: some View {
        VStack(alignment: .leading, spacing: 16) {
            header("DSH 版本", subtitle: "管理 npm 全局版本和 Launcher 独立版本。")
            if model.settings.installations.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "shippingbox").font(.system(size: 38)).foregroundStyle(.secondary)
                    Text("尚未安装 DSH").font(.headline)
                    Text("安装一个版本后即可从菜单栏启动 DSH。").foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, minHeight: 220)
            } else {
                List(selection: $model.settings.selectedInstallationID) {
                    ForEach(model.settings.installations) { installation in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(installation.displayName).font(.headline)
                            Text(installation.installRoot).font(.caption).foregroundStyle(.secondary)
                        }
                        .tag(installation.id)
                    }
                }
                .frame(minHeight: 210)
                if let selected = model.settings.selectedInstallation {
                    GroupBox("所选实例") {
                        Grid(alignment: .leading, horizontalSpacing: 14, verticalSpacing: 6) {
                            GridRow { Text("版本").foregroundStyle(.secondary); Text(selected.installedVersion) }
                            GridRow { Text("Node").foregroundStyle(.secondary); Text(selected.nodeExecutable).textSelection(.enabled) }
                            GridRow { Text("路径").foregroundStyle(.secondary); Text(selected.packageRoot).textSelection(.enabled) }
                        }.frame(maxWidth: .infinity, alignment: .leading).padding(6)
                    }
                }
            }
            HStack {
                Button("安装版本…") { showingInstaller = true }
                Button("检查并更新") { Task { await model.updateSelectedInstallation() } }
                    .disabled(model.settings.selectedInstallation == nil || model.busy)
                Button("卸载", role: .destructive) { Task { await model.uninstallSelectedInstallation() } }
                    .disabled(model.settings.selectedInstallation == nil || model.busy)
            }
        }
    }

    private var homesView: some View {
        VStack(alignment: .leading, spacing: 16) {
            header("DSH_HOME", subtitle: "Launcher 只保存目录路径和兼容性元数据，不读取凭据或会话内容。")
            List(selection: $model.settings.selectedHomeID) {
                ForEach(model.settings.homes) { home in
                    VStack(alignment: .leading, spacing: 4) {
                        Text(home.name).font(.headline)
                        Text(home.path).font(.caption).foregroundStyle(.secondary)
                        if let writer = home.lastObservedWriterVersion {
                            Text("最后观察版本：\(writer)").font(.caption2).foregroundStyle(.secondary)
                        }
                    }.tag(home.id)
                }
            }.frame(minHeight: 260)
            HStack {
                Button("添加已有目录…") { chooseHome(create: false) }
                Button("创建新目录…") { chooseHome(create: true) }
                Button("在 Finder 中显示") {
                    if let path = model.settings.selectedHome?.path { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
                }.disabled(model.settings.selectedHome == nil)
                Button("移除记录", role: .destructive) { model.removeSelectedHome() }
                    .disabled(model.settings.homes.count <= 1)
            }
        }
    }

    private var startupView: some View {
        Form {
            Section {
                LabeledContent("端口") {
                    HStack {
                        TextField("3080", value: $model.settings.port, format: .number).frame(width: 90)
                        Button("查找可用端口") { model.settings.port = PortService.findAvailable(startingAt: model.settings.port) }
                    }
                }
                LabeledContent("工作目录") {
                    HStack {
                        TextField("工作目录", text: $model.settings.workingDirectory)
                        Button("选择…") { chooseWorkingDirectory() }
                    }
                }
                LabeledContent("更新仓库") { TextField("owner/repository", text: $model.settings.launcherUpdateRepository) }
            } header: { Text("运行") }
            Section {
                Toggle("Launcher 启动时启动 DSH", isOn: $model.settings.startDSHWithLauncher)
                Toggle("DSH 启动后打开浏览器", isOn: $model.settings.openBrowserAfterStart)
                Toggle("登录时启动 DSH Launcher", isOn: $model.settings.startAtLogin)
                Toggle("对话完成时发送通知", isOn: $model.settings.notifyOnCompletion)
            } header: { Text("行为") }
        }
    }

    private var footer: some View {
        HStack {
            if let progress = model.progress {
                ProgressView().controlSize(.small)
                Text(progress.stage).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("保存") { Task { await model.save() } }.keyboardShortcut("s")
            Button("保存并重启 DSH") { Task { await model.save(restart: true) } }
                .disabled(model.settings.selectedInstallation == nil)
        }
        .padding(12)
        .background(.bar)
    }

    private func header(_ title: String, subtitle: String) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(.title2.bold())
            Text(subtitle).foregroundStyle(.secondary)
        }
    }

    private func chooseHome(create: Bool) {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = create
        panel.prompt = create ? "创建并选择" : "添加"
        if panel.runModal() == .OK, let url = panel.url { model.addHome(url) }
    }

    private func chooseWorkingDirectory() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        if panel.runModal() == .OK, let url = panel.url { model.settings.workingDirectory = url.path }
    }
}

private struct InstallVersionView: View {
    @ObservedObject var model: AppModel
    @Environment(\.dismiss) private var dismiss
    @State private var selectedVersion: String?
    @State private var scope: DSHInstallScope = .managed
    @State private var search = ""

    private var filtered: [NpmVersionInfo] {
        search.isEmpty ? model.availableVersions : model.availableVersions.filter { $0.version.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("安装 DSH 版本").font(.title2.bold())
            TextField("搜索版本", text: $search)
            Picker("安装范围", selection: $scope) {
                Text("Launcher 管理（推荐）").tag(DSHInstallScope.managed)
                Text("npm 全局").tag(DSHInstallScope.global)
            }.pickerStyle(.segmented)
            List(selection: $selectedVersion) {
                ForEach(filtered) { item in
                    HStack {
                        Text(item.version)
                        if item.isLatest { Text("latest").font(.caption).foregroundStyle(.secondary) }
                    }.tag(item.version)
                }
            }.frame(minHeight: 280)
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                Button("安装") {
                    guard let selectedVersion else { return }
                    Task { await model.install(version: selectedVersion, scope: scope); dismiss() }
                }.disabled(selectedVersion == nil || model.busy)
            }
        }
        .padding(20)
        .frame(width: 520, height: 440)
        .task {
            await model.refreshVersions()
            selectedVersion = model.availableVersions.first(where: \ .isLatest)?.version ?? model.availableVersions.first?.version
        }
    }
}
