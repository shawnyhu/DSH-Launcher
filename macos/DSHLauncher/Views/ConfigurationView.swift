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
    @State private var highlightedInstallationID: UUID?
    @State private var highlightedHomeID: UUID?
    @State private var editingHome: DSHHomeEntry?
    @State private var confirmingUninstall = false
    @State private var confirmingHomeRemoval = false

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
        .sheet(item: $editingHome) { home in
            EditHomeView(home: home) { name, path in
                model.updateHome(id: home.id, name: name, path: path)
            }
        }
        .confirmationDialog(
            "卸载所选 DSH？",
            isPresented: $confirmingUninstall,
            titleVisibility: .visible
        ) {
            Button("卸载 DSH", role: .destructive) { Task { await model.uninstallSelectedInstallation() } }
            Button("取消", role: .cancel) {}
        } message: {
            Text("只删除所选程序包；DSH_HOME、凭据和对话数据会保留。")
        }
        .confirmationDialog(
            "移除所选 DSH_HOME 记录？",
            isPresented: $confirmingHomeRemoval,
            titleVisibility: .visible
        ) {
            Button("仅移除记录", role: .destructive) { model.removeSelectedHome() }
            Button("取消", role: .cancel) {}
        } message: {
            Text("只从 Launcher 列表移除路径，不会删除磁盘上的任何文件。")
        }
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
            versionActions
            if model.settings.installations.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "shippingbox").font(.system(size: 38)).foregroundStyle(.secondary)
                    Text("尚未安装 DSH").font(.headline)
                    Text("安装一个版本后即可从菜单栏启动 DSH。").foregroundStyle(.secondary)
                }.frame(maxWidth: .infinity, minHeight: 220, maxHeight: 260)
            } else {
                List(selection: $highlightedInstallationID) {
                    ForEach(model.settings.installations) { installation in
                        HStack(alignment: .center, spacing: 12) {
                            VStack(alignment: .leading, spacing: 4) {
                                Text(installation.displayName).font(.headline)
                                Text(installation.installRoot).font(.caption).foregroundStyle(.secondary)
                            }
                            Spacer(minLength: 12)
                            if model.settings.selectedInstallationID == installation.id {
                                Label("当前使用", systemImage: "checkmark.circle.fill")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(.green)
                                    .padding(.horizontal, 9)
                                    .padding(.vertical, 5)
                                    .background(.green.opacity(0.12), in: Capsule())
                                    .accessibilityLabel("当前使用的 DSH 软件包")
                            }
                        }
                        .tag(installation.id)
                    }
                }
                .frame(height: 220)
                .onAppear { highlightedInstallationID = model.settings.selectedInstallationID }
                .onChange(of: highlightedInstallationID) { installationID in
                    model.settings.selectInstallation(installationID)
                }
                .onChange(of: model.settings.selectedInstallationID) { installationID in
                    if highlightedInstallationID != nil { highlightedInstallationID = installationID }
                }
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
        }
    }

    private var versionActions: some View {
        HStack(spacing: 8) {
            Button { showingInstaller = true } label: { Label("安装版本…", systemImage: "plus") }
            Button { Task { await model.updateSelectedInstallation() } } label: {
                Label("更新所选", systemImage: "arrow.triangle.2.circlepath")
            }
            .disabled(model.settings.selectedInstallation == nil || model.busy)
            Button { Task { await model.reinstallSelectedInstallation() } } label: {
                Label("重新安装", systemImage: "arrow.clockwise")
            }
            .disabled(model.settings.selectedInstallation == nil || model.busy)
            Spacer()
            Button(role: .destructive) { confirmingUninstall = true } label: {
                Label("卸载", systemImage: "trash")
            }
            .disabled(model.settings.selectedInstallation == nil || model.busy)
        }
        .buttonStyle(.bordered)
    }

    private var homesView: some View {
        VStack(alignment: .leading, spacing: 16) {
            header("DSH_HOME", subtitle: "Launcher 只保存目录路径和兼容性元数据，不读取凭据或会话内容。")
            homeActions
            List(selection: $highlightedHomeID) {
                ForEach(model.settings.homes) { home in
                    HStack(alignment: .center, spacing: 12) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(home.name).font(.headline)
                            Text(home.path).font(.caption).foregroundStyle(.secondary)
                            if let writer = home.lastObservedWriterVersion {
                                Text("最后观察版本：\(writer)").font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                        Spacer(minLength: 12)
                        if model.settings.selectedHomeID == home.id {
                            Label("当前使用", systemImage: "checkmark.circle.fill")
                                .font(.caption.weight(.semibold))
                                .foregroundStyle(.green)
                                .padding(.horizontal, 9)
                                .padding(.vertical, 5)
                                .background(.green.opacity(0.12), in: Capsule())
                                .accessibilityLabel("当前使用的 DSH_HOME")
                        }
                    }
                    .tag(home.id)
                }
            }
            .frame(minHeight: 240, maxHeight: 360)
            .onAppear { highlightedHomeID = model.settings.selectedHomeID }
            .onChange(of: highlightedHomeID) { homeID in
                model.settings.selectHome(homeID)
            }
            .onChange(of: model.settings.selectedHomeID) { homeID in
                if highlightedHomeID != nil { highlightedHomeID = homeID }
            }
        }
    }

    private var homeActions: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 8) {
                homeAddAndEditActions
                Spacer()
                homeOpenAndRemoveActions
            }
            VStack(alignment: .leading, spacing: 8) {
                homeAddAndEditActions
                homeOpenAndRemoveActions
            }
        }
        .buttonStyle(.bordered)
    }

    private var homeAddAndEditActions: some View {
        HStack(spacing: 8) {
            Button { chooseHome(create: false) } label: { Label("添加已有…", systemImage: "plus") }
            Button { chooseHome(create: true) } label: { Label("创建目录…", systemImage: "folder.badge.plus") }
            Button { editingHome = model.settings.selectedHome } label: { Label("编辑所选…", systemImage: "pencil") }
                .disabled(model.settings.selectedHome == nil)
        }
    }

    private var homeOpenAndRemoveActions: some View {
        HStack(spacing: 8) {
            Button {
                if let path = model.settings.selectedHome?.path { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
            } label: { Label("在 Finder 中显示", systemImage: "finder") }
            .disabled(model.settings.selectedHome == nil)
            Button(role: .destructive) { confirmingHomeRemoval = true } label: {
                Label("移除记录", systemImage: "trash")
            }
            .disabled(model.settings.homes.count <= 1 || model.settings.selectedHome == nil)
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

private struct EditHomeView: View {
    let home: DSHHomeEntry
    let onSave: (String, String) -> Bool
    @Environment(\.dismiss) private var dismiss
    @State private var name: String
    @State private var path: String

    init(home: DSHHomeEntry, onSave: @escaping (String, String) -> Bool) {
        self.home = home
        self.onSave = onSave
        _name = State(initialValue: home.name)
        _path = State(initialValue: home.path)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("编辑 DSH_HOME").font(.title2.bold())
            Text("只修改 Launcher 中的名称和路径记录，不移动或修改目录内容。")
                .foregroundStyle(.secondary)
            Form {
                LabeledContent("名称") { TextField("显示名称", text: $name) }
                LabeledContent("路径") {
                    HStack {
                        TextField("DSH_HOME 路径", text: $path)
                        Button("选择…") { chooseDirectory() }
                    }
                }
            }
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                Button("保存记录") {
                    if onSave(name, path) { dismiss() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(22)
        .frame(width: 560, height: 260)
    }

    private func chooseDirectory() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = false
        panel.directoryURL = URL(fileURLWithPath: NSString(string: path).expandingTildeInPath, isDirectory: true)
        panel.prompt = "选择"
        if panel.runModal() == .OK, let url = panel.url { path = url.path }
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
