import Foundation

struct NodeRuntime: Equatable, Sendable {
    let nodeExecutable: String
    let npmExecutable: String
    let nodeVersion: String

    var isCompatible: Bool {
        let values = nodeVersion.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
            .split(separator: ".").compactMap { Int($0) }
        guard let major = values.first else { return false }
        return major >= 24 || (major == 22 && values.dropFirst().first ?? 0 >= 19)
    }
}

actor NodeDiscoveryService {
    private let commands: CommandRunner

    init(commands: CommandRunner) {
        self.commands = commands
    }

    func discover() async -> [NodeRuntime] {
        var directories = systemCandidateDirectories()
        directories.append(contentsOf: versionManagerDirectories())
        var seen = Set<String>()
        var runtimes: [NodeRuntime] = []
        for directory in directories {
            let canonical = directory.standardizedFileURL.resolvingSymlinksInPath()
            guard seen.insert(canonical.path).inserted else { continue }
            let node = canonical.appendingPathComponent("node")
            let npm = canonical.appendingPathComponent("npm")
            guard FileManager.default.isExecutableFile(atPath: node.path),
                  FileManager.default.isExecutableFile(atPath: npm.path) else { continue }
            guard let version = try? await commands.run(
                executable: node.path,
                arguments: ["--version"],
                timeout: .seconds(10)
            ), version.succeeded else { continue }
            runtimes.append(NodeRuntime(
                nodeExecutable: node.path,
                npmExecutable: npm.path,
                nodeVersion: version.standardOutput.trimmingCharacters(in: .whitespacesAndNewlines)
            ))
        }
        return runtimes.sorted {
            if $0.isCompatible != $1.isCompatible { return $0.isCompatible }
            return $0.nodeVersion.localizedStandardCompare($1.nodeVersion) == .orderedDescending
        }
    }

    private func systemCandidateDirectories() -> [URL] {
        var values = (ProcessInfo.processInfo.environment["PATH"] ?? "")
            .split(separator: ":").map { URL(fileURLWithPath: String($0), isDirectory: true) }
        values.append(contentsOf: [
            URL(fileURLWithPath: "/opt/homebrew/bin", isDirectory: true),
            URL(fileURLWithPath: "/usr/local/bin", isDirectory: true),
            URL(fileURLWithPath: "/usr/local/opt/node@24/bin", isDirectory: true),
            FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".volta/bin", isDirectory: true)
        ])
        return values
    }

    private func versionManagerDirectories() -> [URL] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        var values: [URL] = []
        for root in [
            home.appendingPathComponent(".nvm/versions/node", isDirectory: true),
            home.appendingPathComponent(".fnm/node-versions", isDirectory: true)
        ] {
            guard let children = try? FileManager.default.contentsOfDirectory(
                at: root,
                includingPropertiesForKeys: nil,
                options: [.skipsHiddenFiles]
            ) else { continue }
            for child in children {
                values.append(child.appendingPathComponent("bin", isDirectory: true))
                values.append(child.appendingPathComponent("installation/bin", isDirectory: true))
            }
        }
        return values
    }
}
