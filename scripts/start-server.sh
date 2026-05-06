#!/usr/bin/env bash
# =============================================================================
#  Démarre la stack OneAir / Giny via docker compose.
#  Wrapper pratique : équivalent à `docker compose up -d --build`.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
cd "$ROOT_DIR"

[ -f .env ] || { echo "ERREUR : .env manquant. Copie .env.example en .env d'abord." >&2; exit 1; }

# Charge .env pour afficher les ports en feedback
set -a; . ./.env; set +a
: "${PUBLIC_AUTH_PORT:=5555}"
: "${PUBLIC_WORLD_PORT:=5556}"
: "${WEB_PORT:=3000}"

echo "==> docker compose up -d --build"
docker compose up -d --build

echo
echo "==> Attente que giny-auth soit prêt…"
i=0
until docker logs giny-auth 2>&1 | grep -q "(Auth) Server started" ; do
    i=$((i+1))
    [ "$i" -gt 60 ] && { echo "ERREUR : auth ne démarre pas. docker logs giny-auth"; exit 2; }
    sleep 2
done
echo "    auth ready."

echo
echo "==> Stack OneAir démarrée :"
echo "    auth         : ${SERVER_HOST:-127.0.0.1}:${PUBLIC_AUTH_PORT}"
echo "    world        : ${SERVER_HOST:-127.0.0.1}:${PUBLIC_WORLD_PORT}"
echo "    Site web     : http://localhost:${WEB_PORT}"
echo
echo "    Stop  : ./scripts/stop-server.sh"
echo "    Logs  : docker compose logs -f auth world"
