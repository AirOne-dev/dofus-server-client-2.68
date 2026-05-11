#!/usr/bin/env bash
# =============================================================================
#  Construit "OneAir.app" basé sur le client Dofus 2.73.3.14 darwin natif
#  (téléchargé depuis Cytrus avec -platform darwin). Pas de Wine, juste Rosetta 2
#  pour le binaire x86_64 → 3-5x plus rapide que la version Windows via Wine.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"

# Détection environnement : Mac natif (utilise plutil/codesign/xattr) vs
# Linux/Docker (utilise python plistlib + rcodesign, skip xattr). Permet
# d'utiliser ce même script depuis l'image oneair-builder-darwin.
case "${OSTYPE:-$(uname -s)}" in
    darwin*|Darwin) IS_MACOS=1 ;;
    *)              IS_MACOS=0 ;;
esac

# Auto-détection de la version client. On préfère 2.68 (proto compatible avec
# Giny). Si absent, fallback sur 2.73 (qui ne marchera pas côté connexion mais
# permet de tester le pipeline launcher).
if [ -d "$SCRIPT_DIR/dofus-darwin-2.68/dofus/2.68.0.0/darwin/main/Dofus.app" ]; then
    SRC_DIR="$SCRIPT_DIR/dofus-darwin-2.68/dofus/2.68.0.0/darwin"
    echo "==> Client cible : Dofus 2.68.0.0 darwin (proto compatible Giny)"
elif [ -d "$SCRIPT_DIR/dofus-darwin/dofus/2.73.3.14/darwin/main/Dofus.app" ]; then
    SRC_DIR="$SCRIPT_DIR/dofus-darwin/dofus/2.73.3.14/darwin"
    echo "==> Client cible : Dofus 2.73.3.14 darwin (PROTO INCOMPATIBLE Giny)"
else
    echo "ERREUR : aucun client darwin trouvé dans $SCRIPT_DIR" >&2
    exit 1
fi
APP_DIR="$ROOT_DIR/OneAir.app"

[ -d "$SRC_DIR/main/Dofus.app" ] || { echo "ERREUR : $SRC_DIR/main/Dofus.app introuvable." >&2; exit 1; }

# Charge .env (SERVER_HOST + PUBLIC_AUTH_PORT + SERVER_DISPLAY_NAME)
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a
: "${SERVER_HOST:=127.0.0.1}"
: "${PUBLIC_AUTH_PORT:=5555}"
: "${SERVER_DISPLAY_NAME:=OneAir}"

echo "==> Bundle cible : $APP_DIR"
rm -rf "$APP_DIR"

# Étape 1 : copier le main/Dofus.app comme base
echo "==> Copie main/Dofus.app ($(du -sh "$SRC_DIR/main/Dofus.app" | cut -f1))"
cp -R "$SRC_DIR/main/Dofus.app" "$APP_DIR"
# Override config.xml avec celui de Giny (signature Ankama valide pour 127.0.0.1)
GINY_CFG="$SCRIPT_DIR/giny-config.xml"
[ -f "$GINY_CFG" ] && cp "$GINY_CFG" "$APP_DIR/Contents/Resources/config.xml"

# Étape 2 : merger les fragments lang_fr (gfx + i18n + config-lang-fr.xml)
LANG="${DOFUS_LANG:-fr}"
LANG_DIR="$SRC_DIR/lang_${LANG}/Dofus.app/Contents/Resources"
if [ -d "$LANG_DIR" ]; then
    echo "==> Merge lang_${LANG} dans Resources/"
    /usr/bin/rsync -a "$LANG_DIR/" "$APP_DIR/Contents/Resources/"
else
    echo "AVERTISSEMENT : pas de fragment lang_${LANG}"
fi

