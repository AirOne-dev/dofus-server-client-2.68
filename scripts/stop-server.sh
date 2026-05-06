#!/usr/bin/env bash
# Stoppe la stack OneAir / Giny (les conteneurs restent, seuls leurs process
# sont arrêtés). Pour wipe les volumes, voir reset-db.sh.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(dirname "$SCRIPT_DIR")"
cd "$ROOT_DIR"

echo "==> docker compose stop"
docker compose stop
echo "OK. Pour redémarrer : ./scripts/start-server.sh"
