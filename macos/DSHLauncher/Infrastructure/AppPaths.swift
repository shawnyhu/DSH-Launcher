import Foundation

struct AppPathLayout: Equatable, Sendable {
    let dataRoot: URL
    let settingsFile: URL
    let settingsBackupFile: URL
    let managedInstallRoot: URL
    let logDirectory: URL
    let logFile: URL
    let cacheRoot: URL
    let updateCacheRoot: URL
    let defaultDSHHome: URL
}

enum AppPaths {
    static func standard(fileManager: FileManager = .default) -> AppPathLayout {
        let home = fileManager.homeDirectoryForCurrentUser
        let applicationSupport = fileManager.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let logs = fileManager.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Logs", isDirectory: true)
        let caches = fileManager.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        let dataRoot = applicationSupport.appendingPathComponent("DSHLauncher", isDirectory: true)
        let cacheRoot = caches.appendingPathComponent("DSHLauncher", isDirectory: true)
        let logDirectory = logs.appendingPathComponent("DSHLauncher", isDirectory: true)
        return AppPathLayout(
            dataRoot: dataRoot,
            settingsFile: dataRoot.appendingPathComponent("settings.json"),
            settingsBackupFile: dataRoot.appendingPathComponent("settings.backup.json"),
            managedInstallRoot: dataRoot.appendingPathComponent("runtimes", isDirectory: true),
            logDirectory: logDirectory,
            logFile: logDirectory.appendingPathComponent("launcher.log"),
            cacheRoot: cacheRoot,
            updateCacheRoot: cacheRoot.appendingPathComponent("updates", isDirectory: true),
            defaultDSHHome: home.appendingPathComponent(".dsh", isDirectory: true)
        )
    }

    static func isolated(root: URL, home: URL) -> AppPathLayout {
        let dataRoot = root.appendingPathComponent("data", isDirectory: true)
        let logDirectory = root.appendingPathComponent("logs", isDirectory: true)
        let cacheRoot = root.appendingPathComponent("cache", isDirectory: true)
        return AppPathLayout(
            dataRoot: dataRoot,
            settingsFile: dataRoot.appendingPathComponent("settings.json"),
            settingsBackupFile: dataRoot.appendingPathComponent("settings.backup.json"),
            managedInstallRoot: dataRoot.appendingPathComponent("runtimes", isDirectory: true),
            logDirectory: logDirectory,
            logFile: logDirectory.appendingPathComponent("launcher.log"),
            cacheRoot: cacheRoot,
            updateCacheRoot: cacheRoot.appendingPathComponent("updates", isDirectory: true),
            defaultDSHHome: home.appendingPathComponent(".dsh", isDirectory: true)
        )
    }

    static func ensureCreated(_ layout: AppPathLayout, fileManager: FileManager = .default) throws {
        for directory in [layout.dataRoot, layout.managedInstallRoot, layout.logDirectory, layout.cacheRoot, layout.updateCacheRoot] {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }

    static func canonicalizedUserPath(_ value: String, home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        let expanded: String
        if value == "~" {
            expanded = home.path
        } else if value.hasPrefix("~/") {
            expanded = home.appendingPathComponent(String(value.dropFirst(2))).path
        } else {
            expanded = value
        }
        return URL(fileURLWithPath: expanded).standardizedFileURL.resolvingSymlinksInPath()
    }

    static func isDescendant(_ candidate: URL, of root: URL) -> Bool {
        let child = candidate.standardizedFileURL.resolvingSymlinksInPath().path
        let parent = root.standardizedFileURL.resolvingSymlinksInPath().path
        return child == parent || child.hasPrefix(parent.hasSuffix("/") ? parent : parent + "/")
    }
}
