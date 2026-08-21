import Foundation
import Testing

#if canImport(DSHLauncherCore)
@testable import DSHLauncherCore
#else
@testable import DSHLauncher
#endif

@Test func defaultsUseSeparateMacPaths() {
    let home = URL(fileURLWithPath: "/Users/example", isDirectory: true)
    let settings = LauncherSettings.defaults(homeDirectory: home)
    #expect(settings.port == 3080)
    #expect(settings.workingDirectory == "/Users/example")
    #expect(settings.selectedHome?.path == "/Users/example/.dsh")
    #expect(settings.installations.isEmpty)
}

@Test func roundTripAndBackup() async throws {
    let fixture = try makeFixture()
    defer { try? FileManager.default.removeItem(at: fixture.root) }
    let store = SettingsStore(layout: fixture.layout)
    var first = LauncherSettings.defaults(homeDirectory: fixture.home)
    first.port = 4020
    try await store.save(first)
    var second = first
    second.port = 4021
    try await store.save(second)
    let loaded = await store.load()
    #expect(loaded.port == 4021)
    #expect(FileManager.default.fileExists(atPath: fixture.layout.settingsBackupFile.path))
    let backupData = try Data(contentsOf: fixture.layout.settingsBackupFile)
    let decoder = JSONDecoder()
    decoder.dateDecodingStrategy = .iso8601
    let backup = try decoder.decode(LauncherSettings.self, from: backupData)
    #expect(backup.port == 4020)
}

@Test func corruptSettingsAreQuarantined() async throws {
    let fixture = try makeFixture()
    defer { try? FileManager.default.removeItem(at: fixture.root) }
    try AppPaths.ensureCreated(fixture.layout)
    try Data("not-json".utf8).write(to: fixture.layout.settingsFile)
    let store = SettingsStore(layout: fixture.layout, now: { Date(timeIntervalSince1970: 1_700_000_000) })
    let loaded = await store.load()
    #expect(loaded.port == 3080)
    let corrupt = fixture.layout.dataRoot.appendingPathComponent("settings.corrupt.1700000000.json")
    #expect(FileManager.default.fileExists(atPath: corrupt.path))
    #expect(!FileManager.default.fileExists(atPath: fixture.layout.settingsFile.path))
}

@Test func pathBoundaryRejectsPrefixConfusion() {
    let root = URL(fileURLWithPath: "/tmp/DSHLauncher/runtimes")
    #expect(AppPaths.isDescendant(root.appendingPathComponent("0.2.0"), of: root))
    #expect(!AppPaths.isDescendant(URL(fileURLWithPath: "/tmp/DSHLauncher/runtimes-old"), of: root))
}

private func makeFixture() throws -> (root: URL, home: URL, layout: AppPathLayout) {
    let root = FileManager.default.temporaryDirectory.appendingPathComponent("DSHLauncherTests-\(UUID().uuidString)")
    let home = root.appendingPathComponent("home", isDirectory: true)
    try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
    return (root, home, AppPaths.isolated(root: root, home: home))
}
