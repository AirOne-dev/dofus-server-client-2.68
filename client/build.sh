#!/usr/bin/env bash
# =============================================================================
#  build.sh — point d'entrée unique pour construire les clients OneAir.
#
#  Cibles : OneAir.app (macOS) et/ou OneAir-Windows/. Tout passe par Docker
#  (image oneair-builder-{darwin,windows}) sauf si on est sur un Mac et qu'on
#  demande explicitement le mode natif.
#
#  Usage :
#    ./build.sh                            # menu interactif
#    ./build.sh darwin [--no-zip]
#    ./build.sh windows [--no-zip]
#    ./build.sh all [--no-zip]
#    ./build.sh sdk                        # (re)build du Swift SDK Darwin
#
#  Variable d'env interne : ONEAIR_INSIDE_CONTAINER=1 fait que la fonction
#  d'assemblage tourne directement sans dispatch Docker (utilisé par le
#  script lui-même quand il se ré-invoque dans le container).
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
CACHE_DIR="$SCRIPT_DIR/.cache"
DIST_DIR="$ROOT_DIR/dist"

DARWIN_IMAGE="${ONEAIR_BUILDER_MAC_IMAGE:-oneair-builder-darwin:latest}"
WINDOWS_IMAGE="${ONEAIR_BUILDER_WIN_IMAGE:-oneair-builder-windows:latest}"
SDK_BUNDLE="$CACHE_DIR/darwin.artifactbundle.zip"

case "${OSTYPE:-$(uname -s)}" in
    darwin*|Darwin) IS_MACOS=1 ;;
    *)              IS_MACOS=0 ;;
esac

# Charge .env (SERVER_HOST / PUBLIC_AUTH_PORT / SERVER_DISPLAY_NAME)
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a
: "${SERVER_HOST:=127.0.0.1}"
: "${PUBLIC_AUTH_PORT:=5555}"
: "${SERVER_DISPLAY_NAME:=OneAir}"
: "${DOFUS_LANG:=fr}"
: "${ZAAP_GOARCH:=arm64}"

require_docker() {
    command -v docker >/dev/null || { echo "ERREUR : docker absent." >&2; exit 1; }
}

