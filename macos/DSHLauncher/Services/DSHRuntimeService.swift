import Darwin
import Foundation

enum RuntimeOwnership: Equatable, Sendable {
    case stopped
    case launcherOwned(pid: Int32)
    case external
}

enum RuntimeError: LocalizedError {
    case alreadyRunning
    case portInUse(Int)
    case missingExecutable(String)
    case exited(Int32, String)
    case readinessTimeout

    var errorDescription: String? {
        switch self {
        case .alreadyRunning: return "DSH 已由 Launcher 管理并正在运行。"
        case .portInUse(let port): return "端口 \(port) 已被其他程序占用。"
        case .missingExecutable(let path): return "找不到需要的可执行文件：\(path)"
        case .exited(let code, let detail): return "DSH 启动期间退出（code \(code)）。\(detail)"
        case .readinessTimeout: return "DSH Web UI 在 60 秒内未就绪。"
        }
    }
}

private final class RuntimeOutput: @unchecked Sendable {
    private let lock = NSLock()
    private var lines: [String] = []

    func append(_ data: Data) {
        let text = String(decoding: data, as: UTF8.self)
        lock.lock()
        lines.append(contentsOf: text.split(whereSeparator: \ .isNewline).map(String.init))
        if lines.count > 80 { lines.removeFirst(lines.count - 80) }
        lock.unlock()
    }

    func lastUsefulLine() -> String {
        lock.lock()
        defer { lock.unlock() }
        return lines.last(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty }) ?? ""
    }
}

actor DSHRuntimeService {
    private let logger: AppLogger
    private var process: Process?
    private var processGroup: pid_t?
    private var outputPipe: Pipe?
    private var errorPipe: Pipe?

    init(logger: AppLogger) {
        self.logger = logger
    }

    var ownership: RuntimeOwnership {
        if let process, process.isRunning { return .launcherOwned(pid: process.processIdentifier) }
        return .stopped
    }

    func start(
        installation: DSHInstallation,
        dshHome: URL,
        workingDirectory: URL,
        port: Int
    ) async throws -> RuntimeOwnership {
        if case .launcherOwned = ownership { throw RuntimeError.alreadyRunning }
        if await isReady(port: port) { return .external }
        if PortService.isListening(port) { throw RuntimeError.portInUse(port) }
        guard FileManager.default.isExecutableFile(atPath: installation.nodeExecutable) else {
            throw RuntimeError.missingExecutable(installation.nodeExecutable)
        }
        let entry = URL(fileURLWithPath: installation.packageRoot).appendingPathComponent("lib/bin.js")
        guard FileManager.default.fileExists(atPath: entry.path) else {
            throw RuntimeError.missingExecutable(entry.path)
        }

        let child = Process()
        child.executableURL = URL(fileURLWithPath: installation.nodeExecutable)
        child.arguments = [entry.path, "web", "--host", "127.0.0.1", "--port", String(port), "--no-open"]
        child.currentDirectoryURL = FileManager.default.fileExists(atPath: workingDirectory.path)
            ? workingDirectory : FileManager.default.homeDirectoryForCurrentUser
        var environment = ProcessInfo.processInfo.environment
        let nodeDirectory = URL(fileURLWithPath: installation.nodeExecutable).deletingLastPathComponent().path
        environment["PATH"] = nodeDirectory + ":/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"
        environment["DSH_HOME"] = dshHome.standardizedFileURL.path
        child.environment = environment

        let stdout = Pipe()
        let stderr = Pipe()
        let recentErrors = RuntimeOutput()
        stdout.fileHandleForReading.readabilityHandler = { [logger] handle in
            let data = handle.availableData
            guard !data.isEmpty else { return }
            Task { await logger.info("DSH: " + String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)) }
        }
        stderr.fileHandleForReading.readabilityHandler = { [logger] handle in
            let data = handle.availableData
            guard !data.isEmpty else { return }
            recentErrors.append(data)
            Task { await logger.warning("DSH: " + String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)) }
        }
        child.standardOutput = stdout
        child.standardError = stderr
        try child.run()
        let pid = child.processIdentifier
        if setpgid(pid, pid) == 0 { processGroup = pid }
        process = child
        outputPipe = stdout
        errorPipe = stderr
        await logger.info("Started DSH \(installation.installedVersion); PID=\(pid); port=\(port)")

        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(60))
        while clock.now < deadline {
            if !child.isRunning {
                child.waitUntilExit()
                cleanup()
                throw RuntimeError.exited(child.terminationStatus, recentErrors.lastUsefulLine())
            }
            if await isReady(port: port) { return .launcherOwned(pid: pid) }
            try await Task.sleep(for: .milliseconds(500))
        }
        await stop()
        throw RuntimeError.readinessTimeout
    }

    func stop() async {
        guard let process, process.isRunning else { cleanup(); return }
        let pid = process.processIdentifier
        await logger.info("Stopping Launcher-owned DSH process group \(pid)")
        if let processGroup { _ = Darwin.kill(-processGroup, SIGTERM) }
        else { process.terminate() }
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(5))
        while process.isRunning && clock.now < deadline {
            try? await Task.sleep(for: .milliseconds(100))
        }
        if process.isRunning {
            if let processGroup { _ = Darwin.kill(-processGroup, SIGKILL) }
            else { process.interrupt() }
        }
        process.waitUntilExit()
        cleanup()
    }

    func restart(installation: DSHInstallation, dshHome: URL, workingDirectory: URL, port: Int) async throws -> RuntimeOwnership {
        await stop()
        return try await start(installation: installation, dshHome: dshHome, workingDirectory: workingDirectory, port: port)
    }

    func isReady(port: Int) async -> Bool {
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/session.list") else { return false }
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 2
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "type": "client-request", "rpcId": UUID().uuidString,
            "method": "session.list", "payload": [:]
        ])
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let result = root["result"] as? [String: Any], result["ok"] as? Bool == true else { return false }
            return true
        } catch { return false }
    }

    private func cleanup() {
        outputPipe?.fileHandleForReading.readabilityHandler = nil
        errorPipe?.fileHandleForReading.readabilityHandler = nil
        process = nil
        processGroup = nil
        outputPipe = nil
        errorPipe = nil
    }
}