# Étape 3 : config.xml — patch connection.host avec les valeurs du .env.
# Le SWF Giny patché tourne en BUILD_TYPE=DEBUG (5), donc la branche
# Signature.verify() (BUILD_TYPE < INTERNAL=4) est shortcircuitée → on peut
# mettre n'importe quel host:ip:port et la signature peut être vide.
CONFIG_XML="$APP_DIR/Contents/Resources/config.xml"
if [ -f "$CONFIG_XML" ]; then
    echo "==> Patch connection.host → ${SERVER_DISPLAY_NAME}:${SERVER_HOST}:${PUBLIC_AUTH_PORT}"
    /usr/bin/python3 - "$CONFIG_XML" "$SERVER_DISPLAY_NAME" "$SERVER_HOST" "$PUBLIC_AUTH_PORT" <<'PY'
import re, sys, pathlib
path, name, host, port = sys.argv[1:]
p = pathlib.Path(path)
text = p.read_text(encoding="utf-8", errors="ignore")
text = re.sub(
    r'<entry key="connection\.host">[^<]*</entry>',
    f'<entry key="connection.host">{name}:{host}:{port}</entry>',
    text)
text = re.sub(
    r'<entry key="connection\.host\.signature">[^<]*</entry>',
    '<entry key="connection.host.signature"></entry>',
    text)
p.write_text(text, encoding="utf-8")
PY
fi

# Étape 3 bis : DofusInvoker.swf patché Giny avec BuildInfos.BUILD_TYPE = DEBUG (5).
# Le SWF Giny lit alors {Contents/Resources}/credentials.json à la place de
# CommandLineArguments — ce qui résout le bug AIR/Wine où NativeApplication
# ne reçoit pas l'InvokeEvent. credentials.json contient les paramètres
# habituellement passés à Dofus.exe par Giny.Uplauncher.
PATCHED_SWF="$SCRIPT_DIR/DofusInvoker-patched.swf"
if [ -f "$PATCHED_SWF" ]; then
    echo "==> Remplace DofusInvoker.swf par la version Giny DEBUG-patched"
    /bin/cp "$PATCHED_SWF" "$APP_DIR/Contents/Resources/DofusInvoker.swf"
    /bin/rm -f "$APP_DIR/Contents/Resources/META-INF/signatures.xml" \
               "$APP_DIR/Contents/Resources/META-INF/AIR/hash"
fi

# Étape 3 ter : credentials.json (lu par ZaapConnectionHelper.connect en DEBUG)
echo "==> Génère credentials.json (zaap params Giny standard)"
cat > "$APP_DIR/Contents/Resources/credentials.json" <<'JSON'
{"port":4242,"name":"dofus","release":"main","instanceId":1,"hash":"464e4625-67f1-4706-985c-8358f8661e3c"}
JSON

# Étape 4a : Restaurer les symlinks Adobe AIR.framework perdus par cytrus-downloader
FW="$APP_DIR/Contents/Frameworks/Adobe AIR.framework"
if [ -d "$FW/Versions/1.0" ]; then
    (cd "$FW/Versions" && [ -L Current ] || ln -sfn 1.0 Current)
    (cd "$FW" && [ -L "Adobe AIR" ] || ln -sfn "Versions/Current/Adobe AIR" "Adobe AIR")
    (cd "$FW" && [ -L Resources ] || ln -sfn "Versions/Current/Resources" Resources)
    (cd "$FW/Versions/1.0" && [ -L "Adobe AIR_64" ] || ln -sfn "Adobe AIR" "Adobe AIR_64")
fi

# Étape 4b : intégrer le pré-launcher Swift + ses assets (logo / fond portail)
LAUNCHER_BIN="$SCRIPT_DIR/OneAirLauncher/OneAirLauncher"
LAUNCHER_ASSETS="$SCRIPT_DIR/OneAirLauncher/Assets"