# -----------------------------------------------------------------------------
# Swift SDK Darwin (~700 MB) — composé une fois pour cross-compile le launcher
# Swift depuis Linux. Composants : phracker MacOSX SDK 11.3 + swift.org 6.0.3
# macOS toolchain + xtool-org darwin-tools LLVM. Tourne dans swift:6.0-jammy
# (a besoin de rsync/cpio/p7zip/zip).
# -----------------------------------------------------------------------------
build_darwin_sdk() {
    if [ -f "$SDK_BUNDLE" ] && [ "${FORCE:-0}" != "1" ]; then
        echo "==> Swift SDK Darwin déjà en cache : $SDK_BUNDLE ($(du -h "$SDK_BUNDLE" | cut -f1))"
        echo "    Pour forcer la reconstruction : FORCE=1 $0 sdk"
        return 0
    fi
    require_docker
    local work="$CACHE_DIR/build"
    mkdir -p "$work/src" "$work/extract"

    local sdk_url="${PHRACKER_SDK_URL:-https://github.com/phracker/MacOSX-SDKs/releases/download/11.3/MacOSX11.3.sdk.tar.xz}"
    local swift_url="${SWIFT_PKG_URL:-https://download.swift.org/swift-6.0.3-release/xcode/swift-6.0.3-RELEASE/swift-6.0.3-RELEASE-osx.pkg}"
    local toolset_url="${TOOLSET_URL:-https://github.com/xtool-org/darwin-tools-linux-llvm/releases/download/v1.0.1/toolset-x86_64.tar.gz}"

    echo "==> [1/4] Download macOS SDK (phracker, ~50 MB)"
    [ -f "$work/src/macosx-sdk.tar.xz" ] || \
        curl -fL --retry 3 --progress-bar -o "$work/src/macosx-sdk.tar.xz" "$sdk_url"
    echo "==> [2/4] Download Darwin LLVM toolset (~45 MB)"
    [ -f "$work/src/darwin-toolset.tar.gz" ] || \
        curl -fL --retry 3 --progress-bar -o "$work/src/darwin-toolset.tar.gz" "$toolset_url"
    echo "==> [3/4] Download Swift macOS toolchain (~1.4 GB)"
    [ -f "$work/src/swift-osx.pkg" ] || \
        curl -fL --retry 3 --progress-bar -o "$work/src/swift-osx.pkg" "$swift_url"

    echo "==> [4/4] Stitch + repack via swift:6.0-jammy"
    docker run --rm -v "$work:/work" -w /work swift:6.0-jammy bash -ce '
apt-get update -qq && apt-get install -y -qq rsync curl zip cpio p7zip-full >/dev/null 2>&1
[ -d extract/sdk/MacOSX11.3.sdk ] || { mkdir -p extract/sdk && tar xf src/macosx-sdk.tar.xz -C extract/sdk/; }
[ -d extract/swift-toolchain/usr ] || {
    mkdir -p extract/swift-pkg && cd extract/swift-pkg && 7z x -y /work/src/swift-osx.pkg >/dev/null && cd /work
    mkdir -p extract/swift-toolchain && cd extract/swift-toolchain
    cpio -idm --no-absolute-filenames < /work/extract/swift-pkg/Payload~ 2>/dev/null
    cd /work
}
DEV=/work/fake-developer; rm -rf "$DEV"
mkdir -p "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib"
mkdir -p "$DEV/Platforms/MacOSX.platform/Developer/SDKs"
mkdir -p "$DEV/Platforms/MacOSX.platform/Developer/Library/"{Frameworks,PrivateFrameworks}
mkdir -p "$DEV/Platforms/MacOSX.platform/Developer/usr/lib"
for p in iPhoneOS iPhoneSimulator; do
    mkdir -p "$DEV/Platforms/$p.platform/Developer/SDKs/${p}17.0.sdk"
    mkdir -p "$DEV/Platforms/$p.platform/Developer/Library/"{Frameworks,PrivateFrameworks}
    mkdir -p "$DEV/Platforms/$p.platform/Developer/usr/lib"
done
cp -a extract/sdk/MacOSX11.3.sdk "$DEV/Platforms/MacOSX.platform/Developer/SDKs/"
cp -a extract/swift-toolchain/usr/lib/swift "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib/"
cp -a extract/swift-toolchain/usr/lib/swift_static "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib/"
cp -a extract/swift-toolchain/usr/lib/clang "$DEV/Toolchains/XcodeDefault.xctoolchain/usr/lib/"
rm -rf swift-sdk-darwin
curl -fsSL https://github.com/kabiroberai/swift-sdk-darwin/archive/refs/heads/main.tar.gz \
    | tar xz && mv swift-sdk-darwin-main swift-sdk-darwin
cat > swift-sdk-darwin/build.sh <<"SCRIPT"
#!/bin/bash
set -e
DARWIN_TOOLS_VERSION="1.0.1"
dev_dir="$1"; target_arch="${2:-$(arch)}"
[[ "${target_arch}" == arm64 ]] && target_arch=aarch64
cd "$(dirname "$0")"; root="$PWD"
rm -rf staging && mkdir -p staging output
bundle="staging/darwin.artifactbundle"; cp -a layout "$bundle"
MacOSX_SDK="$(basename "$dev_dir"/Platforms/MacOSX.platform/Developer/SDKs/MacOSX*.*.sdk)"
sed -e "s/\$MacOSX_SDK/$MacOSX_SDK/g" -e "s/\$iPhoneOS_SDK/STUB.sdk/g" -e "s/\$iPhoneSimulator_SDK/STUB.sdk/g" \
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
    mv "$work/out.artifactbundle.zip" "$SDK_BUNDLE"
    echo "==> ✓ SDK Darwin assemblé : $SDK_BUNDLE ($(du -h "$SDK_BUNDLE" | cut -f1))"
}

