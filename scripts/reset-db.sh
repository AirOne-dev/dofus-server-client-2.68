#!/usr/bin/env bash
# =============================================================================
#  Wipe complet : supprime les volumes MySQL + DBGate et relance la stack.
#  Tous les comptes, persos, et l'état du monde sont perdus.
#  Les dumps `server/init-sql/*.sql` sont réimportés au boot suivant.
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
cd "$ROOT_DIR"

read -r -p "⚠️  Tu vas perdre TOUTES les données (comptes, persos, monde). Confirmer ? [y/N] " ans
[[ "$ans" =~ ^[Yy]$ ]] || { echo "Abort."; exit 0; }

echo "==> docker compose down -v (supprime aussi les volumes)"
docker compose down -v

# Cleanup explicite au cas où des volumes orphelins traînent
docker volume rm dofus-giny_mysql-data dofus-giny_dbgate-data 2>/dev/null || true

echo "==> Volumes purgés. Pour relancer : ./scripts/start-server.sh"
