import AppKit
import Combine
import Foundation

@MainActor
final class AppModel: ObservableObject {
    @Published var settings: LauncherSettings
    @Published private(set) var status = DSHStatusSnapshot.stopped
    @Published private(set) var ownership = RuntimeOwnership.stopped
    @Published private(set) var busy = false
    @Published private(set) var progress: OperationProgress?
    @Published private(set) var availableVersions: [NpmVersionInfo] = []
    @Published var presentedError: String?

    let layout: AppPathLayout
    private let store: SettingsStore
    private let logger: AppLogger
    private let npm: NpmService
    private let runtime: DSHRuntimeService
    private let launcherUpdates: LauncherUpdateService
    private let notifications: NotificationService
    private let loginItems = LoginItemService()
    private var events: DSHEventMonitor!
    private var homeWatcher: DSHHomeWatcher!

    init(layout: AppPathLayout, store: SettingsStore, logger: AppLogger, notifications: NotificationService) {
        self.layout = layout
        self.store = store
        self.logger = logger
        self.notifications = notifications
        settings = .defaults()
        let commands = CommandRunner()
        let discovery = NodeDiscoveryService(commands: commands)
        npm = NpmService(commands: commands, nodeDiscovery: discovery, layout: layout)
        runtime = DSHRuntimeService(logger: logger)
        launcherUpdates = LauncherUpdateService(
            layout: layout,
            currentVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.2.0"
        )
        events = DSHEventMonitor(
            logger: logger,
            statusHandler: { [weak self] snapshot in self?.status = snapshot },
            notificationHandler: { [weak self] notification in
                guard let self else { return }
                self.notifications.send(notification, enabled: self.settings.notifyOnCompletion)
            }
        )
        homeWatcher = DSHHomeWatcher { [weak self] homeID, version, date in
            guard let self, let index = self.settings.homes.firstIndex(where: { $0.id == homeID }) else { return }
            self.settings.homes[index].lastObservedWriterVersion = version
            self.settings.homes[index].lastObservedWriteAt = date
            self.settings.homes[index].observationReliable = true
            Task { try? await self.store.save(self.settings) }
        }
        notifications.openDSH = { [weak self] in self?.openWeb() }
    }

    func startApplication() async {
        settings = await store.load()
        settings.startAtLogin = loginItems.isEnabled
        do {
            if let global = try await npm.discoverGlobal() {
                if let index = settings.installations.firstIndex(where: { $0.scope == .global }) {
                    var replacement = global
                    replacement.id = settings.installations[index].id
                    settings.installations[index] = replacement
                } else {
                    settings.installations.append(global)
                }
                settings.selectedInstallationID = settings.selectedInstallationID ?? global.id
                try await store.save(settings)
            }
        } catch {
            await logger.warning("Global DSH discovery skipped: \(error.localizedDescription)")
        }
        await events.start(port: settings.port)
        if settings.startDSHWithLauncher, settings.selectedInstallation != nil {
            await startDSH(openBrowser: settings.openBrowserAfterStart)
        }
    }

    func save(restart: Bool = false) async {
        await perform(stage: "正在保存设置…") {
            try loginItems.setEnabled(settings.startAtLogin)
            try await store.save(settings)
            if restart { _ = try await restartRuntime() }
        }
    }

    func startDSH(openBrowser: Bool = true) async {
        await perform(stage: "正在启动 DSH…") {
            guard let installation = settings.selectedInstallation, let home = settings.selectedHome else {
                throw NpmServiceError.invalidInstallation("请先选择 DSH 版本和 DSH_HOME。")
            }
            try FileManager.default.createDirectory(at: URL(fileURLWithPath: home.path), withIntermediateDirectories: true)
            ownership = try await runtime.start(
                installation: installation,
                dshHome: URL(fileURLWithPath: home.path),
                workingDirectory: URL(fileURLWithPath: settings.workingDirectory),
                port: settings.port
            )
            if case .launcherOwned = ownership { await homeWatcher.start(home: home, version: installation.installedVersion) }
            await events.start(port: settings.port)
            if openBrowser { openWeb() }
        }
    }

    func stopDSH() async {
        if ownership == .external {
            presentedError = "当前 DSH 不是由 Launcher 启动；为避免误杀，Launcher 不会停止它。"
            return
        }
        await perform(stage: "正在停止 DSH…") {
            await runtime.stop()
            await homeWatcher.stop()
            ownership = .stopped
            await events.stop()
        }
    }

    func restartDSH() async {
        await perform(stage: "正在重启 DSH…") { _ = try await restartRuntime() }
    }

    func openWeb() {
        guard let url = URL(string: "http://127.0.0.1:\(settings.port)") else { return }
        Task {
            if !(await runtime.isReady(port: settings.port)) { await startDSH(openBrowser: false) }
            if await runtime.isReady(port: settings.port) { NSWorkspace.shared.open(url) }
        }
    }

    func refreshVersions() async {
        await perform(stage: "正在读取可安装版本…") {
            availableVersions = try await npm.versions()
        }
    }

    func install(version: String, scope: DSHInstallScope) async {
        await perform(stage: "正在安装 DSH \(version)…") {
            let installation = try await npm.install(scope: scope, root: nil, version: version)
            if let index = settings.installations.firstIndex(where: {
                $0.scope == installation.scope && ($0.scope == .global || $0.installRoot == installation.installRoot)
            }) {
                var replacement = installation
                replacement.id = settings.installations[index].id
                settings.installations[index] = replacement
                settings.selectedInstallationID = replacement.id
            } else {
                settings.installations.append(installation)
                settings.selectedInstallationID = installation.id
            }
            try await store.save(settings)
        }
    }