# Cross-compile depuis Linux SI un Swift SDK Darwin est dispo. Le SDK est
# soit déjà installé dans le container, soit présent en /sdk-cache (monté
# par build-docker-darwin.sh quand client/.cache/darwin.artifactbundle.zip
# existe). Sans SDK, on utilise le binaire arm64 déjà committed.
if [ "$IS_MACOS" -eq 0 ] && command -v swift >/dev/null 2>&1 \
        && [ -f "$SCRIPT_DIR/OneAirLauncher/Package.swift" ]; then
    if ! swift sdk list 2>/dev/null | grep -q '^darwin' \
            && [ -f /sdk-cache/darwin.artifactbundle.zip ]; then
        echo "==> Install Swift SDK Darwin (1ère fois dans ce container)"
        swift sdk install /sdk-cache/darwin.artifactbundle.zip 2>&1 | tail -3
    fi
    if swift sdk list 2>/dev/null | grep -q '^darwin'; then
        echo "==> Cross-compile OneAirLauncher (swift build --swift-sdk arm64-apple-macosx)"
        (cd "$SCRIPT_DIR/OneAirLauncher" && \
            swift build --swift-sdk arm64-apple-macosx -c release 2>&1 | tail -5)
        BUILT="$SCRIPT_DIR/OneAirLauncher/.build/arm64-apple-macosx/release/OneAirLauncher"
        if [ -f "$BUILT" ]; then
            LAUNCHER_BIN="$BUILT"
            echo "    → $LAUNCHER_BIN ($(du -h "$BUILT" | cut -f1))"
        else
            echo "    AVERTISSEMENT : compile a échoué, fallback sur binaire pré-buildé"
        fi
    fi
fi

# Sur Linux le binaire Mach-O n'est pas exécutable (pas le bit +x ou pas le
# bon arch) — on accepte tout fichier régulier non vide.
launcher_ok=0
if [ "$IS_MACOS" -eq 1 ]; then
    [ -x "$LAUNCHER_BIN" ] && launcher_ok=1
else
    [ -s "$LAUNCHER_BIN" ] && launcher_ok=1
fi
if [ "$launcher_ok" -eq 1 ]; then
    echo "==> Intégration du pré-client Swift"
    /bin/mv "$APP_DIR/Contents/MacOS/Dofus" "$APP_DIR/Contents/MacOS/dofus-real"
    /bin/cp "$LAUNCHER_BIN" "$APP_DIR/Contents/MacOS/Dofus"
    /bin/chmod +x "$APP_DIR/Contents/MacOS/Dofus" "$APP_DIR/Contents/MacOS/dofus-real"
    if [ -d "$LAUNCHER_ASSETS" ]; then
        /bin/cp "$LAUNCHER_ASSETS"/portal-bg.jpg "$APP_DIR/Contents/Resources/" 2>/dev/null
        /bin/cp "$LAUNCHER_ASSETS"/logo.png       "$APP_DIR/Contents/Resources/" 2>/dev/null
        echo "    + assets logo.png + portal-bg.jpg copiés"
    fi
else
    echo "AVERTISSEMENT : $LAUNCHER_BIN absent — recompile avec :"
    echo "  cd client/OneAirLauncher && swiftc -O -o OneAirLauncher OneAirLauncher.swift -framework AppKit -framework Network -framework CoreImage"
fi

# Étape 4b bis : intégrer le faux Zaap server (DivaZaap recompilé).
# Toujours rebuild : le cache Go rend un no-op quasi instantané, et tester
# uniquement la présence/magic Mach-O laissait passer des binaires obsolètes
# (ex: main.go gagne un flag → ancien binaire rejette l'arg → jeu ne se lance
# pas). Sur Linux on cross-compile (GOOS=darwin). Défaut arm64 (Apple
# Silicon), overridable via $ZAAP_GOARCH (= "amd64" pour Intel).
ZAAP_BIN="$SCRIPT_DIR/zaap-server/zaap-server"
ZAAP_GOARCH="${ZAAP_GOARCH:-arm64}"
if [ "$IS_MACOS" -eq 0 ] || [ ! -f "$ZAAP_BIN" ]; then
    echo "==> Cross-compile zaap-server pour darwin/${ZAAP_GOARCH}"
    (cd "$SCRIPT_DIR/zaap-server" && \
        GOOS=darwin GOARCH="$ZAAP_GOARCH" CGO_ENABLED=0 \
        go build -trimpath -ldflags='-s -w' -o zaap-server .)
