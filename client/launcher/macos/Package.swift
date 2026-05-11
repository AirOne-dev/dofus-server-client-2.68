// swift-tools-version:5.9
//
// SwiftPM est utilisé pour la cross-compile depuis Linux (cf.
// client/Dockerfile.darwin). Le canonique reste OneAirLauncher.swift à la
// racine du dossier ; Sources/OneAirLauncher/main.swift y est lié en
// symlink (SwiftPM exige `main.swift` pour le top-level code).
//
//   swift build --swift-sdk arm64-apple-macosx -c release

import PackageDescription

let package = Package(
    name: "OneAirLauncher",
    platforms: [.macOS(.v11)],
    targets: [
        .executableTarget(
            name: "OneAirLauncher",
            path: "Sources/OneAirLauncher"
        )
    ]
)
