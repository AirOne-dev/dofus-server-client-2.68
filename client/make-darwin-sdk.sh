#!/usr/bin/env bash
# =============================================================================
#  make-darwin-sdk.sh — assemble un Swift SDK Darwin utilisable depuis Linux,
#  pour cross-compiler le launcher Swift macOS sans Mac dans la boucle.
#
#  Sortie : client/.cache/darwin.artifactbundle.zip (~700 MB)
#  Durée  : ~3-5 min (download ~1.5 GB + extract + repack)
#
#  Composants stitchés :
#    1. macOS SDK 11.3 (frameworks/headers)
#       → phracker/MacOSX-SDKs (mirror public, EULA Xcode = zone grise)
#    2. Toolchain Swift 6.0.3 macOS (libswiftCore Darwin + Foundation)
#       → swift.org (officiel Apple, public, sans Apple ID)
#    3. Toolset LLVM Linux precompiled (ld64.lld + cctools)
#       → xtool-org/darwin-tools-linux-llvm
#
#  Le script tourne dans un container `swift:6.0-jammy` éphémère pour avoir
#  rsync/cpio/p7zip/zip sans toolchain installée sur l'hôte.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE_DIR="$SCRIPT_DIR/.cache"
OUT="$CACHE_DIR/darwin.artifactbundle.zip"

# Si déjà en cache, ne rien faire (override avec FORCE=1 ./make-darwin-sdk.sh)
if [ -f "$OUT" ] && [ "${FORCE:-0}" != "1" ]; then
    echo "==> SDK déjà en cache : $OUT ($(du -h "$OUT" | cut -f1))"
    echo "    Pour forcer la reconstruction : FORCE=1 $0"
    exit 0
fi

mkdir -p "$CACHE_DIR/build/src" "$CACHE_DIR/build/extract"
WORK="$CACHE_DIR/build"

PHRACKER_SDK_URL="${PHRACKER_SDK_URL:-https://github.com/phracker/MacOSX-SDKs/releases/download/11.3/MacOSX11.3.sdk.tar.xz}"
SWIFT_PKG_URL="${SWIFT_PKG_URL:-https://download.swift.org/swift-6.0.3-release/xcode/swift-6.0.3-RELEASE/swift-6.0.3-RELEASE-osx.pkg}"
TOOLSET_URL="${TOOLSET_URL:-https://github.com/xtool-org/darwin-tools-linux-llvm/releases/download/v1.0.1/toolset-x86_64.tar.gz}"

echo "==> [1/4] Download macOS SDK (phracker, ~50 MB)"
[ -f "$WORK/src/macosx-sdk.tar.xz" ] || \
    curl -fL --retry 3 --progress-bar -o "$WORK/src/macosx-sdk.tar.xz" "$PHRACKER_SDK_URL"

echo "==> [2/4] Download Darwin LLVM toolset (xtool-org, ~45 MB)"
[ -f "$WORK/src/darwin-toolset.tar.gz" ] || \
    curl -fL --retry 3 --progress-bar -o "$WORK/src/darwin-toolset.tar.gz" "$TOOLSET_URL"

echo "==> [3/4] Download Swift macOS toolchain (swift.org, ~1.4 GB)"
[ -f "$WORK/src/swift-osx.pkg" ] || \
    curl -fL --retry 3 --progress-bar -o "$WORK/src/swift-osx.pkg" "$SWIFT_PKG_URL"

echo "==> [4/4] Stitch + repack via swift:6.0-jammy container"
docker run --rm -v "$WORK:/work" -w /work swift:6.0-jammy bash -ce '
apt-get update -qq && apt-get install -y -qq rsync curl zip cpio p7zip-full >/dev/null 2>&1

# Étape A : extract phracker SDK
[ -d extract/sdk/MacOSX11.3.sdk ] || {
    mkdir -p extract/sdk
    tar xf src/macosx-sdk.tar.xz -C extract/sdk/
}

# Étape B : extract Apple .pkg (xar → cpio → fs)
[ -d extract/swift-toolchain/usr ] || {
    mkdir -p extract/swift-pkg
    cd extract/swift-pkg && 7z x -y /work/src/swift-osx.pkg >/dev/null && cd /work
    mkdir -p extract/swift-toolchain && cd extract/swift-toolchain
    cpio -idm --no-absolute-filenames < /work/extract/swift-pkg/Payload~ 2>/dev/null
    cd /work
}

