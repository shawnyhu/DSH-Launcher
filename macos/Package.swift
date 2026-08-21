// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "DSHLauncherMac",
    platforms: [.macOS(.v13)],
    products: [
        .library(name: "DSHLauncherCore", targets: ["DSHLauncherCore"]),
        .executable(name: "DSHLauncherCoreSelfCheck", targets: ["DSHLauncherCoreSelfCheck"])
    ],
    targets: [
        .target(
            name: "DSHLauncherCore",
            path: "DSHLauncher",
            exclude: ["App", "Views", "Resources"],
            sources: ["Models", "Infrastructure", "Services", "Diagnostics"]
        ),
        .executableTarget(
            name: "DSHLauncherCoreSelfCheck",
            dependencies: ["DSHLauncherCore"],
            path: "SelfCheck"
        )
    ]
)
