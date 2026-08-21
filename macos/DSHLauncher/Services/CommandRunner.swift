import Foundation

struct CommandResult: Sendable {
    let exitCode: Int32
    let standardOutput: String
    let standardError: String

    var succeeded: Bool { exitCode == 0 }
    var combinedOutput: String {
        [standardOutput, standardError]
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .joined(separator: "\n")
    }
}

enum CommandRunnerError: LocalizedError {
    case timedOut(String)
    case launchFailed(String)

    var errorDescription: String? {
        switch self {
        case .timedOut(let command): return "命令执行超时：\(command)"
        case .launchFailed(let command): return "无法启动命令：\(command)"
        }
    }
}

private final class CommandOutputBuffer: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()

    func append(_ value: Data) {
        lock.lock()
        data.append(value)
        lock.unlock()
    }

    func string() -> String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: data, as: UTF8.self)
    }
}

actor CommandRunner {
    func run(
        executable: String,
        arguments: [String],
        workingDirectory: URL? = nil,
        environment: [String: String] = [:],
        timeout: Duration = .seconds(120)
    ) async throws -> CommandResult {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        process.currentDirectoryURL = workingDirectory ?? FileManager.default.homeDirectoryForCurrentUser
        var childEnvironment = ProcessInfo.processInfo.environment
        environment.forEach { childEnvironment[$0.key] = $0.value }
        process.environment = childEnvironment

        let outputPipe = Pipe()
        let errorPipe = Pipe()
        let output = CommandOutputBuffer()
        let errors = CommandOutputBuffer()
        outputPipe.fileHandleForReading.readabilityHandler = { handle in
            output.append(handle.availableData)
        }
        errorPipe.fileHandleForReading.readabilityHandler = { handle in
            errors.append(handle.availableData)
        }
        process.standardOutput = outputPipe
        process.standardError = errorPipe

        do {
            try process.run()
        } catch {
            throw CommandRunnerError.launchFailed(executable)
        }

        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while process.isRunning && clock.now < deadline {
            try Task.checkCancellation()
            try await Task.sleep(for: .milliseconds(100))
        }
        if process.isRunning {
            process.terminate()
            try? await Task.sleep(for: .milliseconds(500))
            if process.isRunning { process.interrupt() }
            throw CommandRunnerError.timedOut(URL(fileURLWithPath: executable).lastPathComponent)
        }
        process.waitUntilExit()
        outputPipe.fileHandleForReading.readabilityHandler = nil
        errorPipe.fileHandleForReading.readabilityHandler = nil
        output.append(outputPipe.fileHandleForReading.readDataToEndOfFile())
        errors.append(errorPipe.fileHandleForReading.readDataToEndOfFile())
        return CommandResult(
            exitCode: process.terminationStatus,
            standardOutput: output.string(),
            standardError: errors.string()
        )
    }
}
