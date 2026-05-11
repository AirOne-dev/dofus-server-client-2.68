// swift-tools-version:5.9
//
// Utilisé par client/build.sh pour cross-compiler depuis Linux via le
// Swift SDK Darwin. Le canonique reste OneAirLauncher.swift dans ce
// dossier ; Sources/OneAirLauncher/main.swift est un symlink généré au
// build (SwiftPM exige `main.swift` pour le top-level code).

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
