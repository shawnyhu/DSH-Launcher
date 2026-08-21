import Foundation
import Testing

#if canImport(DSHLauncherCore)
@testable import DSHLauncherCore
#else
@testable import DSHLauncher
#endif

@Test func nodeCompatibility() {
    #expect(runtime("v22.19.0").isCompatible)
    #expect(!runtime("v22.18.0").isCompatible)
    #expect(runtime("v24.0.0").isCompatible)
    #expect(runtime("v25.7.0").isCompatible)
    #expect(!runtime("not-a-version").isCompatible)
}

@Test func normalizationRepairsInvalidSelections() {
    var settings = LauncherSettings.defaults(homeDirectory: URL(fileURLWithPath: "/Users/test"))
    settings.selectedHomeID = UUID()
    settings.port = 99_999
    let normalized = settings.normalized(homeDirectory: URL(fileURLWithPath: "/Users/test"))
    #expect(normalized.selectedHomeID == normalized.homes[0].id)
    #expect(normalized.port == 65_535)
}

private func runtime(_ version: String) -> NodeRuntime {
    NodeRuntime(nodeExecutable: "/node", npmExecutable: "/npm", nodeVersion: version)
}
