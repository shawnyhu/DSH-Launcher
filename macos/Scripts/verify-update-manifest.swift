import CryptoKit
import Foundation

struct Manifest: Codable {
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

guard CommandLine.arguments.count == 5 else {
    fputs("usage: verify-update-manifest <manifest> <signature> <public-keys-json> <asset>\n", stderr)
    exit(2)
}

let manifestURL = URL(fileURLWithPath: CommandLine.arguments[1])
let signatureURL = URL(fileURLWithPath: CommandLine.arguments[2])
let keysURL = URL(fileURLWithPath: CommandLine.arguments[3])
let assetURL = URL(fileURLWithPath: CommandLine.arguments[4])
let manifestBytes = try Data(contentsOf: manifestURL)
let manifest = try JSONDecoder().decode(Manifest.self, from: manifestBytes)
let keys = try JSONDecoder().decode([String: String].self, from: Data(contentsOf: keysURL))

guard manifest.schemaVersion == 1, manifest.platform == "macos",
      manifest.tag == "mac-v\(manifest.version)", manifest.assetName == assetURL.lastPathComponent else {
    fputs("manifest identity validation failed\n", stderr)
    exit(1)
}
guard let encodedKey = keys[manifest.keyId], let keyData = Data(base64Encoded: encodedKey),
      let key = try? Curve25519.Signing.PublicKey(rawRepresentation: keyData),
      let signature = Data(base64Encoded: String(decoding: try Data(contentsOf: signatureURL), as: UTF8.self)
        .trimmingCharacters(in: .whitespacesAndNewlines)),
      key.isValidSignature(signature, for: manifestBytes) else {
    fputs("manifest signature validation failed\n", stderr)
    exit(1)
}

let asset = try Data(contentsOf: assetURL)
let digest = SHA256.hash(data: asset).map { String(format: "%02x", $0) }.joined()
guard asset.count == manifest.assetSize,
      digest.caseInsensitiveCompare(manifest.sha256) == .orderedSame else {
    fputs("asset hash validation failed\n", stderr)
    exit(1)
}
print("Verified \(manifest.tag) \(manifest.assetName) with key \(manifest.keyId)")