# -----------------------------------------------------------------------------
# Récupère les assets Dofus officiels via cytrus-downloader (~5 GB la 1ère fois)
# -----------------------------------------------------------------------------
fetch_cytrus_assets() {
    local platform="$1"  # "darwin" | "windows"
    local image="$2"
    local assets_dir="$SCRIPT_DIR/dofus-${platform}-2.68"
    local target="$assets_dir/dofus/2.68.0.0/${platform}/main"
    if [ -d "$target" ]; then
        echo "==> Assets Dofus 2.68 ${platform} déjà présents → skip fetch"
        return 0
    fi
    echo "==> Fetch Dofus 2.68 ${platform} via cytrus-downloader (~5 GB, 1ère fois)"
    mkdir -p "$assets_dir"
    docker run --rm \
        -v "$assets_dir:/out" \
        -v "oneair-go-cache:/tmp/go-cache" \
        -v "oneair-go-mod:/tmp/go-mod" \
        -e GOCACHE=/tmp/go-cache -e GOMODCACHE=/tmp/go-mod \
        "$image" bash -ce "
            cd /tmp && rm -rf cytrus-src
            git clone --depth 1 https://github.com/loonaire/Cytrus-downloader.git cytrus-src
            cd cytrus-src && go build -o /tmp/cytrus .
            /tmp/cytrus -game dofus -platform ${platform} \
                -release main -version 6.0_2.68.0.0 -outdir /out/
        "
}

# -----------------------------------------------------------------------------
# Patch config.xml — connection.host + signature vidée (BUILD_TYPE=DEBUG dans
# le SWF Giny shortcircuite Signature.verify).
# -----------------------------------------------------------------------------
patch_config_xml() {
    local path="$1"
    /usr/bin/python3 - "$path" "$SERVER_DISPLAY_NAME" "$SERVER_HOST" "$PUBLIC_AUTH_PORT" <<'PY'
import re, sys, pathlib
path, name, host, port = sys.argv[1:]
p = pathlib.Path(path)
text = p.read_text(encoding="utf-8", errors="ignore")
text = re.sub(r'<entry key="connection\.host">[^<]*</entry>',
              f'<entry key="connection.host">{name}:{host}:{port}</entry>', text)
text = re.sub(r'<entry key="connection\.host\.signature">[^<]*</entry>',
              '<entry key="connection.host.signature"></entry>', text)
p.write_text(text, encoding="utf-8")
PY
}

# credentials.json par défaut (override par le launcher au runtime)
write_credentials_json() {
    cat > "$1" <<'JSON'
{"port":4242,"name":"dofus","release":"main","instanceId":1,"hash":"464e4625-67f1-4706-985c-8358f8661e3c"}
JSON
}

