#!/usr/bin/env bash
# Lance Giny.DatabaseSynchronizer dans Docker contre la stack MySQL en cours.
#
# Pré-requis :
#   - docker compose stack OneAir up (au moins giny-mysql)
#   - dossier ./client/build/OneAir.app/Contents/Resources/data accessible
#
# Variables (overridables) :
#   DATA_DIR     : chemin du dossier "data" du client (par défaut OneAir.app)
#   DB_HOST      : hôte MySQL (vu depuis le container, par défaut "mysql")
#   DB_NAME      : nom de la base world (par défaut giny_world)
#   DB_USER      : utilisateur MySQL (par défaut root)
#   DB_PASSWORD  : mot de passe (lu depuis ../.env si dispo)
#
# Usage :
#   ./scripts/giny-d2o-sync/run.sh
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"

# Charge .env si dispo (pour MYSQL_ROOT_PASSWORD)
if [ -f "$ROOT_DIR/.env" ]; then
    set -a; . "$ROOT_DIR/.env"; set +a
fi

: "${DATA_DIR:=$ROOT_DIR/client/build/OneAir.app/Contents/Resources}"
: "${DB_HOST:=mysql}"
: "${DB_NAME:=${MYSQL_WORLD_DB:-giny_world}}"
: "${DB_USER:=root}"
: "${DB_PASSWORD:=${MYSQL_ROOT_PASSWORD:-}}"
: "${NETWORK:=dofus-giny_giny-net}"

if [ ! -d "$DATA_DIR/data/common" ]; then
    echo "❌ $DATA_DIR/data/common introuvable" >&2
    echo "   Le dossier doit contenir data/common/*.d2o et data/i18n/i18n_fr.d2i" >&2
    exit 1
fi

if [ -z "$DB_PASSWORD" ]; then
    echo "❌ DB_PASSWORD vide. Définis MYSQL_ROOT_PASSWORD dans .env ou DB_PASSWORD." >&2
    exit 1
fi

# Vérifie que la stack est démarrée et que mysql répond
if ! docker network inspect "$NETWORK" >/dev/null 2>&1; then
    echo "❌ Réseau Docker '$NETWORK' introuvable. Lance \`docker compose up -d\` d'abord." >&2
    exit 1
fi

# Build l'image (cache si possible)
echo "▸ Build de l'image giny-d2o-sync (peut prendre quelques minutes la 1ère fois)..."
docker build -t giny-d2o-sync:latest "$SCRIPT_DIR"

# Lance la sync.
# ⚠ Le synchronizer DROP les tables ciblées avant de les recréer.
echo
echo "▸ Lancement du sync (les tables monsters/npcs/dungeons/items/spells/etc."
echo "  vont être DROP puis re-remplies depuis tes .d2o locaux)."
echo
read -r -p "Confirmer ? [y/N] " confirm
case "$confirm" in y|Y|yes|YES) ;; *) echo "Annulé."; exit 0 ;; esac

docker run --rm -i \
    --network "$NETWORK" \
    -v "$DATA_DIR:/data:ro" \
    -e DB_HOST="$DB_HOST" \
    -e DB_NAME="$DB_NAME" \
    -e DB_USER="$DB_USER" \
    -e DB_PASSWORD="$DB_PASSWORD" \
    giny-d2o-sync:latest

echo
echo "✓ Sync terminée. Les nouvelles données sont en DB."
echo "  Restart auth/world pour qu'ils rechargent les Records :"
echo "  docker compose restart auth world"
