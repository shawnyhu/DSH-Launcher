import Darwin
import Foundation

enum SettingsStoreError: LocalizedError {
    case unsupportedSchema(Int)
    case atomicReplaceFailed(Int32)

    var errorDescription: String? {
        switch self {
        case .unsupportedSchema(let version):
            return "不支持设置文件版本 \(version)。"
        case .atomicReplaceFailed(let code):
            return "无法原子替换设置文件（errno \(code)）。"
        }
    }
}

actor SettingsStore {
    private let layout: AppPathLayout
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private let now: @Sendable () -> Date

    init(layout: AppPathLayout, now: @escaping @Sendable () -> Date = Date.init) {
        self.layout = layout
        self.now = now
        encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
    }

    func load() async -> LauncherSettings {
        do {
            try AppPaths.ensureCreated(layout)
            guard FileManager.default.fileExists(atPath: layout.settingsFile.path) else {
                return LauncherSettings.defaults().normalized()
            }
            let data = try Data(contentsOf: layout.settingsFile)
            let value = try decoder.decode(LauncherSettings.self, from: data)
            guard value.schemaVersion == LauncherSettings.currentSchemaVersion else {
                throw SettingsStoreError.unsupportedSchema(value.schemaVersion)
            }
            return value.normalized()
        } catch {
            backupCorruptSettings()
            return LauncherSettings.defaults().normalized()
        }
    }

    func save(_ settings: LauncherSettings) throws {
        try AppPaths.ensureCreated(layout)
        let value = settings.normalized()
        let data = try encoder.encode(value)
        let temporary = layout.dataRoot.appendingPathComponent(".settings.\(UUID().uuidString).tmp")
        try writeAndSync(data, to: temporary)

        do {
            if FileManager.default.fileExists(atPath: layout.settingsFile.path) {
                let existing = try Data(contentsOf: layout.settingsFile)
                let backupTemporary = layout.dataRoot.appendingPathComponent(".settings-backup.\(UUID().uuidString).tmp")
                try writeAndSync(existing, to: backupTemporary)
                try atomicRename(backupTemporary, layout.settingsBackupFile)
            }
            try atomicRename(temporary, layout.settingsFile)
            syncDirectory(layout.dataRoot)
        } catch {
            try? FileManager.default.removeItem(at: temporary)
            throw error
        }
    }

    private func writeAndSync(_ data: Data, to url: URL) throws {
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try FileHandle(forWritingTo: url)
        do {
            try handle.write(contentsOf: data)
            try handle.synchronize()
            try handle.close()
        } catch {
            try? handle.close()
            throw error
        }
    }

    private func atomicRename(_ source: URL, _ destination: URL) throws {
        if Darwin.rename(source.path, destination.path) != 0 {
            throw SettingsStoreError.atomicReplaceFailed(errno)
        }
    }

    private func syncDirectory(_ directory: URL) {
        let descriptor = Darwin.open(directory.path, O_RDONLY)
        guard descriptor >= 0 else { return }
        _ = Darwin.fsync(descriptor)
        Darwin.close(descriptor)
    }

    private func backupCorruptSettings() {
        guard FileManager.default.fileExists(atPath: layout.settingsFile.path) else { return }
        let timestamp = Int(now().timeIntervalSince1970)
        let destination = layout.dataRoot.appendingPathComponent("settings.corrupt.\(timestamp).json")
        try? FileManager.default.moveItem(at: layout.settingsFile, to: destination)
    }
}
