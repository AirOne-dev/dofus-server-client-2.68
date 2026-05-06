// swift-tools-version:5.9
//
// Package SwiftPM minimal pour cross-compiler OneAirLauncher depuis Linux
// avec un Swift SDK Darwin (cf. client/Dockerfile.darwin).
//
// Le seul source `OneAirLauncher.swift` (1461 lignes) contient du code
// top-level (`let app = NSApplication.shared; … app.run()` à la fin).
// SwiftPM exige que les sources avec top-level code soient nommées
// `main.swift`. On crée donc Sources/OneAirLauncher/main.swift en symlink
// vers ../../OneAirLauncher.swift — pas de duplication, le fichier
// canonique reste à la racine du dossier pour l'ancien build `swiftc`
// natif sur Mac (build-app-darwin.sh / Mac).
//
// Build cross-compile :
//   swift build --swift-sdk arm64-apple-macosx -c release
//
// Sortie : .build/arm64-apple-macosx/release/OneAirLauncher (Mach-O arm64)
// que le wrapper `build-app-darwin.sh` peut copier dans Contents/MacOS/Dofus.

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
