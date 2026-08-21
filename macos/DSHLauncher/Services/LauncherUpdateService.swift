import CryptoKit
import Foundation

struct LauncherUpdateManifest: Codable, Equatable, Sendable {
    let schemaVersion: Int
    let keyId: String
    let platform: String
    let architecture: String
    let tag: String
    let version: String
    let assetName: String
    let assetSize: Int
    let sha256: String
    let developerTeamId: String
    let publishedAt: String
}

struct LauncherRelease: Sendable {
    let tag: String
    let version: String
    let pageURL: URL
    let packageURL: URL
    let manifestURL: URL
    let signatureURL: URL
}

enum LauncherUpdateError: LocalizedError {
    case invalidRepository
    case noRelease
    case invalidManifest(String)
    case invalidSignature
    case hashMismatch

    var errorDescription: String? {
        switch self {
        case .invalidRepository: return "GitHub 仓库必须使用 owner/repository 格式。"
        case .noRelease: return "没有找到适用于当前 Mac 的 Launcher Release。"
        case .invalidManifest(let reason): return "更新清单无效：\(reason)"
        case .invalidSignature: return "更新清单签名验证失败。"
        case .hashMismatch: return "更新包 SHA-256 校验失败。"
        }
    }
}

actor LauncherUpdateService {
    private let layout: AppPathLayout
    private let currentVersion: String
    private let publicKeys: [String: Data]

    init(layout: AppPathLayout, currentVersion: String, bundle: Bundle = .main) {
        self.layout = layout
        self.currentVersion = currentVersion
        if let url = bundle.url(forResource: "UpdatePublicKeys", withExtension: "json"),
           let data = try? Data(contentsOf: url),
           let values = try? JSONDecoder().decode([String: String].self, from: data) {
            publicKeys = values.compactMapValues { Data(base64Encoded: $0) }
        } else { publicKeys = [:] }
    }

    func latest(repository: String) async throws -> LauncherRelease? {
        let slug = try normalize(repository)
        let url = URL(string: "https://api.github.com/repos/\(slug)/releases?per_page=50")!
        var request = URLRequest(url: url)
        request.setValue("DSHLauncher/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw LauncherUpdateError.noRelease }
        guard let releases = try JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { throw LauncherUpdateError.noRelease }
        let arch = ProcessInfo.processInfo.machineArchitecture
        var candidates: [(version: [Int], release: LauncherRelease)] = []
        for release in releases where release["draft"] as? Bool != true {
            guard let tag = release["tag_name"] as? String,
                  let version = Self.version(fromTag: tag),
                  let page = release["html_url"] as? String,
                  let assets = release["assets"] as? [[String: Any]] else { continue }
            let packageName = "DSHLauncher-Mac-Update-\(version)-\(arch).pkg"
            let manifestName = "update-manifest-macos-\(arch).json"
            let signatureName = "update-manifest-macos-\(arch).sig"
            func download(_ name: String) -> URL? {
                guard let text = assets.first(where: { $0["name"] as? String == name })?["browser_download_url"] as? String else { return nil }
                return URL(string: text)
            }
            guard let packageURL = download(packageName), let manifestURL = download(manifestName), let signatureURL = download(signatureName), let pageURL = URL(string: page) else { continue }
            candidates.append((Self.versionParts(version), LauncherRelease(
                tag: tag, version: version, pageURL: pageURL,
                packageURL: packageURL, manifestURL: manifestURL, signatureURL: signatureURL
            )))
        }
        let current = Self.versionParts(currentVersion)
        return candidates.sorted { Self.compare($0.version, $1.version) > 0 }
            .first(where: { Self.compare($0.version, current) > 0 })?.release
    }

    func downloadAndVerify(_ release: LauncherRelease) async throws -> URL {
        try AppPaths.ensureCreated(layout)
        let (manifestBytes, _) = try await URLSession.shared.data(from: release.manifestURL)
        let (signatureText, _) = try await URLSession.shared.data(from: release.signatureURL)
        let manifest = try JSONDecoder().decode(LauncherUpdateManifest.self, from: manifestBytes)
        guard manifest.schemaVersion == 1, manifest.platform == "macos",
              manifest.architecture == ProcessInfo.processInfo.machineArchitecture,
              manifest.tag == release.tag, manifest.version == release.version else {
            throw LauncherUpdateError.invalidManifest("平台、架构、标签或版本不匹配")
        }
        guard let keyData = publicKeys[manifest.keyId],
              let signature = Data(base64Encoded: String(decoding: signatureText, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)),
              let key = try? Curve25519.Signing.PublicKey(rawRepresentation: keyData),
              key.isValidSignature(signature, for: manifestBytes) else { throw LauncherUpdateError.invalidSignature }
        let (package, _) = try await URLSession.shared.data(from: release.packageURL)
        guard package.count == manifest.assetSize else { throw LauncherUpdateError.hashMismatch }
        let hash = SHA256.hash(data: package).map { String(format: "%02x", $0) }.joined()
        guard hash.caseInsensitiveCompare(manifest.sha256) == .orderedSame else { throw LauncherUpdateError.hashMismatch }
        let destination = layout.updateCacheRoot.appendingPathComponent(manifest.assetName)
        try package.write(to: destination, options: .atomic)
        return destination
    }

    static func version(fromTag tag: String) -> String? {
        let prefix = "mac-v"
        guard tag.hasPrefix(prefix) else { return nil }
        let version = String(tag.dropFirst(prefix.count))
        let parts = version.split(separator: ".")
        guard parts.count == 3, parts.allSatisfy({ Int($0) != nil }) else { return nil }
        return version
    }

    private static func versionParts(_ value: String) -> [Int] { value.split(separator: ".").map { Int($0) ?? 0 } }
    private static func compare(_ lhs: [Int], _ rhs: [Int]) -> Int {
        for index in 0..<max(lhs.count, rhs.count) {
            let left = index < lhs.count ? lhs[index] : 0, right = index < rhs.count ? rhs[index] : 0
            if left != right { return left < right ? -1 : 1 }
        }
        return 0
    }

    private func normalize(_ value: String) throws -> String {
        var text = value.trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        if let url = URL(string: text), url.host == "github.com" { text = url.path.trimmingCharacters(in: CharacterSet(charactersIn: "/")) }
        if text.hasSuffix(".git") { text.removeLast(4) }
        let parts = text.split(separator: "/")
        guard parts.count == 2, parts.allSatisfy({ !$0.isEmpty }) else { throw LauncherUpdateError.invalidRepository }
        return parts.map(String.init).joined(separator: "/")
    }
}

private extension ProcessInfo {
    var machineArchitecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "x64"
        #endif
    }
}