# -----------------------------------------------------------------------------
# OneAir.app (macOS). Tourne soit nativement (utilise plutil/codesign/xattr)
# soit dans le container oneair-builder-darwin (utilise plistlib + rcodesign).
# -----------------------------------------------------------------------------
assemble_darwin_app() {
    local app_dir="$ROOT_DIR/OneAir.app"
    local src_dir
    if [ -d "$SCRIPT_DIR/dofus-darwin-2.68/dofus/2.68.0.0/darwin/main/Dofus.app" ]; then
        src_dir="$SCRIPT_DIR/dofus-darwin-2.68/dofus/2.68.0.0/darwin"
    elif [ -d "$SCRIPT_DIR/dofus-darwin/dofus/2.73.3.14/darwin/main/Dofus.app" ]; then
        src_dir="$SCRIPT_DIR/dofus-darwin/dofus/2.73.3.14/darwin"
        echo "AVERTISSEMENT : fallback 2.73.3.14 — proto incompatible Giny"
    else
        echo "ERREUR : aucun client darwin trouvé dans $SCRIPT_DIR" >&2
        return 1
    fi

    echo "==> Bundle cible : $app_dir"
    rm -rf "$app_dir"
    echo "==> Copie main/Dofus.app ($(du -sh "$src_dir/main/Dofus.app" | cut -f1))"
    cp -R "$src_dir/main/Dofus.app" "$app_dir"

    local lang_dir="$src_dir/lang_${DOFUS_LANG}/Dofus.app/Contents/Resources"
    if [ -d "$lang_dir" ]; then
        echo "==> Merge lang_${DOFUS_LANG} dans Resources/"
        /usr/bin/rsync -a "$lang_dir/" "$app_dir/Contents/Resources/"
    fi

    local giny_cfg="$SCRIPT_DIR/giny-config.xml"
    local config_xml="$app_dir/Contents/Resources/config.xml"
    [ -f "$giny_cfg" ] && cp "$giny_cfg" "$config_xml"
    if [ -f "$config_xml" ]; then
        echo "==> Patch connection.host → ${SERVER_DISPLAY_NAME}:${SERVER_HOST}:${PUBLIC_AUTH_PORT}"
        patch_config_xml "$config_xml"
    fi

    local patched_swf="$SCRIPT_DIR/DofusInvoker-patched.swf"
    if [ -f "$patched_swf" ]; then
        echo "==> Remplace DofusInvoker.swf par la version Giny DEBUG-patched"
        cp "$patched_swf" "$app_dir/Contents/Resources/DofusInvoker.swf"
        rm -f "$app_dir/Contents/Resources/META-INF/signatures.xml" \
              "$app_dir/Contents/Resources/META-INF/AIR/hash"
    fi
    write_credentials_json "$app_dir/Contents/Resources/credentials.json"

    # Restaure les symlinks Adobe AIR.framework perdus par cytrus-downloader
    local fw="$app_dir/Contents/Frameworks/Adobe AIR.framework"
    if [ -d "$fw/Versions/1.0" ]; then
        (cd "$fw/Versions" && [ -L Current ] || ln -sfn 1.0 Current)
        (cd "$fw" && [ -L "Adobe AIR" ] || ln -sfn "Versions/Current/Adobe AIR" "Adobe AIR")
        (cd "$fw" && [ -L Resources ] || ln -sfn "Versions/Current/Resources" Resources)
        (cd "$fw/Versions/1.0" && [ -L "Adobe AIR_64" ] || ln -sfn "Adobe AIR" "Adobe AIR_64")
    fi

    # Launcher Swift : cross-compile si SDK Darwin dispo, sinon binaire pré-buildé
    local launcher_bin="$SCRIPT_DIR/OneAirLauncher/OneAirLauncher"
    local launcher_assets="$SCRIPT_DIR/OneAirLauncher/Assets"
    if [ "$IS_MACOS" -eq 0 ] && command -v swift >/dev/null 2>&1 \
            && [ -f "$SCRIPT_DIR/OneAirLauncher/Package.swift" ]; then
        if ! swift sdk list 2>/dev/null | grep -q '^darwin' \
                && [ -f /sdk-cache/darwin.artifactbundle.zip ]; then
            echo "==> Install Swift SDK Darwin (1ère fois dans ce container)"
            swift sdk install /sdk-cache/darwin.artifactbundle.zip 2>&1 | tail -3
        fi
        if swift sdk list 2>/dev/null | grep -q '^darwin'; then
            echo "==> Cross-compile OneAirLauncher"
            (cd "$SCRIPT_DIR/OneAirLauncher" && \
                swift build --swift-sdk arm64-apple-macosx -c release 2>&1 | tail -5)
            local built="$SCRIPT_DIR/OneAirLauncher/.build/arm64-apple-macosx/release/OneAirLauncher"
            [ -f "$built" ] && launcher_bin="$built"
        fi
    fi
    local launcher_ok=0
    if [ "$IS_MACOS" -eq 1 ]; then [ -x "$launcher_bin" ] && launcher_ok=1
    else [ -s "$launcher_bin" ] && launcher_ok=1
    fi
    if [ "$launcher_ok" -eq 1 ]; then
        echo "==> Intégration du pré-launcher Swift"
        mv "$app_dir/Contents/MacOS/Dofus" "$app_dir/Contents/MacOS/dofus-real"
        cp "$launcher_bin" "$app_dir/Contents/MacOS/Dofus"
        chmod +x "$app_dir/Contents/MacOS/Dofus" "$app_dir/Contents/MacOS/dofus-real"
        if [ -d "$launcher_assets" ]; then
            cp "$launcher_assets"/portal-bg.jpg "$app_dir/Contents/Resources/" 2>/dev/null || true
            cp "$launcher_assets"/logo.png       "$app_dir/Contents/Resources/" 2>/dev/null || true
        fi
    fi

    # zaap-server : toujours recompile (cache Go = no-op si rien n'a changé)
    local zaap_bin="$SCRIPT_DIR/zaap-server/zaap-server"
    if [ "$IS_MACOS" -eq 0 ] || [ ! -f "$zaap_bin" ]; then
        echo "==> Cross-compile zaap-server pour darwin/${ZAAP_GOARCH}"
        (cd "$SCRIPT_DIR/zaap-server" && \
            GOOS=darwin GOARCH="$ZAAP_GOARCH" CGO_ENABLED=0 \
            go build -trimpath -ldflags='-s -w' -o zaap-server .)
    else
        echo "==> Rebuild zaap-server (natif macOS)"
        (cd "$SCRIPT_DIR/zaap-server" && CGO_ENABLED=0 \
            go build -trimpath -ldflags='-s -w' -o zaap-server .)
    fi
    cp "$zaap_bin" "$app_dir/Contents/MacOS/zaap-server"
    chmod +x "$app_dir/Contents/MacOS/zaap-server"

    # Info.plist
    local plist="$app_dir/Contents/Info.plist"
    if [ "$IS_MACOS" -eq 1 ]; then
        plutil -replace CFBundleName -string "OneAir" "$plist" 2>/dev/null || true
        plutil -replace CFBundleDisplayName -string "OneAir" "$plist" 2>/dev/null || true
        plutil -replace CFBundleIdentifier -string "local.oneair" "$plist" 2>/dev/null || true
        plutil -replace CFBundleExecutable -string "Dofus" "$plist" 2>/dev/null || true
    else
        /usr/bin/python3 - "$plist" <<'PY' || true
import plistlib, sys
path = sys.argv[1]
with open(path, "rb") as f: data = plistlib.load(f)
data["CFBundleName"] = "OneAir"
data["CFBundleDisplayName"] = "OneAir"
data["CFBundleIdentifier"] = "local.oneair"
data["CFBundleExecutable"] = "Dofus"
with open(path, "wb") as f: plistlib.dump(data, f)
PY
    fi

    # Signature ad-hoc : codesign (natif) ou rcodesign (Linux)
    if [ "$IS_MACOS" -eq 1 ]; then
        xattr -drc "$app_dir" 2>/dev/null || true
        codesign --force --deep -s - "$app_dir" 2>&1 | tail -2 || true
    elif command -v rcodesign >/dev/null 2>&1; then
        rcodesign sign "$app_dir" 2>&1 | tail -5 || true
    else
        echo "AVERTISSEMENT : rcodesign absent — bundle non signé."
    fi

    echo "==> ✓ $app_dir ($(du -sh "$app_dir" | cut -f1))"
}

