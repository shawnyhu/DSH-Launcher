import Foundation

public enum CoreSelfCheck {
    public static func run() async throws {
        let home = URL(fileURLWithPath: "/Users/example", isDirectory: true)
        let defaults = LauncherSettings.defaults(homeDirectory: home)
        try check(defaults.port == 3080, "default port")
        try check(defaults.selectedHome?.path == "/Users/example/.dsh", "default DSH_HOME")

        var invalid = defaults
        invalid.port = 99_999
        invalid.selectedHomeID = UUID()
        let normalized = invalid.normalized(homeDirectory: home)
        try check(normalized.port == 65_535, "port normalization")
        try check(normalized.selectedHomeID == normalized.homes[0].id, "selection normalization")

        var persistentSelection = defaults
        let firstInstallation = DSHInstallation(
            name: "DSH A", scope: .managed, installRoot: "/tmp/dsh-a", packageRoot: "/tmp/dsh-a/package",
            nodeExecutable: "/tmp/node", npmExecutable: "/tmp/npm", installedVersion: "1.0.0"
        )
        let secondInstallation = DSHInstallation(
            name: "DSH B", scope: .global, installRoot: "/tmp/dsh-b", packageRoot: "/tmp/dsh-b/package",
            nodeExecutable: "/tmp/node", npmExecutable: "/tmp/npm", installedVersion: "2.0.0"
        )
        let secondHome = DSHHomeEntry(name: "其他数据", path: "/Users/example/.dsh-other")
        persistentSelection.installations = [firstInstallation, secondInstallation]
        persistentSelection.selectedInstallationID = firstInstallation.id
        persistentSelection.homes.append(secondHome)
        persistentSelection.selectInstallation(nil)
        persistentSelection.selectHome(nil)
        try check(persistentSelection.selectedInstallationID == firstInstallation.id, "persistent package selection")
        try check(persistentSelection.selectedHomeID == defaults.selectedHomeID, "persistent DSH_HOME selection")
        persistentSelection.selectInstallation(secondInstallation.id)
        persistentSelection.selectHome(secondHome.id)
        try check(persistentSelection.selectedInstallationID == secondInstallation.id, "explicit package switch")
        try check(persistentSelection.selectedHomeID == secondHome.id, "explicit DSH_HOME switch")

        let runtime22 = NodeRuntime(nodeExecutable: "/node", npmExecutable: "/npm", nodeVersion: "v22.19.0")
        let runtime24 = NodeRuntime(nodeExecutable: "/node", npmExecutable: "/npm", nodeVersion: "v24.0.0")
        let runtimeOld = NodeRuntime(nodeExecutable: "/node", npmExecutable: "/npm", nodeVersion: "v22.18.0")
        try check(runtime22.isCompatible && runtime24.isCompatible && !runtimeOld.isCompatible, "Node engines")

        let root = URL(fileURLWithPath: "/tmp/DSHLauncher/runtimes")
        try check(AppPaths.isDescendant(root.appendingPathComponent("0.2.0"), of: root), "safe descendant")
        try check(!AppPaths.isDescendant(URL(fileURLWithPath: "/tmp/DSHLauncher/runtimes-old"), of: root), "prefix confusion")
        try check(LauncherUpdateService.version(fromTag: "mac-v0.2.0") == "0.2.0", "Mac release tag")
        try check(LauncherUpdateService.version(fromTag: "win-v0.2.0") == nil, "Windows tag rejection")

        let fixtureRoot = FileManager.default.temporaryDirectory.appendingPathComponent("DSHLauncherSelfCheck-\(UUID().uuidString)")
        let fixtureHome = fixtureRoot.appendingPathComponent("home", isDirectory: true)
        try FileManager.default.createDirectory(at: fixtureHome, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixtureRoot) }
        let layout = AppPaths.isolated(root: fixtureRoot, home: fixtureHome)
        let store = SettingsStore(layout: layout, now: { Date(timeIntervalSince1970: 1_700_000_000) })
        var first = LauncherSettings.defaults(homeDirectory: fixtureHome)
        first.port = 4010
        try await store.save(first)
        var second = first
        second.port = 4011
        try await store.save(second)
        let roundTrip = await store.load()
        try check(roundTrip.port == 4011, "settings round trip")
        try check(FileManager.default.fileExists(atPath: layout.settingsBackupFile.path), "settings backup")

        try Data("invalid".utf8).write(to: layout.settingsFile)
        let recovered = await store.load()
        try check(recovered.port == 3080, "corrupt fallback")
        try check(FileManager.default.fileExists(atPath: layout.dataRoot.appendingPathComponent("settings.corrupt.1700000000.json").path), "corrupt quarantine")

        if ProcessInfo.processInfo.environment["DSH_LAUNCHER_LIVE_CHECK"] == "1" {
            let commands = CommandRunner()
            let discovery = NodeDiscoveryService(commands: commands)
            let runtimes = await discovery.discover()
            try check(runtimes.contains(where: \ .isCompatible), "live Node discovery")
            let npm = NpmService(commands: commands, nodeDiscovery: discovery, layout: layout)
            let versions = try await npm.versions()
            try check(!versions.isEmpty && versions.contains(where: \ .isLatest), "live npm registry")
        }
    }

    private static func check(_ condition: @autoclosure () -> Bool, _ name: String) throws {
        guard condition() else { throw SelfCheckError.failed(name) }
        print("[OK] \(name)")
    }
}

private enum SelfCheckError: LocalizedError {
    case failed(String)
    var errorDescription: String? {
        if case .failed(let name) = self { return "Self-check failed: \(name)" }
        return nil
    }
}
