#!/usr/bin/env bash
# =============================================================================
#  Construit "OneAir-Windows/" basé sur le client Dofus 2.68.0.0 windows natif
#  (téléchargé via cytrus-downloader -platform windows). Pas de Wine — c'est
#  un client Adobe AIR captive 64-bit Windows que l'utilisateur lancera
#  directement sur son poste.
#
#  Sortie : OneAir-Windows/ (dossier prêt à zipper et servir)
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
SRC_BASE="$SCRIPT_DIR/dofus-windows-2.68/dofus/2.68.0.0/windows"
APP_DIR="$ROOT_DIR/OneAir-Windows"

# On part du build win64 (Dofus.exe + Adobe AIR runtime captive 64-bit).
# main/ contient les assets (DofusInvoker.swf, config.xml, content/) qu'on
# fusionne flat à côté du Dofus.exe — c'est ce qu'Ankama Launcher fait à
# l'install via Cytrus.
[ -d "$SRC_BASE/win64" ] || { echo "ERREUR : $SRC_BASE/win64 introuvable. Lance d'abord cytrus-downloader." >&2; exit 1; }
[ -d "$SRC_BASE/main" ]  || { echo "ERREUR : $SRC_BASE/main introuvable." >&2; exit 1; }

# Charge .env (SERVER_HOST + PUBLIC_AUTH_PORT + SERVER_DISPLAY_NAME)
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a
: "${SERVER_HOST:=127.0.0.1}"
: "${PUBLIC_AUTH_PORT:=5555}"
: "${SERVER_DISPLAY_NAME:=OneAir}"
: "${DOFUS_LANG:=fr}"

echo "==> Bundle cible : $APP_DIR"
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"

# Étape 1 : copier win64/* (Dofus.exe + Adobe AIR/ + META-INF/ + icon/ + ...)
echo "==> Copie win64/ ($(du -sh "$SRC_BASE/win64" | cut -f1))"
cp -R "$SRC_BASE/win64/." "$APP_DIR/"

# Étape 2 : merger main/ flat à la racine (DofusInvoker.swf, config.xml, content/, ...)
echo "==> Merge main/ → bundle (assets + config.xml + SWF)"
cp -R "$SRC_BASE/main/." "$APP_DIR/"

# Étape 3 : merger lang_${DOFUS_LANG}/ flat à la racine
LANG_DIR="$SRC_BASE/lang_${DOFUS_LANG}"
if [ -d "$LANG_DIR" ]; then
    echo "==> Merge lang_${DOFUS_LANG}/ → bundle"
    cp -R "$LANG_DIR/." "$APP_DIR/"
else
    echo "AVERTISSEMENT : lang_${DOFUS_LANG} absent"
fi

# Étape 4 : config.xml — patch connection.host avec valeurs .env, vide la signature.
# Le SWF Giny patché tourne BUILD_TYPE=DEBUG (5) > INTERNAL (4), donc
# Signature.verify() est shortcircuité — la valeur peut être vide.
GINY_CFG="$SCRIPT_DIR/giny-config.xml"
CONFIG_XML="$APP_DIR/config.xml"
if [ -f "$GINY_CFG" ]; then
    echo "==> Override config.xml par giny-config.xml"
    cp "$GINY_CFG" "$CONFIG_XML"
fi
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

# Étape 5 : SWF Giny patché (le même fichier que pour macOS)
PATCHED_SWF="$SCRIPT_DIR/DofusInvoker-patched.swf"
if [ -f "$PATCHED_SWF" ]; then
    echo "==> Remplace DofusInvoker.swf par la version Giny DEBUG-patched"
    cp "$PATCHED_SWF" "$APP_DIR/DofusInvoker.swf"
    rm -f "$APP_DIR/META-INF/signatures.xml"
    rm -f "$APP_DIR/META-INF/AIR/hash"
fi

# Étape 6 : credentials.json (lu par ZaapConnectionHelper.connect en DEBUG)
echo "==> Génère credentials.json (zaap params Giny standard)"
cat > "$APP_DIR/credentials.json" <<'JSON'
{"port":4242,"name":"dofus","release":"main","instanceId":1,"hash":"464e4625-67f1-4706-985c-8358f8661e3c"}
JSON

# Étape 7 : zaap-server.exe (cross-compilé Go)
ZAAP_EXE="$SCRIPT_DIR/zaap-server/zaap-server.exe"
if [ ! -x "$ZAAP_EXE" ]; then
    echo "==> Cross-compile zaap-server.exe"
    (cd "$SCRIPT_DIR/zaap-server" && GOOS=windows GOARCH=amd64 go build -o zaap-server.exe .)
fi
cp "$ZAAP_EXE" "$APP_DIR/zaap-server.exe"

# Étape 8 : OneAirLauncher.exe (build C# WPF self-contained single-file).
# On RENOMME Dofus.exe → dofus-real.exe, puis on dépose le launcher sous
# le nom Dofus.exe — c'est le même hack qu'on fait sur macOS où le binaire
# Swift remplace Contents/MacOS/Dofus et l'original devient dofus-real.
LAUNCHER_DIR="$SCRIPT_DIR/OneAirLauncher-win"
LAUNCHER_EXE="$LAUNCHER_DIR/bin/Release/net8.0-windows/win-x64/publish/OneAirLauncher.exe"
if [ ! -x "$LAUNCHER_EXE" ]; then
    echo "==> Build OneAirLauncher.exe (dotnet publish)"
    if ! command -v dotnet >/dev/null 2>&1 && [ -x /opt/dotnet/dotnet ]; then
        export PATH="/opt/dotnet:$PATH"
    fi
    (cd "$LAUNCHER_DIR" && dotnet publish -c Release -r win-x64 --self-contained \
        -p:PublishSingleFile=true -p:EnableWindowsTargeting=true)
fi
echo "==> Intégration du launcher Windows"
mv "$APP_DIR/Dofus.exe" "$APP_DIR/dofus-real.exe"
cp "$LAUNCHER_EXE" "$APP_DIR/Dofus.exe"

# Étape 9 : nettoyage des debug AIR / meta inutilement publics
rm -f "$APP_DIR/META-INF/AIR/debug" 2>/dev/null || true

echo
echo "==> Bundle généré : $APP_DIR"
echo "    Taille        : $(du -sh "$APP_DIR" | cut -f1)"
echo "    Contenu top   :"
ls -la "$APP_DIR" | head -25
echo
echo "    Lancement     : double-clic sur $APP_DIR/Dofus.exe (Windows)"
echo "    Logs joueur   : %LOCALAPPDATA%\\OneAir\\Logs\\launcher.log"
echo "    Logs zaap     : %LOCALAPPDATA%\\OneAir\\Logs\\zaap-server.log"