# -----------------------------------------------------------------------------
# OneAir-Windows/ — toujours via Docker (cross-compile WPF + Go)
# -----------------------------------------------------------------------------
assemble_windows_bundle() {
    local app_dir="$ROOT_DIR/OneAir-Windows"
    local src_base="$SCRIPT_DIR/dofus-windows-2.68/dofus/2.68.0.0/windows"
    [ -d "$src_base/win64" ] || { echo "ERREUR : $src_base/win64 introuvable." >&2; return 1; }
    [ -d "$src_base/main" ]  || { echo "ERREUR : $src_base/main introuvable."  >&2; return 1; }

    echo "==> Bundle cible : $app_dir"
    rm -rf "$app_dir"; mkdir -p "$app_dir"
    echo "==> Copie win64/ ($(du -sh "$src_base/win64" | cut -f1))"
    cp -R "$src_base/win64/." "$app_dir/"
    echo "==> Merge main/ → bundle"
    cp -R "$src_base/main/." "$app_dir/"
    if [ -d "$src_base/lang_${DOFUS_LANG}" ]; then
        echo "==> Merge lang_${DOFUS_LANG}/"
        cp -R "$src_base/lang_${DOFUS_LANG}/." "$app_dir/"
    fi

    local giny_cfg="$SCRIPT_DIR/giny-config.xml"
    local config_xml="$app_dir/config.xml"
    [ -f "$giny_cfg" ] && cp "$giny_cfg" "$config_xml"
    if [ -f "$config_xml" ]; then
        echo "==> Patch connection.host → ${SERVER_DISPLAY_NAME}:${SERVER_HOST}:${PUBLIC_AUTH_PORT}"
        patch_config_xml "$config_xml"
    fi

    local patched_swf="$SCRIPT_DIR/DofusInvoker-patched.swf"
    if [ -f "$patched_swf" ]; then
        echo "==> Remplace DofusInvoker.swf par la version Giny DEBUG-patched"
        cp "$patched_swf" "$app_dir/DofusInvoker.swf"
        rm -f "$app_dir/META-INF/signatures.xml" "$app_dir/META-INF/AIR/hash"
    fi
    write_credentials_json "$app_dir/credentials.json"

    echo "==> Cross-compile zaap-server.exe"
    (cd "$SCRIPT_DIR/zaap-server" && GOOS=windows GOARCH=amd64 CGO_ENABLED=0 \
        go build -trimpath -ldflags='-s -w' -o zaap-server.exe .)
    cp "$SCRIPT_DIR/zaap-server/zaap-server.exe" "$app_dir/zaap-server.exe"

    echo "==> Build OneAirLauncher.exe (dotnet publish)"
    if ! command -v dotnet >/dev/null 2>&1 && [ -x /opt/dotnet/dotnet ]; then
        export PATH="/opt/dotnet:$PATH"
    fi
    (cd "$SCRIPT_DIR/OneAirLauncher-win" && dotnet publish -c Release -r win-x64 \
        --self-contained -p:PublishSingleFile=true -p:EnableWindowsTargeting=true)
    # On renomme Dofus.exe → dofus-real.exe et on dépose le launcher sous Dofus.exe
    mv "$app_dir/Dofus.exe" "$app_dir/dofus-real.exe"
    cp "$SCRIPT_DIR/OneAirLauncher-win/bin/Release/net8.0-windows/win-x64/publish/OneAirLauncher.exe" \
        "$app_dir/Dofus.exe"
    rm -f "$app_dir/META-INF/AIR/debug" 2>/dev/null || true

    echo "==> ✓ $app_dir ($(du -sh "$app_dir" | cut -f1))"
}

