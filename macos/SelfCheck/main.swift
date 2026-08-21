import DSHLauncherCore
import Foundation

do {
    try await CoreSelfCheck.run()
    print("SELF-CHECK PASSED")
} catch {
    fputs("SELF-CHECK FAILED: \(error.localizedDescription)\n", stderr)
    exit(1)
}
