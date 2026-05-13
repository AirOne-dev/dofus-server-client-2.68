#!/usr/bin/env bash
# =============================================================================
#  save-world.sh — flush l'état mémoire du World vers la DB puis dump SQL.
#
#  Usage :
#    ./scripts/save-world.sh                     # flush + dump (recommandé)
#    ./scripts/save-world.sh --no-flush          # dump uniquement
#
#  Sans flush, le dump perd l'activité depuis le dernier auto-save
#  (SaveIntervalMinutes = 5 par défaut). À lancer SYSTÉMATIQUEMENT avant un
#  docker compose down / restart.
#
#  Auth : utilise les credentials admin in-game (Role >= 5) via /api/public/login.
#  Crée /tmp/oneair-cookies.txt pour ne pas re-loguer à chaque call.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
[ -f "$ROOT_DIR/.env" ] && set -a && . "$ROOT_DIR/.env" && set +a

BASE_URL="${ONEAIR_ADMIN_URL:-http://localhost}"
COOKIE_JAR="/tmp/oneair-cookies.txt"

ACTION="flush_and_trigger"
if [ "${1:-}" = "--no-flush" ]; then
    ACTION="trigger"
fi

if [ -z "${ONEAIR_ADMIN_USER:-}" ] || [ -z "${ONEAIR_ADMIN_PASSWORD:-}" ]; then
    echo "Définir ONEAIR_ADMIN_USER et ONEAIR_ADMIN_PASSWORD (compte Role>=5) dans .env" >&2
    exit 1
fi

# Login (ré-utilise le cookie si encore valide).
if [ ! -f "$COOKIE_JAR" ] || ! curl -s -b "$COOKIE_JAR" "$BASE_URL/api/status" | grep -q '"'; then
    echo "→ Login admin…"
    curl -s -c "$COOKIE_JAR" -X POST "$BASE_URL/api/public/login" \
        -H "Content-Type: application/json" \
        -d "{\"username\":\"$ONEAIR_ADMIN_USER\",\"password\":\"$ONEAIR_ADMIN_PASSWORD\"}" \
        > /dev/null
fi

echo "→ ${ACTION} en cours (peut prendre 5-15s)…"
RESPONSE=$(curl -s -b "$COOKIE_JAR" -X POST "$BASE_URL/api/backups" \
    -H "Content-Type: application/json" \
    -d "{\"action\":\"${ACTION}\"}")

if echo "$RESPONSE" | grep -q '"ok"'; then
    echo "✓ Backup créé (avec flush=${ACTION:0:1})."
    ls -lt "$ROOT_DIR/server/backups/" 2>/dev/null | head -3 | tail -1
else
    echo "✗ Échec : $RESPONSE" >&2
    exit 1
fi