# -----------------------------------------------------------------------------
# Zip Store (pas de compression — assets déjà compressés). Tourne dans le
# container pour avoir un binaire zip cohérent entre hôte/CI.
# -----------------------------------------------------------------------------
zip_output() {
    local kind="$1"  # "darwin" | "windows"
    require_docker
    mkdir -p "$DIST_DIR"
    local image folder out
    if [ "$kind" = "darwin" ]; then
        image="$DARWIN_IMAGE"; folder="OneAir.app"; out="$DIST_DIR/OneAir-MacOS.zip"
    else
        image="$WINDOWS_IMAGE"; folder="OneAir-Windows"; out="$DIST_DIR/OneAir-Windows.zip"
    fi
    echo "==> Zip → $out"
    docker run --rm -v "$ROOT_DIR:/work" -w /work "$image" bash -c "
        command -v zip >/dev/null || { apt-get update -qq && apt-get install -y -qq zip >/dev/null 2>&1; }
        cd /work && rm -f $(basename "$out").tmp
        zip -ryq0 dist/$(basename "$out").tmp $folder
        mv dist/$(basename "$out").tmp $out
        echo \"    \$(du -h $out | cut -f1)\"
    "
}

# -----------------------------------------------------------------------------
# Dispatchers : build l'image puis lance l'assembly. Si le SDK Darwin existe,
# il est monté en /sdk-cache (lecture seule) pour activer le cross-compile.
# -----------------------------------------------------------------------------
build_darwin_via_docker() {
    require_docker
    local dockerfile="$SCRIPT_DIR/Dockerfile.darwin"
    [ -f "$dockerfile" ] || { echo "ERREUR : $dockerfile introuvable." >&2; exit 1; }
    echo "==> Image $DARWIN_IMAGE"
    docker build --pull -f "$dockerfile" -t "$DARWIN_IMAGE" "$SCRIPT_DIR"
    fetch_cytrus_assets darwin "$DARWIN_IMAGE"
    local sdk_opts=()
    if [ "${ONEAIR_SKIP_SWIFT_SDK:-0}" = "1" ]; then
        echo "==> Swift SDK Darwin skipped (binaire pré-buildé)"
    elif [ ! -f "$SDK_BUNDLE" ]; then
        echo "==> Swift SDK Darwin absent — assemblage"
        build_darwin_sdk
    fi
    if [ -f "$SDK_BUNDLE" ]; then
        sdk_opts=(-v "$CACHE_DIR:/sdk-cache:ro" -v "oneair-swiftpm:/root/.swiftpm")
    fi
    docker run --rm \
        -v "$ROOT_DIR:/work" \
        -v "oneair-go-cache:/tmp/go-cache" \
        -v "oneair-go-mod:/tmp/go-mod" \
        "${sdk_opts[@]}" \
        -e SERVER_HOST -e PUBLIC_AUTH_PORT -e SERVER_DISPLAY_NAME \
        -e DOFUS_LANG -e ZAAP_GOARCH \
        -e ONEAIR_INSIDE_CONTAINER=1 \
        -w /work "$DARWIN_IMAGE" \
        bash /work/client/build.sh darwin --no-zip
}

