#!/usr/bin/env bash
# =============================================================================
#  build-docker-windows.sh — single-script qui produit OneAir-Windows/ depuis
#  Docker. Aucune toolchain hôte requise. Le script :
#
#    1. build l'image `oneair-builder-windows` si absente / Dockerfile modifié
#    2. lance build-app-windows.sh dans le container (cross-compile WPF + Go)
#    3. zippe le bundle final dans dist/OneAir-Windows.zip pour le download web
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
IMAGE_TAG="${ONEAIR_BUILDER_WIN_IMAGE:-oneair-builder-windows:latest}"
DOCKERFILE="$SCRIPT_DIR/Dockerfile.windows"
DIST_DIR="$ROOT_DIR/dist"
APP_DIR="$ROOT_DIR/OneAir-Windows"
ZIP_OUT="$DIST_DIR/OneAir-Windows.zip"

[ -f "$DOCKERFILE" ] || { echo "ERREUR : $DOCKERFILE introuvable." >&2; exit 1; }
command -v docker >/dev/null || { echo "ERREUR : docker absent." >&2; exit 1; }

# ---- Étape 1 : image builder Docker -----------------------------------------
echo "==> [1/4] Image $IMAGE_TAG (cache si déjà à jour)"
docker build --pull -f "$DOCKERFILE" -t "$IMAGE_TAG" "$SCRIPT_DIR"

# ---- Étape 2 : fetch des assets Dofus officiels via cytrus-downloader -------
# Le bundle final embarque le client AIR captif Ankama 2.68.0.0 windows. Si
# absent, on le récupère depuis cytrus.cdn.ankama.com (build de l'outil Go
# loonaire/Cytrus-downloader dans le container, fetch ~5 GB).
ASSETS_DIR="$SCRIPT_DIR/dofus-windows-2.68"
ASSETS_TARGET="$ASSETS_DIR/dofus/2.68.0.0/windows/main"
if [ ! -d "$ASSETS_TARGET" ]; then
    echo "==> [2/4] Fetch Dofus 2.68 windows via cytrus-downloader (~5 GB, 1ère fois)"
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
            /tmp/cytrus -game dofus -platform windows \
                -release main -version 6.0_2.68.0.0 -outdir /out/
        '
else
    echo "==> [2/4] Assets Dofus 2.68 windows déjà présents → skip fetch"
fi

# ---- Étape 3 : assembly OneAir-Windows/ dans le container -------------------
echo "==> [3/4] Assembly OneAir-Windows/"
docker run --rm \
    -v "$ROOT_DIR:/work" \
    -v "oneair-go-cache:/tmp/go-cache" \
    -v "oneair-go-mod:/tmp/go-mod" \
    -e SERVER_HOST="${SERVER_HOST:-}" \
    -e PUBLIC_AUTH_PORT="${PUBLIC_AUTH_PORT:-}" \
    -e SERVER_DISPLAY_NAME="${SERVER_DISPLAY_NAME:-}" \
    -e DOFUS_LANG="${DOFUS_LANG:-fr}" \
    -w /work \
    "$IMAGE_TAG" \
    bash /work/client/build-app-windows.sh

# ---- Étape 4 : zip → dist/ pour download web --------------------------------
mkdir -p "$DIST_DIR"
echo "==> [4/4] Zip → $ZIP_OUT (Store, pas de compression)"
docker run --rm -v "$ROOT_DIR:/work" -w /work "$IMAGE_TAG" bash -c "
    cd /work && rm -f dist/OneAir-Windows.zip.tmp
    zip -ryq0 dist/OneAir-Windows.zip.tmp OneAir-Windows
    mv dist/OneAir-Windows.zip.tmp dist/OneAir-Windows.zip
    echo \"    Zip : \$(du -h dist/OneAir-Windows.zip | cut -f1)\"
"

echo
echo "==> ✓ OneAir-Windows/ : $APP_DIR ($(du -sh "$APP_DIR" | cut -f1))"
echo "    Zip téléchargeable : $ZIP_OUT ($(du -h "$ZIP_OUT" | cut -f1))"
echo "    /download/windows servira ce fichier directement"
