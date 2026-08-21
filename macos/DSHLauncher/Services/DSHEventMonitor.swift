import Foundation

struct DSHNotification: Sendable {
    let title: String
    let message: String
    let isCompletion: Bool
}

actor DSHEventMonitor {
    typealias StatusHandler = @MainActor @Sendable (DSHStatusSnapshot) -> Void
    typealias NotificationHandler = @MainActor @Sendable (DSHNotification) -> Void

    private var monitorTask: Task<Void, Never>?
    private var runningSessions = Set<String>()
    private var runningJobs = Set<String>()
    private var pendingQuestions = Set<String>()
    private var pendingApprovals = Set<String>()
    private var notifiedAttention = Set<String>()
    private var titles: [String: String] = [:]
    private let logger: AppLogger
    private let statusHandler: StatusHandler
    private let notificationHandler: NotificationHandler

    init(logger: AppLogger, statusHandler: @escaping StatusHandler, notificationHandler: @escaping NotificationHandler) {
        self.logger = logger
        self.statusHandler = statusHandler
        self.notificationHandler = notificationHandler
    }

    func start(port: Int) {
        monitorTask?.cancel()
        monitorTask = Task { [weak self] in await self?.monitorLoop(port: port) }
    }

    func stop() {
        monitorTask?.cancel()
        monitorTask = nil
        clear()
        publish(ready: false)
    }

    private func monitorLoop(port: Int) async {
        var delay = 2.0
        while !Task.isCancelled {
            do {
                guard await loadBaseline(port: port) else {
                    clear(); publish(ready: false)
                    try await Task.sleep(for: .seconds(delay))
                    delay = min(15, delay * 1.6)
                    continue
                }
                delay = 2
                publish(ready: true)
                try await withThrowingTaskGroup(of: Void.self) { group in
                    group.addTask { try await self.receive(path: "/api/events.host", port: port) }
                    group.addTask { try await self.receive(path: "/api/events.mux", port: port) }
                    _ = try await group.next()
                    group.cancelAll()
                }
            } catch is CancellationError { return }
            catch { await logger.warning("DSH event connection failed: \(error.localizedDescription)") }
            try? await Task.sleep(for: .seconds(delay))
        }
    }

    private func receive(path: String, port: Int) async throws {
        var request = URLRequest(url: URL(string: "ws://127.0.0.1:\(port)\(path)")!)
        request.setValue("http://127.0.0.1:\(port)", forHTTPHeaderField: "Origin")
        let socket = URLSession.shared.webSocketTask(with: request)
        socket.resume()
        await logger.info("Connected to \(path)")
        defer { socket.cancel(with: .goingAway, reason: nil) }
        while !Task.isCancelled {
            let message = try await socket.receive()
            switch message {
            case .string(let text): processFrame(Data(text.utf8))
            case .data(let data): processFrame(data)
            @unknown default: break
            }
        }
    }

    private func processFrame(_ data: Data) {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let method = root["method"] as? String,
              let payload = root["payload"] as? [String: Any] else { return }
        let sessionID = payload["sessionId"] as? String ?? "unknown"
        switch method {
        case "host/session-status": set(&runningSessions, key: sessionID, enabled: payload["running"] as? Bool == true)
        case "question/requested":
            let key = "question:\(sessionID)"; pendingQuestions.insert(key)
            if notifiedAttention.insert(key).inserted {
                let questions = payload["questions"] as? [[String: Any]]
                let message = questions?.first?["header"] as? String ?? "打开 DSH 回答待处理问题。"
                sendNotification(DSHNotification(title: "DSH 需要你的回答", message: message, isCompletion: false))
            }
        case "question/resolved":
            pendingQuestions = pendingQuestions.filter { !$0.hasPrefix("question:\(sessionID)") }
            notifiedAttention.remove("question:\(sessionID)")
        case "approval/requested":
            let id = payload["approvalId"] as? String ?? sessionID; let key = "approval:\(id)"
            pendingApprovals.insert(key)
            if notifiedAttention.insert(key).inserted {
                sendNotification(DSHNotification(
                    title: "DSH 请求权限审批",
                    message: payload["toolName"] as? String ?? "打开 DSH 查看审批请求。",
                    isCompletion: false
                ))
            }
        case "approval/resolved":
            let id = payload["approvalId"] as? String ?? sessionID
            pendingApprovals.remove("approval:\(id)"); notifiedAttention.remove("approval:\(id)")
        case "session/jobs": processJobs(sessionID: sessionID, payload: payload)
        case "session/projection":
            if payload["key"] as? String == "title", let title = payload["value"] as? String { titles[sessionID] = title }
        case "session/event": processSessionEvent(sessionID: sessionID, payload: payload)
        default: break
        }
        publish(ready: true)
    }

    private func processJobs(sessionID: String, payload: [String: Any]) {
        runningJobs = runningJobs.filter { !$0.hasPrefix(sessionID + ":") }
        for job in payload["jobs"] as? [[String: Any]] ?? [] {
            if let status = job["status"] as? String, status == "running" || status == "stopping" {
                runningJobs.insert(sessionID + ":" + (job["id"] as? String ?? UUID().uuidString))
            }
        }
    }

    private func processSessionEvent(sessionID: String, payload: [String: Any]) {
        guard let event = payload["event"] as? [String: Any], let type = event["type"] as? String else { return }
        if type == "turn/start" { runningSessions.insert(sessionID) }
        if type == "turn/end" {
            runningSessions.remove(sessionID)
            let title = titles[sessionID] ?? "DSH 对话"
            Task {
                try? await Task.sleep(for: .seconds(3))
                await notificationHandler(DSHNotification(title: "对话完成", message: title, isCompletion: true))
            }
        }
    }

    private func loadBaseline(port: Int) async -> Bool {
        guard let url = URL(string: "http://127.0.0.1:\(port)/api/session.list") else { return false }
        var request = URLRequest(url: url); request.httpMethod = "POST"; request.timeoutInterval = 4
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONSerialization.data(withJSONObject: [
            "type": "client-request", "rpcId": UUID().uuidString, "method": "session.list", "payload": [:]
        ])
        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let result = root["result"] as? [String: Any], result["ok"] as? Bool == true else { return false }
            runningSessions.removeAll()
            if let value = result["value"] as? [String: Any], let items = value["items"] as? [[String: Any]] {
                for item in items {
                    guard let id = item["sessionId"] as? String else { continue }
                    if item["running"] as? Bool == true { runningSessions.insert(id) }
                }
            }
            return true
        } catch { return false }
    }

    private func publish(ready: Bool) {
        guard ready else {
            let handler = statusHandler
            Task { @MainActor in handler(.stopped) }
            return
        }
        let attention = pendingQuestions.count + pendingApprovals.count
        let busy = runningSessions.count + runningJobs.count
        let state: DSHActivityState = attention > 0 ? .attention : busy > 0 ? .busy : .idle
        let snapshot = DSHStatusSnapshot(
            state: state,
            runningSessions: runningSessions.count,
            runningJobs: runningJobs.count,
            pendingQuestions: pendingQuestions.count,
            pendingApprovals: pendingApprovals.count,
            summary: attention > 0 ? "DSH 等待处理" : busy > 0 ? "DSH 正在运行任务" : "DSH 已运行，当前空闲"
        )
        let handler = statusHandler
        Task { @MainActor in handler(snapshot) }
    }

    private func clear() {
        runningSessions.removeAll(); runningJobs.removeAll(); pendingQuestions.removeAll(); pendingApprovals.removeAll(); titles.removeAll()
    }

    private func set(_ set: inout Set<String>, key: String, enabled: Bool) {
        if enabled { set.insert(key) } else { set.remove(key) }
    }

    private func sendNotification(_ notification: DSHNotification) {
        let handler = notificationHandler
        Task { @MainActor in handler(notification) }
    }
}
