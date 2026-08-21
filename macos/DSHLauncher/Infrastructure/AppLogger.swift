import Foundation

actor AppLogger {
    private let fileURL: URL

    init(fileURL: URL) {
        self.fileURL = fileURL
    }

    func info(_ message: String) { write(level: "INFO", message: message) }
    func warning(_ message: String) { write(level: "WARN", message: message) }
    func error(_ message: String) { write(level: "ERROR", message: message) }

    private func write(level: String, message: String) {
        let sanitized = message.replacingOccurrences(of: "\n", with: " ")
        let formatter = ISO8601DateFormatter()
        let line = "\(formatter.string(from: Date())) [\(level)] \(sanitized)\n"
        do {
            try FileManager.default.createDirectory(
                at: fileURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            if !FileManager.default.fileExists(atPath: fileURL.path) {
                FileManager.default.createFile(atPath: fileURL.path, contents: nil)
            }
            let handle = try FileHandle(forWritingTo: fileURL)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(line.utf8))
            try handle.synchronize()
        } catch {
            fputs("DSH Launcher log error: \(error)\n", stderr)
        }
    }
}
