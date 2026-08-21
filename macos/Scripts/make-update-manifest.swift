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

guard CommandLine.arguments.count == 8 else {
    fputs("usage: make-update-manifest <asset> <version> <arch> <key-id> <private-key-raw> <team-id> <output-dir>\n", stderr)
    exit(2)
}
let asset = URL(fileURLWithPath: CommandLine.arguments[1])
let version = CommandLine.arguments[2]
let architecture = CommandLine.arguments[3]
let keyID = CommandLine.arguments[4]
let privateKeyURL = URL(fileURLWithPath: CommandLine.arguments[5])
let teamID = CommandLine.arguments[6]
let output = URL(fileURLWithPath: CommandLine.arguments[7], isDirectory: true)
let assetData = try Data(contentsOf: asset)
let digest = SHA256.hash(data: assetData).map { String(format: "%02x", $0) }.joined()
let manifest = Manifest(
    schemaVersion: 1,
    keyId: keyID,
    platform: "macos",
    architecture: architecture,
    tag: "mac-v\(version)",
    version: version,
    assetName: asset.lastPathComponent,
    assetSize: assetData.count,
    sha256: digest,
    developerTeamId: teamID,
    publishedAt: ISO8601DateFormatter().string(from: Date())
)
let encoder = JSONEncoder()
encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
let bytes = try encoder.encode(manifest)
let manifestURL = output.appendingPathComponent("update-manifest-macos-\(architecture).json")
try bytes.write(to: manifestURL, options: .atomic)
let privateKey = try Curve25519.Signing.PrivateKey(rawRepresentation: Data(contentsOf: privateKeyURL))
let signature = try privateKey.signature(for: bytes)
try Data(signature.base64EncodedString().utf8).write(
    to: output.appendingPathComponent("update-manifest-macos-\(architecture).sig"),
    options: .atomic
)