# Étape C : assemble fake Developer dir (MacOSX-only)
DEV=/work/fake-developer
rm -rf "$DEV"
mkdir -p "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib"
mkdir -p "$DEV/Platforms/MacOSX.platform/Developer/SDKs"
mkdir -p "$DEV/Platforms/MacOSX.platform/Developer/Library/"{Frameworks,PrivateFrameworks}
mkdir -p "$DEV/Platforms/MacOSX.platform/Developer/usr/lib"
# Stubs iPhoneOS/iPhoneSimulator (build.sh a besoin que la glob basename matche)
for p in iPhoneOS iPhoneSimulator; do
    mkdir -p "$DEV/Platforms/$p.platform/Developer/SDKs/${p}17.0.sdk"
    mkdir -p "$DEV/Platforms/$p.platform/Developer/Library/"{Frameworks,PrivateFrameworks}
    mkdir -p "$DEV/Platforms/$p.platform/Developer/usr/lib"
done

cp -a extract/sdk/MacOSX11.3.sdk "$DEV/Platforms/MacOSX.platform/Developer/SDKs/"
cp -a extract/swift-toolchain/usr/lib/swift "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib/"
cp -a extract/swift-toolchain/usr/lib/swift_static "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib/"
cp -a extract/swift-toolchain/usr/lib/clang "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib/"

# Étape D : clone swift-sdk-darwin + patch + run build.sh
rm -rf swift-sdk-darwin
curl -fsSL https://github.com/kabiroberai/swift-sdk-darwin/archive/refs/heads/main.tar.gz \
    | tar xz && mv swift-sdk-darwin-main swift-sdk-darwin

cat > swift-sdk-darwin/build.sh <<"SCRIPT"
#!/bin/bash
set -e
DARWIN_TOOLS_VERSION="1.0.1"
dev_dir="$1"
target_arch="${2:-$(arch)}"
[[ "${target_arch}" == arm64 ]] && target_arch=aarch64
cd "$(dirname "$0")"
root="$PWD"
rm -rf staging && mkdir -p staging output
bundle="staging/darwin.artifactbundle"
cp -a layout "$bundle"
MacOSX_SDK="$(basename "$dev_dir"/Platforms/MacOSX.platform/Developer/SDKs/MacOSX*.*.sdk)"
sed -e "s/\$MacOSX_SDK/$MacOSX_SDK/g" \
    -e "s/\$iPhoneOS_SDK/STUB.sdk/g" \
    -e "s/\$iPhoneSimulator_SDK/STUB.sdk/g" \
    templates/swift-sdk.json > "$bundle/swift-sdk.json"
echo "develop" > "$bundle/darwin-sdk-version.txt"
mkdir -p "$bundle/toolset"
curl -#L "https://github.com/xtool-org/darwin-tools-linux-llvm/releases/download/v${DARWIN_TOOLS_VERSION}/toolset-${target_arch}.tar.gz" \
    | tar xzf - -C "$bundle/toolset"
mkdir -p "$bundle/Developer"
rsync -aW --relative \
    "$dev_dir/./"Toolchains/XcodeDefault.xctoolchain/usr/lib/{swift,swift_static,clang} \
    "$dev_dir/./"Platforms/MacOSX.platform/Developer/{SDKs,Library/{Private,}Frameworks,usr/lib} \
    --exclude "Toolchains/XcodeDefault.xctoolchain/usr/lib/swift/*/prebuilt-modules" \
    "$bundle/Developer/" || true
fwdir="${bundle}/Developer/Platforms/MacOSX.platform/Developer/SDKs/${MacOSX_SDK}/System/Library/Frameworks"
mkdir -p "$fwdir"
for fw in Testing XCTest XCUIAutomation; do
    ln -sfn "../../../../../Library/Frameworks/${fw}.framework" "$fwdir/${fw}.framework" || true
done
ln -sfn "../../../../../Library/PrivateFrameworks/XCTestCore.framework" "$fwdir/XCTestCore.framework" || true
(cd "$(dirname "$bundle")" && zip -yqr "$root/staging/out.zip.tmp" "$(basename "$bundle")")
mv -f "$root/staging/out.zip.tmp" "$root/output/darwin-linux-${target_arch}.artifactbundle.zip"
rm -rf staging
SCRIPT
chmod +x swift-sdk-darwin/build.sh
swift-sdk-darwin/build.sh "$DEV" x86_64

cp swift-sdk-darwin/output/darwin-linux-x86_64.artifactbundle.zip /work/out.artifactbundle.zip
'

mv "$WORK/out.artifactbundle.zip" "$OUT"
echo
echo "==> ✓ SDK Darwin assemblé : $OUT ($(du -h "$OUT" | cut -f1))"
echo "    SHA256 : $(sha256sum "$OUT" | cut -d' ' -f1)"
echo
echo "    Le wrapper build-docker-darwin.sh installera ce bundle automatiquement"
echo "    et bascule en mode cross-compile via 'swift build --swift-sdk'."