    func updateSelectedInstallation() async {
        guard let selected = settings.selectedInstallation else { return }
        await refreshVersions()
        guard let latest = availableVersions.first(where: \ .isLatest) else { return }
        guard latest.version != selected.installedVersion else {
            presentedError = "所选 DSH \(selected.installedVersion) 已是 npm latest。"
            return
        }
        let wasRunning: Bool
        if case .launcherOwned = ownership { wasRunning = true } else { wasRunning = false }
        if wasRunning { await stopDSH() }
        await perform(stage: "正在更新到 DSH \(latest.version)…") {
            var updated = try await npm.install(
                scope: selected.scope,
                root: selected.scope == .managed ? URL(fileURLWithPath: selected.installRoot) : nil,
                version: latest.version
            )
            updated.id = selected.id
            if let index = settings.installations.firstIndex(where: { $0.id == selected.id }) {
                settings.installations[index] = updated
            }
            try await store.save(settings)
        }
        if wasRunning { await startDSH(openBrowser: false) }
    }

    func reinstallSelectedInstallation() async {
        guard let selected = settings.selectedInstallation else { return }
        let wasRunning: Bool
        if case .launcherOwned = ownership { wasRunning = true } else { wasRunning = false }
        if wasRunning { await stopDSH() }
        await perform(stage: "正在重新安装 DSH \(selected.installedVersion)…") {
            var reinstalled = try await npm.install(
                scope: selected.scope,
                root: selected.scope == .managed ? URL(fileURLWithPath: selected.installRoot) : nil,
                version: selected.installedVersion
            )
            reinstalled.id = selected.id
            if let index = settings.installations.firstIndex(where: { $0.id == selected.id }) {
                settings.installations[index] = reinstalled
            }
            try await store.save(settings)
        }
        if wasRunning { await startDSH(openBrowser: false) }
    }

    func updateLauncher() async {
        await perform(stage: "正在检查 Launcher 更新…") {
            guard let release = try await launcherUpdates.latest(repository: settings.launcherUpdateRepository) else {
                presentedError = "当前已是最新的 macOS Launcher。"
                return
            }
            let package = try await launcherUpdates.downloadAndVerify(release)
            guard NSWorkspace.shared.open(package) else {
                throw LauncherUpdateError.invalidManifest("无法打开系统安装器")
            }
        }
    }

    func uninstallSelectedInstallation() async {
        guard let selected = settings.selectedInstallation else { return }
        if case .launcherOwned = ownership { await stopDSH() }
        await perform(stage: "正在卸载 DSH \(selected.installedVersion)…") {
            try await npm.uninstall(selected)
            settings.installations.removeAll { $0.id == selected.id }
            settings.selectedInstallationID = settings.installations.first?.id
            try await store.save(settings)
        }
    }

    func addHome(_ url: URL) {
        let canonical = canonicalHomeURL(url.path)
        if let existing = settings.homes.first(where: { $0.path == canonical.path }) {
            settings.selectedHomeID = existing.id
            return
        }
        let home = DSHHomeEntry(name: canonical.lastPathComponent, path: canonical.path)
        settings.homes.append(home)
        settings.selectedHomeID = home.id
    }

    @discardableResult
    func updateHome(id: UUID, name: String, path: String) -> Bool {
        guard let index = settings.homes.firstIndex(where: { $0.id == id }) else { return false }
        let canonical = canonicalHomeURL(path)
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: canonical.path, isDirectory: &isDirectory), isDirectory.boolValue else {
            presentedError = "DSH_HOME 必须是已经存在的目录。"
            return false
        }
        guard !settings.homes.contains(where: { $0.id != id && $0.path == canonical.path }) else {
            presentedError = "该 DSH_HOME 已经在列表中。"
            return false
        }
        let trimmedName = name.trimmingCharacters(in: .whitespacesAndNewlines)
        let pathChanged = settings.homes[index].path != canonical.path
        settings.homes[index].name = trimmedName.isEmpty ? canonical.lastPathComponent : trimmedName
        settings.homes[index].path = canonical.path
        if pathChanged {
            settings.homes[index].lastObservedWriterVersion = nil
            settings.homes[index].lastObservedWriteAt = nil
            settings.homes[index].observationReliable = false
        }
        settings.selectedHomeID = id
        return true
    }

    func removeSelectedHome() {
        guard settings.homes.count > 1, let id = settings.selectedHomeID else { return }
        settings.homes.removeAll { $0.id == id }
        settings.selectedHomeID = settings.homes[0].id
    }

    private func canonicalHomeURL(_ path: String) -> URL {
        let expanded = NSString(string: path.trimmingCharacters(in: .whitespacesAndNewlines)).expandingTildeInPath
        return URL(fileURLWithPath: expanded, isDirectory: true)
            .standardizedFileURL
            .resolvingSymlinksInPath()
    }

    private func restartRuntime() async throws -> RuntimeOwnership {
        guard let installation = settings.selectedInstallation, let home = settings.selectedHome else {
            throw NpmServiceError.invalidInstallation("请先选择 DSH 版本和 DSH_HOME。")
        }
        let value = try await runtime.restart(
            installation: installation,
            dshHome: URL(fileURLWithPath: home.path),
            workingDirectory: URL(fileURLWithPath: settings.workingDirectory),
            port: settings.port
        )
        ownership = value
        await events.start(port: settings.port)
        return value
    }

    private func perform(stage: String, operation: () async throws -> Void) async {
        guard !busy else { return }
        busy = true
        progress = OperationProgress(stage: stage, percentage: nil, detail: nil)
        defer { busy = false; progress = nil }
        do { try await operation() }
        catch {
            presentedError = error.localizedDescription
            await logger.error("\(stage) failed: \(error.localizedDescription)")
        }
    }
}
