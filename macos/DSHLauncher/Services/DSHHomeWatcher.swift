import Foundation

actor DSHHomeWatcher {
    typealias ChangeHandler = @MainActor @Sendable (UUID, String, Date) -> Void
    private var task: Task<Void, Never>?
    private let changeHandler: ChangeHandler

    init(changeHandler: @escaping ChangeHandler) {
        self.changeHandler = changeHandler
    }

    func start(home: DSHHomeEntry, version: String) {
        task?.cancel()
        task = Task { [weak self] in await self?.watch(home: home, version: version) }
    }

    func stop() {
        task?.cancel()
        task = nil
    }

    private func watch(home: DSHHomeEntry, version: String) async {
        let root = URL(fileURLWithPath: home.path, isDirectory: true)
        var baseline = latestModification(root) ?? Date()
        while !Task.isCancelled {
            try? await Task.sleep(for: .seconds(2))
            guard let latest = latestModification(root), latest > baseline else { continue }
            baseline = latest
            await changeHandler(home.id, version, latest)
        }
    }

    private func latestModification(_ root: URL) -> Date? {
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.contentModificationDateKey, .isRegularFileKey],
            options: [.skipsHiddenFiles, .skipsPackageDescendants]
        ) else { return nil }
        var latest: Date?
        for case let url as URL in enumerator {
            if url.lastPathComponent == ".credentials.yaml" { continue }
            guard let values = try? url.resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey]),
                  values.isRegularFile == true, let date = values.contentModificationDate else { continue }
            if latest == nil || date > latest! { latest = date }
        }
        return latest
    }
}