build_windows_via_docker() {
    require_docker
    local dockerfile="$SCRIPT_DIR/Dockerfile.windows"
    [ -f "$dockerfile" ] || { echo "ERREUR : $dockerfile introuvable." >&2; exit 1; }
    echo "==> Image $WINDOWS_IMAGE"
    docker build --pull -f "$dockerfile" -t "$WINDOWS_IMAGE" "$SCRIPT_DIR"
    fetch_cytrus_assets windows "$WINDOWS_IMAGE"
    docker run --rm \
        -v "$ROOT_DIR:/work" \
        -v "oneair-go-cache:/tmp/go-cache" \
        -v "oneair-go-mod:/tmp/go-mod" \
        -e SERVER_HOST -e PUBLIC_AUTH_PORT -e SERVER_DISPLAY_NAME \
        -e DOFUS_LANG \
        -e ONEAIR_INSIDE_CONTAINER=1 \
        -w /work "$WINDOWS_IMAGE" \
        bash /work/client/build.sh windows --no-zip
}

# -----------------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------------
TARGET=""
DO_ZIP=1
NATIVE_DARWIN=0

while [ $# -gt 0 ]; do
    case "$1" in
        darwin|windows|all|sdk) TARGET="$1" ;;
        --no-zip)               DO_ZIP=0 ;;
        --native)               NATIVE_DARWIN=1 ;;  # macOS host uniquement
        -h|--help)
            sed -n '2,18p' "$0" | sed 's/^# \?//'; exit 0 ;;
        *) echo "Argument inconnu : $1" >&2; exit 1 ;;
    esac
    shift
done

if [ -z "$TARGET" ]; then
    echo "Cible ?"
    echo "  1) macOS (OneAir.app)"
    echo "  2) Windows (OneAir-Windows/)"
    echo "  3) Les deux"
    echo "  4) (Re)build Swift SDK Darwin uniquement"
    read -r -p "Choix [1] : " choice
    case "${choice:-1}" in
        1) TARGET=darwin ;;
        2) TARGET=windows ;;
        3) TARGET=all ;;
        4) TARGET=sdk ;;
        *) echo "Choix invalide" >&2; exit 1 ;;
    esac
    if [ "$TARGET" != "sdk" ]; then
        read -r -p "Zipper le résultat dans dist/ ? [Y/n] : " yn
        case "$yn" in n|N|no|NO) DO_ZIP=0 ;; esac
        if [ "$IS_MACOS" -eq 1 ] && [ "$TARGET" != "windows" ]; then
            read -r -p "macOS natif (sans Docker) pour le bundle macOS ? [y/N] : " yn
            case "$yn" in y|Y|yes|YES) NATIVE_DARWIN=1 ;; esac
        fi
    fi
fi

# Dispatch
case "$TARGET" in
    sdk) build_darwin_sdk ;;
    darwin)
        if [ "${ONEAIR_INSIDE_CONTAINER:-0}" = "1" ] || [ "$NATIVE_DARWIN" = "1" ]; then
            assemble_darwin_app
        else
            build_darwin_via_docker
        fi
        [ "$DO_ZIP" = "1" ] && [ "${ONEAIR_INSIDE_CONTAINER:-0}" != "1" ] && zip_output darwin
        ;;
    windows)
        if [ "${ONEAIR_INSIDE_CONTAINER:-0}" = "1" ]; then
            assemble_windows_bundle
        else
            build_windows_via_docker
        fi
        [ "$DO_ZIP" = "1" ] && [ "${ONEAIR_INSIDE_CONTAINER:-0}" != "1" ] && zip_output windows
        ;;
    all)
        if [ "$NATIVE_DARWIN" = "1" ]; then
            assemble_darwin_app
        else
            build_darwin_via_docker
        fi
        [ "$DO_ZIP" = "1" ] && zip_output darwin
        build_windows_via_docker
        [ "$DO_ZIP" = "1" ] && zip_output windows
        ;;
esac
