import Foundation

struct NpmVersionInfo: Codable, Identifiable, Equatable, Sendable {
    var id: String { version }
    let version: String
    let publishedAt: Date?
    let isLatest: Bool
}

enum NpmServiceError: LocalizedError {
    case noCompatibleRuntime
    case commandFailed(String)
    case unsafeManagedDirectory(String)
    case invalidInstallation(String)

    var errorDescription: String? {
        switch self {
        case .noCompatibleRuntime: return "未检测到兼容的 Node.js。请安装 Node.js 24 LTS。"
        case .commandFailed(let message): return message
        case .unsafeManagedDirectory(let path): return "拒绝操作不安全的托管目录：\(path)"
        case .invalidInstallation(let message): return message
        }
    }
}

actor NpmService {
    static let packageName = "@deepseek-ai/dsh"
    static let ownershipFile = ".dsh-launcher-instance.json"

    private let commands: CommandRunner
    private let nodeDiscovery: NodeDiscoveryService
    private let layout: AppPathLayout

    init(commands: CommandRunner, nodeDiscovery: NodeDiscoveryService, layout: AppPathLayout) {
        self.commands = commands
        self.nodeDiscovery = nodeDiscovery
        self.layout = layout
    }

    func preferredRuntime() async throws -> NodeRuntime {
        guard let runtime = await nodeDiscovery.discover().first(where: \ .isCompatible) else {
            throw NpmServiceError.noCompatibleRuntime
        }
        return runtime
    }

    func discoverGlobal() async throws -> DSHInstallation? {
        let runtime = try await preferredRuntime()
        let environment = pathEnvironment(runtime)
        let root = try await requireSuccess(runtime.npmExecutable, ["root", "--global"], environment: environment)
        let packageRoot = URL(fileURLWithPath: root).appendingPathComponent("@deepseek-ai/dsh")
        guard FileManager.default.fileExists(atPath: packageRoot.path) else { return nil }
        let prefix = try await requireSuccess(runtime.npmExecutable, ["prefix", "--global"], environment: environment)
        let version = try installedVersion(packageRoot: packageRoot)
        return DSHInstallation(
            name: "全局 DSH",
            scope: .global,
            installRoot: prefix,
            packageRoot: packageRoot.path,
            nodeExecutable: runtime.nodeExecutable,
            npmExecutable: runtime.npmExecutable,
            installedVersion: version,
            lastVerifiedAt: Date()
        )
    }

    func versions() async throws -> [NpmVersionInfo] {
        let runtime = try await preferredRuntime()
        let environment = pathEnvironment(runtime)
        async let versionText = requireSuccess(runtime.npmExecutable, ["view", Self.packageName, "versions", "--json"], environment: environment)
        async let tagsText = requireSuccess(runtime.npmExecutable, ["view", Self.packageName, "dist-tags", "--json"], environment: environment)
        async let timeText = requireSuccess(runtime.npmExecutable, ["view", Self.packageName, "time", "--json"], environment: environment)
        let (versionsJSON, tagsJSON, timeJSON) = try await (versionText, tagsText, timeText)
        let decoder = JSONDecoder()
        let versions = try decoder.decode([String].self, from: Data(versionsJSON.utf8))
        let tags = try decoder.decode([String: String].self, from: Data(tagsJSON.utf8))
        let times = try decoder.decode([String: String].self, from: Data(timeJSON.utf8))
        let formatter = ISO8601DateFormatter()
        return versions.map { version in
            NpmVersionInfo(
                version: version,
                publishedAt: times[version].flatMap(formatter.date(from:)),
                isLatest: tags["latest"] == version
            )
        }.sorted {
            ($0.publishedAt ?? .distantPast) > ($1.publishedAt ?? .distantPast)
        }
    }

    func install(scope: DSHInstallScope, root requestedRoot: URL?, version: String) async throws -> DSHInstallation {
        let runtime = try await preferredRuntime()
        let environment = pathEnvironment(runtime)
        let spec = "\(Self.packageName)@\(version)"
        if scope == .global {
            _ = try await requireSuccess(
                runtime.npmExecutable,
                ["install", "--global", spec, "--install-strategy=shallow", "--no-audit", "--no-fund"],
                environment: environment,
                timeout: .seconds(3_600)
            )
            guard let installation = try await discoverGlobal() else {
                throw NpmServiceError.invalidInstallation("安装完成，但无法定位全局 DSH。")
            }
            return installation
        }

        let root = (requestedRoot ?? layout.managedInstallRoot.appendingPathComponent(version, isDirectory: true))
            .standardizedFileURL.resolvingSymlinksInPath()
        try validateManagedRoot(root)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let marker = ["owner": "DSH Launcher", "version": version, "createdAt": ISO8601DateFormatter().string(from: Date())]
        let markerData = try JSONSerialization.data(withJSONObject: marker, options: [.prettyPrinted, .sortedKeys])
        try markerData.write(to: root.appendingPathComponent(Self.ownershipFile), options: .atomic)
        _ = try await requireSuccess(
            runtime.npmExecutable,
            [
                "install", "--prefix", root.path, "--no-package-lock", "--no-save",
                "--install-strategy=shallow", spec, "--no-audit", "--no-fund"
            ],
            environment: environment,
            timeout: .seconds(3_600)
        )
        let packageRoot = root.appendingPathComponent("node_modules/@deepseek-ai/dsh")
        let actual = try installedVersion(packageRoot: packageRoot)
        guard actual == version, FileManager.default.fileExists(atPath: entryScript(packageRoot: packageRoot).path) else {
            throw NpmServiceError.invalidInstallation("安装验证失败：目标版本或 DSH 入口不匹配。")
        }
        return DSHInstallation(
            name: "DSH \(actual)",
            scope: .managed,
            installRoot: root.path,
            packageRoot: packageRoot.path,
            nodeExecutable: runtime.nodeExecutable,
            npmExecutable: runtime.npmExecutable,
            installedVersion: actual,
            lastVerifiedAt: Date()
        )
    }

    func uninstall(_ installation: DSHInstallation) async throws {
        let environment = pathEnvironment(NodeRuntime(
            nodeExecutable: installation.nodeExecutable,
            npmExecutable: installation.npmExecutable,
            nodeVersion: "v24"
        ))
        if installation.scope == .global {
            _ = try await requireSuccess(
                installation.npmExecutable,
                ["uninstall", "--global", Self.packageName, "--no-audit", "--no-fund"],
                environment: environment,
                timeout: .seconds(300)
            )
            return
        }
        let root = URL(fileURLWithPath: installation.installRoot).standardizedFileURL.resolvingSymlinksInPath()
        guard AppPaths.isDescendant(root, of: layout.managedInstallRoot),
              FileManager.default.fileExists(atPath: root.appendingPathComponent(Self.ownershipFile).path) else {
            throw NpmServiceError.unsafeManagedDirectory(root.path)
        }
        try FileManager.default.removeItem(at: root)
    }

    func entryScript(for installation: DSHInstallation) -> URL {
        entryScript(packageRoot: URL(fileURLWithPath: installation.packageRoot))
    }

    private func entryScript(packageRoot: URL) -> URL {
        packageRoot.appendingPathComponent("lib/bin.js")
    }

    private func installedVersion(packageRoot: URL) throws -> String {
        let data = try Data(contentsOf: packageRoot.appendingPathComponent("package.json"))
        let object = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        guard let version = object?["version"] as? String else {
            throw NpmServiceError.invalidInstallation("无法读取 DSH package.json 版本。")
        }
        return version
    }

    private func validateManagedRoot(_ root: URL) throws {
        guard AppPaths.isDescendant(root, of: layout.managedInstallRoot), root != layout.managedInstallRoot else {
            throw NpmServiceError.unsafeManagedDirectory(root.path)
        }
        guard FileManager.default.fileExists(atPath: root.path) else { return }
        let entries = try FileManager.default.contentsOfDirectory(atPath: root.path)
        if !entries.isEmpty && !entries.contains(Self.ownershipFile) {
            throw NpmServiceError.unsafeManagedDirectory(root.path)
        }
    }

    private func pathEnvironment(_ runtime: NodeRuntime) -> [String: String] {
        let nodeDirectory = URL(fileURLWithPath: runtime.nodeExecutable).deletingLastPathComponent().path
        let safe = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        let npmCache = layout.cacheRoot.appendingPathComponent("npm", isDirectory: true)
        try? FileManager.default.createDirectory(at: npmCache, withIntermediateDirectories: true)
        return [
            "PATH": nodeDirectory + ":" + safe,
            "npm_config_cache": npmCache.path,
            "npm_config_fetch_retries": "5",
            "npm_config_fetch_retry_mintimeout": "1000",
            "npm_config_fetch_retry_maxtimeout": "15000",
            "npm_config_maxsockets": "5",
            "npm_config_prefer_offline": "true"
        ]
    }

    private func requireSuccess(
        _ executable: String,
        _ arguments: [String],
        environment: [String: String],
        timeout: Duration = .seconds(120)
    ) async throws -> String {
        let result = try await commands.run(
            executable: executable,
            arguments: arguments,
            environment: environment,
            timeout: timeout
        )
        guard result.succeeded else {
            throw NpmServiceError.commandFailed(result.combinedOutput)
        }
        return result.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
