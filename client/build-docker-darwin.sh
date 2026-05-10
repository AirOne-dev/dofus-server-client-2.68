#!/usr/bin/env bash
# =============================================================================
#  build-docker-darwin.sh — single-script qui produit OneAir.app depuis Docker.
#  Aucune toolchain hôte requise. Le script :
#
#    1. build l'image `oneair-builder-darwin` si absente / Dockerfile modifié
#    2. fetch les assets Dofus 2.68 darwin via cytrus-downloader si absents
#    3. assemble un Swift SDK Darwin si pas déjà en cache (~3-5 min, 1ère fois)
#    4. lance build-app-darwin.sh dans le container avec le SDK monté
#    5. zippe le bundle final dans dist/OneAir-MacOS.zip pour le download web
#
#  Pour skip le cross-compile Swift et utiliser uniquement le binaire
#  pré-buildé : ONEAIR_SKIP_SWIFT_SDK=1 ./client/build-docker-darwin.sh
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
IMAGE_TAG="${ONEAIR_BUILDER_MAC_IMAGE:-oneair-builder-darwin:latest}"
DOCKERFILE="$SCRIPT_DIR/Dockerfile.darwin"
SDK_BUNDLE="$SCRIPT_DIR/.cache/darwin.artifactbundle.zip"
DIST_DIR="$ROOT_DIR/dist"
APP_DIR="$ROOT_DIR/OneAir.app"
ZIP_OUT="$DIST_DIR/OneAir-MacOS.zip"

[ -f "$DOCKERFILE" ] || { echo "ERREUR : $DOCKERFILE introuvable." >&2; exit 1; }
command -v docker >/dev/null || { echo "ERREUR : docker absent." >&2; exit 1; }

# ---- Étape 1 : image builder Docker -----------------------------------------
echo "==> [1/5] Image $IMAGE_TAG (cache si déjà à jour)"
docker build --pull -f "$DOCKERFILE" -t "$IMAGE_TAG" "$SCRIPT_DIR"

# ---- Étape 2 : fetch des assets Dofus officiels via cytrus-downloader -------
ASSETS_DIR="$SCRIPT_DIR/dofus-darwin-2.68"
ASSETS_TARGET="$ASSETS_DIR/dofus/2.68.0.0/darwin/main"
if [ ! -d "$ASSETS_TARGET" ]; then
    echo "==> [2/5] Fetch Dofus 2.68 darwin via cytrus-downloader (~5 GB, 1ère fois)"
    mkdir -p "$ASSETS_DIR"
    docker run --rm \
        -v "$ASSETS_DIR:/out" \
        -v "oneair-go-cache:/tmp/go-cache" \
        -v "oneair-go-mod:/tmp/go-mod" \
        -e GOCACHE=/tmp/go-cache -e GOMODCACHE=/tmp/go-mod \
        "$IMAGE_TAG" bash -ce '
            cd /tmp && rm -rf cytrus-src
            git clone --depth 1 https://github.com/loonaire/Cytrus-downloader.git cytrus-src
            cd cytrus-src && go build -o /tmp/cytrus .
            /tmp/cytrus -game dofus -platform darwin \
                -release main -version 6.0_2.68.0.0 -outdir /out/
        '
else
    echo "==> [2/5] Assets Dofus 2.68 darwin déjà présents → skip fetch"
fi

# ---- Étape 3 : Swift SDK Darwin (cross-compile launcher Swift) --------------
SDK_MOUNT_OPTS=()
if [ "${ONEAIR_SKIP_SWIFT_SDK:-0}" = "1" ]; then
    echo "==> [3/5] Swift SDK Darwin SKIP (ONEAIR_SKIP_SWIFT_SDK=1)"
    echo "    Le launcher sera repris depuis client/OneAirLauncher/OneAirLauncher (pré-buildé)"
elif [ ! -f "$SDK_BUNDLE" ]; then
    echo "==> [3/5] Swift SDK Darwin absent — assemblage (1ère fois, ~3-5 min, ~1.5 GB download)"
    bash "$SCRIPT_DIR/make-darwin-sdk.sh"
fi
if [ -f "$SDK_BUNDLE" ]; then
    echo "==> Swift SDK Darwin prêt ($(du -h "$SDK_BUNDLE" | cut -f1)) → cross-compile activé"
    SDK_MOUNT_OPTS=(-v "$SCRIPT_DIR/.cache:/sdk-cache:ro" -v "oneair-swiftpm:/root/.swiftpm")
fi

# ---- Étape 4 : assembly OneAir.app dans le container ------------------------
echo "==> [4/5] Assembly OneAir.app"
docker run --rm \
    -v "$ROOT_DIR:/work" \
    -v "oneair-go-cache:/tmp/go-cache" \
    -v "oneair-go-mod:/tmp/go-mod" \
    "${SDK_MOUNT_OPTS[@]}" \
    -e SERVER_HOST="${SERVER_HOST:-}" \
    -e PUBLIC_AUTH_PORT="${PUBLIC_AUTH_PORT:-}" \
    -e SERVER_DISPLAY_NAME="${SERVER_DISPLAY_NAME:-}" \
    -e DOFUS_LANG="${DOFUS_LANG:-fr}" \
    -e ZAAP_GOARCH="${ZAAP_GOARCH:-arm64}" \
    -w /work \
    "$IMAGE_TAG" \
    bash /work/client/build-app-darwin.sh

# ---- Étape 5 : zip → dist/ pour download web --------------------------------
mkdir -p "$DIST_DIR"
echo "==> [5/5] Zip → $ZIP_OUT (Store, pas de compression)"
# zip -y préserve les symlinks (Adobe AIR.framework en a quelques-uns).
# On run zip dans le container pour avoir le binaire et éviter les
# différences de version host → container.
docker run --rm -v "$ROOT_DIR:/work" -w /work "$IMAGE_TAG" bash -c "
    apt-get update -qq && apt-get install -y -qq zip >/dev/null 2>&1
    cd /work && rm -f dist/OneAir-MacOS.zip.tmp
    zip -ryq0 dist/OneAir-MacOS.zip.tmp OneAir.app
    mv dist/OneAir-MacOS.zip.tmp dist/OneAir-MacOS.zip
    echo \"    Zip : \$(du -h dist/OneAir-MacOS.zip | cut -f1)\"
"

echo
echo "==> ✓ OneAir.app : $APP_DIR ($(du -sh "$APP_DIR" | cut -f1))"
echo "    Zip téléchargeable : $ZIP_OUT ($(du -h "$ZIP_OUT" | cut -f1))"
echo "    /download/macos servira ce fichier directement (pas de zip à la volée)"
