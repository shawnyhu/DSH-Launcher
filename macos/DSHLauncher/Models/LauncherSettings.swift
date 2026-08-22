import Foundation

enum DSHInstallScope: String, Codable, CaseIterable, Sendable {
    case global
    case managed
}

struct DSHInstallation: Codable, Identifiable, Equatable, Sendable {
    var id = UUID()
    var name: String
    var scope: DSHInstallScope
    var installRoot: String
    var packageRoot: String
    var nodeExecutable: String
    var npmExecutable: String
    var installedVersion: String
    var lastVerifiedAt: Date?

    var displayName: String {
        let kind = scope == .global ? "全局" : "独立"
        return "\(name) \(installedVersion)（\(kind)）"
    }
}

struct DSHHomeEntry: Codable, Identifiable, Equatable, Sendable {
    var id = UUID()
    var name: String
    var path: String
    var lastObservedWriterVersion: String?
    var lastObservedWriteAt: Date?
    var observationReliable = false
}

struct LauncherSettings: Codable, Equatable, Sendable {
    static let currentSchemaVersion = 1

    var schemaVersion = currentSchemaVersion
    var installations: [DSHInstallation] = []
    var homes: [DSHHomeEntry]
    var selectedInstallationID: UUID?
    var selectedHomeID: UUID?
    var port = 3080
    var workingDirectory: String
    var startDSHWithLauncher = true
    var openBrowserAfterStart = true
    var startAtLogin = false
    var notifyOnCompletion = true
    var launcherUpdateRepository = "shawnyhu/DSH-Launcher"

    static func defaults(homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser) -> Self {
        let home = DSHHomeEntry(name: "默认数据", path: homeDirectory.appendingPathComponent(".dsh").path)
        return LauncherSettings(
            homes: [home],
            selectedHomeID: home.id,
            workingDirectory: homeDirectory.path
        )
    }

    var selectedInstallation: DSHInstallation? {
        installations.first { $0.id == selectedInstallationID }
    }

    var selectedHome: DSHHomeEntry? {
        homes.first { $0.id == selectedHomeID }
    }

    mutating func selectInstallation(_ id: UUID?) {
        guard let id, installations.contains(where: { $0.id == id }) else { return }
        selectedInstallationID = id
    }

    mutating func selectHome(_ id: UUID?) {
        guard let id, homes.contains(where: { $0.id == id }) else { return }
        selectedHomeID = id
    }

    func normalized(homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser) -> Self {
        var value = self
        if value.homes.isEmpty {
            let home = DSHHomeEntry(name: "默认数据", path: homeDirectory.appendingPathComponent(".dsh").path)
            value.homes = [home]
            value.selectedHomeID = home.id
        } else if value.selectedHome == nil {
            value.selectedHomeID = value.homes[0].id
        }
        if value.selectedInstallation == nil {
            value.selectedInstallationID = value.installations.first?.id
        }
        value.port = min(max(value.port, 1), 65_535)
        if value.workingDirectory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            value.workingDirectory = homeDirectory.path
        }
        return value
    }
}

enum DSHActivityState: String, Sendable {
    case stopped
    case idle
    case busy
    case attention
    case incompatible
}

struct DSHStatusSnapshot: Equatable, Sendable {
    var state: DSHActivityState
    var runningSessions: Int
    var runningJobs: Int
    var pendingQuestions: Int
    var pendingApprovals: Int
    var summary: String

    static let stopped = DSHStatusSnapshot(
        state: .stopped,
        runningSessions: 0,
        runningJobs: 0,
        pendingQuestions: 0,
        pendingApprovals: 0,
        summary: "DSH 未运行"
    )
}

struct OperationProgress: Equatable, Sendable {
    var stage: String
    var percentage: Int?
    var detail: String?
}