else
    echo "==> Rebuild zaap-server (natif macOS, cache Go)"
    (cd "$SCRIPT_DIR/zaap-server" && CGO_ENABLED=0 \
        go build -trimpath -ldflags='-s -w' -o zaap-server .)
fi
if [ -s "$ZAAP_BIN" ]; then
    echo "==> Intégration de zaap-server (DivaZaap)"
    /bin/cp "$ZAAP_BIN" "$APP_DIR/Contents/MacOS/zaap-server"
    chmod +x "$APP_DIR/Contents/MacOS/zaap-server"
else
    echo "AVERTISSEMENT : $ZAAP_BIN absent — recompile avec :"
    echo "  cd client/zaap-server && go build -o zaap-server ."
fi

# Étape 4c : Info.plist customisé.
# Sur Mac on utilise plutil (binaire Apple). Sur Linux on passe par plistlib
# (Python stdlib) — gère les .plist binaires ET XML.
PLIST="$APP_DIR/Contents/Info.plist"
if [ "$IS_MACOS" -eq 1 ]; then
    /usr/bin/plutil -replace CFBundleName -string "OneAir" "$PLIST" 2>/dev/null || true
    /usr/bin/plutil -replace CFBundleDisplayName -string "OneAir" "$PLIST" 2>/dev/null || true
    /usr/bin/plutil -replace CFBundleIdentifier -string "local.oneair" "$PLIST" 2>/dev/null || true
    /usr/bin/plutil -replace CFBundleExecutable -string "Dofus" "$PLIST" 2>/dev/null || true
else
    /usr/bin/python3 - "$PLIST" <<'PY' || true
import plistlib, sys
path = sys.argv[1]
with open(path, "rb") as f:
    data = plistlib.load(f)
data["CFBundleName"] = "OneAir"
data["CFBundleDisplayName"] = "OneAir"
data["CFBundleIdentifier"] = "local.oneair"
data["CFBundleExecutable"] = "Dofus"
with open(path, "wb") as f:
    plistlib.dump(data, f)
PY
fi

# Étape 5 : nettoyer xattrs (Mac uniquement, Linux n'a rien à nettoyer) +
# signature ad-hoc. Sur Linux on passe par rcodesign (impl. Rust open-source).
if [ "$IS_MACOS" -eq 1 ]; then
    xattr -drc "$APP_DIR" 2>/dev/null || true
    echo "==> Signature ad-hoc (codesign)"
    codesign --force --deep -s - "$APP_DIR" 2>&1 | tail -2 || true
else
    if command -v rcodesign >/dev/null 2>&1; then
        echo "==> Signature ad-hoc (rcodesign, depuis Linux)"
        # rcodesign signe en mode ad-hoc par défaut quand aucune clé/cert
        # n'est fourni (équivalent de `codesign -s -`). Pas besoin du flag
        # explicite "adhoc" qui n'existe pas dans rcodesign 0.29.
        rcodesign sign "$APP_DIR" 2>&1 | tail -5 || true
    else
        echo "AVERTISSEMENT : rcodesign absent — bundle non signé. Installer :"
        echo "  curl -fsSL .../apple-codesign-...-x86_64-unknown-linux-musl.tar.gz | tar xz"
    fi
fi

echo
echo "==> Bundle généré : $APP_DIR"
echo "    Taille        : $(du -sh "$APP_DIR" | cut -f1)"
echo "    Lancement     : open \"$APP_DIR\"  ou double-clic dans Finder"
echo
echo "    Logs Dofus    : ~/Library/Logs/OneAir/dofus.log (si activé)"
